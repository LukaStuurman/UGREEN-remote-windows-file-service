FROM python:3.12-alpine

ENV PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1 \
    DATA_ROOT=/data \
    PORT=8080

WORKDIR /app
COPY app.py /app/app.py

RUN addgroup -S drive && adduser -S -G drive drive \
    && mkdir -p /data \
    && chown drive:drive /data

USER drive:drive
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=3s --start-period=5s --retries=3 \
  CMD python -c "import urllib.request; urllib.request.urlopen('http://127.0.0.1:8080/api/v1/health', timeout=2)"

ENTRYPOINT ["python", "/app/app.py"]
