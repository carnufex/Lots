"""Voice registry (#131): only permissively licensed voices by default, and only installed ones are offered."""
from voice.config import RESEARCH_VOICES, Settings
from voice.engines import PiperTts


def test_research_only_voices_are_off_unless_asked_for():
    plain = Settings.from_env({"VOICE_API_KEY": "k"})
    research = Settings.from_env({"VOICE_API_KEY": "k", "VOICE_RESEARCH_VOICES": "1"})

    assert "en-lessac" not in plain.voices
    assert "en-ljspeech" in plain.voices
    assert set(RESEARCH_VOICES) <= set(research.voices)


def test_only_installed_voices_are_offered(tmp_path):
    settings = Settings.from_env({"VOICE_API_KEY": "k", "VOICE_MODELS_DIR": str(tmp_path)})
    v = settings.voices["sv-nst"]
    (tmp_path / v.directory).mkdir()
    (tmp_path / v.directory / v.onnx).write_bytes(b"onnx")

    assert PiperTts(settings).voices() == {"sv-nst": "sv"}
