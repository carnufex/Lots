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


def test_json_log_lines(monkeypatch):
    import json
    import logging

    from voice.logs import JsonFormatter, configure

    monkeypatch.setenv("VOICE_LOG_FORMAT", "json")
    assert configure()["formatters"]["json"]["()"] is JsonFormatter
    record = logging.LogRecord("voice", logging.WARNING, __file__, 1, "GPU low: %s MB", (512,), None)
    line = json.loads(JsonFormatter().format(record))
    assert line["level"] == "warn" and line["msg"] == "GPU low: 512 MB" and line["time"].endswith("Z")
