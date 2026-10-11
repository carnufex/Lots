"""Streaming transcription (#45): PCM16 frames in, partial and final text out, with voice-activity endpointing.

The session is plain logic (testable without a socket or a GPU): feed it 16 kHz mono PCM16, it says when an utterance starts, when a
partial transcript is due, and when the utterance has ended (about 300 ms of silence). Partials are made while the person speaks, so
at the endpoint the latest partial usually covers the whole utterance already and becomes the final text at once; only when speech
arrived after it is the utterance transcribed once more.
"""
from __future__ import annotations

import struct
from dataclasses import dataclass, field

import numpy as np

RATE = 16000
FRAME = 320  # 20 ms


@dataclass
class StreamConfig:
    silence_ms: int = 400           # silence that ends an utterance (sessions may ask for 200-2000)
    min_speech_ms: int = 150        # shorter blips are noise
    partial_every_ms: int = 600     # a partial transcript at most this often while speaking
    early_final_ms: int = 150       # this far into a pause, transcribe what was said: at the endpoint the text is ready
    max_utterance_s: float = 30.0   # a monologue is cut into utterances of at most this
    pre_roll_ms: int = 200          # audio kept from before the speech started, so the first word is not clipped
    threshold_factor: float = 3.0   # speech = this many times louder than the noise floor
    min_threshold: float = 0.004    # never treat quieter than this as speech (RMS; quiet microphones speak at ~0.01)


@dataclass
class Event:
    kind: str                        # "start" | "partial_due" | "end"
    audio: np.ndarray | None = None  # the utterance so far (partial_due) or in full (end)
    speech_end: int = 0              # sample index where speech last ended (end)


@dataclass
class StreamSession:
    config: StreamConfig = field(default_factory=StreamConfig)
    noise: float = 0.005
    _buf: list[np.ndarray] = field(default_factory=list)
    _pre: list[np.ndarray] = field(default_factory=list)
    _carry: np.ndarray = field(default_factory=lambda: np.zeros(0, dtype=np.float32))
    in_speech: bool = False
    _speech_samples: int = 0
    _silence_samples: int = 0
    _since_partial: int = 0
    _spoke_since_partial: bool = False
    total_samples: int = 0

    @property
    def in_pause(self) -> bool:
        """Speaking, but quiet right now (the last frames were silence)."""
        return self.in_speech and self._silence_samples > 0

    def utterance(self) -> np.ndarray:
        return np.concatenate(self._buf) if self._buf else np.zeros(0, dtype=np.float32)

    def feed(self, pcm16: bytes) -> list[Event]:
        """Adds audio; returns what happened in it, in order."""
        samples = np.concatenate([self._carry, np.frombuffer(pcm16[: len(pcm16) // 2 * 2], dtype="<i2").astype(np.float32) / 32768.0])
        events: list[Event] = []
        c = self.config
        n_frames = len(samples) // FRAME
        for i in range(n_frames):
            frame = samples[i * FRAME:(i + 1) * FRAME]
            self.total_samples += FRAME
            rms = float(np.sqrt(np.mean(frame * frame)))
            threshold = max(c.min_threshold, self.noise * c.threshold_factor)
            loud = rms > threshold
            if not self.in_speech:
                # Quiet frames teach the noise floor (slowly), and are kept as pre-roll.
                if not loud:
                    self.noise = 0.95 * self.noise + 0.05 * rms
                self._pre.append(frame)
                keep = c.pre_roll_ms * RATE // 1000 // FRAME
                del self._pre[:-keep or None]
                if loud:
                    self.in_speech = True
                    self._buf = list(self._pre)
                    self._pre = []
                    self._speech_samples = FRAME
                    self._silence_samples = 0
                    self._since_partial = 0
                    events.append(Event("start"))
                continue
            self._buf.append(frame)
            self._since_partial += FRAME
            if loud:
                self._speech_samples += FRAME
                self._silence_samples = 0
                self._spoke_since_partial = True
            else:
                self._silence_samples += FRAME
            utterance_len = sum(len(b) for b in self._buf)
            ended = self._silence_samples >= c.silence_ms * RATE // 1000 or utterance_len >= c.max_utterance_s * RATE
            if ended:
                self.in_speech = False
                if self._speech_samples >= c.min_speech_ms * RATE // 1000:
                    events.append(Event("end", self.utterance(), utterance_len - self._silence_samples))
                self._buf = []
                continue
            enough = self._speech_samples >= c.min_speech_ms * RATE // 1000
            # Partials only for new speech (never for trailing silence), on a timer while speaking, and once early in a pause: that
            # last one covers everything said, so the endpoint can answer with it at once.
            pause_started = self._silence_samples == c.early_final_ms * RATE // 1000 // FRAME * FRAME
            if enough and self._spoke_since_partial and (pause_started or self._since_partial >= c.partial_every_ms * RATE // 1000):
                self._since_partial = 0
                self._spoke_since_partial = False
                events.append(Event("partial_due", self.utterance()))
        self._carry = samples[n_frames * FRAME:]
        return events

    def flush(self) -> Event | None:
        """The client said it is done: whatever speech is pending is an utterance."""
        if self.in_speech and self._speech_samples >= self.config.min_speech_ms * RATE // 1000:
            self.in_speech = False
            audio = self.utterance()
            self._buf = []
            return Event("end", audio, len(audio))
        return None


def wav_bytes(samples: np.ndarray, rate: int = RATE) -> bytes:
    """A complete WAV file for the engines, which decode files."""
    pcm = (np.clip(samples, -1, 1) * 32767).astype("<i2").tobytes()
    return (b"RIFF" + struct.pack("<I", 36 + len(pcm)) + b"WAVE" + b"fmt " + struct.pack("<IHHIIHH", 16, 1, 1, rate, rate * 2, 2, 16)
            + b"data" + struct.pack("<I", len(pcm)) + pcm)
