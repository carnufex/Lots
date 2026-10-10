"""Engines behind the API. The API depends only on the two protocols, so it is testable without GPU or models."""
from __future__ import annotations

import io
import os
import re
import site
import threading
import time
from collections.abc import Iterator
from dataclasses import dataclass
from pathlib import Path
from typing import Protocol

from .config import Settings


@dataclass(frozen=True)
class Segment:
    start: float
    end: float
    text: str


@dataclass(frozen=True)
class Transcription:
    text: str
    language: str
    duration: float
    segments: list[Segment]


class SttEngine(Protocol):
    def transcribe(self, audio: bytes, language: str | None, prompt: str | None = None) -> Transcription: ...

    def warm_up(self) -> None:
        """Load models now so the first request is not slow."""
        ...


class TtsEngine(Protocol):
    sample_rate: int

    def voices(self) -> dict[str, str]:
        """voice id -> language."""
        ...

    def synthesize(self, text: str, voice: str, speed: float, options: "SynthOptions | None" = None) -> Iterator[object]:
        """Yields numpy float32 sample chunks, one per sentence, as they are produced."""
        ...

    def warm_up(self) -> None: ...


@dataclass(frozen=True)
class SynthOptions:
    """Per-request hints. Engines ignore what they do not support."""
    language: str | None = None
    expressiveness: float | None = None  # 0..1 (Chatterbox exaggeration)
    pace: float | None = None            # 0..1 (Chatterbox cfg_weight: lower = faster, looser)


def _add_cuda_dll_dirs() -> None:
    """On Windows the CUDA libraries from the nvidia-* pip packages are not on the DLL search path by default."""
    for sp in site.getsitepackages():
        for sub in Path(sp, "nvidia").glob("*/bin"):
            os.add_dll_directory(str(sub))
            os.environ["PATH"] = str(sub) + os.pathsep + os.environ["PATH"]


class WhisperStt:
    """faster-whisper (CTranslate2) with one model per language, loaded on first use."""

    def __init__(self, settings: Settings) -> None:
        self._settings = settings
        self._models: dict[str, object] = {}
        self._lock = threading.Lock()
        if os.name == "nt":
            _add_cuda_dll_dirs()

    def _model(self, key: str):
        """key: a language (sv/en) or "detect"."""
        with self._lock:
            if key not in self._models:
                from faster_whisper import WhisperModel

                name = self._settings.stt_detect_model if key == "detect" else self._settings.stt_models[key]
                self._models[key] = WhisperModel(
                    name, device=self._settings.stt_device, compute_type=self._settings.stt_compute_type)
            return self._models[key]

    def warm_up(self) -> None:
        for key in ("detect", *self._settings.stt_models):
            self._model(key)

    def transcribe(self, audio: bytes, language: str | None, prompt: str | None = None) -> Transcription:
        from faster_whisper.audio import decode_audio

        samples = decode_audio(io.BytesIO(audio), sampling_rate=16000)
        duration = len(samples) / 16000
        if language is None:
            language = self._identify(samples)
        segments, _info = self._model(language).transcribe(
            samples, language=language, beam_size=1, vad_filter=False, condition_on_previous_text=False,
            initial_prompt=prompt or None)  # words the speaker is likely to say: names, products, jargon
        segs = [Segment(s.start, s.end, s.text.strip()) for s in segments]
        return Transcription(" ".join(s.text for s in segs).strip(), language, duration, segs)


    def _identify(self, samples) -> str:
        _lang, _prob, all_probs = self._model("detect").detect_language(samples)
        return choose_language(dict(all_probs), tuple(self._settings.stt_models), self._settings.default_language,
                               self._settings.detect_min_confidence)


def choose_language(probs: dict[str, float], languages: tuple[str, ...], default: str, min_confidence: float = 0.5) -> str:
    """
    Picks between our languages from the identifier's probabilities. The model's own transcription confidence cannot decide
    this (the Swedish model happily translates English speech into Swedish), so only identification counts.
    If the two languages together carry little probability, the clip is probably something else (noise, a name, a very
    short utterance): use the configured default instead of a coin flip.
    """
    mine = {lang: probs.get(lang, 0.0) for lang in languages}
    if sum(mine.values()) < min_confidence:
        return default if default in languages else languages[0]
    return max(mine, key=mine.get)


_SENTENCE_END = re.compile(r"(?<=[.!?])\s+")


def split_sentences(text: str) -> list[str]:
    """Sentences for streaming synthesis; the first one is spoken while the rest are still being produced."""
    return [s.strip() for s in _SENTENCE_END.split(text.strip()) if s.strip()]


class PiperTts:
    """Piper (VITS) voices through sherpa-onnx; sentences are streamed as they are synthesized."""

    def __init__(self, settings: Settings) -> None:
        self._settings = settings
        self._engines: dict[str, object] = {}
        self._lock = threading.Lock()
        self.sample_rate = 22050

    def voices(self) -> dict[str, str]:
        """Installed voices only: a configured voice whose model was never fetched is not offered (and cannot fail later)."""
        return {vid: v.language for vid, v in self._settings.voices.items()
                if (self._settings.models_dir / v.directory / v.onnx).exists()}

    def _engine(self, voice: str):
        with self._lock:
            if voice not in self._engines:
                import sherpa_onnx

                v = self._settings.voices[voice]
                d = self._settings.models_dir / v.directory
                cfg = sherpa_onnx.OfflineTtsConfig(model=sherpa_onnx.OfflineTtsModelConfig(
                    vits=sherpa_onnx.OfflineTtsVitsModelConfig(
                        model=str(d / v.onnx), tokens=str(d / "tokens.txt"), data_dir=str(d / "espeak-ng-data")),
                    num_threads=self._settings.tts_threads, provider="cpu"))
                engine = sherpa_onnx.OfflineTts(cfg)
                self.sample_rate = engine.sample_rate
                self._engines[voice] = engine
            return self._engines[voice]

    def warm_up(self) -> None:
        missing = sorted(set(self._settings.voices) - set(self.voices()))
        if missing:
            print(f"voice: models not installed, not offered: {', '.join(missing)} (run scripts/fetch_models.py)", flush=True)
        for voice in self.voices():
            self._engine(voice).generate("Warm up.", sid=0)

    def synthesize(self, text: str, voice: str, speed: float, options: SynthOptions | None = None) -> Iterator[object]:
        import numpy as np

        engine = self._engine(voice)
        for sentence in split_sentences(text):
            audio = engine.generate(sentence, sid=0, speed=speed)
            yield np.asarray(audio.samples, dtype=np.float32)



LANGUAGES_SUPPORTED = ("sv", "en")


class ChatterboxTts:
    """
    Chatterbox Multilingual on the GPU. Voices are reference clips: `cb-default` uses the model's built-in voice (or the clip
    in refs/default.wav), every other id is a registered clip refs/<id>.wav. Computed voice conditionals are cached per id.
    Generation is serialised (one GPU), sentence by sentence so the first sentence can be played early.
    """
    DEFAULT = "cb-default"
    sample_rate = 24000

    def __init__(self, settings: Settings) -> None:
        self._settings = settings
        self._model = None
        self._default_conds = None
        self._conds: dict[str, object] = {}
        self._gpu = threading.Lock()
        self._load = threading.Lock()
        self._waiting = 0
        self._waiting_lock = threading.Lock()
        self._last_used = time.monotonic()
        self._loading = False
        settings.refs_dir.mkdir(parents=True, exist_ok=True)
        if settings.chatterbox_idle_unload_s > 0:
            threading.Thread(target=self._unload_when_idle, daemon=True).start()

    @property
    def loaded(self) -> bool:
        return self._model is not None

    @property
    def waiting(self) -> int:
        return self._waiting

    def load_in_background(self) -> None:
        """Starts loading the model without blocking the caller (who is served by the fast voice meanwhile)."""
        with self._load:
            if self._model is not None or self._loading:
                return
            self._loading = True
        threading.Thread(target=self._background_load, daemon=True).start()

    def _background_load(self) -> None:
        try:
            self._load_model()
        finally:
            self._loading = False

    def _unload_when_idle(self) -> None:
        while True:
            time.sleep(30)
            if self._model is None or time.monotonic() - self._last_used < self._settings.chatterbox_idle_unload_s:
                continue
            with self._gpu, self._load:
                if self._model is None or time.monotonic() - self._last_used < self._settings.chatterbox_idle_unload_s:
                    continue
                self._model = None
                self._default_conds = None
                self._conds.clear()
                try:
                    import gc

                    import torch

                    gc.collect()
                    torch.cuda.empty_cache()
                except Exception:
                    pass

    def _path(self, voice: str) -> Path:
        return self._settings.refs_dir / f"{'default' if voice == self.DEFAULT else voice}.wav"

    def voices(self) -> dict[str, str]:
        found = {p.stem: "*" for p in self._settings.refs_dir.glob("*.wav") if p.stem != "default"}
        return {self.DEFAULT: "*", **found}

    def _load_model(self):
        with self._load:
            if self._model is None:
                from chatterbox.mtl_tts import ChatterboxMultilingualTTS

                self._model = ChatterboxMultilingualTTS.from_pretrained(device=self._settings.chatterbox_device)
                self._default_conds = self._model.conds
            return self._model

    def warm_up(self) -> None:
        model = self._load_model()
        with self._gpu:
            model.generate("Hej.", language_id="sv")

    def register(self, voice: str, data: bytes) -> float:
        """Stores a reference clip (any format PyAV can decode) as 24 kHz mono wav. Returns its length in seconds."""
        import wave

        import numpy as np
        from faster_whisper.audio import decode_audio

        samples = decode_audio(io.BytesIO(data), sampling_rate=self.sample_rate)
        seconds = len(samples) / self.sample_rate
        if not (self._settings.ref_min_seconds <= seconds <= self._settings.ref_max_seconds):
            raise ValueError(
                f"The clip must be {self._settings.ref_min_seconds:.0f}-{self._settings.ref_max_seconds:.0f} seconds, got {seconds:.1f}.")
        peak = float(np.abs(samples).max())
        if peak < 0.02:
            raise ValueError("The clip is almost silent.")
        samples = samples / peak * 0.95
        with wave.open(str(self._path(voice)), "wb") as w:
            w.setnchannels(1)
            w.setsampwidth(2)
            w.setframerate(self.sample_rate)
            w.writeframes((samples * 32767).astype(np.int16).tobytes())
        self._conds.pop(voice, None)
        return seconds

    def delete(self, voice: str) -> bool:
        self._conds.pop(voice, None)
        path = self._path(voice)
        if not path.exists():
            return False
        path.unlink()
        return True

    def _prepare(self, model, voice: str, exaggeration: float) -> None:
        """Makes `voice` the model's current voice (the GPU lock must be held)."""
        if voice not in self._conds:
            ref = self._path(voice)
            if ref.exists():
                model.prepare_conditionals(str(ref), exaggeration=exaggeration)
                self._conds[voice] = model.conds
            else:
                self._conds[voice] = self._default_conds
        model.conds = self._conds[voice]

    def synthesize(self, text: str, voice: str, speed: float, options: SynthOptions | None = None) -> Iterator[object]:
        import numpy as np

        o = options or SynthOptions()
        language = o.language if o.language in LANGUAGES_SUPPORTED else "sv"
        exaggeration = 0.6 if o.expressiveness is None else min(max(o.expressiveness, 0.25), 1.2)
        cfg = 0.4 if o.pace is None else min(max(o.pace, 0.0), 1.0)
        model = self._load_model()
        for sentence in split_sentences(text):
            with self._waiting_lock:
                self._waiting += 1
            try:
                self._gpu.acquire()
            finally:
                with self._waiting_lock:
                    self._waiting -= 1
            try:
                self._last_used = time.monotonic()
                self._prepare(model, voice, exaggeration)
                wav = model.generate(sentence, language_id=language, exaggeration=exaggeration, cfg_weight=cfg)
                # The GPU is shared with the language model: hand cached blocks back instead of hoarding them.
                import torch

                torch.cuda.empty_cache()
            finally:
                self._gpu.release()
            yield np.asarray(wav.squeeze().cpu(), dtype=np.float32)


class CompositeTts:
    """
    Chatterbox for its voices, Piper for the rest, with a GPU guard (#84): an expressive request is served by the fast voice of the
    same language when free GPU memory is below the threshold, when too many expressive requests already wait for the GPU, or while
    Chatterbox is still loading. `last_fallback` tells the caller why (the API returns it as X-Voice-Fallback).
    """

    def __init__(self, expressive: ChatterboxTts, fast: PiperTts, settings: Settings | None = None, gpu=None) -> None:
        self._expressive = expressive
        self._fast = fast
        self._settings = settings or Settings()
        self._gpu = gpu
        self.sample_rate = fast.sample_rate

    def fallback_reason(self, voice: str) -> str | None:
        """Why an expressive voice would not be used now, or None."""
        if not self._is_expressive(voice):
            return None
        if self._gpu is not None and (mem := self._gpu.memory()) is not None and mem.free_bytes < self._settings.min_free_vram_mb * 1024 * 1024:
            # Loaded already: its memory is in use and does not need to be found again.
            if not self._expressive.loaded:
                return "vram"
        if self._expressive.waiting >= self._settings.max_expressive_queue:
            return "busy"
        if not self._expressive.loaded:
            self._expressive.load_in_background()
            return "loading"
        return None

    def fast_voice_for(self, language: str | None) -> str:
        lang = language if language in LANGUAGES_SUPPORTED else "sv"
        return next((v for v, l in self._fast.voices().items() if l == lang), next(iter(self._fast.voices())))

    def resolve(self, voice: str, options: SynthOptions | None) -> tuple[str, str | None]:
        """The voice to use for this request and the fallback reason, if any."""
        reason = self.fallback_reason(voice)
        return (self.fast_voice_for((options or SynthOptions()).language), reason) if reason else (voice, None)

    def _is_expressive(self, voice: str) -> bool:
        return voice in self._expressive.voices()

    def voices(self) -> dict[str, str]:
        return {**self._fast.voices(), **self._expressive.voices()}

    def sample_rate_of(self, voice: str) -> int:
        return self._expressive.sample_rate if self._is_expressive(voice) else self._fast.sample_rate

    def warm_up(self) -> None:
        self._fast.warm_up()
        self._expressive.warm_up()

    def synthesize(self, text: str, voice: str, speed: float, options: SynthOptions | None = None) -> Iterator[object]:
        engine = self._expressive if self._is_expressive(voice) else self._fast
        return engine.synthesize(text, voice, speed, options)

    # Voice registration only concerns the expressive engine.
    def register(self, voice: str, data: bytes) -> float:
        return self._expressive.register(voice, data)

    def delete(self, voice: str) -> bool:
        return self._expressive.delete(voice)
