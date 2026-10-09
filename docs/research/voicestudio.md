# How VoiceStudio is built (a study, not a dependency)

Status: desk research, 2026-10-10. Source: the project's own repository and docs
([debpalash/VoiceStudio](https://github.com/debpalash/VoiceStudio), v0.5.6, AGPL-3.0). **We do not use or run VoiceStudio and we do not copy its code.**
Lots is Apache-2.0; AGPL code must not flow into it. This page records design ideas only (facts and architecture, not
expression), so that we can build our own, smaller solution. Anyone implementing from this document should work from
this text and from the permissively licensed engines named in [voice-stack.md](voice-stack.md), not from VoiceStudio's source.

## What it is

An open-source local-first voice toolkit (cloning, voice design, dubbing, dictation, transcription, audiobooks, 646 languages)
with an Electron desktop app and a headless Python/FastAPI backend on port 3900, also shipped as a Docker image.

## Design ideas worth taking

1. **A provider-neutral speech edge.** The backend speaks the *OpenAI audio API shape*: `POST /v1/audio/speech` (TTS, with streaming
   as chunked bytes or SSE), `POST /v1/audio/transcriptions` (batch STT, formats `json`, `text`, `verbose_json` with segments and
   optional word timestamps, `srt`, `vtt`), `WS /v1/audio/transcriptions/stream` (partial and final text from PCM or WebM),
   `GET /v1/models`, and a discovery document. Existing SDKs and agent frameworks work unchanged. Lesson: **copy the shape of the
   public contract (it is an industry convention), keep the engine behind it replaceable.**
2. **Engines behind one adapter interface**, chosen by configuration; heavy or conflicting engines run as isolated sidecar
   processes so a crash or dependency clash does not take the service down.
3. **Engine acceptance bar.** An engine is only added for a named job, with a documented license for each component (code,
   weights, tokenizer), a CPU path, a smoke test and an owner. Good discipline to copy: it prevents a pile of half-working engines.
4. **Model lifecycle as a feature**: weights download on first use, visible install state, disk footprints documented, device
   (CUDA/Metal/CPU) auto-detected and overridable.
5. **Safe file handling for agents**: file inputs and outputs confined to one configured base directory (symlinks resolved,
   no-follow opens), audio returned by reference instead of inline base64 to keep it out of an LLM's context.
6. **Remote workers** (GPU elsewhere, a scheduler, circuit breaker, capacity) for when the machine with the microphone is not the machine with the GPU.
7. **Privacy defaults**: loopback unauthenticated, remote needs a key; analytics opt-in; no cloud call is required.
8. **Product observations**: a push-to-talk dictation shortcut, partial results while speaking, one place to see model install state.

## What we deliberately leave out

Voice cloning, voice design, dubbing, audiobooks, 646-language coverage and the desktop app. Lots needs **Swedish and English**,
speaking to the agent and hearing it, plus meeting transcription with speakers. The pretrained OmniVoice weights that VoiceStudio
defaults to are also licensed CC-BY-NC, so even on license grounds they are not an option for us.

## Risks it taught us about

- Young and fast-moving (v0.5.x, a desktop-framework migration in 0.5.3): a good reason not to depend on it.
- Model licenses differ from code licenses (here: Apache-2.0 code, CC-BY-NC weights). Audit code and weights separately, always.
- An API key authenticates but does not isolate: keep the voice service on a private network, behind Lots.
