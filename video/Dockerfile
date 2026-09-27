# Video clip censoring service (see README). ffmpeg 7.1 with NVENC, which needs NVIDIA driver 550 or newer;
# without a GPU it encodes with x264.
FROM jrottenberg/ffmpeg:7.1-nvidia2204
RUN apt-get update && apt-get install -y --no-install-recommends python3 python3-pip && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY requirements.txt .
RUN pip3 install --no-cache-dir -r requirements.txt
COPY video_censor/ video_censor/
COPY tests/ tests/
ENV PYTHONUNBUFFERED=1 \
    NVIDIA_DRIVER_CAPABILITIES=compute,video,utility
EXPOSE 2383
ENTRYPOINT ["python3", "-m", "video_censor.server"]
