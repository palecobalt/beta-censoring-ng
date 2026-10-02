# Video clip censoring service (see README).
# Debian 13's ffmpeg 7.1 encodes with x264 on the CPU, and with NVENC when the container is given an NVIDIA GPU
# (docker-compose.gpu.yml); its NVENC support is built for driver 531 and newer.
FROM python:3.14-slim-trixie
RUN apt-get update && apt-get install -y --no-install-recommends ffmpeg && rm -rf /var/lib/apt/lists/*
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
