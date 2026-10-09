# Voice spike results (issue #39)

Measured 2026-10-10 on the owner's PC: **RTX 4070 Ti SUPER (16 GB)**, driver 617.14, Windows, Python 3.11, GPU otherwise idle.
Scripts: `spikes/voice/*.py` (reproducible; models are downloaded, not committed). Decision context: ADR 0012.

## Caveats first

- STT input is **synthetic speech** produced by our own TTS (clean, one speaker, no noise). Latencies are valid; **accuracy
  numbers are only a rough sanity check** and say little about real microphones, accents or meeting audio. Real recordings go into the evals (#36).
- Questions in the end-to-end test are simple and answered without tools and with the model's thinking switched off.
- The GPU was idle. Behaviour while a game is running was **not** measured.
- Single stream, no concurrency.

## 1. Text to speech (Piper through sherpa-onnx, on CPU)

| Voice | First audio (p50) | Whole sentence (p50) | Real-time factor |
|---|---|---|---|
| Swedish `nst` | 83 ms | 126 ms | 0.026 |
| Swedish `alma` | 76 ms | 95 ms | 0.023 |
| English `lessac` | 33 ms | 89 ms | 0.024 |

TTS needs **no GPU**. (The pip build of sherpa-onnx has no CUDA provider; the "cuda" rows in the raw output silently ran on CPU.)
Naturalness of the Swedish voices still has to be judged by ear; samples are generated into `spikes/voice/out/`.

## 2. Speech to text (faster-whisper, CTranslate2, float16, GPU), 4-5 s utterance

| Model | Language | Latency p50 | VRAM | Round-trip WER (synthetic) |
|---|---|---|---|---|
| KB-Whisper small | sv | 94 ms | 0.7 GB | 13.5 % |
| KB-Whisper medium | sv | 189 ms | 1.9 GB | 6.3 % |
| KB-Whisper large | sv | 283 ms | 3.8 GB | 6.5 % |
| Whisper small.en | en | 77 ms | 0.6 GB | 2.2 % |

KB-Whisper loads directly in faster-whisper (CTranslate2 files are in the model repo), no conversion needed. `medium` is the
sweet spot for Swedish here. Whisper large-v3-turbo was not tested (the Systran repository id was not reachable; community CT2
conversions exist).

## 3. LLM time to first token (Ollama, streaming, **thinking off**)

| Model | Warm TTFT p50 | Tokens/s | VRAM | Cold load |
|---|---|---|---|---|
| `qwen3.5:latest` (9.7B) | **258 ms** | 84 | 5.5 GB | 3.7 s |

`gemma3:4b` and `phi4` are listed by Ollama but their model files are missing on the host (same as `llama3.1:8b` earlier), so they were
not measured. **This corrects the earlier assumption that the model is the bottleneck:** the multi-second numbers came from the
27B model and from thinking mode. A mid-size model with thinking off meets the budget. Tool-using runs are still slower and need
the spoken acknowledgement.

## 4. End to end: end of speech to first audio of the answer

STT, then LLM until the first sentence is complete, then TTS of that sentence, all local and warm, models resident together
(`spikes/voice/e2e_bench.py`, 6 turns per language):

| Language | STT | LLM first token | LLM first sentence | TTS first audio | **Total p50** | max |
|---|---|---|---|---|---|---|
| Swedish (KB-Whisper medium) | 145 ms | 221 ms | 381 ms | 75 ms | **619 ms** | 719 ms |
| English (Whisper small.en) | 86 ms | 227 ms | 500 ms | 167 ms | **755 ms** | 842 ms |

Add endpointing (detecting that the user stopped talking, about 300 ms) and the answer starts about **0.9-1.1 s after the last word**.
Ways to feel faster, all compatible with the design: streaming STT so the transcript is ready at end of speech (saves 90-150 ms),
speaking after the first clause instead of the first sentence, and an immediate spoken acknowledgement (a pre-synthesised "Jag kollar") for anything that needs tools.
Peak extra VRAM with Whisper medium, Piper and `qwen3.5` loaded together: the whole stack fits in 16 GB with room to spare
(about 8.4 GB in use with `qwen3.5` loaded on top of the desktop's 1.8 GB).

## 5. Speaker diarization (sherpa-onnx, pyannote segmentation 3.0 + 3D-Speaker embeddings, CPU)

34 s two-speaker recording (real speech) processed in 2.3 s: **real-time factor 0.07**. Speaker count depends strongly on the clustering
threshold: 0.5 found 4 speakers, 0.7 found 2, 0.8 found 3, 0.9 found 2; with the number of speakers given (2) the result is right.
Consequence: the meeting flow should accept an optional speaker count and show labels as suggestions.

## Verdict

- The one-second goal is **reachable locally** for simple questions in both languages. GO for the own-GPU path.
- Meetings run on CPU or GPU without a problem (diarization 0.07x real time; Whisper on GPU is much faster than real time).
- Not yet known and carried into the evals (#36) and the service issue (#40): accuracy on real recordings, behaviour with a game using the GPU,
  concurrent users, streaming STT latency, tool-using questions, Swedish TTS naturalness by ear, Whisper large-v3-turbo for English.
