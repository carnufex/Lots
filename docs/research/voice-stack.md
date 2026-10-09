# Our voice stack: Swedish and English, speech in and out, meetings with speakers

Status: desk research, 2026-10-10, licenses checked against the upstream repositories and model cards.
**Nothing here has been run or measured.** The first issues of milestone M5 do that. Scope is deliberately small:

1. Talk to the agent in **Swedish and English** (speech to text, text to speech).
2. **Upload a meeting recording** and get a transcript that says **who said what** (speaker diarization), WhisperX-style.

No voice cloning, dubbing or other languages.

## Latency decides the design (added after review)

Talking to an agent only feels natural if it reacts within about **one second**. Anything slower reads as "it did not hear me".
The one-second budget covers the whole chain, and our own measurements show the LLM alone already breaks it:

| Step | Rough budget for "feels instant" | Where we are today |
|---|---|---|
| End of speech detected (endpointing) | 200-300 ms | not built |
| Speech to text of the utterance | 150-300 ms | CPU Whisper is typically 1-2 s for a short utterance, **unmeasured** |
| LLM time to first token | 300-500 ms | **3-30 s per call** with `qwen3.5`/Qwen3.8-27B on Ollama (measured in M1 evals), and agent runs make several calls |
| Text to speech, first audio chunk | 150-250 ms | CPU Kokoro/Piper plausible for short sentences, **unmeasured** |

Consequences:

1. **CPU is not enough for the real-time path.** Streaming STT and fast TTS want a GPU (or a hosted provider). CPU stays fine for
   *meetings*, which are asynchronous and judged by real-time factor, not by reaction time.
2. **The model is the biggest problem.** A voice mode needs a small, fast model (or a hosted fast model) with streamed output; the
   27B model used for tool-heavy runs cannot do it.
3. **Agent runs with tools can never answer in one second.** The voice path therefore answers in layers: an **immediate spoken
   acknowledgement** (under a second: "I'll check", "Jag kollar") as soon as the user stops speaking, then the real answer streamed
   sentence by sentence as the model produces it, so speech starts when the first sentence exists.
4. **Streaming everywhere**: streaming STT with partial results (so the transcript is ready at end of speech), token streaming
   from the model, sentence-level TTS, barge-in.
5. **Measure end to end**, p50 and p95, from end of speech to first audio. That number, not component benchmarks, is the budget.

Where the GPU lives is therefore a first-class decision (issue #30): the PC with the GPU is also the Ollama host and a gaming
machine; a hosted provider (ElevenLabs, a hosted STT and a fast hosted model) removes the problem at a recurring cost and with
audio leaving the homelab. The own-service path stays the answer for **meetings** and for privacy-first use.

## Proposed shape

A separate, private service **`Lots.Voice`** (.NET, container, no public route), reached only by the shell. It exposes the
OpenAI-style audio contract (`/v1/audio/speech`, `/v1/audio/transcriptions`) plus a meeting-oriented endpoint, and runs its models
in-process through [sherpa-onnx](https://github.com/k2-fsa/sherpa-onnx), which has an official **NuGet package
(`org.k2fsa.sherpa.onnx`, Apache-2.0)** with C# examples for VAD, ASR, TTS and speaker diarization. One native dependency, ONNX
models, no Python in the image. The shell talks to it through provider-neutral contracts (`ISpeechToText`, `ITextToSpeech`),
so a hosted provider such as ElevenLabs stays a drop-in alternative.

```
browser ─audio─▶ shell ─(policy, limits, audit)─▶ Lots.Voice ─▶ sherpa-onnx ─▶ VAD / ASR / TTS / diarization models
                  │                                  (private service, models on a volume)
                  └─ meetings: job (leased like runs) ─▶ transcript + speakers in PostgreSQL
```

## Components and licenses (checked 2026-10-10)

| Job | Candidate | Code license | Model license | Notes |
|---|---|---|---|---|
| Runtime | sherpa-onnx (NuGet `org.k2fsa.sherpa.onnx`) | Apache-2.0 | n/a | CPU first, C# API, 1.13.x |
| STT, Swedish | KBLab `kb-whisper-*` (National Library of Sweden) | MIT/Apache | Apache-2.0 | Whisper fine-tune for Swedish; needs an ONNX/CT2 conversion that sherpa-onnx can load (spike) |
| STT, English | OpenAI Whisper (`small`/`medium`/`large-v3-turbo`) | MIT | MIT | ONNX exports are supported by sherpa-onnx |
| VAD | Silero VAD | MIT | MIT | bundled with sherpa-onnx examples |
| TTS, English | Kokoro-82M | Apache-2.0 | Apache-2.0 | small, fast on CPU, English only |
| TTS, Swedish | Piper VITS voices `sv_SE` (KBLab) | MIT (original Piper) | `nst`: CC0, `alma`: CC BY 4.0 (attribution), `lisa`: unclear, skip | the weakest link: quality must be judged by ear in the spike |
| Diarization: segmentation | pyannote segmentation 3.0 (ONNX, via sherpa-onnx) | MIT | MIT | |
| Diarization: speaker embeddings | 3D-Speaker or WeSpeaker ResNet (ONNX) | Apache-2.0 / CC-BY-4.0 | see model card | pick one after the spike |
| Alternative stack (not chosen now) | faster-whisper (MIT) + WhisperX (BSD-2) + pyannote (MIT, gated weights) | | | best known recipe for meetings (ASR, word alignment, diarization) but Python and Hugging Face gated models |

**Excluded on license grounds:** OmniVoice weights (CC-BY-NC), XTTS/Coqui models (CPML), F5-TTS weights (CC-BY-NC),
Fish-Speech (unclear), piper1-gpl (GPL-3.0), VoiceStudio itself (AGPL-3.0). Each model gets a row in `docs/third-party-licenses.md`
before it ships; code and weights are audited separately.

## How meeting transcription works (the WhisperX recipe, our version)

1. Decode the upload to 16 kHz mono; reject over a size/duration limit.
2. VAD cuts speech from silence.
3. ASR transcribes each speech segment (language per segment: sv or en, detected or hinted).
4. Diarization segments the audio by speaker and clusters speaker embeddings into `Speaker 1..N`.
5. Merge: each transcript segment gets the speaker with the largest time overlap. (Word-level alignment, which WhisperX does with
   a wav2vec2 aligner, is a later refinement.)
6. Store segments `{start, end, speaker, text, language}`; the user can rename speakers; export as text, SRT/VTT, JSON.

## Policy fit

Voice is an input and output channel, not an authority: no approvals by voice (no speaker verification). Transcripts are
untrusted data when an agent reads them. Audio is not stored by default for dictation; meeting audio is stored only as long as
needed to process it (configurable retention), transcripts belong to their owner and are audited when read by others.

## Hardware and risks

- Meetings can run on **CPU** (asynchronous, judged by real-time factor). Dictation and spoken answers need the latency budget above and
  therefore a GPU or a hosted provider. The spike measures both.
- Swedish TTS quality is the main risk. Fallbacks: another Piper voice, a hosted provider behind the same contract.
- Diarization accuracy on overlapping speech and far-field audio is limited; show speaker labels as suggestions.
