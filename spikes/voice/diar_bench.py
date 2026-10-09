"""Speaker diarization benchmark (spike #39): sherpa-onnx pyannote segmentation + 3D-Speaker embeddings on CPU.

Input: the two-speaker English sample from the sherpa-onnx release (real speech). Run: .venv/Scripts/python diar_bench.py
"""
import time
from pathlib import Path

import numpy as np
import sherpa_onnx
import soundfile as sf

M = Path(__file__).parent / "models"
import sys
THRESH = float(sys.argv[1]) if len(sys.argv) > 1 else 0.5
NUM = int(sys.argv[2]) if len(sys.argv) > 2 else -1
cfg = sherpa_onnx.OfflineSpeakerDiarizationConfig(
    segmentation=sherpa_onnx.OfflineSpeakerSegmentationModelConfig(
        pyannote=sherpa_onnx.OfflineSpeakerSegmentationPyannoteModelConfig(
            model=str(M / "sherpa-onnx-pyannote-segmentation-3-0" / "model.onnx")),
        num_threads=4, provider="cpu"),
    embedding=sherpa_onnx.SpeakerEmbeddingExtractorConfig(
        model=str(M / "3dspeaker_speech_eres2net_sv_en_voxceleb_16k.onnx"), num_threads=4, provider="cpu"),
    clustering=sherpa_onnx.FastClusteringConfig(num_clusters=NUM, threshold=THRESH),
    min_duration_on=0.3, min_duration_off=0.5)
sd = sherpa_onnx.OfflineSpeakerDiarization(cfg)

audio, sr = sf.read(M / "2-two-speakers-en.wav", dtype="float32")
if audio.ndim > 1:
    audio = audio.mean(axis=1)
assert sr == sd.sample_rate, (sr, sd.sample_rate)
dur = len(audio) / sr

for i in range(1):
    t = time.perf_counter()
    result = sd.process(audio).sort_by_start_time()
    el = time.perf_counter() - t
    print(f"run {i}: {len(result)} segments, speakers={len({s.speaker for s in result})}, audio={dur:.0f}s, "
          f"took={el:.1f}s, real-time factor={el / dur:.3f}")
for s in list(result)[:10]:
    print(f"  {s.start:6.2f}-{s.end:6.2f}  speaker_{s.speaker}")
