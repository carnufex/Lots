# 0012 – Voice runs on our own GPU first; hosted providers come later

Status: accepted (2026-10-10)

## Context
Talking to the agent must react within about one second end to end (see `docs/research/voice-stack.md`, "Latency decides the
design"). CPU cannot deliver that for streaming speech recognition and speech synthesis, and the large model we use for tool
runs is far too slow for voice. The Ollama host (the owner's PC, RTX 4070 Ti SUPER, 16 GB VRAM) is available. Hosted APIs
(ElevenLabs, hosted STT, hosted fast models) would meet the latency but cost money and move audio out of the homelab.
Meeting transcription is asynchronous and has no latency requirement.

## Decision
- **The voice service runs on the owner's GPU first** (a container with the NVIDIA runtime, or native), on the same machine as
  Ollama. It is a private service reached only by the Lots shell over the LAN, like Ollama is today. No public route.
- **Implementation: a small Python service** (`services/voice`) that exposes an OpenAI-shaped audio contract
  (`/v1/audio/transcriptions`, `/v1/audio/speech`, streaming variants, `/v1/models`, health) and meeting jobs. Python is chosen because the
  GPU ecosystem is there; every dependency is permissively licensed: faster-whisper (MIT), KB-Whisper (Apache-2.0, Swedish),
  Whisper (MIT), Silero VAD (MIT), pyannote.audio (MIT; gated weights accepted under the owner's Hugging Face account),
  Kokoro (Apache-2.0, English TTS), Piper (MIT) with Swedish `nst` (CC0) and `alma` (CC BY 4.0, attribution) voices. We write our own
  glue; VoiceStudio (AGPL) is a study object only, see `docs/research/voicestudio.md`.
- **The shell uses provider-neutral contracts** (`ISpeechToText`, `ITextToSpeech`). Hosted API providers (ElevenLabs and others)
  are added later as further adapters behind the same contracts, selectable per deployment. Nothing in the shell is GPU-specific.
- **Voice mode uses a small fast LLM**, not the 27B tool model, so everything fits in 16 GB of VRAM together
  (speech models need a few GB; the voice LLM a few more; the big model is evicted while voice is in use). The exact VRAM budget
  comes out of the spike.
- Scope stays Swedish and English, speaking to the agent, and meeting transcription with speakers. No cloning or dubbing.
- When the PC is off, voice is unavailable and the UI says so; text keeps working.

## Consequences
- Voice shares the GPU with Ollama and with games: a VRAM budget and a "pause voice" switch are needed; latency numbers are only
  valid when the GPU is not under heavy load.
- A Python service is a second runtime next to .NET; it stays small, behind a contract, in its own container, and runs under
  an arbitrary non-root UID like the other images.
- The cluster reaches the service at the PC's LAN address (Service plus Endpoints, a NetworkPolicy egress rule), as with Ollama.
- Resolves issue #30.
