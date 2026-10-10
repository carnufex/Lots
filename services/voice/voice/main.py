"""Entry point: `python -m voice.main` (or uvicorn voice.main:app)."""
from __future__ import annotations

import os

import uvicorn

from .app import create_app
from .config import Settings
from .engines import ChatterboxTts, CompositeTts, PiperTts, WhisperStt
from .gpu import GpuMonitor
from .logs import configure as configure_logging


def build():
    settings = Settings.from_env()
    stt, tts = WhisperStt(settings), PiperTts(settings)
    gpu = GpuMonitor()
    if settings.chatterbox:
        tts = CompositeTts(ChatterboxTts(settings), tts, settings, gpu)
    if settings.preload:
        stt.warm_up()
        tts.warm_up()
    return create_app(settings, stt, tts, gpu)


app = build() if os.environ.get("VOICE_BUILD_ON_IMPORT") == "1" else None

if __name__ == "__main__":
    log_config = configure_logging()
    if log_config:
        import logging.config

        logging.config.dictConfig(log_config)
    uvicorn.run(build(), host=os.environ.get("VOICE_HOST", "127.0.0.1"), port=int(os.environ.get("VOICE_PORT", "8700")),
                **({"log_config": log_config} if log_config else {}))
