"""Builds the audio fixtures for the conversation e2e test from fixtures/hallo-sv.wav (a synthetic Swedish clip).

conversation.wav  : the fake microphone: 2 s silence, speech, 3 s silence, speech again (to interrupt the agent), 20 s silence
agent-long.wav    : a 15 s soft tone, standing in for a long spoken answer
ack.wav           : a 0.8 s soft tone, standing in for the acknowledgement
"""
import wave
import numpy as np

def read(path, rate=48000):
    with wave.open(path, "rb") as w:
        data = np.frombuffer(w.readframes(w.getnframes()), dtype="<i2").astype(np.float32) / 32768
        src = w.getframerate()
    t = np.linspace(0, len(data) / src, int(len(data) * rate / src), endpoint=False)
    return np.interp(t, np.arange(len(data)) / src, data).astype(np.float32)

def write(path, samples, rate=48000):
    with wave.open(path, "wb") as w:
        w.setnchannels(1); w.setsampwidth(2); w.setframerate(rate)
        w.writeframes((np.clip(samples, -1, 1) * 32767).astype("<i2").tobytes())

R = 48000
speech = read("fixtures/hallo-sv.wav")
speech = speech / max(np.abs(speech).max(), 1e-3) * 0.6          # clearly above the noise floor
silence = lambda s: np.zeros(int(R * s), dtype=np.float32)
write("fixtures/conversation.wav", np.concatenate([silence(2), speech, silence(3), speech, silence(20)]))
tone = lambda s, f: (0.15 * np.sin(2 * np.pi * f * np.arange(int(R * s)) / R)).astype(np.float32)
write("fixtures/agent-long.wav", tone(15, 220))
write("fixtures/ack.wav", tone(0.8, 330))
print("fixtures written; speech length %.1f s" % (len(speech) / R))
