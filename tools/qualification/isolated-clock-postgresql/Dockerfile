# Qualification only. Build explicitly with --platform linux/amd64.
FROM postgres:18.4-bookworm@sha256:7e6103cf85f88f7a0eddb3ec0b1ba8940eba098ed118ade25a729ca9daee5568 AS compiler
RUN test "$(dpkg --print-architecture)" = amd64 \
    && apt-get update \
    && apt-get install --no-install-recommends -y gcc=4:12.2.0-3 libc6-dev \
    && rm -rf /var/lib/apt/lists/*
COPY clock-control.c /tmp/clock-control.c
RUN gcc -std=c11 -O2 -Wall -Wextra -Werror /tmp/clock-control.c -o /tmp/clock-control

FROM postgres:18.4-bookworm@sha256:7e6103cf85f88f7a0eddb3ec0b1ba8940eba098ed118ade25a729ca9daee5568
# apt checks the signed Debian Release/package indexes; do not bypass verification.
RUN test "$(dpkg --print-architecture)" = amd64 \
    && apt-get update \
    && apt-get install --no-install-recommends -y libfaketime:amd64=0.9.10-2.1 \
    && test "$(dpkg-query -W -f='${Version}' libfaketime)" = 0.9.10-2.1 \
    && rm -rf /var/lib/apt/lists/* \
    && mkdir /opt/sqlobserver-clock
COPY --from=compiler /tmp/clock-control /opt/sqlobserver-clock/clock-control
COPY clock-offset.txt /opt/sqlobserver-clock/clock-offset.txt
RUN chmod 0555 /opt/sqlobserver-clock/clock-control \
    && chmod 0644 /opt/sqlobserver-clock/clock-offset.txt \
    && chown postgres:postgres /opt/sqlobserver-clock /opt/sqlobserver-clock/clock-offset.txt
ENV LD_PRELOAD=/usr/lib/x86_64-linux-gnu/faketime/libfaketime.so.1 \
    FAKETIME_TIMESTAMP_FILE=/opt/sqlobserver-clock/clock-offset.txt \
    FAKETIME_DONT_FAKE_MONOTONIC=1 \
    FAKETIME_NO_CACHE=1 \
    NO_FAKE_STAT=1 \
    TZ=UTC
LABEL org.sqlobserver.qualification.clock="isolated-forward-days-v1" \
    org.sqlobserver.qualification.libfaketime="0.9.10-2.1"
# libfaketime's private semaphore must be created by the same identity as the
# server. Avoid the stock root-to-postgres re-exec under an active preload.
USER postgres
