"""TTS benchmark (spike #39): time to first audio and real-time factor for Swedish and English voices.

Run: .venv/Scripts/python tts_bench.py
Writes wav files to out/ so they can be listened to and reused as STT test input.
"""
import json
import statistics
import time
from pathlib import Path

import numpy as np
import sherpa_onnx
import soundfile as sf

HERE = Path(__file__).parent
MODELS = HERE / "models"
OUT = HERE / "out"
OUT.mkdir(exist_ok=True)

SV = [
    "Hej! Jag kollar det åt dig, ett ögonblick.",
    "Två av de tolv containrarna är inte friska just nu. Databasen startade om för fyra minuter sedan.",
    "Kan du säga vilken site du menar, så tittar jag på kablarna som går dit?",
]
EN = [
    "Hello! Let me check that for you, one moment.",
    "Two of the twelve containers are unhealthy right now. The database restarted four minutes ago.",
    "Which site do you mean? Then I can look at the cables that go there.",
]


def piper(dirname: str, onnx: str, threads: int = 4, provider: str = "cpu") -> sherpa_onnx.OfflineTts:
    d = MODELS / dirname
    cfg = sherpa_onnx.OfflineTtsConfig(
        model=sherpa_onnx.OfflineTtsModelConfig(
            vits=sherpa_onnx.OfflineTtsVitsModelConfig(
                model=str(d / onnx), tokens=str(d / "tokens.txt"), data_dir=str(d / "espeak-ng-data")),
            num_threads=threads, provider=provider),
    )
    return sherpa_onnx.OfflineTts(cfg)


def bench(name: str, tts: sherpa_onnx.OfflineTts, sentences: list[str], sid: int = 0) -> dict:
    tts.generate("Warm up.", sid=sid)  # first call includes lazy init
    first_chunk, rtf, totals = [], [], []
    for i, text in enumerate(sentences):
        t0 = time.perf_counter()
        first = []

        def cb(samples, progress, first=first, t0=t0):
            if not first:
                first.append(time.perf_counter() - t0)
            return 1

        audio = tts.generate(text, sid=sid, speed=1.0, callback=cb)
        total = time.perf_counter() - t0
        dur = len(audio.samples) / audio.sample_rate
        first_chunk.append(first[0] if first else total)
        totals.append(total)
        rtf.append(total / dur)
        sf.write(OUT / f"{name}-{i}.wav", np.array(audio.samples, dtype=np.float32), audio.sample_rate)
    return {
        "voice": name,
        "first_audio_ms_p50": round(statistics.median(first_chunk) * 1000),
        "first_audio_ms_max": round(max(first_chunk) * 1000),
        "total_ms_p50": round(statistics.median(totals) * 1000),
        "rtf_p50": round(statistics.median(rtf), 3),
    }


results = []
for name, dirname, onnx, texts in [
    ("sv-nst", "vits-piper-sv_SE-nst-medium", "sv_SE-nst-medium.onnx", SV),
    ("sv-alma", "vits-piper-sv_SE-alma-medium", "sv_SE-alma-medium.onnx", SV),
    ("en-lessac", "vits-piper-en_US-lessac-medium", "en_US-lessac-medium.onnx", EN),
]:
    for provider in ("cpu", "cuda"):
        try:
            tts = piper(dirname, onnx, provider=provider)
            r = bench(f"{name}-{provider}", tts, texts)
            r["provider"] = provider
            results.append(r)
            print(json.dumps(r))
        except Exception as e:  # CUDA provider may be missing in the pip build
            print(json.dumps({"voice": name, "provider": provider, "error": str(e)[:160]}))

print("done")
