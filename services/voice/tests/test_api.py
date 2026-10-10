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
        return {"sv-nst": "sv", "en-ljspeech": "en"}

    def register(self, voice, data):
        if data == b"short":
            raise ValueError("The clip must be 3-40 seconds, got 1.0.")
        self.registered = (voice, len(data))
        return 12.34

    def delete(self, voice):
        return voice == "u-abc"

    def warm_up(self):
        pass

    def synthesize(self, text, voice, speed, options=None):
        self.calls.append((text, voice, speed))
        self.options = options
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
    assert {"stt-sv", "stt-en", "voice:sv-nst", "voice:en-ljspeech"} <= set(ids)


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

    r = c.post("/v1/audio/speech", headers=auth, json={"input": "Hi", "voice": "en-ljspeech", "response_format": "pcm", "speed": 9})

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


def test_language_choice_uses_identification_and_falls_back_to_the_default_when_unsure():
    from voice.engines import choose_language

    langs = ("sv", "en")
    assert choose_language({"sv": 0.96, "en": 0.01}, langs, "sv") == "sv"
    assert choose_language({"sv": 0.02, "en": 0.90}, langs, "sv") == "en"
    assert choose_language({"sv": 0.30, "en": 0.45, "no": 0.2}, langs, "sv") == "en"      # together 0.75: trusted
    assert choose_language({"sv": 0.22, "en": 0.15, "de": 0.4}, langs, "sv") == "sv"      # together 0.37: unsure -> default
    assert choose_language({"sv": 0.10, "en": 0.12, "de": 0.7}, langs, "en") == "en"      # a deployment default can differ
    assert choose_language({}, langs, "xx") == "sv"                                       # an unknown default is ignored


def test_speech_passes_language_and_expressiveness_to_the_engine(client, parts):
    c, h = client
    r = c.post("/v1/audio/speech", headers=h, json={
        "input": "hej", "voice": "sv-nst", "language": "sv", "expressiveness": 0.8, "pace": 0.3})
    assert r.status_code == 200
    o = parts[2].options
    assert (o.language, o.expressiveness, o.pace) == ("sv", 0.8, 0.3)


def test_register_and_delete_a_reference_voice(client, parts):
    c, h = client
    r = c.put("/v1/voices/u-abc", headers=h, files={"audio": ("clip.wav", b"RIFFdata", "audio/wav")})
    assert r.status_code == 200 and r.json() == {"id": "u-abc", "seconds": 12.3}
    assert parts[2].registered == ("u-abc", 8)
    assert c.delete("/v1/voices/u-abc", headers=h).status_code == 200
    assert c.delete("/v1/voices/u-none", headers=h).status_code == 404


def test_register_rejects_bad_ids_short_clips_and_missing_key(client):
    c, h = client
    files = {"audio": ("c.wav", b"x", "audio/wav")}
    assert c.put("/v1/voices/cb-default", headers=h, files=files).status_code == 400
    assert c.put("/v1/voices/Bad_Id", headers=h, files=files).status_code == 400
    assert c.put("/v1/voices/u-abc", headers=h, files={"audio": ("c.wav", b"short", "audio/wav")}).status_code == 400
    assert c.put("/v1/voices/u-abc", files=files).status_code == 401


class FakeExpressive:
    def __init__(self, loaded=True, waiting=0):
        self.loaded = loaded
        self.waiting = waiting
        self.load_requested = False

    def voices(self):
        return {"cb-default": "*"}

    def load_in_background(self):
        self.load_requested = True


class FakeFast:
    sample_rate = 22050

    def voices(self):
        return {"sv-nst": "sv", "en-ljspeech": "en"}


class FakeGpu:
    def __init__(self, free_mb):
        self.free_mb = free_mb

    def memory(self):
        from voice.gpu import GpuMemory

        return GpuMemory(self.free_mb * 1024 * 1024, 16 * 1024 * 1024 * 1024)


def composite(expressive, free_mb=8000):
    from voice.engines import CompositeTts, SynthOptions

    return CompositeTts(expressive, FakeFast(), Settings(min_free_vram_mb=1200, max_expressive_queue=2), FakeGpu(free_mb)), SynthOptions


def test_expressive_voice_is_used_when_the_gpu_has_room():
    tts, Options = composite(FakeExpressive())
    assert tts.resolve("cb-default", Options(language="en")) == ("cb-default", None)


def test_low_vram_before_loading_falls_back_to_the_fast_voice_of_the_language():
    tts, Options = composite(FakeExpressive(loaded=False), free_mb=500)
    assert tts.resolve("cb-default", Options(language="en")) == ("en-ljspeech", "vram")


def test_a_full_queue_and_a_cold_model_fall_back_and_loading_starts_in_the_background():
    busy, Options = composite(FakeExpressive(waiting=2))
    assert busy.resolve("cb-default", Options(language="sv")) == ("sv-nst", "busy")
    cold_engine = FakeExpressive(loaded=False)
    cold, _ = composite(cold_engine)
    assert cold.resolve("cb-default", Options(language="sv")) == ("sv-nst", "loading")
    assert cold_engine.load_requested


def test_fast_voices_are_never_swapped():
    tts, Options = composite(FakeExpressive(loaded=False), free_mb=10)
    assert tts.resolve("sv-nst", Options(language="sv")) == ("sv-nst", None)


def test_health_reports_gpu_memory(parts):
    settings, stt, tts = parts
    client = TestClient(create_app(settings, stt, tts, FakeGpu(800)))
    body = client.get("/health").json()
    assert body["gpu"]["low"] is True and body["gpu"]["free_bytes"] == 800 * 1024 * 1024


def test_urlencoded_bodies_are_refused_before_parsing(client):
    # Mitigation for CVE-2026-54283 (#147): no endpoint takes urlencoded forms.
    c, auth = client
    r = c.post("/v1/audio/transcriptions", headers={**auth, "Content-Type": "application/x-www-form-urlencoded"}, content=b"a=" + b"x" * 1000)
    assert r.status_code == 415


def test_a_registered_voice_can_be_looked_up_so_deletion_can_be_verified(client, parts):
    c, h = client
    parts[2].voices = lambda: {"sv-nst": "sv", "u-abc": "*"}
    assert c.get("/v1/voices/u-abc", headers=h).json() == {"id": "u-abc"}
    assert c.get("/v1/voices/u-gone", headers=h).status_code == 404
    assert c.get("/v1/voices/cb-default", headers=h).status_code == 404  # built-in voices are not user voices
    assert c.get("/v1/voices/u-abc").status_code == 401
