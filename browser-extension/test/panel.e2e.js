// End-to-end check of the "Download this video" panel in Chromium (section 23, Integration): the extension is
// loaded unpacked, the native host is replaced by a recorder inside the service worker, and three local pages are
// opened: a plain <video src=mp4>, an HLS player (playlist fetched from script, MediaSource video) and a DRM player.
// Needs Playwright with its Chromium; run with: node browser-extension/test/panel.e2e.js
'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs');
const http = require('node:http');
const os = require('node:os');
const path = require('node:path');

function loadPlaywright() {
  const candidates = ['playwright', path.join(path.dirname(process.execPath), '..', 'lib', 'node_modules', 'playwright'),
    path.join(path.dirname(process.execPath), 'node_modules', 'playwright')];
  for (const candidate of candidates) {
    try {
      return require(candidate);
    } catch {
      // try the next place
    }
  }
  return null;
}

const playwright = loadPlaywright();
if (!playwright) {
  console.log('SKIP: Playwright is not installed');
  process.exit(77);
}

const root = path.join(__dirname, '..');
const work = fs.mkdtempSync(path.join(os.tmpdir(), 'novaget-panel-'));
const ext = path.join(work, 'ext');
fs.cpSync(path.join(root, 'src'), ext, { recursive: true });
fs.copyFileSync(path.join(root, 'chromium', 'manifest.json'), path.join(ext, 'manifest.json'));

const pages = {
  '/video.html': '<!doctype html><title>Sample MP4 video</title><body style="margin:40px"><video src="/movie.mp4" width="640" height="360" controls muted></video>',
  '/hls.html': `<!doctype html><title>Sample HLS stream</title><body style="margin:40px"><video width="640" height="360" controls></video>
    <script>document.querySelector('video').src = URL.createObjectURL(new MediaSource()); fetch('/hls/master.m3u8');</script>`,
  '/drm.html': `<!doctype html><title>Protected stream</title><body style="margin:40px"><video width="640" height="360" controls></video>
    <script>document.querySelector('video').src = URL.createObjectURL(new MediaSource()); fetch('/hls/master.m3u8');
    navigator.requestMediaKeySystemAccess('org.w3.clearkey', [{ initDataTypes: ['cenc'], videoCapabilities: [{ contentType: 'video/mp4; codecs="avc1.42E01E"' }] }]).catch(() => {});</script>`,
};
const movie = Buffer.alloc(400 * 1024, 7);

const server = http.createServer((req, res) => {
  const url = new URL(req.url, 'http://localhost').pathname;
  if (pages[url]) {
    res.writeHead(200, { 'Content-Type': 'text/html' });
    res.end(pages[url]);
  } else if (url === '/movie.mp4') {
    res.writeHead(200, { 'Content-Type': 'video/mp4', 'Content-Length': movie.length });
    res.end(movie);
  } else if (url === '/hls/master.m3u8') {
    res.writeHead(200, { 'Content-Type': 'application/vnd.apple.mpegurl' });
    res.end('#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=800000,RESOLUTION=640x360\nlow.m3u8\n');
  } else {
    res.writeHead(404);
    res.end();
  }
});

async function main() {
  await new Promise((resolve) => server.listen(0, '127.0.0.1', resolve));
  const base = `http://127.0.0.1:${server.address().port}`;
  const context = await playwright.chromium.launchPersistentContext(path.join(work, 'profile'), {
    channel: 'chromium',
    headless: true,
    args: [`--disable-extensions-except=${ext}`, `--load-extension=${ext}`],
    viewport: { width: 900, height: 600 },
  });
  try {
    let [worker] = context.serviceWorkers();
    if (!worker) worker = await context.waitForEvent('serviceworker');

    // The native host, as the app would answer.
    await worker.evaluate(() => {
      self.sentToApp = [];
      chrome.runtime.sendNativeMessage = async (host, message) => {
        self.sentToApp.push(message);
        return message.type === 'hello' || message.type === 'getSettings'
          ? { ok: true, payload: { enabled: true, capture: true, revision: 'e2e', fileTypes: ['zip'], panelShow: true, panelInPopups: true, panelPosition: 'topRight', panelOnHover: false, panelExcludedSites: [] } }
          : { ok: true, payload: { accepted: true } };
      };
    });

    const open = async (name) => {
      const page = await context.newPage();
      await page.goto(`${base}/${name}.html`);
      await page.waitForFunction(() => document.querySelector('novaget-video-panel'), null, { timeout: 10000 });
      await page.waitForTimeout(500);
      const panel = await page.$('novaget-video-panel');
      const video = await (await page.$('video')).boundingBox();
      return { page, panel, box: await panel.boundingBox(), video };
    };

    // 1. A plain video: the panel sits in the top-right corner; its main button sends the file with the page title.
    const plain = await open('video');
    assert.ok(Math.abs(plain.box.x + plain.box.width - (plain.video.x + plain.video.width - 8)) <= 2, 'top-right corner');
    assert.ok(Math.abs(plain.box.y - (plain.video.y + 8)) <= 2, 'near the top');
    await plain.page.mouse.click(plain.box.x + 40, plain.box.y + plain.box.height / 2);
    await plain.page.waitForTimeout(800);
    const sent = (await worker.evaluate(() => self.sentToApp)).filter((m) => m.type === 'media');
    assert.equal(sent.length, 1, 'one media message');
    assert.equal(sent[0].items[0].url, `${base}/movie.mp4`);
    assert.equal(sent[0].pageTitle, 'Sample MP4 video');
    assert.equal(sent[0].items[0].manifest, false);

    // × hides the panel for this video.
    await plain.page.mouse.click(plain.box.x + plain.box.width - 10, plain.box.y + plain.box.height / 2);
    await plain.page.waitForTimeout(300);
    assert.equal(await plain.page.$('novaget-video-panel'), null, 'hidden after ×');

    // 2. An HLS player: offered through the playlist the page loaded.
    const hls = await open('hls');
    await hls.page.mouse.click(hls.box.x + 40, hls.box.y + hls.box.height / 2);
    await hls.page.waitForTimeout(800);
    const hlsSent = (await worker.evaluate(() => self.sentToApp)).filter((m) => m.type === 'media');
    assert.equal(hlsSent.length, 2);
    assert.equal(hlsSent[1].items[0].url, `${base}/hls/master.m3u8`);
    assert.equal(hlsSent[1].items[0].manifest, true);
    assert.equal(hlsSent[1].pageTitle, 'Sample HLS stream');

    // 3. A DRM player: the panel is there but disabled, and nothing reaches the app.
    const drm = await open('drm');
    assert.equal(await drm.page.evaluate(() => document.documentElement.hasAttribute('data-novaget-eme')), true);
    await drm.page.mouse.click(drm.box.x + 40, drm.box.y + drm.box.height / 2);
    await drm.page.waitForTimeout(800);
    assert.equal((await worker.evaluate(() => self.sentToApp)).filter((m) => m.type === 'media').length, 2, 'protected media is never sent');
    const session = await worker.evaluate(() => chrome.storage.session.get(null));
    assert.ok(Object.values(session).some((entry) => entry.protected), 'the tab is marked protected');

    console.log('panel e2e: all checks passed');
  } finally {
    await context.close();
    server.close();
    fs.rmSync(work, { recursive: true, force: true });
  }
}

main().catch((e) => {
  console.error(e);
  process.exit(1);
});
