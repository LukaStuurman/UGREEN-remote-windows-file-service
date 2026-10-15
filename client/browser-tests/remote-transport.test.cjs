const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { JSDOM } = require('jsdom');
const script = fs.readFileSync(path.join(__dirname, '../UGREENRemoteDrive/Browser/RemoteTransport.js'), 'utf8');

function setup(fetch) {
  const dom = new JSDOM('', { url: 'https://fixture.ugdocker.link/', runScripts: 'outside-only' });
  const handlers = new Map();
  const released = [];
  let complete;
  const reply = new Promise(resolve => { complete = resolve; });
  dom.window.AbortController = AbortController;
  dom.window.fetch = fetch;
  dom.window.chrome = { webview: {
    addEventListener: (name, handler) => handlers.set(name, handler),
    postMessage: message => complete(message),
    releaseBuffer: buffer => released.push(buffer)
  } };
  dom.window.eval(script);
  return { dom, reply, released,
    send(request, capacity) {
      const buffer = new dom.window.ArrayBuffer(capacity);
      handlers.get('sharedbufferreceived')({ additionalData: request, getBuffer: () => buffer });
      return buffer;
    },
    message(request) { handlers.get('message')({ data: request }); },
    shared(request, buffer) { handlers.get('sharedbufferreceived')({ additionalData: request, getBuffer: () => buffer }); }
  };
}
const request = (extra = {}) => ({ id: 'fixture', url: 'https://fixture.ugdocker.link/api/v1/read',
  method: 'GET', body: null, bodyKind: 'json', responseKind: 'shared', ...extra });

test('streamed 4 MiB binary response stays byte-exact without a text payload', async () => {
  const bytes = Uint8Array.from({ length: 4 * 1024 * 1024 }, (_, i) => i % 251);
  const s = setup(async (url, options) => {
    assert.equal(options.credentials, 'include');
    assert.equal(options.redirect, 'manual');
    assert.equal(options.headers['X-UGREEN-Remote-Drive'], '1');
    return new Response(new ReadableStream({ start(controller) {
      for (let at = 0; at < bytes.length; at += 65536) controller.enqueue(bytes.subarray(at, at + 65536));
      controller.close();
    } }));
  });
  try {
    const buffer = s.send(request(), bytes.length);
    const result = await s.reply;
    assert.equal(result.ok, true);
    assert.equal(result.length, bytes.length);
    assert.equal(result.body, '');
    assert.deepEqual(Buffer.from(buffer), Buffer.from(bytes));
    assert.equal(s.released.length, 1);
  } finally { s.dom.window.close(); }
});

test('upload passes shared bytes to fetch and waits for the server acknowledgement', async () => {
  let acknowledge;
  const server = new Promise(resolve => { acknowledge = resolve; });
  let sent;
  const s = setup(async (url, options) => { sent = Buffer.from(options.body); return server; });
  try {
    const buffer = new s.dom.window.ArrayBuffer(256);
    new Uint8Array(buffer).set(Uint8Array.from({ length: 256 }, (_, i) => i));
    s.shared(request({ method: 'PUT', bodyKind: 'shared', bodyLength: 256, responseKind: 'json' }), buffer);
    assert.deepEqual(sent, Buffer.from(new Uint8Array(buffer)));
    assert.equal(s.released.length, 0);
    acknowledge(new Response('{"written":256}'));
    const result = await s.reply;
    assert.equal(JSON.parse(result.body).written, 256);
    assert.equal(result.binary, false);
    assert.equal(s.released.length, 1);
  } finally { s.dom.window.close(); }
});

test('oversized responses cancel the stream and release their buffer', async () => {
  let cancelled = false;
  const s = setup(async () => new Response(new ReadableStream({
    start(controller) { controller.enqueue(new Uint8Array(17)); },
    cancel() { cancelled = true; }
  })));
  try {
    s.send(request(), 16);
    const result = await s.reply;
    assert.equal(result.ok, false);
    assert.match(result.error, /exceeds/);
    assert.equal(cancelled, true);
    assert.equal(s.released.length, 1);
  } finally { s.dom.window.close(); }
});

test('other origins are rejected before fetch and the buffer is released', async () => {
  let calls = 0;
  const s = setup(async () => { calls++; throw new Error(); });
  try {
    s.send(request({ url: 'https://other.ugdocker.link/api/v1/read' }), 16);
    assert.equal((await s.reply).ok, false);
    assert.equal(calls, 0);
    assert.equal(s.released.length, 1);
  } finally { s.dom.window.close(); }
});

test('login redirects and server errors remain failures with released buffers', async () => {
  for (const response of [{ type: 'opaqueredirect' }, new Response('{"error":"denied"}', { status: 403 })]) {
    const s = setup(async () => response);
    try {
      s.send(request(), 16);
      const result = await s.reply;
      assert.equal(result.ok, false);
      assert.equal(result.binary, false);
      assert.equal(result.loginRedirect === true || result.status === 403, true);
      assert.equal(s.released.length, 1);
    } finally { s.dom.window.close(); }
  }
});

test('JSON metadata works without a shared buffer', async () => {
  const s = setup(async () => new Response('{"name":"fixture"}'));
  try {
    s.message(request({ responseKind: 'json' }));
    assert.equal(JSON.parse((await s.reply).body).name, 'fixture');
    assert.equal(s.released.length, 0);
  } finally { s.dom.window.close(); }
});

test('invalid upload lengths fail without fetching', async () => {
  let calls = 0;
  const s = setup(async () => { calls++; throw new Error(); });
  try {
    s.send(request({ method: 'PUT', bodyKind: 'shared', bodyLength: 17, responseKind: 'json' }), 16);
    assert.equal((await s.reply).ok, false);
    assert.equal(calls, 0);
    assert.equal(s.released.length, 1);
  } finally { s.dom.window.close(); }
});
