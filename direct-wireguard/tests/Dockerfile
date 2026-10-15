FROM ugreen-direct-wireguard:ci
RUN apt-get update \
    && apt-get install --no-install-recommends -y samba smbclient \
    && rm -rf /var/lib/apt/lists/*
COPY tests/fixture-nas.sh /app/fixture-nas.sh
COPY tests/fixture-client.sh /app/fixture-client.sh
RUN chmod 755 /app/fixture-*.sh
