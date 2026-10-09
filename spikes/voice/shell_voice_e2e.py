"""End to end through the Lots shell (dev auth): dictate -> start run -> wait -> speak the answer.

Needs: shell on :8088 with Speech__BaseUrl set, the voice service, a model for runs.
Run: .venv/Scripts/python shell_voice_e2e.py [audio.wav] [language]
"""
import sys
import time
from pathlib import Path

import httpx

BASE = "http://localhost:8088"
H = {"X-Dev-User": "dev", "X-Dev-Roles": "operator"}
OUT = Path(__file__).parent / "out"
audio = Path(sys.argv[1]) if len(sys.argv) > 1 else OUT / "sv-alma-cpu-2.wav"
lang = sys.argv[2] if len(sys.argv) > 2 else None

with httpx.Client(base_url=BASE, headers=H, timeout=120) as c:
    t0 = time.perf_counter()
    r = c.post("/voice/transcribe", files={"Audio": (audio.name, audio.read_bytes(), "audio/wav")}, data={"Language": lang} if lang else {})
    print(f"dictation: {r.status_code} in {(time.perf_counter() - t0) * 1000:.0f} ms ->", r.json())
    prompt = r.json()["text"]

    run = c.post("/runs", json={"prompt": prompt + " Svara kort.", "profile": "homelab"}).json()["id"]
    t1 = time.perf_counter()
    while True:
        d = c.get(f"/runs/{run}").json()
        if d["status"] in ("Completed", "Failed"):
            break
        time.sleep(0.5)
    print(f"run {d['status']} in {time.perf_counter() - t1:.1f} s: {(d['finalAnswer'] or d['error'] or '')[:200]!r}")

    t2 = time.perf_counter()
    with c.stream("POST", f"/runs/{run}/speak", json={}) as s:
        first, size = None, 0
        for chunk in s.iter_bytes():
            first = first or time.perf_counter() - t2
            size += len(chunk)
        print(f"speak: {s.status_code} {s.headers.get('content-type')} first byte {first * 1000:.0f} ms, {size} bytes "
              f"({max(size - 44, 0) / 2 / 22050:.1f} s of audio)")
