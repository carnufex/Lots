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



def test_requests_join_the_callers_trace():
    import logging

    from fastapi.testclient import TestClient

    from tests.test_api import FakeStt, FakeTts
    from voice.app import create_app
    from voice.logs import JsonFormatter

    seen = {}

    class Capture(logging.Handler):
        def emit(self, record):
            seen.update(__import__("json").loads(JsonFormatter().format(record)))

    log = logging.getLogger("voice.test")
    log.addHandler(Capture())
    app = create_app(Settings(api_key="secret"), FakeStt(), FakeTts())

    @app.get("/probe")
    def probe():
        log.warning("inside a request")
        return {}

    trace_id = "4bf92f3577b34da6a3ce929d0e0e4736"
    TestClient(app).get("/probe", headers={"traceparent": f"00-{trace_id}-00f067aa0ba902b7-01"})
    assert seen.get("trace_id") == trace_id
