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

`dotnet run --project src/Lots.Evals -- --mode voice-stt --dev-user claude-test-voice-evals [--max-wer 0.2]` sends every file through
the shell's own `POST /voice/transcribe` (the eval identity's dictation vocabulary included), with and without the language hint. It reports
word error rate per file and per language, whether language detection got it right, and the latency. Results are stored under
`evals/history/voice-stt/` and compared with the previous run; the exit code is 1 when the corpus WER is above `--max-wer`.

The older spike script `python spikes/voice/eval_recordings.py` does the same straight against the voice service (with and without the language hint),
and reports word error rate per file and in total, whether language detection got it right, and the latency. That gives us a
real number to improve against (model size, detection, endpointing) and the first real data for the voice evals (#36).

## What to record

See `phrases.md`: about 14 Swedish and 8 English phrases that cover short greetings, questions with numbers and technical
words, names, and mixed Swedish and English. Variation is more valuable than quantity: a few in a noisier place, a few fast,
a few quiet.

## Latency budget

`dotnet run --project src/Lots.Evals -- --mode voice-latency --dev-user claude-test-voice-evals --dev-roles admin [--days 7] [--user <id>]`
reads the spoken turns of the last days from History and reports p50/p95 per stage against `budget.json`: speech to text, model,
tools, time to first audio of speech output, and end of speech to the first audio of the answer. End of speech is when the transcription
started, so upload time counts against speech to text. An admin identity sees everyone's turns; `--user` narrows it to one person. The exit
code is 1 when a stage is over budget. Runs are stored as `voice-latency` history, so `--mode history --dataset voice-latency` shows the
trend.

## Listening test (speech output)

`dotnet run --project src/Lots.Evals -- --mode voice-tts [--voices-sv sv-nst,cb-default,<own voice id>] [--voices-en en-ljspeech,cb-default]`
synthesises every sentence in `tts-sentences.json` with every voice straight from the voice service (`--voice-url`, key from
`VOICE_API_KEY` or `services/voice/.env`). The output goes to `listening/<time>/` (git-ignored: clips can be a person's own voice):

- `clips/` holds the audio under neutral codes, and `key.json` maps each code to its voice.
- Automatic measures per clip: time to first audio, whether the voice fell back to the fast one, and round-trip intelligibility (the
  clip is transcribed again and compared with the sentence).
- `sheet.html` is a blind rating page in a shuffled order. Rate naturalness and clarity from 1 to 5, add notes, then download `ratings.csv`
  into the same folder.

`--mode voice-tts-score --dir evals/voice/listening/<time>` reveals the voices and writes `report.md`: mean opinion scores per voice and
language next to the automatic measures. Numbers read as digits ("12" for "tolv") count as recognition errors in the round trip, so
compare voices with each other rather than reading the WER as absolute.

