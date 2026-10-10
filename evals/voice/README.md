# Voice recordings with a known transcript (facit)

Real recordings tell us how well speech-to-text works for **your** voice and microphone; synthetic test audio does not.
Audio is personal data: `recordings/` is git-ignored, only this README, the phrase list and the scripts are committed.

## How to record

Record with the microphone you actually use, in the way you would talk to the agent: a normal distance, normal volume.
Any format works (wav, m4a, mp3, webm, ogg). One phrase per file, 2 to 8 seconds, a pause of about half a second before and after.
Easiest: the Windows Voice Recorder or your phone, then copy the files into `evals/voice/recordings/`.

Name the files after the phrase id in `phrases.md` (for example `sv-01.m4a`, `en-03.wav`). If you read something else than the
suggested phrase, write what you actually said in the manifest.

## The manifest (the facit)

Create `evals/voice/recordings/manifest.json`. Copy `manifest.example.json` and edit it:

```json
[
  { "file": "sv-01.m4a", "language": "sv", "text": "Hallå hallå, fungerar det här?", "note": "quiet room" },
  { "file": "en-01.wav", "language": "en", "text": "Hello hello, does this work?" }
]
```

- `text` is exactly what you said, with normal punctuation (punctuation and case are ignored when scoring).
- `language` is `sv` or `en`. `note` is optional (noise, distance, speed, accent) and helps us understand failures.
- A meeting recording with several speakers can be added later with a `speakers` list; start with single phrases.

## How we use it

`python spikes/voice/eval_recordings.py` sends every file to the running voice service (with and without the language hint),
and reports word error rate per file and in total, whether language detection got it right, and the latency. That gives us a
real number to improve against (model size, detection, endpointing) and the first real data for the voice evals (#36).

## What to record

See `phrases.md`: about 14 Swedish and 8 English phrases that cover short greetings, questions with numbers and technical
words, names, and mixed Swedish and English. Variation is more valuable than quantity: a few in a noisier place, a few fast,
a few quiet.
