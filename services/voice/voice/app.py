"""The HTTP API: OpenAI-shaped audio endpoints (ADR 0012). Engines are injected."""
from __future__ import annotations

import asyncio
import hmac
import time
from collections.abc import AsyncIterator

from fastapi import Depends, FastAPI, File, Form, Header, HTTPException, UploadFile
from fastapi.concurrency import run_in_threadpool
from fastapi.responses import JSONResponse, PlainTextResponse, StreamingResponse
from pydantic import BaseModel

from .config import LANGUAGES, Settings
from .engines import SttEngine, TtsEngine
from .wav import streaming_header, to_pcm16


class SpeechRequest(BaseModel):
    model: str = "piper"
    input: str
    voice: str
    response_format: str = "wav"
    speed: float = 1.0


def create_app(settings: Settings, stt: SttEngine, tts: TtsEngine) -> FastAPI:
    settings.validate()
    app = FastAPI(title="Lots voice service", version="0.1.0")
    gate = asyncio.Semaphore(settings.max_concurrency)

    def authorize(authorization: str | None = Header(default=None)) -> None:
        if settings.api_key is None:
            return  # validate() guarantees this only happens with VOICE_ALLOW_ANONYMOUS=1
        supplied = authorization.removeprefix("Bearer ").strip() if authorization else ""
        if not hmac.compare_digest(supplied.encode(), settings.api_key.encode()):
            raise HTTPException(status_code=401, detail="Invalid or missing API key.")

    @app.get("/health")
    def health() -> dict:
        return {"status": "ok"}

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
    ):
        if language is not None and language not in LANGUAGES:
            raise HTTPException(400, f"Unsupported language '{language}' (supported: {', '.join(LANGUAGES)}).")
        if response_format not in ("json", "verbose_json", "text"):
            raise HTTPException(400, "response_format must be json, verbose_json or text.")
        data = await file.read(settings.max_audio_bytes + 1)
        if not data:
            raise HTTPException(400, "Empty audio.")
        if len(data) > settings.max_audio_bytes:
            raise HTTPException(413, f"Audio is larger than {settings.max_audio_bytes // (1024 * 1024)} MB.")

        started = time.perf_counter()
        async with gate:
            try:
                result = await run_in_threadpool(stt.transcribe, data, language)
            except Exception as ex:
                raise HTTPException(422, f"Could not transcribe this audio: {type(ex).__name__}") from ex
        elapsed = time.perf_counter() - started

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

        async def body() -> AsyncIterator[bytes]:
            async with gate:
                chunks = tts.synthesize(text, req.voice, speed)
                sentinel = object()
                first = True
                while True:
                    chunk = await run_in_threadpool(next, chunks, sentinel)
                    if chunk is sentinel:
                        break
                    if first and req.response_format == "wav":
                        yield streaming_header(tts.sample_rate)
                    first = False
                    yield to_pcm16(chunk)

        media = "audio/wav" if req.response_format == "wav" else "audio/L16"
        return StreamingResponse(body(), media_type=media)

    return app
