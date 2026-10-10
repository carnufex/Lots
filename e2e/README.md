# Browser end-to-end checks

Playwright with a real Chromium and a **fake microphone** (Chromium plays a wav file as the microphone). Run manually against a running
stack (`docker compose up`, voice service on :8700). `npm install && npx playwright install chromium` once.

| Script | What it checks |
|---|---|
| `make-fixtures.py` | builds the audio fixtures (`python make-fixtures.py` after generating `fixtures/hallo-sv.wav`) |
| `dictation.mjs`, `dictation-more.mjs` | the Dictate button: start/stop, appending text, Alt+M, recovery after an error |
| `vocabulary.mjs` | the per-user vocabulary: add, persist, spelling fix in a real dictation, remove |
| `conversation.mjs` | conversation mode with simulated answers: mic on/off, listening, hearing, thinking, speaking, **barge-in**, shared conversation id |
| `conversation-real.mjs` | the same against the real stack (no mocks): times end of speech to the agent speaking |

Fixtures that contain a real voice are git-ignored.
