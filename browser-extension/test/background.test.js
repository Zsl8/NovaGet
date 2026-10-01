// Tests for the background script's media detection and messaging, against a fake extension API.
// Run with: node browser-extension/test/background.test.js (the xUnit suite runs it when node is installed).
'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

function createApi() {
  const listeners = {};
  const event = (name) => ({ addListener: (fn) => (listeners[name] = fn) });
  const sent = { native: [], tabs: [] };
  const store = { local: {}, session: {} };
  const area = (bucket) => ({
    get: async (keys) => {
      const list = Array.isArray(keys) ? keys : [keys];
      return Object.fromEntries(list.filter((k) => k in store[bucket]).map((k) => [k, structuredClone(store[bucket][k])]));
    },
    set: async (values) => Object.assign(store[bucket], structuredClone(values)),
    remove: async (key) => delete store[bucket][key],
  });
  const api = {
    runtime: {
      sendNativeMessage: async (host, message) => {
        sent.native.push(message);
        if (message.type === 'hello' || message.type === 'getSettings') {
          return { ok: true, payload: { enabled: true, capture: true, revision: '1', panelShow: true, panelInPopups: true, panelPosition: 'topLeft', panelOnHover: false, panelExcludedSites: ['*.blocked.example'] } };
        }
        return { ok: true, payload: { accepted: true } };
      },
      onMessage: event('message'),
      onInstalled: event('installed'),
      onStartup: event('startup'),
    },
    downloads: { onDeterminingFilename: event('determining'), cancel: async () => {}, erase: async () => {}, download: async () => {} },
    webRequest: { onBeforeRequest: event('beforeRequest'), onHeadersReceived: event('headers') },
    tabs: { onUpdated: event('tabUpdated'), onRemoved: event('tabRemoved'), sendMessage: async (tabId, message) => sent.tabs.push({ tabId, message }) },
    contextMenus: { onClicked: event('menu'), removeAll: async () => {}, create: () => {} },
    cookies: { getAll: async () => [{ name: 'sid', value: 'abc' }] },
    storage: { local: area('local'), session: area('session') },
    i18n: { getMessage: (key) => key },
    scripting: { executeScript: async () => [] },
  };
  return { api, listeners, sent, store };
}

function load() {
  const fake = createApi();
  const context = vm.createContext({ chrome: fake.api, navigator: { userAgent: 'TestAgent/1.0' }, setTimeout, clearTimeout, URL, console, structuredClone });
  vm.runInContext(fs.readFileSync(path.join(__dirname, '..', 'src', 'background.js'), 'utf8'), context, { filename: 'background.js' });
  return fake;
}

const flush = () => new Promise((resolve) => setTimeout(resolve, 0));

/** Values from the script's realm, as plain JSON (arrays there have another Array prototype). */
const plain = (value) => (value === undefined ? undefined : JSON.parse(JSON.stringify(value)));

function response(url, type, headers, tabId = 7, statusCode = 200) {
  return { url, type, tabId, statusCode, responseHeaders: Object.entries(headers).map(([name, value]) => ({ name, value })) };
}

function ask(fake, message, tab = { id: 7, url: 'https://video.example.com/watch?v=1', title: 'A talk' }) {
  return new Promise((resolve) => {
    const async = fake.listeners.message(message, { tab }, resolve);
    if (!async) resolve(undefined);
  });
}

const tests = [];
const test = (name, fn) => tests.push([name, fn]);

test('playlists and large media files are offered, small files and stream pieces are not', async () => {
  const fake = load();
  const headers = fake.listeners.headers;
  headers(response('https://cdn.example.com/a/master.m3u8?token=1', 'xmlhttprequest', { 'Content-Type': 'application/vnd.apple.mpegurl' }));
  headers(response('https://cdn.example.com/a/seg1.ts', 'xmlhttprequest', { 'Content-Type': 'video/mp2t', 'Content-Length': '900000' }));
  headers(response('https://cdn.example.com/a/v.m4s', 'xmlhttprequest', { 'Content-Type': 'video/iso.segment', 'Content-Length': '900000' }));
  headers(response('https://files.example.com/movie.mp4', 'media', { 'Content-Type': 'video/mp4', 'Content-Range': 'bytes 0-1023/52428800' }));
  headers(response('https://files.example.com/tiny.mp4', 'media', { 'Content-Type': 'video/mp4', 'Content-Length': '1000' }));
  headers(response('https://files.example.com/song.mp3', 'media', { 'Content-Type': 'audio/mpeg', 'Content-Length': '4000000' }));
  headers(response('https://files.example.com/broken.mp4', 'media', { 'Content-Type': 'video/mp4', 'Content-Length': '9000000' }, 7, 404));
  headers(response('https://cdn.example.com/b/manifest.mpd', 'xmlhttprequest', { 'Content-Type': 'application/dash+xml' }));
  headers(response('https://files.example.com/other-tab.mp4', 'media', { 'Content-Type': 'video/mp4', 'Content-Length': '9000000' }, -1));
  await flush();

  const reply = await ask(fake, { kind: 'getMedia' });
  assert.deepEqual(plain(reply.items.map((i) => [i.url, i.manifest, i.kind, i.size])), [
    ['https://cdn.example.com/a/master.m3u8?token=1', true, 'hls', 0],
    ['https://cdn.example.com/b/manifest.mpd', true, 'dash', 0],
    ['https://files.example.com/movie.mp4', false, 'video', 52428800],
    ['https://files.example.com/song.mp3', false, 'audio', 4000000],
  ]);
  assert.equal(reply.protected, false);
  assert.deepEqual(
    plain(reply.panel),
    { show: true, inPopups: true, position: 'topLeft', onHover: false, excludedSites: ['*.blocked.example'], pageHost: 'video.example.com' },
  );
  const pushed = fake.sent.tabs.filter((s) => s.tabId === 7 && s.message.kind === 'media');
  assert.ok(pushed.length >= 4, 'the content script hears about new media');
});

test('pieces fetched before the playlist are dropped once it arrives', async () => {
  const fake = load();
  fake.listeners.headers(response('https://cdn.example.com/s/0.ts', 'xmlhttprequest', { 'Content-Type': 'video/mp2t', 'Content-Length': '800000' }));
  await flush();
  fake.listeners.headers(response('https://cdn.example.com/s/index.m3u8', 'xmlhttprequest', { 'Content-Type': 'application/x-mpegURL' }));
  await flush();

  const reply = await ask(fake, { kind: 'getMedia' });
  assert.deepEqual(plain(reply.items.map((i) => i.url)), ['https://cdn.example.com/s/index.m3u8']);
});

test('the chosen item goes to the app first, with the page title and cookies', async () => {
  const fake = load();
  fake.listeners.headers(response('https://cdn.example.com/a/master.m3u8', 'xmlhttprequest', { 'Content-Type': 'application/vnd.apple.mpegurl' }));
  fake.listeners.headers(response('https://files.example.com/movie.mp4', 'media', { 'Content-Type': 'video/mp4', 'Content-Length': '9000000' }));
  await flush();

  const reply = await ask(fake, { kind: 'downloadMedia', url: 'https://files.example.com/movie.mp4', title: 'My video', width: 1280, height: 720 });

  assert.equal(reply.ok, true);
  const message = fake.sent.native.find((m) => m.type === 'media');
  assert.equal(message.pageTitle, 'My video');
  assert.equal(message.pageUrl, 'https://video.example.com/watch?v=1');
  assert.equal(message.cookies, 'sid=abc');
  assert.equal(message.userAgent, 'TestAgent/1.0');
  assert.deepEqual(plain(message.items.map((i) => [i.url, i.manifest, i.width ?? null, i.height ?? null])), [
    ['https://files.example.com/movie.mp4', false, 1280, 720],
    ['https://cdn.example.com/a/master.m3u8', true, null, null],
  ]);
});

test('a protected page sends nothing', async () => {
  const fake = load();
  fake.listeners.headers(response('https://cdn.example.com/drm/master.m3u8', 'xmlhttprequest', { 'Content-Type': 'application/vnd.apple.mpegurl' }));
  await flush();
  await ask(fake, { kind: 'protected' });
  await flush();

  const reply = await ask(fake, { kind: 'downloadMedia', url: 'https://cdn.example.com/drm/master.m3u8' });

  assert.deepEqual(plain(reply), { ok: false, protected: true });
  assert.equal(fake.sent.native.filter((m) => m.type === 'media').length, 0);
  assert.equal((await ask(fake, { kind: 'getMedia' })).protected, true);
  assert.ok(fake.sent.tabs.some((s) => s.message.protected === true), 'the panels are told');
});

test('unsupported addresses are refused', async () => {
  const fake = load();
  const reply = await ask(fake, { kind: 'downloadMedia', url: 'javascript:alert(1)' });
  assert.equal(reply.ok, false);
  assert.equal(fake.sent.native.filter((m) => m.type === 'media').length, 0);
});

test('a new page in the tab starts a new list; closing the tab forgets it', async () => {
  const fake = load();
  fake.listeners.headers(response('https://files.example.com/movie.mp4', 'media', { 'Content-Type': 'video/mp4', 'Content-Length': '9000000' }));
  await flush();
  assert.equal(fake.store.session['media:7'].items.length, 1, 'kept in session storage');

  fake.listeners.tabUpdated(7, { status: 'loading', url: 'https://video.example.com/next' });
  await flush();
  assert.deepEqual(plain((await ask(fake, { kind: 'getMedia' })).items), []);
  assert.equal(fake.store.session['media:7'], undefined);
});

test('the list survives the background being restarted', async () => {
  const first = load();
  first.listeners.headers(response('https://files.example.com/movie.mp4', 'media', { 'Content-Type': 'video/mp4', 'Content-Length': '9000000' }));
  await flush();

  const second = load();
  second.store.session = first.store.session;
  Object.assign(second.api.storage.session, {
    get: async (key) => ({ [key]: structuredClone(first.store.session[key]) }),
  });
  const reply = await ask(second, { kind: 'getMedia' });
  assert.deepEqual(plain(reply.items.map((i) => i.url)), ['https://files.example.com/movie.mp4']);
});

(async () => {
  let failed = 0;
  for (const [name, fn] of tests) {
    try {
      await fn();
      console.log(`ok - ${name}`);
    } catch (e) {
      failed++;
      console.log(`not ok - ${name}\n${e && e.stack}`);
    }
  }
  console.log(`${tests.length - failed}/${tests.length} passed`);
  process.exit(failed ? 1 : 0);
})();
