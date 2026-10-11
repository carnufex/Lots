# Lots voice service

Speech to text and text to speech for Swedish and English behind an OpenAI-shaped API (ADR 0012). It runs on the GPU PC
next to Ollama and is reached only by the Lots shell. Engines (all permissive): faster-whisper with KB-Whisper (Swedish)
and Whisper (English), Piper voices through sherpa-onnx for synthesis. Measured numbers: `docs/research/voice-spike-results.md`.

## API

| Endpoint | Notes |
|---|---|
| `POST /v1/audio/transcriptions` | multipart `file`, optional `language` (`sv`/`en`, otherwise detected), `response_format` `json`/`verbose_json`/`text` |
| `POST /v1/audio/speech` | JSON `{input, voice, response_format: wav|pcm, speed}`; streams one sentence at a time |
| `WS /v1/audio/transcriptions/stream` | streaming (#45): binary 16 kHz mono PCM16 frames in, JSON `speech_start` / `partial` / `final` (`latency_ms` after the endpoint) / `error` out, `{"type":"end"}` closes. Query `language`, `prompt`, `silence_ms` (200-2000, default 400). Energy VAD with an adaptive noise floor; a partial 150 ms into each pause makes the final ready at the endpoint (measured on a real recording: 0 ms after a 700 ms pause, ~110 ms after a 400 ms one). Max session `VOICE_MAX_STREAM_SECONDS` (600) |
| `POST /v1/audio/meetings` | a whole recording (#41): multipart `file`, optional `language`, `num_speakers` (1-20), `prompt`, `word_timestamps`; returns timestamped `segments` (with `words`) and speaker `turns` (sherpa-onnx diarization, CPU). Limits `VOICE_MAX_MEETING_BYTES` (300 MB), `VOICE_MAX_MEETING_SECONDS` (4 h); models from `scripts/fetch_models.py` into `VOICE_DIARIZATION_DIR` (default `/models/diarization`) |
| `GET /v1/models` | languages and voices |
| `GET /health` | open; everything else needs `Authorization: Bearer $VOICE_API_KEY` |

The service refuses to start without `VOICE_API_KEY` (`VOICE_ALLOW_ANONYMOUS=1` for local experiments only).

## Run it

```bash
python scripts/fetch_models.py ./models        # Piper voices
pip install -e ".[engines]"
VOICE_API_KEY=... VOICE_MODELS_DIR=./models python -m voice.main     # http://127.0.0.1:8700
```

Container (needs the NVIDIA runtime): `docker compose up -d` (see `compose.yaml`). Models and the Hugging Face cache live on the
`/models` volume. Settings are environment variables, see `voice/config.py` (`VOICE_STT_SV`, `VOICE_STT_EN`, `VOICE_STT_DEVICE`, limits, ...).

## Tests

`pip install -e ".[test]" && pytest` runs the API layer against fake engines (no GPU or models needed).

## GPU memory budget and guard (#84)

The voice service shares the GPU with the language model (Ollama). Measured on the RTX 4070 Ti SUPER (16 GB):

| Component | VRAM |
|---|---|
| Ollama, qwen3.5 (9B, Q4) with an 8k context | ~7-8 GB |
| Whisper medium (Swedish, `int8_float16`) + small (English, language id) | ~1.5 GB |
| Chatterbox Multilingual (expressive voice) | ~3.5 GB |
| Piper | 0 (CPU) |

That leaves little room for anything else on the card; on 2026-10-10 another application filled the rest and the model stalled.
The guard keeps the expressive voice from making it worse:

- `VOICE_MIN_FREE_VRAM_MB` (1200): if Chatterbox is not loaded and less memory than this is free, answers use the fast voice of
  the same language instead of loading it.
- `VOICE_MAX_EXPRESSIVE_QUEUE` (2): with this many expressive requests already waiting for the GPU, the next ones use the fast voice.
- While Chatterbox loads (about a minute, in the background) answers use the fast voice instead of waiting.
- `VOICE_CHATTERBOX_IDLE_UNLOAD_S` (900): Chatterbox is unloaded after this long without use and gives its memory back.
- Every fallback is reported in the `X-Voice-Fallback` response header (`vram`, `busy`, `loading`); `GET /health` reports free and
  total GPU memory (`gpu`) and whether the expressive voice is loaded. The shell exports `lots_voice_gpu_free_bytes` and alerts on it.
