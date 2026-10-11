FROM python:3.11-slim@sha256:e88e9763f943ec1834f992a4b51e0f24500486803e8bc534e5767af9ea65f6ce

# CUDA libraries come from pip (nvidia-cublas-cu12 / nvidia-cudnn-cu12); the GPU itself is provided by the NVIDIA
# container runtime (docker run --gpus all). No root needed at runtime: any UID with group 0 works (OpenShift style).
WORKDIR /app
# Dependencies first, in their own layer: a code change then rebuilds in seconds instead of reinstalling PyTorch.
COPY pyproject.toml constraints.txt ./
# CHATTERBOX=1 adds the expressive voice (PyTorch + Chatterbox, several GB). Piper alone is much smaller.
ARG CHATTERBOX=0
# chatterbox-tts itself goes in with --no-deps (#147): its exact pins are vulnerable, and it would pull in gradio (demo UI) and
# pykakasi (GPL-3.0, Japanese only, imported lazily with a fallback, #131). Its real dependencies are the `chatterbox` extra.
RUN mkdir voice && touch voice/__init__.py  && if [ "$CHATTERBOX" = "1" ]; then pip install --no-cache-dir -c constraints.txt ".[engines,chatterbox]" && pip install --no-cache-dir --no-deps -c constraints.txt chatterbox-tts;     else pip install --no-cache-dir -c constraints.txt ".[engines]"; fi  && pip uninstall -y lots-voice && rm -rf voice  && ! pip show gradio pykakasi 2>/dev/null | grep -q Name
# Trace export (#139), its own small layer next to the big one.
RUN pip install --no-cache-dir -c constraints.txt opentelemetry-sdk==1.45.1 opentelemetry-exporter-otlp-proto-http==1.45.1
COPY voice/ voice/
COPY scripts/ scripts/
RUN pip install --no-cache-dir --no-deps .

ENV HOME=/tmp \
    HF_HOME=/models/hf \
    VOICE_MODELS_DIR=/models/piper \
    VOICE_REFS_DIR=/models/refs \
    VOICE_HOST=0.0.0.0 \
    VOICE_PORT=8700 \
    VOICE_LOG_FORMAT=json \
    LD_LIBRARY_PATH=/usr/local/lib/python3.11/site-packages/nvidia/cublas/lib:/usr/local/lib/python3.11/site-packages/nvidia/cudnn/lib

RUN mkdir -p /models && chgrp -R 0 /app /models && chmod -R g=u /app /models
USER 10001:0
EXPOSE 8700
VOLUME ["/models"]
ENTRYPOINT ["python", "-m", "voice.main"]
