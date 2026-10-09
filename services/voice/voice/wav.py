"""Minimal WAV helpers for streaming PCM16 mono audio."""
from __future__ import annotations

import struct

import numpy as np


def streaming_header(sample_rate: int, channels: int = 1, bits: int = 16) -> bytes:
    """A WAV header for audio of unknown length (sizes 0xFFFFFFFF), usable while the body is still being produced."""
    byte_rate = sample_rate * channels * bits // 8
    return (
        b"RIFF" + struct.pack("<I", 0xFFFFFFFF) + b"WAVE"
        + b"fmt " + struct.pack("<IHHIIHH", 16, 1, channels, sample_rate, byte_rate, channels * bits // 8, bits)
        + b"data" + struct.pack("<I", 0xFFFFFFFF)
    )


def to_pcm16(samples: np.ndarray) -> bytes:
    """Float samples in [-1, 1] to little-endian signed 16-bit."""
    clipped = np.clip(np.asarray(samples, dtype=np.float32), -1.0, 1.0)
    return (clipped * 32767.0).astype("<i2").tobytes()
