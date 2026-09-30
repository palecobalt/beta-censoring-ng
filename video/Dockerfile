# Video clip censoring service (see README).
# Default: Debian 13's ffmpeg 7.1, encoding with x264 on the CPU (the published image).
# GPU: --build-arg BASE=nvidia gives ffmpeg 7.1 with NVENC, which needs NVIDIA driver 550 or newer.
ARG BASE=cpu

FROM python:3.12-slim-trixie AS cpu
RUN apt-get update && apt-get install -y --no-install-recommends ffmpeg && rm -rf /var/lib/apt/lists/*

FROM jrottenberg/ffmpeg:7.1-nvidia2204 AS nvidia
RUN apt-get update && apt-get install -y --no-install-recommends python3 python3-pip && rm -rf /var/lib/apt/lists/*
ENV NVIDIA_DRIVER_CAPABILITIES=compute,video,utility

FROM ${BASE}
LABEL org.opencontainers.image.source="https://github.com/palecobalt/beta-censoring-ng" \
      org.opencontainers.image.description="Censors short video clips for the beta-censoring-ng proxy" \
      org.opencontainers.image.licenses="GPL-3.0-or-later"
WORKDIR /app
COPY requirements.txt .
RUN pip3 install --no-cache-dir -r requirements.txt
COPY video_censor/ video_censor/
COPY tests/ tests/
ENV PYTHONUNBUFFERED=1
EXPOSE 2383
ENTRYPOINT ["python3", "-m", "video_censor.server"]
