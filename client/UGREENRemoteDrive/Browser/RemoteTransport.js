(() => {
  if (window.top !== window || !window.chrome?.webview) return;
  const bridge = window.chrome.webview;
  async function send(request, buffer) {
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), 30000);
    try {
      if (!request?.id || window.top !== window) return;
      const target = new URL(request.url, location.href);
      if (target.origin !== location.origin) throw new Error('The active page is not the configured UGREENlink app.');
      const headers = { 'X-UGREEN-Remote-Drive': '1' };
      let body;
      if (request.bodyKind === 'shared') {
        if (!buffer || !Number.isInteger(request.bodyLength) || request.bodyLength < 0 ||
            request.bodyLength > buffer.byteLength) throw new Error('Invalid binary request length.');
        body = new Uint8Array(buffer, 0, request.bodyLength);
      } else if (request.body !== null) {
        headers['Content-Type'] = 'application/json';
        body = request.body;
      }
      const response = await fetch(target.href, {
        method: request.method, headers, body,
        credentials: 'include', cache: 'no-store', redirect: 'manual', signal: controller.signal
      });
      if (response.type === 'opaqueredirect') {
        bridge.postMessage({ id: request.id, status: 0, ok: false, body: '', binary: false,
          error: 'UGREENlink login redirect', loginRedirect: true });
      } else if (request.responseKind === 'shared' && response.ok) {
        if (!buffer || !response.body) throw new Error('Missing binary response buffer.');
        const reader = response.body.getReader();
        let length = 0;
        try {
          while (true) {
            const part = await reader.read();
            if (part.done) break;
            if (part.value.byteLength > buffer.byteLength - length) {
              await reader.cancel();
              throw new Error('Binary response exceeds request capacity.');
            }
            new Uint8Array(buffer, length, part.value.byteLength).set(part.value);
            length += part.value.byteLength;
          }
        } finally { reader.releaseLock(); }
        bridge.postMessage({ id: request.id, status: response.status, ok: true,
          body: '', binary: true, length });
      } else {
        bridge.postMessage({ id: request.id, status: response.status, ok: response.ok,
          body: await response.text(), binary: false });
      }
    } catch (error) {
      bridge.postMessage({ id: request.id, status: 0, ok: false, body: '', binary: false,
        error: String(error?.message || error) });
    } finally {
      clearTimeout(timer);
      if (buffer) bridge.releaseBuffer(buffer);
    }
  }
  bridge.addEventListener('message', event => { void send(event.data); });
  bridge.addEventListener('sharedbufferreceived', event => {
    void send(event.additionalData, event.getBuffer());
  });
})();
