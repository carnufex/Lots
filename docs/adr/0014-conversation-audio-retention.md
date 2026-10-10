# 0014. Conversation audio is stored for 30 days by default

Status: accepted (2026-10-10). Amends point 5 of ADR 0013 ("Audio is not kept") for conversations.

## Context

The conversation history (issue #46) lets users replay a conversation and see where time went. Replay needs the audio of both sides.
Voice recordings are personal data.

## Decision

1. Audio of voice turns (the user's recording and the spoken answer) **is stored by default** and **deleted after 30 days**
   (`Speech:AudioRetentionDays`, default 30; 0 disables storage).
2. Access: the owner of the conversation, plus roles in `Auth:AuditRoles`. Every playback is audited.
3. The owner can delete a conversation's audio (or the whole conversation) at any time. A purge job removes expired audio.
4. Stored encrypted at rest (data protection keys as for run tokens), in a volume or object store behind a replaceable contract.
5. Metadata (`voice_usage`, timings, tokens) is kept longer than the audio; it contains no content.
6. Unchanged from ADR 0013: audio is never a source of authority, no approvals by voice, no voice cloning.
7. Dictation without a conversation (microphone button in the prompt box) is still not stored.
8. Hosted speech providers remain a deployment decision; stored audio never leaves the installation.

## Consequences

Users must be told in the UI that conversations are recorded and for how long. The Helm chart needs a persistent volume.
