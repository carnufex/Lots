"""Streams a recording to the voice service at real-time pace and prints partials, finals and latency (#45).

Usage: python scripts/stream_client.py <audio> "ws://localhost:8700/v1/audio/transcriptions/stream?silence_ms=700" <key> [sv|en]
"""
import asyncio
import json
import sys
import time

import numpy as np
import websockets
from faster_whisper.audio import decode_audio

path, url, key = sys.argv[1], sys.argv[2], sys.argv[3]
lang = sys.argv[4] if len(sys.argv) > 4 else "sv"
samples = decode_audio(path, sampling_rate=16000)
pcm = (np.clip(samples, -1, 1) * 32767).astype("<i2").tobytes()
chunk = 16000 * 2 // 10  # 100 ms


async def main():
    async with websockets.connect(f"{url}{'&' if '?' in url else '?'}language={lang}", additional_headers={"Authorization": f"Bearer {key}"}) as ws:
        t0 = time.perf_counter()

        async def send():
            for i in range(0, len(pcm), chunk):
                await ws.send(pcm[i:i + chunk])
                await asyncio.sleep(0.1)
            await ws.send(b"\x00" * 16000 * 2)  # one second of silence
            await asyncio.sleep(1.0)
            await ws.send(json.dumps({"type": "end"}))

        sender = asyncio.create_task(send())
        async for msg in ws:
            e = json.loads(msg)
            stamp = time.perf_counter() - t0
            if e["type"] == "final":
                print(f"{stamp:6.2f}s FINAL ({e['latency_ms']} ms after the endpoint, {e['audio_ms']} ms audio): {e['text']}")
            elif e["type"] == "partial":
                print(f"{stamp:6.2f}s partial: {e['text'][:80]}")
            else:
                print(f"{stamp:6.2f}s {e['type']}")
            if e["type"] in ("done", "error"):
                break
        await sender


asyncio.run(main())
