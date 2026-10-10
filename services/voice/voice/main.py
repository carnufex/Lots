"""Entry point: `python -m voice.main` (or uvicorn voice.main:app)."""
from __future__ import annotations

import os

import uvicorn

from .app import create_app
from .config import Settings
from .engines import ChatterboxTts, CompositeTts, PiperTts, WhisperStt


def build():
    settings = Settings.from_env()
    stt, tts = WhisperStt(settings), PiperTts(settings)
    if settings.chatterbox:
        tts = CompositeTts(ChatterboxTts(settings), tts)
    if settings.preload:
        stt.warm_up()
        tts.warm_up()
    return create_app(settings, stt, tts)


app = build() if os.environ.get("VOICE_BUILD_ON_IMPORT") == "1" else None

if __name__ == "__main__":
    uvicorn.run(build(), host=os.environ.get("VOICE_HOST", "127.0.0.1"), port=int(os.environ.get("VOICE_PORT", "8700")))
