"""End-to-end voice latency (spike #39): from "end of speech" to "first audio of the answer".

Chain per turn: STT (faster-whisper, GPU) -> LLM streaming (Ollama, thinking off) until the first sentence is complete
-> TTS of that sentence (Piper via sherpa-onnx) -> first audio chunk. Endpointing (silence detection, a constant
~300 ms in a real system) is reported separately. Input audio is synthetic TTS speech (see stt_bench.py caveat).

Run: .venv/Scripts/python e2e_bench.py
"""
import json
import os
import re
import site
import statistics
import subprocess
import time
from pathlib import Path

for sp in site.getsitepackages():
    for sub in Path(sp, "nvidia").glob("*/bin"):
        os.add_dll_directory(str(sub))
        os.environ["PATH"] = str(sub) + os.pathsep + os.environ["PATH"]

import httpx  # noqa: E402
import sherpa_onnx  # noqa: E402
from faster_whisper import WhisperModel  # noqa: E402
from faster_whisper.audio import decode_audio  # noqa: E402

HERE = Path(__file__).parent
OUT = HERE / "out"
MODELS = HERE / "models"
LLM = "qwen3.5:latest"
SYSTEM = ("You are a voice assistant for a homelab. Answer in the language of the question, in one or two short "
          "sentences, without lists or markdown.")
SENTENCE_END = re.compile(r"[.!?]\s")

LANGS = {
    "sv": dict(whisper="KBLab/kb-whisper-medium", clip="sv-nst-cpu-0", piper=("vits-piper-sv_SE-nst-medium", "sv_SE-nst-medium.onnx")),
    "en": dict(whisper="Systran/faster-whisper-small.en", clip="en-lessac-cpu-1", piper=("vits-piper-en_US-lessac-medium", "en_US-lessac-medium.onnx")),
}


def piper(dirname, onnx):
    d = MODELS / dirname
    return sherpa_onnx.OfflineTts(sherpa_onnx.OfflineTtsConfig(model=sherpa_onnx.OfflineTtsModelConfig(
        vits=sherpa_onnx.OfflineTtsVitsModelConfig(model=str(d / onnx), tokens=str(d / "tokens.txt"), data_dir=str(d / "espeak-ng-data")),
        num_threads=4, provider="cpu")))


def vram():
    return int(subprocess.run(["nvidia-smi", "--query-gpu=memory.used", "--format=csv,noheader,nounits"],
                              capture_output=True, text=True).stdout.split()[0])


def llm_first_sentence(question: str) -> tuple[str, float, float]:
    """Returns (sentence, ttft_s, sentence_ready_s) measured from the call."""
    body = {"model": LLM, "stream": True, "think": False, "keep_alive": "10m", "options": {"num_predict": 60},
            "messages": [{"role": "system", "content": SYSTEM}, {"role": "user", "content": question}]}
    t0 = time.perf_counter()
    ttft, text = None, ""
    with httpx.stream("POST", "http://localhost:11434/api/chat", json=body, timeout=120) as r:
        for line in r.iter_lines():
            if not line:
                continue
            d = json.loads(line)
            piece = d.get("message", {}).get("content", "")
            if piece:
                ttft = ttft or (time.perf_counter() - t0)
                text += piece
                m = SENTENCE_END.search(text + " ")
                if m:
                    return text[: m.end()].strip(), ttft, time.perf_counter() - t0
            if d.get("done"):
                break
    return text.strip(), ttft or 0.0, time.perf_counter() - t0


results = {}
base = vram()
for lang, cfg in LANGS.items():
    stt = WhisperModel(cfg["whisper"], device="cuda", compute_type="float16")
    tts = piper(*cfg["piper"])
    audio = decode_audio(str(OUT / f"{cfg['clip']}.wav"), sampling_rate=16000)
    tts.generate("Warm up.")
    list(stt.transcribe(audio, language=lang, beam_size=1)[0])
    llm_first_sentence("Hej")  # makes sure the LLM is loaded and warm
    rows = []
    for _ in range(6):
        t_start = time.perf_counter()
        segs, _info = stt.transcribe(audio, language=lang, beam_size=1, vad_filter=False, condition_on_previous_text=False)
        question = " ".join(s.text.strip() for s in segs)
        t_stt = time.perf_counter()
        sentence, ttft, ready = llm_first_sentence(question)
        t_llm = time.perf_counter()
        first = []
        tts.generate(sentence, callback=lambda s, p: (first.append(time.perf_counter()) if not first else None) or 1)
        t_audio = first[0] if first else time.perf_counter()
        rows.append({
            "stt_ms": (t_stt - t_start) * 1000, "llm_first_token_ms": ttft * 1000,
            "llm_first_sentence_ms": ready * 1000, "tts_first_audio_ms": (t_audio - t_llm) * 1000,
            "total_ms": (t_audio - t_start) * 1000, "question": question, "sentence": sentence,
        })
    med = lambda k: round(statistics.median(r[k] for r in rows))  # noqa: E731
    results[lang] = {
        "stt_ms_p50": med("stt_ms"), "llm_first_token_ms_p50": med("llm_first_token_ms"),
        "llm_first_sentence_ms_p50": med("llm_first_sentence_ms"), "tts_first_audio_ms_p50": med("tts_first_audio_ms"),
        "total_ms_p50": med("total_ms"), "total_ms_max": round(max(r["total_ms"] for r in rows)),
        "example": {"heard": rows[0]["question"], "said": rows[0]["sentence"]},
    }
    print(lang, json.dumps(results[lang], ensure_ascii=False))
    del stt
print("vram_mb_in_use_peak_delta", vram() - base)
