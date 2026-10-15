const { test } = require('node:test');
const assert = require('node:assert/strict');
const { readFileSync } = require('node:fs');
const { JSDOM } = require('jsdom');
const source = readFileSync(require('node:path').join(__dirname, '../UGREENRemoteDrive/Browser/DesktopAutomation.js'), 'utf8');
const wait = ms => new Promise(resolve => setTimeout(resolve, ms));
const tile = (name = 'ugreen-remote-drive', id = 'docker.shortcut.1') =>
  `<div class="list-item" moduleid="${id}"><div class="click-region"><img></div><p class="app-name">${name}</p></div>`;
const defaults = { expectedOrigin: 'https://remote.ugdocker.link', rememberLogin: true,
  autoOpen: true, tileName: 'ugreen-remote-drive', tileId: '', waitMilliseconds: 15 * 60 * 1000 };

function fixture(t, html, config = {}, url = 'https://nas.eur3.ug.link/desktop/#/login/account') {
  const dom = new JSDOM(html, { url, runScripts: 'outside-only', pretendToBeVisual: true });
  const { window } = dom;
  // jsdom has no layout engine. Supply rectangles only for rendered fixture elements.
  window.Element.prototype.getClientRects = function () {
    return this.closest('[hidden], [style*="display: none"]') ? [] : [{ width: 100, height: 40 }];
  };
  const messages = [];
  window.chrome = { webview: { postMessage: message => messages.push(message) } };
  const inject = () => window.eval(source.replace('__CONFIG__', JSON.stringify({ ...defaults, ...config })));
  t.after(() => window.close());
  return { window, document: window.document, messages, inject };
}

test('uses the NAS keep-login checkbox without reading login fields', t => {
  const f = fixture(t, '<input type="password"><div class="is-login"><div class="login-public-checkbox"><input type="checkbox"></div></div>');
  const password = f.document.querySelector('input[type=password]');
  Object.defineProperty(password, 'value', { get() { throw new Error('Credentials must never be read'); } });
  const checkbox = f.document.querySelector('input[type=checkbox]');
  assert.equal(f.inject(), true);
  assert.equal(checkbox.checked, true);
});

test('remember-login opt-out and a subsequent manual checkbox choice are respected', async t => {
  const html = '<div class="is-login"><div class="login-public-checkbox"><input type="checkbox"></div></div>';
  const f = fixture(t, html, { rememberLogin: false });
  f.inject();
  assert.equal(f.document.querySelector('input').checked, false);
  const g = fixture(t, html);
  g.inject();
  g.document.querySelector('input').click();
  g.document.body.appendChild(g.document.createElement('span'));
  await wait(150);
  assert.equal(g.document.querySelector('input').checked, false);
});

test('clicks the actual UGOS click-region, records its identity, and never clicks twice', async t => {
  const f = fixture(t, `<div class="apply-list">${tile()}</div>`);
  let clicks = 0;
  f.document.querySelector('.click-region').addEventListener('click', () => clicks++);
  f.inject();
  f.document.querySelector('.app-name').textContent = 'ugreen-remote-drive';
  f.window.dispatchEvent(new f.window.HashChangeEvent('hashchange'));
  await wait(150);
  f.inject(); // A SPA source notification must not issue a second click.
  assert.equal(clicks, 1);
  assert.equal(f.messages.at(-1).id, 'docker.shortcut.1');
  assert.equal(f.messages.at(-1).name, 'ugreen-remote-drive');
});

test('survives a slow login and a late SPA desktop, beyond the old four-second deadline', async t => {
  const f = fixture(t, '<div id="login"><input type="password"></div><div class="apply-list"></div>');
  let clicks = 0;
  f.document.addEventListener('click', e => { if (e.target.classList.contains('click-region')) clicks++; });
  f.inject();
  await wait(4200);
  f.document.querySelector('.apply-list').innerHTML = tile();
  await wait(150);
  assert.equal(clicks, 0, 'Do not open a tile while a login form is still visible');
  f.document.querySelector('#login').remove();
  await wait(150);
  assert.equal(clicks, 1);
});

test('ambiguous or hidden desktop tiles are not clicked', t => {
  const f = fixture(t, `<div class="apply-list">${tile()}${tile('ugreen-remote-drive', 'docker.shortcut.2')}</div>`);
  let clicks = 0;
  f.document.addEventListener('click', () => clicks++);
  f.inject();
  assert.equal(clicks, 0);
  const hidden = fixture(t, `<div class="apply-list" hidden>${tile()}</div>`);
  hidden.document.addEventListener('click', () => clicks++);
  hidden.inject();
  assert.equal(clicks, 0);
});

test('a learned module id selects a renamed tile; an absent id never falls back to another tile', t => {
  const f = fixture(t, `<div class="apply-list">${tile('Other name', 'known')}${tile()}</div>`, { tileId: 'known' });
  const clicked = [];
  f.document.addEventListener('click', e => clicked.push(e.target.closest('.list-item').getAttribute('moduleid')));
  f.inject();
  assert.deepEqual(clicked, ['known']);
  const g = fixture(t, `<div class="apply-list">${tile()}</div>`, { tileId: 'missing' });
  g.document.addEventListener('click', () => { throw new Error('Wrong tile'); });
  g.inject();
  assert.equal(g.window.__ugreenRemoteDriveDesktop.clicked, false);
});

test('exact shortcut links still work and restore their original target', t => {
  const f = fixture(t, '<a href="https://remote.ugdocker.link/api/ugreen/auth?ticket=fixture-only" target="original">Open</a>');
  const anchor = f.document.querySelector('a');
  let clicks = 0;
  anchor.addEventListener('click', e => { e.preventDefault(); clicks++; assert.equal(anchor.target, '_blank'); });
  f.inject();
  assert.equal(clicks, 1);
  assert.equal(anchor.target, 'original');
  assert.equal(f.messages.length, 1);
  assert.equal(f.messages[0].name, '', 'A standalone anchor clears an unrelated earlier tile selection');
  assert.equal(JSON.stringify(f.messages).includes('fixture-only'), false, 'Do not export a bootstrap query');
});

test('other origins, HTTP, API links, and automatic first-time discovery are refused', t => {
  for (const href of ['https://other.ugdocker.link/', 'http://remote.ugdocker.link/', 'https://remote.ugdocker.link/api/v1/list']) {
    const f = fixture(t, `<a href="${href}">Open</a>`);
    f.inject();
    assert.equal(f.window.__ugreenRemoteDriveDesktop.clicked, false);
  }
  const unknown = fixture(t, `<div class="apply-list">${tile()}</div>`, { expectedOrigin: null });
  unknown.inject();
  assert.equal(unknown.window.__ugreenRemoteDriveDesktop.clicked, false);
  const untrusted = fixture(t, `<div class="apply-list">${tile()}</div>`, {}, 'https://attacker.example/desktop/');
  assert.equal(untrusted.inject(), false);
});

test('disabling automatic opening leaves tiles untouched', t => {
  const f = fixture(t, `<div class="apply-list">${tile()}</div>`, { autoOpen: false });
  f.inject();
  assert.equal(f.window.__ugreenRemoteDriveDesktop.clicked, false);
});
