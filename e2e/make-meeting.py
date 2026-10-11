"""Builds a two-speaker test meeting from the voice service's own voices, with the ground truth of who spoke when.

Usage: python make_meeting.py <out-dir> <voice-url> <api-key>
"""
import io
import json
import sys
import urllib.request
import wave

out, url, key = sys.argv[1], sys.argv[2].rstrip("/"), sys.argv[3]
PAUSE = float(sys.argv[4]) if len(sys.argv) > 4 else 0.6
NAME = sys.argv[5] if len(sys.argv) > 5 else "meeting"
lines = [
    ("A", "sv-nst", "Hej, välkomna till veckans driftmöte. Vi börjar med läget i klustret."),
    ("B", "sv-alma", "Tack. Allt rullar, men lots-shell startade om två gånger i natt efter ett minnesfel."),
    ("A", "sv-nst", "Okej. Kan du höja minnesgränsen till en gigabyte och följa upp i morgon?"),
    ("B", "sv-alma", "Ja, jag gör det efter mötet och skriver en kommentar i ärendet."),
    ("A", "sv-nst", "Bra. Nästa punkt är säkerhetskopiorna, de gick igenom utan fel."),
    ("B", "sv-alma", "Skönt. Då har jag inget mer att ta upp i dag."),
]
rate, frames, truth, t = None, [], [], 0.0
for speaker, voice, text in lines:
    req = urllib.request.Request(url + "/audio/speech", method="POST", data=json.dumps(
        {"input": text, "voice": voice, "response_format": "wav"}).encode(),
        headers={"Authorization": f"Bearer {key}", "Content-Type": "application/json"})
    data = urllib.request.urlopen(req).read()
    with wave.open(io.BytesIO(data)) as w:
        rate = rate or w.getframerate()
        assert w.getframerate() == rate and w.getnchannels() == 1
        pcm = w.readframes(w.getnframes())
    dur = len(pcm) / 2 / rate
    truth.append({"start": round(t, 3), "end": round(t + dur, 3), "speaker": speaker, "text": text})
    pause = int(rate * PAUSE) * b"\x00\x00"
    frames += [pcm, pause]
    t += dur + PAUSE
with wave.open(f"{out}/{NAME}.wav", "wb") as w:
    w.setnchannels(1)
    w.setsampwidth(2)
    w.setframerate(rate)
    w.writeframes(b"".join(frames))
json.dump(truth, open(f"{out}/{NAME}-truth.json", "w", encoding="utf-8"), ensure_ascii=False, indent=1)
print(f"{t:.1f} s, {len(lines)} turns, rate {rate}")
