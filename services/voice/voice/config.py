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


# Licenses (docs/third-party-licenses.md): nst CC0, alma CC BY 4.0 (attribution), ljspeech public domain.
DEFAULT_VOICES = {
    "sv-nst": VoiceDef("vits-piper-sv_SE-nst-medium", "sv_SE-nst-medium.onnx", "sv"),
    "sv-alma": VoiceDef("vits-piper-sv_SE-alma-medium", "sv_SE-alma-medium.onnx", "sv"),
    "en-ljspeech": VoiceDef("vits-piper-en_US-ljspeech-medium", "en_US-ljspeech-medium.onnx", "en"),
}

# Trained on data licensed for research only (Blizzard 2013 Lessac: no commercial use). Never on by default; enable with
# VOICE_RESEARCH_VOICES=1 only where that license fits (fetch with scripts/fetch_models.py --research).
RESEARCH_VOICES = {
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
    stt_detect_model: str = "Systran/faster-whisper-small"  # "base" was too unsure on real speech (22 % for a clear Swedish clip)
    # Language used when identification is not confident (the owner mostly speaks Swedish).
    default_language: str = "sv"
    # Minimum combined probability of Swedish and English for the identification to be trusted.
    detect_min_confidence: float = 0.5
    preload: bool = True
    stt_device: str = "cuda"
    stt_compute_type: str = "float16"
    tts_threads: int = 4
    voices: dict[str, VoiceDef] = field(default_factory=lambda: dict(DEFAULT_VOICES))
    max_audio_bytes: int = 25 * 1024 * 1024
    # Meetings (#41): whole recordings, much larger than a dictation.
    max_meeting_bytes: int = 300 * 1024 * 1024
    max_meeting_seconds: int = 4 * 3600
    max_text_chars: int = 2000
    max_prompt_chars: int = 600
    max_concurrency: int = 2
    # Chatterbox Multilingual (MIT) as the expressive voice. Piper stays as the fast fallback.
    chatterbox: bool = False
    chatterbox_device: str = "cuda"
    # Where registered reference voices live (one wav per voice id). Personal data: keep it on a private volume.
    refs_dir: Path = Path("refs")
    max_ref_bytes: int = 10 * 1024 * 1024
    ref_min_seconds: float = 3.0
    ref_max_seconds: float = 40.0
    # GPU guard (#84): below this much free GPU memory the expressive voice is skipped for the fast one (Piper on the CPU).
    min_free_vram_mb: int = 1200
    # More expressive requests than this waiting for the GPU: the next ones use the fast voice instead of queueing.
    max_expressive_queue: int = 2
    # Unload Chatterbox after this many idle seconds to give its ~3.5 GB back (0 = keep it loaded).
    chatterbox_idle_unload_s: int = 900

    @staticmethod
    def from_env(env: dict[str, str] | None = None) -> "Settings":
        e = os.environ if env is None else env
        base = Settings()
        voices = base.voices
        if e.get("VOICE_VOICES"):
            voices = {k: VoiceDef(**v) for k, v in json.loads(e["VOICE_VOICES"]).items()}
        if e.get("VOICE_RESEARCH_VOICES") == "1":
            voices = {**voices, **RESEARCH_VOICES}
        return Settings(
            api_key=e.get("VOICE_API_KEY") or None,
            allow_anonymous=e.get("VOICE_ALLOW_ANONYMOUS") == "1",
            models_dir=Path(e.get("VOICE_MODELS_DIR", "models")),
            stt_models={"sv": e.get("VOICE_STT_SV", base.stt_models["sv"]), "en": e.get("VOICE_STT_EN", base.stt_models["en"])},
            stt_detect_model=e.get("VOICE_STT_DETECT", base.stt_detect_model),
            default_language=e.get("VOICE_DEFAULT_LANGUAGE", base.default_language),
            detect_min_confidence=float(e.get("VOICE_DETECT_MIN_CONFIDENCE", base.detect_min_confidence)),
            preload=e.get("VOICE_PRELOAD", "1") == "1",
            stt_device=e.get("VOICE_STT_DEVICE", base.stt_device),
            stt_compute_type=e.get("VOICE_STT_COMPUTE", base.stt_compute_type),
            tts_threads=int(e.get("VOICE_TTS_THREADS", base.tts_threads)),
            voices=voices,
            max_audio_bytes=int(e.get("VOICE_MAX_AUDIO_BYTES", base.max_audio_bytes)),
            max_meeting_bytes=int(e.get("VOICE_MAX_MEETING_BYTES", base.max_meeting_bytes)),
            max_meeting_seconds=int(e.get("VOICE_MAX_MEETING_SECONDS", base.max_meeting_seconds)),
            max_text_chars=int(e.get("VOICE_MAX_TEXT_CHARS", base.max_text_chars)),
            max_prompt_chars=int(e.get("VOICE_MAX_PROMPT_CHARS", base.max_prompt_chars)),
            max_concurrency=int(e.get("VOICE_MAX_CONCURRENCY", base.max_concurrency)),
            chatterbox=e.get("VOICE_CHATTERBOX", "0") == "1",
            chatterbox_device=e.get("VOICE_CHATTERBOX_DEVICE", base.chatterbox_device),
            refs_dir=Path(e.get("VOICE_REFS_DIR", "refs")),
            min_free_vram_mb=int(e.get("VOICE_MIN_FREE_VRAM_MB", base.min_free_vram_mb)),
            max_expressive_queue=int(e.get("VOICE_MAX_EXPRESSIVE_QUEUE", base.max_expressive_queue)),
            chatterbox_idle_unload_s=int(e.get("VOICE_CHATTERBOX_IDLE_UNLOAD_S", base.chatterbox_idle_unload_s)),
        )

    def validate(self) -> None:
        """Fail at startup rather than expose an open service by accident."""
        if not self.api_key and not self.allow_anonymous:
            raise RuntimeError("VOICE_API_KEY is required (set VOICE_ALLOW_ANONYMOUS=1 only for local development).")
