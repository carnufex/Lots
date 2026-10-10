import numpy as np
import pytest
from fastapi.testclient import TestClient

from voice.app import create_app
from voice.config import Settings
from voice.engines import Segment, Transcription
from voice.wav import streaming_header, to_pcm16


class FakeStt:
    def __init__(self):
        self.calls = []
        self.fail = False

    def warm_up(self):
        pass

    def transcribe(self, audio, language, prompt=None):
        if self.fail:
            raise ValueError("bad audio")
        self.prompts = getattr(self, "prompts", []) + [prompt]
        self.calls.append((len(audio), language))
        lang = language or "sv"
        text = getattr(self, "text", "hej världen")
        return Transcription(text, lang, 1.5, [Segment(0.0, 1.5, text)])


class FakeTts:
    sample_rate = 22050

    def __init__(self):
        self.calls = []

    def voices(self):
        return {"sv-nst": "sv", "en-lessac": "en"}

    def warm_up(self):
        pass

    def synthesize(self, text, voice, speed):
        self.calls.append((text, voice, speed))
        yield np.full(100, 0.5, dtype=np.float32)
        yield np.full(50, -0.5, dtype=np.float32)


@pytest.fixture
def parts():
    return Settings(api_key="secret", max_audio_bytes=1000, max_text_chars=50), FakeStt(), FakeTts()


@pytest.fixture
def client(parts):
    return TestClient(create_app(*parts)), {"Authorization": "Bearer secret"}


def test_refuses_to_start_without_a_key_unless_anonymous_is_explicit():
    with pytest.raises(RuntimeError, match="VOICE_API_KEY"):
        create_app(Settings(), FakeStt(), FakeTts())
    create_app(Settings(allow_anonymous=True), FakeStt(), FakeTts())  # explicit opt-in works


def test_health_is_open_and_everything_else_needs_the_key(client):
    c, auth = client
    assert c.get("/health").json() == {"status": "ok"}
    assert c.get("/v1/models").status_code == 401
    assert c.get("/v1/models", headers={"Authorization": "Bearer nope"}).status_code == 401
    assert c.post("/v1/audio/speech", json={"input": "hi", "voice": "sv-nst"}).status_code == 401
    assert c.get("/v1/models", headers=auth).status_code == 200


def test_models_lists_languages_and_voices(client):
    c, auth = client
    ids = [m["id"] for m in c.get("/v1/models", headers=auth).json()["data"]]
    assert {"stt-sv", "stt-en", "voice:sv-nst", "voice:en-lessac"} <= set(ids)


def test_transcription_json_verbose_and_text(client, parts):
    c, auth = client
    files = {"file": ("a.wav", b"x" * 100, "audio/wav")}

    assert c.post("/v1/audio/transcriptions", headers=auth, files=files, data={"language": "sv"}).json() == {"text": "hej världen"}
    verbose = c.post("/v1/audio/transcriptions", headers=auth, files=files, data={"response_format": "verbose_json"}).json()
    assert verbose["language"] == "sv" and verbose["duration"] == 1.5
    assert verbose["segments"] == [{"id": 0, "start": 0.0, "end": 1.5, "text": "hej världen"}]
    text = c.post("/v1/audio/transcriptions", headers=auth, files=files, data={"response_format": "text"})
    assert text.text == "hej världen"
    assert "x-processing-ms" in text.headers
    assert parts[1].calls[0] == (100, "sv") and parts[1].calls[1] == (100, None)  # no language -> the engine detects


def test_vocabulary_prompt_reaches_the_engine_and_is_limited(client, parts):
    c, auth = client
    files = {"file": ("a.wav", b"x" * 10, "audio/wav")}
    assert c.post("/v1/audio/transcriptions", headers=auth, files=files, data={"prompt": "Christopher, Lots"}).status_code == 200
    assert c.post("/v1/audio/transcriptions", headers=auth, files=files).status_code == 200
    assert parts[1].prompts == ["Christopher, Lots", None]
    too_long = c.post("/v1/audio/transcriptions", headers=auth, files=files, data={"prompt": "x" * 601})
    assert too_long.status_code == 413


def test_vocabulary_spelling_is_applied_to_text_and_segments(client, parts):
    c, auth = client
    parts[1].text = "Hej, jag heter Christoffer."
    r = c.post("/v1/audio/transcriptions", headers=auth, files={"file": ("a.wav", b"x" * 10, "audio/wav")},
               data={"prompt": "Christopher, Lots", "response_format": "verbose_json"}).json()
    assert r["text"] == "Hej, jag heter Christopher."
    assert r["segments"][0]["text"] == "Hej, jag heter Christopher."
    plain = c.post("/v1/audio/transcriptions", headers=auth, files={"file": ("a.wav", b"x" * 10, "audio/wav")}).json()
    assert plain["text"] == "Hej, jag heter Christoffer."  # nothing is changed without a vocabulary


def test_transcription_validation(client, parts):
    c, auth = client
    ok = {"file": ("a.wav", b"x" * 10, "audio/wav")}
    assert c.post("/v1/audio/transcriptions", headers=auth, files=ok, data={"language": "de"}).status_code == 400
    assert c.post("/v1/audio/transcriptions", headers=auth, files=ok, data={"response_format": "srt"}).status_code == 400
    assert c.post("/v1/audio/transcriptions", headers=auth, files={"file": ("a.wav", b"", "audio/wav")}).status_code == 400
    assert c.post("/v1/audio/transcriptions", headers=auth, files={"file": ("a.wav", b"x" * 1001, "audio/wav")}).status_code == 413
    parts[1].fail = True
    assert c.post("/v1/audio/transcriptions", headers=auth, files=ok).status_code == 422


def test_speech_streams_a_wav_with_header_and_pcm(client, parts):
    c, auth = client

    r = c.post("/v1/audio/speech", headers=auth, json={"input": "Hej!", "voice": "sv-nst"})

    assert r.status_code == 200 and r.headers["content-type"] == "audio/wav"
    header = streaming_header(22050)
    assert r.content[:44] == header and r.content[:4] == b"RIFF" and r.content[8:12] == b"WAVE"
    assert r.content[44:] == to_pcm16(np.full(100, 0.5, np.float32)) + to_pcm16(np.full(50, -0.5, np.float32))
    assert parts[2].calls == [("Hej!", "sv-nst", 1.0)]


def test_speech_raw_pcm_has_no_header_and_speed_is_clamped(client, parts):
    c, auth = client

    r = c.post("/v1/audio/speech", headers=auth, json={"input": "Hi", "voice": "en-lessac", "response_format": "pcm", "speed": 9})

    assert len(r.content) == (100 + 50) * 2
    assert parts[2].calls[0][2] == 2.0


def test_speech_validation(client, parts):
    c, auth = client
    post = lambda **kw: c.post("/v1/audio/speech", headers=auth, json={"input": "hi", "voice": "sv-nst", **kw})  # noqa: E731
    assert post(voice="nope").status_code == 400
    assert post(response_format="mp3").status_code == 400
    assert post(input="   ").status_code == 400
    assert post(input="x" * 51).status_code == 413
    assert parts[2].calls == []  # nothing was synthesized for rejected requests


def test_sentences_are_split_for_streaming_synthesis():
    from voice.engines import split_sentences

    assert split_sentences("Hej! Hur mår du? Bra.") == ["Hej!", "Hur mår du?", "Bra."]
    assert split_sentences("  Ingen punkt  ") == ["Ingen punkt"]
    assert split_sentences("Version 1.5 är ute. Ja.") == ["Version 1.5 är ute.", "Ja."]
    assert split_sentences("   ") == []


def test_wav_helpers():
    assert len(streaming_header(16000)) == 44
    pcm = to_pcm16(np.array([0.0, 1.0, -1.0, 2.0], dtype=np.float32))
    assert np.frombuffer(pcm, dtype="<i2").tolist() == [0, 32767, -32767, 32767]  # clipped
