"""GPU memory (#84): what is free on the card the voice service shares with the language model."""
from __future__ import annotations

import shutil
import subprocess
import threading
import time
from dataclasses import dataclass


@dataclass(frozen=True)
class GpuMemory:
    free_bytes: int
    total_bytes: int

    @property
    def used_share(self) -> float:
        return 1 - self.free_bytes / self.total_bytes if self.total_bytes else 0.0


class GpuMonitor:
    """Free/total memory of GPU 0, cached for a moment. Reads torch when it is loaded (exact, no subprocess), else nvidia-smi."""

    def __init__(self, cache_seconds: float = 2.0, reader=None) -> None:
        self._cache_seconds = cache_seconds
        self._reader = reader or self._read
        self._lock = threading.Lock()
        self._at = 0.0
        self._value: GpuMemory | None = None

    def memory(self) -> GpuMemory | None:
        with self._lock:
            if time.monotonic() - self._at > self._cache_seconds:
                try:
                    self._value = self._reader()
                except Exception:  # no GPU, no driver: the guard simply does nothing
                    self._value = None
                self._at = time.monotonic()
            return self._value

    @staticmethod
    def _read() -> GpuMemory | None:
        try:
            import sys

            if "torch" in sys.modules:
                import torch

                if torch.cuda.is_available():
                    free, total = torch.cuda.mem_get_info()
                    return GpuMemory(int(free), int(total))
        except Exception:
            pass
        smi = shutil.which("nvidia-smi")
        if smi is None:
            return None
        out = subprocess.run([smi, "--query-gpu=memory.free,memory.total", "--format=csv,noheader,nounits"],
                             capture_output=True, text=True, timeout=3, check=True).stdout.splitlines()[0]
        free_mb, total_mb = (int(x.strip()) for x in out.split(","))
        return GpuMemory(free_mb * 1024 * 1024, total_mb * 1024 * 1024)
