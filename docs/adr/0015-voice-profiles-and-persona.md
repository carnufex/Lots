# 0015. Per-user voice profile and agent persona

Status: accepted (2026-10-10). Refines ADR 0013 (no cloning) for the user's own voice.

## Context

Piper is flat. Chatterbox Multilingual sounds far better but speaks Swedish with an accent unless it gets a Swedish reference clip.
Users want to set how talkative and emotional the agent is (text and voice), and to record their own voice for the agent to use.

## Decision

1. **Settings per user**, edited on the website (Settings page), stored per user:
   - Persona (text): `talkativeness` (terse..chatty), `warmth` (neutral..warm), `formality`. Rendered into the system prompt
     by the shell; they never change policy, tools or approvals.
   - Voice: `voiceId` (a built-in voice or the user's own), `expressiveness` (maps to Chatterbox `exaggeration`),
     `pace`/`stability` (maps to `cfg_weight`).
   - Deployment defaults come from configuration; a user setting overrides them within configured limits.
2. **Own voice**: a user records 10-30 s on the website (guided script, level check, preview). The clip is that user's data:
   - Usable only for that user's own conversations. Never shared, never offered to other users, never usable for text the
     user did not cause the agent to say (ADR 0013 point 3 still holds: only final answers are spoken).
   - Consent is explicit in the UI (the user confirms it is their own voice). Cloning someone else's voice is prohibited.
   - Stored encrypted; deletable at any time (deletes the clip and the cached embedding). Retention follows the account.
3. **Shared deployment voice**: an admin may configure one fixed agent voice from a consenting speaker (reference clip in
   configuration, not in git). Used when the user has not chosen their own.
4. **Multi-user**: voice service takes `voice` per request (an id or an inline reference), caches the computed voice
   conditionals per voice id (small), and synthesises with a bounded queue. Capacity is a GPU matter (Chatterbox is about 0.6x
   real time on an RTX 4070 Ti SUPER, serial), so concurrent users queue; a fixed acknowledgement phrase covers the wait.
   When the queue is too long the shell falls back to the fast voice (Piper) rather than make the user wait.
5. Piper stays as the fast fallback. Hosted providers remain a deployment decision (ADR 0012).

## Consequences

New tables: user settings, user voice (encrypted clip, consent timestamp). New voice service endpoints for voice registration.
The Helm chart needs a persistent volume. Evaluating accent quality is by ear; no automatic score.
