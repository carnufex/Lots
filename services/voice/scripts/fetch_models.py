"""Downloads the Piper voices into the models directory (Whisper models are fetched by faster-whisper on first use).

Usage: python scripts/fetch_models.py [target-dir]    (default: $VOICE_MODELS_DIR or ./models)

Licenses (see docs/third-party-licenses.md in the Lots repository):
  sv_SE nst   CC0         (dataset: NST, Sprakbanken)
  sv_SE alma  CC BY 4.0   (attribution required: NST Swedish TTS dataset, Sprakbanken / National Library of Norway)
  en_US lessac  see the model card inside the archive
"""
from __future__ import annotations

import os
import sys
import tarfile
import urllib.request
from pathlib import Path

BASE = "https://github.com/k2-fsa/sherpa-onnx/releases/download/tts-models"
VOICES = ["vits-piper-sv_SE-nst-medium", "vits-piper-sv_SE-alma-medium", "vits-piper-en_US-lessac-medium"]


def main() -> None:
    target = Path(sys.argv[1] if len(sys.argv) > 1 else os.environ.get("VOICE_MODELS_DIR", "models"))
    target.mkdir(parents=True, exist_ok=True)
    for name in VOICES:
        if (target / name / "tokens.txt").exists():
            print(f"{name}: already present")
            continue
        archive = target / f"{name}.tar.bz2"
        print(f"{name}: downloading")
        urllib.request.urlretrieve(f"{BASE}/{name}.tar.bz2", archive)
        with tarfile.open(archive, "r:bz2") as tar:
            tar.extractall(target, filter="data")  # 'data' filter: no absolute paths, links or special files
        archive.unlink()
    print("done")


if __name__ == "__main__":
    main()
