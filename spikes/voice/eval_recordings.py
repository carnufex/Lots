"""Scores real recordings against their facit (spike #39 follow-up, input to the voice evals #36).

Reads evals/voice/recordings/manifest.json (see evals/voice/README.md) and sends every file to the running voice service,
once with the language hint and once without (to see whether automatic language detection gets it right).

Run: .venv/Scripts/python eval_recordings.py [--url http://127.0.0.1:8700] [--key KEY]   (key: $VOICE_API_KEY or services/voice/.env)
"""
import argparse
import json
import os
import statistics
import sys
import time
from pathlib import Path

import httpx
import jiwer

ROOT = Path(__file__).resolve().parents[2]
REC = ROOT / "evals" / "voice" / "recordings"


def norm(s: str) -> str:
    return jiwer.RemovePunctuation()(s.lower()).replace("  ", " ").strip()


def key_from_env_file() -> str | None:
    f = ROOT / "services" / "voice" / ".env"
    if f.exists():
        for line in f.read_text(encoding="utf-8").splitlines():
            if line.startswith("VOICE_API_KEY="):
                return line.split("=", 1)[1].strip()
    return None


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--url", default="http://127.0.0.1:8700")
    ap.add_argument("--key", default=os.environ.get("VOICE_API_KEY") or key_from_env_file())
    args = ap.parse_args()
    manifest = REC / "manifest.json"
    if not manifest.exists():
        print(f"No {manifest}. See evals/voice/README.md")
        return 2

    items = json.loads(manifest.read_text(encoding="utf-8"))
    headers = {"Authorization": f"Bearer {args.key}"}
    rows = []
    with httpx.Client(base_url=args.url, headers=headers, timeout=120) as c:
        for it in items:
            path = REC / it["file"]
            if not path.exists():
                print(f"missing: {it['file']}")
                continue
            data = path.read_bytes()
            row = {"file": it["file"], "lang": it["language"], "ref": it["text"], "note": it.get("note", "")}
            for mode, form in (("hinted", {"language": it["language"]}), ("auto", {})):
                t = time.perf_counter()
                r = c.post("/v1/audio/transcriptions", files={"file": (path.name, data)}, data={"response_format": "verbose_json", **form})
                ms = (time.perf_counter() - t) * 1000
                if r.status_code != 200:
                    row[mode] = {"error": r.status_code, "ms": ms}
                    continue
                j = r.json()
                row[mode] = {"text": j["text"], "language": j["language"], "wer": jiwer.wer(norm(it["text"]), norm(j["text"])), "ms": ms}
            rows.append(row)

    for r in rows:
        print(f"\n{r['file']} [{r['lang']}] {r['note']}\n  facit : {r['ref']}")
        for mode in ("hinted", "auto"):
            m = r.get(mode, {})
            if "error" in m:
                print(f"  {mode:6}: HTTP {m['error']}")
            else:
                flag = "" if m["language"] == r["lang"] else f"   <-- detected {m['language']}"
                print(f"  {mode:6}: {m['text']}   (WER {m['wer']:.2f}, {m['ms']:.0f} ms){flag}")

    def mean_wer(mode, lang=None):
        v = [r[mode]["wer"] for r in rows if "wer" in r.get(mode, {}) and (lang is None or r["lang"] == lang)]
        return f"{statistics.mean(v):.3f} over {len(v)}" if v else "n/a"

    wrong = [r["file"] for r in rows if "language" in r.get("auto", {}) and r["auto"]["language"] != r["lang"]]
    print("\n== summary")
    for lang in (None, "sv", "en"):
        print(f"  WER hinted {lang or 'all':>3}: {mean_wer('hinted', lang)}   | auto {lang or 'all':>3}: {mean_wer('auto', lang)}")
    print(f"  language detection wrong on {len(wrong)} of {len(rows)}: {', '.join(wrong) or '-'}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
