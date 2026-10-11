"""Meetings (#41, #44): a whole recording in, timestamped text and speaker turns out. The shell merges them into a transcript.

Speaker diarization runs on sherpa-onnx (already a dependency): pyannote segmentation 3.0 (MIT) and a 3D-Speaker embedding model
(Apache-2.0), both from the sherpa-onnx releases, so no gated model or account is needed. Labels are suggestions: diarization is
weak on overlapping and far-field speech.
"""
from __future__ import annotations

import io
import re
import threading
from dataclasses import dataclass, field
from pathlib import Path
from typing import Protocol

SEGMENTATION = "sherpa-onnx-pyannote-segmentation-3-0"
_SPECIAL = re.compile(r"<\|[^|>]*\|>")  # Whisper's own tokens (<|nospeech|>) that leak into long-form output
EMBEDDING = "3dspeaker_speech_eres2net_base_sv_zh-cn_3dspeaker_16k.onnx"


@dataclass(frozen=True)
class Word:
    start: float
    end: float
    word: str


@dataclass(frozen=True)
class TimedSegment:
    start: float
    end: float
    text: str
    words: list[Word] = field(default_factory=list)


@dataclass(frozen=True)
class Turn:
    start: float
    end: float
    speaker: int


@dataclass(frozen=True)
class MeetingResult:
    language: str
    duration: float
    segments: list[TimedSegment]
    turns: list[Turn]


class MeetingEngine(Protocol):
    def process(self, audio: bytes, language: str | None, num_speakers: int | None, prompt: str | None, words: bool) -> MeetingResult: ...


class SherpaDiarizer:
    """Who spoke when. Loaded on first use; CPU (the GPU stays with Whisper and the expressive voice)."""

    def __init__(self, models_dir: Path, threads: int = 4) -> None:
        self._dir = models_dir
        self._threads = threads
        self._lock = threading.Lock()
        self._cache: dict[int, object] = {}

    def _diarizer(self, num_speakers: int | None):
        import sherpa_onnx

        key = num_speakers or -1
        with self._lock:
            if key not in self._cache:
                config = sherpa_onnx.OfflineSpeakerDiarizationConfig(
                    segmentation=sherpa_onnx.OfflineSpeakerSegmentationModelConfig(
                        pyannote=sherpa_onnx.OfflineSpeakerSegmentationPyannoteModelConfig(model=str(self._dir / SEGMENTATION / "model.onnx")),
                        num_threads=self._threads),
                    embedding=sherpa_onnx.SpeakerEmbeddingExtractorConfig(model=str(self._dir / EMBEDDING), num_threads=self._threads),
                    # A known number of speakers clusters exactly; otherwise a distance threshold decides how many there are.
                    clustering=sherpa_onnx.FastClusteringConfig(num_clusters=key, threshold=0.5),
                    min_duration_on=0.3, min_duration_off=0.5)
                if not config.validate():
                    raise RuntimeError(f"Diarization models are missing in {self._dir} (run scripts/fetch_models.py).")
                self._cache[key] = sherpa_onnx.OfflineSpeakerDiarization(config)
            return self._cache[key]

    def diarize(self, samples, num_speakers: int | None) -> list[Turn]:
        result = self._diarizer(num_speakers).process(samples).sort_by_start_time()
        return [Turn(round(r.start, 3), round(r.end, 3), int(r.speaker)) for r in result]


class WhisperMeetings:
    """Transcribes with timestamps (and word timestamps for #44) on the loaded Whisper models, then diarizes the same samples."""

    def __init__(self, stt, diarizer: SherpaDiarizer) -> None:
        self._stt = stt
        self._diarizer = diarizer

    def process(self, audio: bytes, language: str | None, num_speakers: int | None, prompt: str | None, words: bool) -> MeetingResult:
        from faster_whisper.audio import decode_audio

        samples = decode_audio(io.BytesIO(audio), sampling_rate=16000)
        duration = len(samples) / 16000
        if language is None:
            language = self._stt._identify(samples[: 16000 * 30])  # the first half minute decides the language
        segments, _info = self._stt._model(language).transcribe(
            samples, language=language, beam_size=1, vad_filter=True, condition_on_previous_text=False,
            initial_prompt=prompt or None, word_timestamps=words)
        timed = [TimedSegment(round(s.start, 3), round(s.end, 3), _SPECIAL.sub("", s.text).strip(),
                              [Word(round(w.start, 3), round(w.end, 3), w.word) for w in (s.words or []) if not _SPECIAL.search(w.word)] if words else [])
                 for s in segments if _SPECIAL.sub("", s.text).strip()]
        return MeetingResult(language, duration, timed, self._diarizer.diarize(samples, num_speakers))
