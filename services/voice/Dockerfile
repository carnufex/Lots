FROM python:3.11-slim

# CUDA libraries come from pip (nvidia-cublas-cu12 / nvidia-cudnn-cu12); the GPU itself is provided by the NVIDIA
# container runtime (docker run --gpus all). No root needed at runtime: any UID with group 0 works (OpenShift style).
WORKDIR /app
COPY pyproject.toml ./
COPY voice/ voice/
COPY scripts/ scripts/
# CHATTERBOX=1 adds the expressive voice (PyTorch + Chatterbox, several GB). Piper alone is much smaller.
ARG CHATTERBOX=0
RUN if [ "$CHATTERBOX" = "1" ]; then pip install --no-cache-dir ".[engines,chatterbox]"; else pip install --no-cache-dir ".[engines]"; fi

ENV HOME=/tmp \
    HF_HOME=/models/hf \
    VOICE_MODELS_DIR=/models/piper \
    VOICE_REFS_DIR=/models/refs \
    VOICE_HOST=0.0.0.0 \
    VOICE_PORT=8700 \
    LD_LIBRARY_PATH=/usr/local/lib/python3.11/site-packages/nvidia/cublas/lib:/usr/local/lib/python3.11/site-packages/nvidia/cudnn/lib

RUN mkdir -p /models && chgrp -R 0 /app /models && chmod -R g=u /app /models
USER 10001:0
EXPOSE 8700
VOLUME ["/models"]
ENTRYPOINT ["python", "-m", "voice.main"]
