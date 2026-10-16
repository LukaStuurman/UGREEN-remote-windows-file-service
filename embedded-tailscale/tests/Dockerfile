FROM debian:trixie-slim
RUN apt-get update && apt-get install --no-install-recommends -y samba python3 \
    && rm -rf /var/lib/apt/lists/*
COPY direct-wireguard/tests/fixture-nas.sh /fixture-nas.sh
RUN chmod 755 /fixture-nas.sh
ENTRYPOINT ["/fixture-nas.sh"]
