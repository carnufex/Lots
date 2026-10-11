# 0025. Speak answers while they are written

Status: accepted (2026-10-11). Extends ADR 0013 for #37.

## Context

ADR 0013 allows speech output only for a run's final answer, so tool output, errors and approval requests never reach a speech
provider and there is no free text-to-speech. Waiting for the final answer and then synthesising all of it made the first audio of a
spoken answer arrive 16-17 s after the question on the local stack, most of it model and synthesis time.

## Decision

`POST /runs/{id}/speak/next {from, hash}` speaks the next whole sentences of the run's **own answer text**: the text the answering model
call is streaming (the same text that becomes the final answer), or the final answer once the run is done. The client asks again with
the returned offset and hash while it plays, so sentence two synthesises while sentence one plays.

The ADR 0013 guarantees hold: the server takes the text from the run, never from the client; tool output, errors and approvals are
never in it; only the run's owner (or an admin) may ask. If the text stops continuing what was spoken (the model wrote, then called a
tool and started a new message) the server says so (`X-Speak-Reset`) and speaks from the start of the new text.

## Consequences

- First audio on the local stack: 7.4-7.8 s instead of 16.4-17.0 s for a three-sentence answer.
- A sentence the model later revises in a new message may already have been spoken; the transcript and the run show the final text.
- `/runs/{id}/speak` stays for the whole answer (the run page's speak button, older clients).
