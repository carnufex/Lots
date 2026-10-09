"""Engines behind the API. The API depends only on the two protocols, so it is testable without GPU or models."""
from __future__ import annotations

import io
import os
import re
import site
import threading
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
    def transcribe(self, audio: bytes, language: str | None) -> Transcription: ...

    def warm_up(self) -> None:
        """Load models now so the first request is not slow."""
        ...


class TtsEngine(Protocol):
    sample_rate: int

    def voices(self) -> dict[str, str]:
        """voice id -> language."""
        ...

    def synthesize(self, text: str, voice: str, speed: float) -> Iterator[object]:
        """Yields numpy float32 sample chunks, one per sentence, as they are produced."""
        ...

    def warm_up(self) -> None: ...


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

    def transcribe(self, audio: bytes, language: str | None) -> Transcription:
        from faster_whisper.audio import decode_audio

        samples = decode_audio(io.BytesIO(audio), sampling_rate=16000)
        duration = len(samples) / 16000
        if language is None:
            language = self._identify(samples)
        segments, _info = self._model(language).transcribe(
            samples, language=language, beam_size=1, vad_filter=False, condition_on_previous_text=False)
        segs = [Segment(s.start, s.end, s.text.strip()) for s in segments]
        return Transcription(" ".join(s.text for s in segs).strip(), language, duration, segs)


    def _identify(self, samples) -> str:
        """Swedish or English, by the larger probability among just those two (anything else is Swedish or English anyway)."""
        _lang, _prob, all_probs = self._model("detect").detect_language(samples)
        probs = dict(all_probs)
        return max(self._settings.stt_models, key=lambda lang: probs.get(lang, 0.0))


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
        return {vid: v.language for vid, v in self._settings.voices.items()}

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
        for voice in self._settings.voices:
            self._engine(voice).generate("Warm up.", sid=0)

    def synthesize(self, text: str, voice: str, speed: float) -> Iterator[object]:
        import numpy as np

        engine = self._engine(voice)
        for sentence in split_sentences(text):
            audio = engine.generate(sentence, sid=0, speed=speed)
            yield np.asarray(audio.samples, dtype=np.float32)
