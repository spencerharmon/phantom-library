# Locked linux/amd64 build. The base digest is the exact amd64 OCI manifest.
FROM docker.io/library/debian:trixie-slim@sha256:ab3b3c8c5159eebfca4376430132d53c4d425f869c765ecbe47177a030da575d AS build
WORKDIR /src/tools/swarm-health
COPY lock-packages.sh build-packages.sha256 ./
RUN sh lock-packages.sh build > /actual.sha256 && diff -u build-packages.sha256 /actual.sha256 \
    && apt-get -y --no-install-recommends --no-download install g++ pkg-config libtorrent-rasterbar-dev nlohmann-json3-dev
COPY build-bridge.sh dht_bridge.cpp ./
WORKDIR /src
RUN OUTPUT=/dht-bridge sh tools/swarm-health/build-bridge.sh \
    && dpkg-query -W -f='${Package}\t${Version}\t${Architecture}\n' | LC_ALL=C sort > /build-packages.tsv

FROM docker.io/library/debian:trixie-slim@sha256:ab3b3c8c5159eebfca4376430132d53c4d425f869c765ecbe47177a030da575d
ARG SOURCE_REVISION
ARG SOURCE_DATE_EPOCH
LABEL org.opencontainers.image.title="Phantom independent swarm-health service" \
      org.opencontainers.image.source="https://github.com/spencerharmon/phantom-library" \
      org.opencontainers.image.revision=$SOURCE_REVISION \
      org.opencontainers.image.base.name="docker.io/library/debian:trixie-slim" \
      org.opencontainers.image.base.digest="sha256:ab3b3c8c5159eebfca4376430132d53c4d425f869c765ecbe47177a030da575d"
COPY lock-packages.sh runtime-packages.sha256 /build/
RUN sh /build/lock-packages.sh runtime > /actual.sha256 && diff -u /build/runtime-packages.sha256 /actual.sha256 \
    && apt-get -y --no-install-recommends --no-download install python3 libtorrent-rasterbar2.0t64 \
    && mkdir -p /usr/share/swarm-health /app /var/lib/swarm-health \
    && dpkg-query -W -f='${Package}\t${Version}\t${Architecture}\n' | LC_ALL=C sort > /usr/share/swarm-health/runtime-packages.tsv \
    && rm -rf /build /actual.sha256 /var/lib/apt/lists/* /var/cache/apt/* /var/log/* /etc/apt/sources.list* \
    && rm -f /usr/bin/apt /usr/bin/apt-get /usr/bin/apt-cache \
    && chown 10001:10001 /var/lib/swarm-health && chmod 0700 /var/lib/swarm-health
COPY --from=build /dht-bridge /app/dht-bridge
COPY --from=build /build-packages.tsv /usr/share/swarm-health/
COPY swarm_health_service.py /app/swarm_health_service.py
COPY Dockerfile build-bridge.sh dht_bridge.cpp lock-packages.sh build-packages.sha256 runtime-packages.sha256 /usr/share/swarm-health/
RUN python3 -c 'import os,re; assert re.fullmatch("[0-9a-f]{40}",os.environ["SOURCE_REVISION"]); assert int(os.environ["SOURCE_DATE_EPOCH"])>0' \
    && printf '%s\n' "$SOURCE_REVISION" > /usr/share/swarm-health/source-revision \
    && printf '%s\n' "$SOURCE_DATE_EPOCH" > /usr/share/swarm-health/source-date-epoch \
    && { ldd /app/dht-bridge; ldd /usr/bin/python3.13; } > /usr/share/swarm-health/runtime-linkage.txt \
    && { printf '%s\n' /usr/bin/python3.13; awk '/=> \// {print $3} $1 ~ /^\// {print $1}' /usr/share/swarm-health/runtime-linkage.txt; } \
       | LC_ALL=C sort -u | xargs sha256sum > /usr/share/swarm-health/runtime-files.sha256 \
    && rm /usr/share/swarm-health/runtime-linkage.txt \
    && sha256sum /app/* /usr/share/swarm-health/* > /usr/share/swarm-health/files.sha256
ENV PYTHONDONTWRITEBYTECODE=1 PYTHONUNBUFFERED=1 STATE_DIR=/var/lib/swarm-health
WORKDIR /app
USER 10001:10001
VOLUME ["/var/lib/swarm-health"]
EXPOSE 8095/tcp 6881/udp
STOPSIGNAL SIGTERM
ENTRYPOINT ["python3", "/app/swarm_health_service.py"]
