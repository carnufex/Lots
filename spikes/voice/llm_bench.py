"""LLM time-to-first-token benchmark (spike #39) against the local Ollama, thinking disabled.

Run: .venv/Scripts/python llm_bench.py [model ...]
"""
import json
import statistics
import sys
import time

import httpx

BASE = "http://localhost:11434"
SYSTEM = ("You are a voice assistant for a homelab. Answer in the language of the question, in one or two short "
          "sentences, without lists or markdown.")
QUESTIONS = [
    "Hur många containrar kör vi just nu, ungefär?",
    "What does an unhealthy container mean?",
    "Kan du förklara vad en kabel-skarv är i ett fibernät?",
]
MODELS = sys.argv[1:] or ["qwen3.5:latest", "gemma3:4b", "phi4:latest", "llama3.1:8b", "gpt-oss:20b"]


def one(model: str, question: str) -> dict:
    body = {"model": model, "stream": True, "think": False, "keep_alive": "10m",
            "messages": [{"role": "system", "content": SYSTEM}, {"role": "user", "content": question}],
            "options": {"num_predict": 80}}
    t0 = time.perf_counter()
    first = None
    n = 0
    with httpx.stream("POST", f"{BASE}/api/chat", json=body, timeout=300) as r:
        if r.status_code != 200:
            return {"error": r.read().decode()[:160]}
        for line in r.iter_lines():
            if not line:
                continue
            d = json.loads(line)
            piece = d.get("message", {}).get("content", "")
            if piece and first is None:
                first = time.perf_counter() - t0
            if piece:
                n += 1
            if d.get("done"):
                break
    total = time.perf_counter() - t0
    return {"ttft_ms": round((first or total) * 1000), "tokens": n, "tok_s": round(n / max(total - (first or 0), 0.001), 1)}


def vram(model: str) -> str:
    ps = httpx.get(f"{BASE}/api/ps", timeout=10).json().get("models", [])
    return ", ".join(f"{m['name']}={m.get('size_vram', 0) // 2**20}MiB" for m in ps)


for model in MODELS:
    print(f"== {model}")
    cold = one(model, QUESTIONS[0])
    if "error" in cold:
        print(json.dumps({"model": model, **cold}))
        continue
    runs = [one(model, q) for q in QUESTIONS * 2]
    res = {
        "model": model, "cold_first_token_ms": cold["ttft_ms"],
        "warm_ttft_ms_p50": round(statistics.median(r["ttft_ms"] for r in runs)),
        "warm_ttft_ms_max": max(r["ttft_ms"] for r in runs),
        "tok_s_p50": round(statistics.median(r["tok_s"] for r in runs), 1),
        "loaded": vram(model),
    }
    print(json.dumps(res))
