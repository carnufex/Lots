"""The HTTP API: OpenAI-shaped audio endpoints (ADR 0012). Engines are injected."""
from __future__ import annotations

import asyncio
import hmac
import re
import time
from dataclasses import replace
from collections.abc import AsyncIterator

from fastapi import Depends, FastAPI, File, Form, Header, HTTPException, UploadFile
from fastapi.concurrency import run_in_threadpool
from fastapi.responses import JSONResponse, PlainTextResponse, StreamingResponse
from pydantic import BaseModel

from . import tracing
from .config import LANGUAGES, Settings
from .engines import SttEngine, SynthOptions, TtsEngine
from .gpu import GpuMonitor
from .meetings import MeetingEngine
from .vocabulary import apply_vocabulary, parse_vocabulary
from .wav import streaming_header, to_pcm16


class SpeechRequest(BaseModel):
    model: str = "piper"
    input: str
    voice: str
    response_format: str = "wav"
    speed: float = 1.0
    # Extensions for the expressive engine; other engines ignore them.
    language: str | None = None
    expressiveness: float | None = None
    pace: float | None = None


def create_app(settings: Settings, stt: SttEngine, tts: TtsEngine, gpu: GpuMonitor | None = None,
               meetings: MeetingEngine | None = None) -> FastAPI:
    settings.validate()
    app = FastAPI(title="Lots voice service", version="0.1.0")
    gate = asyncio.Semaphore(settings.max_concurrency)

    tracing.configure()

    @app.middleware("http")
    async def trace_requests(request, call_next):
        # Joins the run's trace: the shell sends traceparent with every call (#139).
        with tracing.request_span(f"{request.method} {request.url.path}", dict(request.headers)) as span:
            response = await call_next(request)
            if span is not None:
                span.set_attribute("http.response.status_code", response.status_code)
            return response

    @app.middleware("http")
    async def no_urlencoded_forms(request, call_next):
        # No endpoint takes urlencoded forms, and the Starlette version held by chatterbox's pins parses them without size
        # limits (CVE-2026-54283, #147): refuse them before any parsing happens.
        if request.headers.get("content-type", "").lower().startswith("application/x-www-form-urlencoded"):
            return JSONResponse({"detail": "Form-encoded bodies are not accepted."}, status_code=415)
        return await call_next(request)

    def authorize(authorization: str | None = Header(default=None)) -> None:
        if settings.api_key is None:
            return  # validate() guarantees this only happens with VOICE_ALLOW_ANONYMOUS=1
        supplied = authorization.removeprefix("Bearer ").strip() if authorization else ""
        if not hmac.compare_digest(supplied.encode(), settings.api_key.encode()):
            raise HTTPException(status_code=401, detail="Invalid or missing API key.")

    @app.get("/health")
    def health() -> dict:
        body: dict = {"status": "ok"}
        mem = gpu.memory() if gpu is not None else None
        if mem is not None:
            body["gpu"] = {"free_bytes": mem.free_bytes, "total_bytes": mem.total_bytes,
                           "low": mem.free_bytes < settings.min_free_vram_mb * 1024 * 1024}
        expressive = getattr(tts, "_expressive", None)
        if expressive is not None:
            body["expressive"] = {"loaded": expressive.loaded, "waiting": expressive.waiting}
        return body

    @app.get("/v1/models", dependencies=[Depends(authorize)])
    def models() -> dict:
        data = [{"id": f"stt-{lang}", "object": "model", "owned_by": settings.stt_models[lang]} for lang in LANGUAGES]
        data += [{"id": f"voice:{vid}", "object": "model", "owned_by": lang} for vid, lang in tts.voices().items()]
        return {"object": "list", "data": data}

    @app.post("/v1/audio/transcriptions", dependencies=[Depends(authorize)])
    async def transcriptions(
        file: UploadFile = File(...),
        model: str = Form("whisper"),
        language: str | None = Form(None),
        response_format: str = Form("json"),
        prompt: str | None = Form(None),
    ):
        if language is not None and language not in LANGUAGES:
            raise HTTPException(400, f"Unsupported language '{language}' (supported: {', '.join(LANGUAGES)}).")
        if response_format not in ("json", "verbose_json", "text"):
            raise HTTPException(400, "response_format must be json, verbose_json or text.")
        if prompt is not None and len(prompt) > settings.max_prompt_chars:
            raise HTTPException(413, f"prompt is longer than {settings.max_prompt_chars} characters.")
        data = await file.read(settings.max_audio_bytes + 1)
        if not data:
            raise HTTPException(400, "Empty audio.")
        if len(data) > settings.max_audio_bytes:
            raise HTTPException(413, f"Audio is larger than {settings.max_audio_bytes // (1024 * 1024)} MB.")

        started = time.perf_counter()
        async with gate:
            try:
                result = await run_in_threadpool(stt.transcribe, data, language, prompt)
            except Exception as ex:
                raise HTTPException(422, f"Could not transcribe this audio: {type(ex).__name__}") from ex
        elapsed = time.perf_counter() - started

        # The prompt is a comma separated vocabulary: a hint for the model and a spelling correction afterwards.
        words = parse_vocabulary(prompt)
        if words:
            result = replace(result, text=apply_vocabulary(result.text, words),
                             segments=[replace(s, text=apply_vocabulary(s.text, words)) for s in result.segments])

        headers = {"X-Processing-Ms": str(round(elapsed * 1000))}
        if response_format == "text":
            return PlainTextResponse(result.text, headers=headers)
        body: dict = {"text": result.text}
        if response_format == "verbose_json":
            body |= {
                "language": result.language, "duration": round(result.duration, 3),
                "segments": [{"id": i, "start": round(s.start, 3), "end": round(s.end, 3), "text": s.text}
                             for i, s in enumerate(result.segments)],
            }
        return JSONResponse(body, headers=headers)

    @app.post("/v1/audio/meetings", dependencies=[Depends(authorize)])
    async def meeting(
        file: UploadFile = File(...),
        language: str | None = Form(None),
        num_speakers: int | None = Form(None),
        prompt: str | None = Form(None),
        word_timestamps: bool = Form(False),
    ):
        """A whole recording (#41): timestamped segments (with words for #44) and speaker turns. The shell merges them."""
        if meetings is None:
            raise HTTPException(503, "Meeting transcription is not available on this voice service.")
        if language is not None and language not in LANGUAGES:
            raise HTTPException(400, f"Unsupported language '{language}' (supported: {', '.join(LANGUAGES)}).")
        if num_speakers is not None and not 1 <= num_speakers <= 20:
            raise HTTPException(400, "num_speakers must be 1-20.")
        if prompt is not None and len(prompt) > settings.max_prompt_chars:
            raise HTTPException(413, f"prompt is longer than {settings.max_prompt_chars} characters.")
        data = await file.read(settings.max_meeting_bytes + 1)
        if not data:
            raise HTTPException(400, "Empty audio.")
        if len(data) > settings.max_meeting_bytes:
            raise HTTPException(413, f"Recording is larger than {settings.max_meeting_bytes // (1024 * 1024)} MB.")
        started = time.perf_counter()
        async with gate:
            try:
                result = await run_in_threadpool(meetings.process, data, language, num_speakers, prompt, word_timestamps)
            except RuntimeError as ex:
                raise HTTPException(503, str(ex)) from ex
            except Exception as ex:
                raise HTTPException(422, f"Could not process this recording: {type(ex).__name__}") from ex
        if result.duration > settings.max_meeting_seconds:
            raise HTTPException(413, f"Recording is longer than {settings.max_meeting_seconds // 60} minutes.")
        words = parse_vocabulary(prompt)
        return JSONResponse({
            "language": result.language, "duration": round(result.duration, 3),
            "segments": [{"start": s.start, "end": s.end, "text": apply_vocabulary(s.text, words) if words else s.text,
                          **({"words": [{"start": w.start, "end": w.end, "word": w.word} for w in s.words]} if s.words else {})}
                         for s in result.segments],
            "turns": [{"start": t.start, "end": t.end, "speaker": t.speaker} for t in result.turns],
        }, headers={"X-Processing-Ms": str(round((time.perf_counter() - started) * 1000))})

    @app.post("/v1/audio/speech", dependencies=[Depends(authorize)])
    async def speech(req: SpeechRequest):
        voices = tts.voices()
        if req.voice not in voices:
            raise HTTPException(400, f"Unknown voice '{req.voice}' (available: {', '.join(sorted(voices))}).")
        if req.response_format not in ("wav", "pcm"):
            raise HTTPException(400, "response_format must be wav or pcm.")
        text = req.input.strip()
        if not text:
            raise HTTPException(400, "Empty input.")
        if len(text) > settings.max_text_chars:
            raise HTTPException(413, f"Text is longer than {settings.max_text_chars} characters.")
        speed = min(max(req.speed, 0.5), 2.0)
        options = SynthOptions(language=req.language, expressiveness=req.expressiveness, pace=req.pace)
        rate_of = getattr(tts, "sample_rate_of", None)
        # GPU guard (#84): the expressive voice may be swapped for the fast one; the caller learns why.
        voice, fallback = tts.resolve(req.voice, options) if hasattr(tts, "resolve") else (req.voice, None)

        async def body() -> AsyncIterator[bytes]:
            async with gate:
                chunks = tts.synthesize(text, voice, speed, options)
                sentinel = object()
                first = True
                while True:
                    chunk = await run_in_threadpool(next, chunks, sentinel)
                    if chunk is sentinel:
                        break
                    if first and req.response_format == "wav":
                        yield streaming_header(rate_of(voice) if rate_of else tts.sample_rate)
                    first = False
                    yield to_pcm16(chunk)

        media = "audio/wav" if req.response_format == "wav" else "audio/L16"
        headers = {"X-Voice-Used": voice} | ({"X-Voice-Fallback": fallback} if fallback else {})
        return StreamingResponse(body(), media_type=media, headers=headers)

    _VOICE_ID = re.compile(r"^[a-z0-9][a-z0-9-]{2,63}$")

    @app.put("/v1/voices/{voice_id}", dependencies=[Depends(authorize)])
    async def register_voice(voice_id: str, audio: UploadFile = File(...)):
        """Registers (or replaces) a reference voice from a short clip of the speaker. Personal data: callers must have consent."""
        register = getattr(tts, "register", None)
        if register is None:
            raise HTTPException(501, "This service has no expressive voices.")
        if not _VOICE_ID.match(voice_id) or voice_id.startswith("cb-") or voice_id == "default":
            raise HTTPException(400, "Voice id must be 3-64 lowercase letters, digits or dashes and not start with 'cb-'.")
        data = await audio.read()
        if not data or len(data) > settings.max_ref_bytes:
            raise HTTPException(413, f"The clip must be between 1 byte and {settings.max_ref_bytes // (1024 * 1024)} MB.")
        try:
            seconds = await run_in_threadpool(register, voice_id, data)
        except ValueError as e:
            raise HTTPException(400, str(e))
        return {"id": voice_id, "seconds": round(seconds, 1)}

    @app.get("/v1/voices/{voice_id}", dependencies=[Depends(authorize)])
    def get_voice(voice_id: str):
        """Whether a registered voice exists: the shell checks it after deleting one (#93)."""
        if not _VOICE_ID.match(voice_id) or voice_id.startswith("cb-") or voice_id not in tts.voices():
            raise HTTPException(404, "No such voice.")
        return {"id": voice_id}

    @app.delete("/v1/voices/{voice_id}", dependencies=[Depends(authorize)])
    async def delete_voice(voice_id: str):
        delete = getattr(tts, "delete", None)
        if delete is None or not _VOICE_ID.match(voice_id) or voice_id.startswith("cb-"):
            raise HTTPException(404, "No such voice.")
        if not await run_in_threadpool(delete, voice_id):
            raise HTTPException(404, "No such voice.")
        return {"id": voice_id, "deleted": True}

    return app
