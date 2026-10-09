"""STT benchmark (spike #39): faster-whisper on the GPU, Swedish (KB-Whisper) and English (Whisper large-v3-turbo).

Input is the TTS output from tts_bench.py (clean synthetic speech: good for latency and a rough round-trip check,
NOT a substitute for real recordings). Run: .venv/Scripts/python stt_bench.py
"""
import json
import os
import site
import statistics
import subprocess
import sys
import time
from pathlib import Path

# CUDA libraries come from the pip packages nvidia-cublas-cu12 / nvidia-cudnn-cu12.
for sp in site.getsitepackages():
    for sub in Path(sp, "nvidia").glob("*/bin"):
        os.add_dll_directory(str(sub))
        os.environ["PATH"] = str(sub) + os.pathsep + os.environ["PATH"]

import jiwer  # noqa: E402
from faster_whisper import WhisperModel  # noqa: E402
from faster_whisper.audio import decode_audio  # noqa: E402

HERE = Path(__file__).parent
OUT = HERE / "out"

REF = {
    "sv": [
        "Hej! Jag kollar det åt dig, ett ögonblick.",
        "Två av de tolv containrarna är inte friska just nu. Databasen startade om för fyra minuter sedan.",
        "Kan du säga vilken site du menar, så tittar jag på kablarna som går dit?",
    ],
    "en": [
        "Hello! Let me check that for you, one moment.",
        "Two of the twelve containers are unhealthy right now. The database restarted four minutes ago.",
        "Which site do you mean? Then I can look at the cables that go there.",
    ],
}


def vram_mb() -> int:
    out = subprocess.run(["nvidia-smi", "--query-gpu=memory.used", "--format=csv,noheader,nounits"],
                         capture_output=True, text=True).stdout.strip()
    return int(out.splitlines()[0])


def norm(s: str) -> str:
    return jiwer.RemovePunctuation()(s.lower())


def bench(model_id: str, lang: str, voices: list[str], compute: str = "float16", device: str = "cuda") -> dict:
    base = vram_mb()
    t0 = time.perf_counter()
    model = WhisperModel(model_id, device=device, compute_type=compute)
    load_s = time.perf_counter() - t0
    vram = vram_mb() - base

    clips = [(v, i) for v in voices for i in range(3)]
    audio = {c: decode_audio(str(OUT / f"{c[0]}-{c[1]}.wav"), sampling_rate=16000) for c in clips}

    def transcribe(a):
        segs, _ = model.transcribe(a, language=lang, beam_size=1, vad_filter=False, condition_on_previous_text=False)
        return " ".join(s.text.strip() for s in segs)

    transcribe(audio[clips[0]])  # warm up (CUDA kernels, caches)
    lat, wers = [], []
    for c in clips:
        t = time.perf_counter()
        text = transcribe(audio[c])
        lat.append(time.perf_counter() - t)
        wers.append(jiwer.wer(norm(REF[lang][c[1]]), norm(text)))
        print(f"   {c[0]}-{c[1]} {lat[-1]*1000:5.0f} ms  WER {wers[-1]:.2f}  | {text}")
    dur = statistics.mean(len(a) / 16000 for a in audio.values())
    res = {
        "model": model_id, "lang": lang, "device": device, "compute": compute,
        "load_s": round(load_s, 1), "vram_mb": vram,
        "latency_ms_p50": round(statistics.median(lat) * 1000),
        "latency_ms_max": round(max(lat) * 1000),
        "mean_clip_s": round(dur, 1),
        "wer_mean": round(statistics.mean(wers), 3),
    }
    print(json.dumps(res))
    del model
    return res


if __name__ == "__main__":
    runs = [
        ("KBLab/kb-whisper-small", "sv", ["sv-nst-cpu", "sv-alma-cpu"]),
        ("KBLab/kb-whisper-medium", "sv", ["sv-nst-cpu", "sv-alma-cpu"]),
        ("KBLab/kb-whisper-large", "sv", ["sv-nst-cpu", "sv-alma-cpu"]),
        ("Systran/faster-whisper-large-v3-turbo", "sv", ["sv-nst-cpu", "sv-alma-cpu"]),
        ("Systran/faster-whisper-large-v3-turbo", "en", ["en-lessac-cpu"]),
        ("Systran/faster-whisper-small.en", "en", ["en-lessac-cpu"]),
    ]
    only = sys.argv[1:]  # optional substring filter
    for model_id, lang, voices in runs:
        if only and not any(o in model_id for o in only):
            continue
        print(f"== {model_id} ({lang})")
        try:
            bench(model_id, lang, voices)
        except Exception as e:
            print(json.dumps({"model": model_id, "error": str(e)[:300]}))
