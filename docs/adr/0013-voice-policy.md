# 0013 – Voice is a channel, not an authority

Status: accepted (2026-10-10)

## Context
Voice (ADR 0012) lets users speak to the agent and hear it, and upload meetings. Speech has no strong identity: a voice
cannot be verified the way a login can, and audio can come from anywhere (a recording, a speaker in the room).

## Decision
1. **Voice never grants anything.** Policy is evaluated on the authenticated user, exactly as for typed text. A transcript is
   just text the user may edit and send; it carries no extra authority.
2. **No approvals by voice.** Approvals and denials are explicit UI actions. There is no voice command that approves, denies or
   changes policy, and none will be added without speaker verification and a new ADR.
3. **Only final answers are spoken.** Speech synthesis is exposed as "speak the final answer of this run" (owner or admin of the
   run), not as a free text-to-speech endpoint. Tool output, arguments, errors and approval requests are never sent to a speech provider.
   Fixed phrases such as a spoken acknowledgement are a closed list in configuration, not user input.
4. **No voice cloning, no voice design, no dubbing.** Out of scope; nothing in the shell can create or select a cloned voice.
5. **Audio is not kept.** Dictation audio is processed and discarded. Only metadata is recorded per use (`voice_usage`: user,
   direction, language, audio seconds or characters, latency, provider host, outcome), never content. Meeting recordings are kept
   only as long as processing needs them, with a configured retention, and are deleted on request.
6. **Transcripts are untrusted data** when an agent reads them (a recording can contain instructions): they follow the same rules as tool output.
7. **Limits protect the service**: maximum audio size, maximum text length, a request timeout, supported languages (`sv`, `en`).
8. The speech provider is a replaceable contract (ADR 0012). Sending audio or text to a hosted provider is a deployment decision the
   operator makes explicitly; the audit shows which provider host handled each call.

## Consequences
- The voice endpoints need authentication like every other endpoint; unavailability of the provider degrades to text, never to a bypass.
- Tests assert the deny cases: no approval via the voice endpoints, no speech for someone else's run, no speech for text that is
  not a final answer, oversize and unsupported-language requests rejected.
- Resolves issue #34.
