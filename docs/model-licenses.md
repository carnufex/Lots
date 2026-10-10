# Model licenses

Models are not packages: the voice service downloads them at first start into its `/models` volume, and the chat and embedding
models come from your own model server. Their licenses decide what a deployment may do, so they are listed here by hand and checked
against the model cards (last checked 2026-10-10).

## Downloaded by the voice service

| Model | Used for | License | Notes |
|---|---|---|---|
| `KBLab/kb-whisper-medium` | Swedish speech to text | Apache-2.0 | |
| `Systran/faster-whisper-small.en` | English speech to text | MIT | |
| `Systran/faster-whisper-small` | Telling Swedish from English | MIT | |
| Piper `sv_SE-nst-medium` (`sv-nst`) | Swedish speech output, default | CC0 | Dataset: NST, Språkbanken |
| Piper `sv_SE-alma-medium` (`sv-alma`) | Swedish speech output | CC BY 4.0 | **Attribution required**: "NST Swedish TTS dataset, Språkbanken, National Library of Norway" |
| Piper `en_US-ljspeech-medium` (`en-ljspeech`) | English speech output, default | Public domain | Dataset: LJ Speech |
| Chatterbox Multilingual (`cb-default`, own voices) | Expressive speech output | MIT | Every clip carries Resemble AI's inaudible Perth watermark |

### Research-only, never on by default

| Model | License | How to enable |
|---|---|---|
| Piper `en_US-lessac-medium` (`en-lessac`) | Trained on the Blizzard 2013 Lessac data, licensed for **research only**: no commercial use and no product use | `VOICE_RESEARCH_VOICES=1` and `scripts/fetch_models.py --research`. Only where that license fits |

Lessac was the English default before #131. Deployments that set `Speech__Voices__en=en-lessac` should move to `en-ljspeech`
(or `cb-default`), unless their use is covered by the research license.

## Not shipped: your model server

The chat model (for example `qwen3.5`) and the embedding model (`nomic-embed-text`, Apache-2.0; `bge-m3`, MIT) run on your own
OpenAI-compatible server. Lots does not distribute them. Check the license of whatever you pull there, and its acceptable-use policy.

## Own voices

A user's own voice is a recording of that person, stored only in the voice service (ADR 0015). It is personal data, not a licensed
model. Consent, erasure and the consent trail are covered by the voice policy (ADR 0013) and #93.
