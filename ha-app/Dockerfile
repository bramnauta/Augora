# syntax=docker/dockerfile:1

# Home Assistant app image: the regular Augora image plus an entrypoint that maps the app options to
# configuration. Built by .github/workflows/ha-app.yml with the repository root as context.
ARG BASE_IMAGE=augora:latest
FROM ${BASE_IMAGE}

USER root
RUN apt-get update \
    && apt-get install -y --no-install-recommends jq \
    && rm -rf /var/lib/apt/lists/*

COPY ha-app/run.sh /run.sh
RUN chmod +x /run.sh

# run.sh needs root to fix ownership of /data, then drops to the app user itself.
ENTRYPOINT ["/run.sh"]
