FROM debian:trixie-slim

RUN apt-get update \
    && apt-get install --no-install-recommends -y python3 wireguard-tools wireguard-go iproute2 iptables socat tini \
    && rm -rf /var/lib/apt/lists/*

ENV PYTHONDONTWRITEBYTECODE=1 PYTHONUNBUFFERED=1
WORKDIR /app
COPY app.py /app/app.py
EXPOSE 51820/udp
HEALTHCHECK --interval=30s --timeout=5s --start-period=15s --retries=3 \
    CMD ["python3", "/app/app.py", "health"]
ENTRYPOINT ["/usr/bin/tini", "--", "python3", "/app/app.py"]
