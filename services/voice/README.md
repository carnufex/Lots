# Lots voice service

Speech to text and text to speech for Swedish and English behind an OpenAI-shaped API (ADR 0012). It runs on the GPU PC
next to Ollama and is reached only by the Lots shell. Engines (all permissive): faster-whisper with KB-Whisper (Swedish)
and Whisper (English), Piper voices through sherpa-onnx for synthesis. Measured numbers: `docs/research/voice-spike-results.md`.

## API

| Endpoint | Notes |
|---|---|
| `POST /v1/audio/transcriptions` | multipart `file`, optional `language` (`sv`/`en`, otherwise detected), `response_format` `json`/`verbose_json`/`text` |
| `POST /v1/audio/speech` | JSON `{input, voice, response_format: wav|pcm, speed}`; streams one sentence at a time |
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
