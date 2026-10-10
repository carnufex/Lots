"""JSON log lines (#138), one object per line on stdout, so the voice service's logs join the shell's in the same backend.

VOICE_LOG_FORMAT=json (the container default) or text. Request bodies and transcripts are never logged.
"""
from __future__ import annotations

import json
import logging
import os
import time


class JsonFormatter(logging.Formatter):
    def format(self, record: logging.LogRecord) -> str:
        line = {
            "time": time.strftime("%Y-%m-%dT%H:%M:%S", time.gmtime(record.created)) + f".{int(record.msecs):03d}Z",
            "level": record.levelname.lower().replace("warning", "warn"),
            "category": record.name,
            "msg": record.getMessage(),
        }
        from .tracing import current_ids

        if ids := current_ids():
            line["trace_id"], line["span_id"] = ids
        if record.exc_info:
            line["exception"] = self.formatException(record.exc_info)
        return json.dumps(line, ensure_ascii=False)


def configure() -> dict | None:
    """Installs the formatter on the root and uvicorn loggers; returns uvicorn's log_config (None keeps uvicorn's own)."""
    if os.environ.get("VOICE_LOG_FORMAT", "text") != "json":
        return None
    return {
        "version": 1,
        "disable_existing_loggers": False,
        "formatters": {"json": {"()": JsonFormatter}},
        "handlers": {"stdout": {"class": "logging.StreamHandler", "formatter": "json", "stream": "ext://sys.stdout"}},
        "root": {"handlers": ["stdout"], "level": os.environ.get("VOICE_LOG_LEVEL", "INFO")},
        "loggers": {name: {"handlers": ["stdout"], "level": "INFO", "propagate": False} for name in ("uvicorn", "uvicorn.error", "uvicorn.access")},
    }
