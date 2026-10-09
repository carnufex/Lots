"""Configuration, read from environment variables (no config files: containers and services set env)."""
from __future__ import annotations

import json
import os
from dataclasses import dataclass, field
from pathlib import Path

LANGUAGES = ("sv", "en")


@dataclass(frozen=True)
class VoiceDef:
    """A synthesis voice: a Piper (VITS) model directory below the models directory."""
    directory: str
    onnx: str
    language: str


DEFAULT_VOICES = {
    "sv-nst": VoiceDef("vits-piper-sv_SE-nst-medium", "sv_SE-nst-medium.onnx", "sv"),
    "sv-alma": VoiceDef("vits-piper-sv_SE-alma-medium", "sv_SE-alma-medium.onnx", "sv"),
    "en-lessac": VoiceDef("vits-piper-en_US-lessac-medium", "en_US-lessac-medium.onnx", "en"),
}


@dataclass(frozen=True)
class Settings:
    api_key: str | None = None
    allow_anonymous: bool = False
    models_dir: Path = Path("models")
    stt_models: dict[str, str] = field(default_factory=lambda: {
        "sv": "KBLab/kb-whisper-medium",     # Apache-2.0, Swedish fine-tune of Whisper
        "en": "Systran/faster-whisper-small.en",  # MIT
    })
    # Small multilingual Whisper used only to tell Swedish from English (the Swedish model is specialised and biased).
    stt_detect_model: str = "Systran/faster-whisper-base"
    preload: bool = True
    stt_device: str = "cuda"
    stt_compute_type: str = "float16"
    tts_threads: int = 4
    voices: dict[str, VoiceDef] = field(default_factory=lambda: dict(DEFAULT_VOICES))
    max_audio_bytes: int = 25 * 1024 * 1024
    max_text_chars: int = 2000
    max_concurrency: int = 2

    @staticmethod
    def from_env(env: dict[str, str] | None = None) -> "Settings":
        e = os.environ if env is None else env
        base = Settings()
        voices = base.voices
        if e.get("VOICE_VOICES"):
            voices = {k: VoiceDef(**v) for k, v in json.loads(e["VOICE_VOICES"]).items()}
        return Settings(
            api_key=e.get("VOICE_API_KEY") or None,
            allow_anonymous=e.get("VOICE_ALLOW_ANONYMOUS") == "1",
            models_dir=Path(e.get("VOICE_MODELS_DIR", "models")),
            stt_models={"sv": e.get("VOICE_STT_SV", base.stt_models["sv"]), "en": e.get("VOICE_STT_EN", base.stt_models["en"])},
            stt_detect_model=e.get("VOICE_STT_DETECT", base.stt_detect_model),
            preload=e.get("VOICE_PRELOAD", "1") == "1",
            stt_device=e.get("VOICE_STT_DEVICE", base.stt_device),
            stt_compute_type=e.get("VOICE_STT_COMPUTE", base.stt_compute_type),
            tts_threads=int(e.get("VOICE_TTS_THREADS", base.tts_threads)),
            voices=voices,
            max_audio_bytes=int(e.get("VOICE_MAX_AUDIO_BYTES", base.max_audio_bytes)),
            max_text_chars=int(e.get("VOICE_MAX_TEXT_CHARS", base.max_text_chars)),
            max_concurrency=int(e.get("VOICE_MAX_CONCURRENCY", base.max_concurrency)),
        )

    def validate(self) -> None:
        """Fail at startup rather than expose an open service by accident."""
        if not self.api_key and not self.allow_anonymous:
            raise RuntimeError("VOICE_API_KEY is required (set VOICE_ALLOW_ANONYMOUS=1 only for local development).")
