# The censoring proxy: mitmproxy with the add-on from this folder (see README). Published as
# ghcr.io/palecobalt/censor-proxy by the release workflow.
FROM mitmproxy/mitmproxy:12.2.3
LABEL org.opencontainers.image.source="https://github.com/palecobalt/beta-censoring-ng" \
      org.opencontainers.image.description="Censoring proxy for beta-censoring-ng: images and video clips are censored as they pass through" \
      org.opencontainers.image.licenses="GPL-3.0-or-later"
COPY censor_proxy.py video_rules.py censor-options.json /addon/
