// NovaGet Integration: hands downloads, links and videos to the NovaGet app through its native messaging host.
// Runs as the Chromium service worker and as the Firefox background script.
'use strict';

const api = typeof browser !== 'undefined' ? browser : chrome;
const HOST = 'com.novaget.nativehost';
const SETTINGS_TTL_MS = 60 * 1000;
const CLICK_WINDOW_MS = 4000;
const POST_WINDOW_MS = 30 * 1000;
const SEND_TIMEOUT_MS = 8000;
const MAX_LINKS = 20000;

let settings = null;
let settingsAt = 0;
let lastClick = null; // { alt, ctrl, shift, insert, at }
const postedUrls = new Map(); // url -> time of a POST that may have produced a download

// ---------------------------------------------------------------- native messaging

/** Sends one message to the app. Resolves to { ok, error, payload }; never throws. */
async function send(message) {
  try {
    const reply = await Promise.race([
      api.runtime.sendNativeMessage(HOST, message),
      new Promise((resolve) => setTimeout(() => resolve({ ok: false, error: 'timeout' }), SEND_TIMEOUT_MS)),
    ]);
    return reply && typeof reply === 'object' ? reply : { ok: false, error: 'no reply' };
  } catch (e) {
    return { ok: false, error: String((e && e.message) || e), unreachable: true };
  }
}

async function getSettings(force) {
  if (!force && settings && Date.now() - settingsAt < SETTINGS_TTL_MS) {
    return settings;
  }

  const reply = await send({ type: settings ? 'getSettings' : 'hello' });
  if (reply.ok && reply.payload) {
    const changed = !settings || settings.revision !== reply.payload.revision;
    settings = reply.payload;
    settingsAt = Date.now();
    await api.storage.local.set({ cachedSettings: settings });
    if (changed) {
      await updateMenus();
    }
  } else if (!settings) {
    const stored = await api.storage.local.get('cachedSettings');
    settings = stored.cachedSettings || null;
  }

  return settings;
}

// ---------------------------------------------------------------- capture rules

function wildcard(pattern) {
  const escaped = String(pattern).trim().replace(/[.+^${}()|[\]\\]/g, '\\$&').replace(/\*/g, '.*').replace(/\?/g, '.');
  return new RegExp(`^${escaped}$`, 'i');
}

function hostMatches(pattern, host) {
  pattern = String(pattern).trim().toLowerCase();
  host = String(host).toLowerCase();
  if (!pattern) return false;
  if (pattern.startsWith('*.')) {
    const bare = pattern.slice(2);
    return host === bare || host.endsWith(`.${bare}`) || wildcard(pattern).test(host);
  }
  return pattern.includes('*') || pattern.includes('?') ? wildcard(pattern).test(host) : host === pattern;
}

function extensionOf(name) {
  const base = String(name || '').split(/[\\/]/).pop();
  const dot = base.lastIndexOf('.');
  return dot > 0 && dot < base.length - 1 ? base.slice(dot + 1).toLowerCase() : '';
}

function nameFromUrl(url) {
  try {
    const path = new URL(url).pathname;
    return decodeURIComponent(path.split('/').pop() || '');
  } catch {
    return '';
  }
}

function isSupportedUrl(url) {
  return /^(https?|ftps?):\/\//i.test(String(url || ''));
}

/** Modifier names from the app: none, alt, ctrl, shift, insert, ctrlAlt, ctrlShift, altShift. */
function keysMatch(name, click) {
  const want = {
    none: null,
    alt: { alt: true },
    ctrl: { ctrl: true },
    shift: { shift: true },
    insert: { insert: true },
    ctrlAlt: { ctrl: true, alt: true },
    ctrlShift: { ctrl: true, shift: true },
    altShift: { alt: true, shift: true },
  }[name];
  if (!want || !click) return false;
  return ['alt', 'ctrl', 'shift', 'insert'].every((k) => Boolean(want[k]) === Boolean(click[k]));
}

async function captureOffFor(url) {
  const { captureOff, offSites } = await api.storage.local.get(['captureOff', 'offSites']);
  if (captureOff) return true;
  try {
    const host = new URL(url).hostname;
    return Array.isArray(offSites) && offSites.some((site) => hostMatches(site, host));
  } catch {
    return false;
  }
}

/** Should this browser download go to NovaGet? Returns 'force', 'capture' or null. */
async function captureDecision(url, fileName) {
  if (!isSupportedUrl(url)) return null; // blob:, data:, file: stay in the browser
  const s = await getSettings(false);
  if (!s || !s.enabled || !s.capture) return null;

  const click = lastClick && Date.now() - lastClick.at < CLICK_WINDOW_MS ? lastClick : null;
  if (keysMatch(s.forceKey, click)) return 'force';
  if (keysMatch(s.preventKey, click)) return null;
  if (await captureOffFor(url)) return null;

  const posted = postedUrls.get(url);
  if (posted && Date.now() - posted < POST_WINDOW_MS) return null; // form results need the browser's POST

  let host = '';
  try {
    host = new URL(url).hostname;
  } catch {
    return null;
  }
  if ((s.excludedSites || []).some((site) => hostMatches(site, host))) return null;
  if ((s.excludedAddresses || []).some((pattern) => wildcard(pattern).test(url))) return null;

  const ext = extensionOf(fileName) || extensionOf(nameFromUrl(url));
  return ext && (s.fileTypes || []).some((pattern) => wildcard(pattern).test(ext)) ? 'capture' : null;
}

// ---------------------------------------------------------------- request details

async function cookiesFor(url) {
  try {
    const cookies = await api.cookies.getAll({ url });
    const header = cookies.map((c) => `${c.name}=${c.value}`).join('; ');
    return header.length > 0 && header.length <= 64 * 1024 ? header : undefined;
  } catch {
    return undefined;
  }
}

async function requestInfo(url, referrer) {
  return {
    referrer: isSupportedUrl(referrer) ? referrer : undefined,
    cookies: await cookiesFor(url),
    userAgent: navigator.userAgent,
  };
}

/** Hands a download to the app; true when it took it. */
async function sendDownload({ url, finalUrl, referrer, fileName, fileSize, mime, pageTitle, forced }) {
  const message = {
    type: 'download',
    url,
    finalUrl: finalUrl && finalUrl !== url && isSupportedUrl(finalUrl) ? finalUrl : undefined,
    fileName: fileName ? String(fileName).split(/[\\/]/).pop() : undefined,
    fileSize: typeof fileSize === 'number' && fileSize > 0 ? fileSize : undefined,
    mime: mime || undefined,
    pageTitle: pageTitle || undefined,
    forced: Boolean(forced),
    ...(await requestInfo(finalUrl || url, referrer)),
  };
  const reply = await send(message);
  return Boolean(reply.ok);
}

// ---------------------------------------------------------------- download capture

async function takeOver(item, decision) {
  const accepted = await sendDownload({
    url: item.url,
    finalUrl: item.finalUrl,
    referrer: item.referrer,
    fileName: item.filename,
    fileSize: item.totalBytes || item.fileSize,
    mime: item.mime,
    forced: decision === 'force',
  });
  if (accepted) {
    try {
      await api.downloads.cancel(item.id);
    } catch {
      // already finished or gone
    }
    try {
      await api.downloads.erase({ id: item.id });
    } catch {
      // nothing to erase
    }
  }
  return accepted;
}

if (api.downloads.onDeterminingFilename) {
  // Chromium: the download waits for our answer, so it continues in the browser whenever NovaGet can't take it.
  api.downloads.onDeterminingFilename.addListener((item, suggest) => {
    (async () => {
      let handled = false;
      try {
        const decision = await captureDecision(item.finalUrl || item.url, item.filename);
        if (decision) {
          handled = await takeOver(item, decision);
        }
      } finally {
        if (!handled) suggest();
      }
    })();
    return true; // we call suggest() asynchronously
  });
} else {
  // Firefox: the download has started; cancel it only once NovaGet has accepted it.
  api.downloads.onCreated.addListener(async (item) => {
    const decision = await captureDecision(item.url, item.filename);
    if (decision) {
      await takeOver(item, decision);
    }
  });
}

// Remember POSTs: a download that answers a form must stay in the browser (NovaGet downloads with GET).
api.webRequest.onBeforeRequest.addListener(
  (details) => {
    if (details.method === 'POST') {
      postedUrls.set(details.url, Date.now());
      if (postedUrls.size > 200) {
        const oldest = postedUrls.keys().next().value;
        postedUrls.delete(oldest);
      }
    }
  },
  { urls: ['<all_urls>'], types: ['main_frame', 'sub_frame'] },
);

// ---------------------------------------------------------------- media detection (the "Download this video" panel)

const MEDIA_MIN_BYTES = 300 * 1024;
const MAX_MEDIA_PER_TAB = 40;
const MANIFEST_TYPES = ['application/vnd.apple.mpegurl', 'application/x-mpegurl', 'audio/mpegurl', 'audio/x-mpegurl', 'application/dash+xml'];
const MEDIA_EXTENSIONS = ['mp4', 'm4v', 'webm', 'mov', 'mkv', 'flv', 'ts', 'm4a', 'mp3', 'aac', 'ogg', 'ogv', 'oga', 'opus', 'wav'];
const SEGMENT_EXTENSIONS = ['m4s', 'cmfv', 'cmfa']; // pieces of a stream, only offered through their playlist
const mediaLoads = new Map(); // tabId -> Promise<{ items: Map(url -> item), protected: boolean }>

function headerValue(headers, name) {
  const found = (headers || []).find((h) => h.name.toLowerCase() === name);
  return found ? String(found.value || '') : '';
}

/** The whole resource's size: Content-Range's total for a 206, else Content-Length. */
function resourceSize(headers) {
  const total = /\/(\d+)\s*$/.exec(headerValue(headers, 'content-range'));
  if (total) return Number(total[1]);
  const length = Number(headerValue(headers, 'content-length'));
  return Number.isFinite(length) ? length : 0;
}

/** A response worth offering in the panel, or null. Playlists always count; files from about 300 KB. */
function classifyMedia(url, contentType, size, viaScript) {
  const mime = String(contentType || '').split(';')[0].trim().toLowerCase();
  const ext = extensionOf(nameFromUrl(url));
  if (SEGMENT_EXTENSIONS.includes(ext)) return null;
  if (MANIFEST_TYPES.includes(mime) || ext === 'm3u8' || ext === 'mpd') {
    const dash = ext === 'mpd' || mime === 'application/dash+xml';
    return { url, mime, size: 0, manifest: true, kind: dash ? 'dash' : 'hls', viaScript };
  }
  const isMedia = mime.startsWith('video/') || mime.startsWith('audio/');
  if ((!isMedia && !MEDIA_EXTENSIONS.includes(ext)) || size < MEDIA_MIN_BYTES) return null;
  const audio = mime.startsWith('audio/') || ['m4a', 'mp3', 'aac', 'oga', 'opus', 'wav'].includes(ext);
  const segment = mime === 'video/mp2t' || ext === 'ts';
  return { url, mime, size, manifest: false, kind: audio ? 'audio' : segment ? 'ts' : 'video', viaScript };
}

async function loadTabMedia(tabId) {
  const entry = { items: new Map(), protected: false };
  try {
    const key = `media:${tabId}`;
    const saved = (await api.storage.session.get(key))[key];
    if (saved) {
      (saved.items || []).forEach((item) => entry.items.set(item.url, item));
      entry.protected = Boolean(saved.protected);
    }
  } catch {
    // storage.session is unavailable: the list lives as long as the background page
  }
  return entry;
}

/** The media seen in a tab (kept in session storage, so it survives the service worker being stopped). */
function tabMedia(tabId) {
  if (!mediaLoads.has(tabId)) mediaLoads.set(tabId, loadTabMedia(tabId));
  return mediaLoads.get(tabId);
}

function saveTabMedia(tabId, entry) {
  try {
    api.storage.session.set({ [`media:${tabId}`]: { items: [...entry.items.values()], protected: entry.protected } }).catch(() => {});
  } catch {
    // not available
  }
}

function clearTabMedia(tabId) {
  mediaLoads.delete(tabId);
  try {
    api.storage.session.remove(`media:${tabId}`).catch(() => {});
  } catch {
    // not available
  }
}

/** Playlists first, then files by size. */
function mediaList(entry) {
  return [...entry.items.values()]
    .sort((a, b) => Number(b.manifest) - Number(a.manifest) || (b.size || 0) - (a.size || 0))
    .map(({ url, mime, size, manifest, kind }) => ({ url, mime, size, manifest, kind }));
}

function notifyTab(tabId, entry) {
  try {
    Promise.resolve(api.tabs.sendMessage(tabId, { kind: 'media', items: mediaList(entry), protected: entry.protected })).catch(() => {});
  } catch {
    // no content script in that tab
  }
}

async function addMedia(tabId, item) {
  const entry = await tabMedia(tabId);
  if (entry.items.has(item.url)) return;
  const values = [...entry.items.values()];
  // A page that streams (HLS/DASH) fetches its pieces from script; offer the playlist, not the pieces.
  if (!item.manifest && (item.kind === 'ts' || item.viaScript) && values.some((other) => other.manifest)) return;
  if (item.manifest) {
    for (const other of values) {
      if (!other.manifest && (other.kind === 'ts' || other.viaScript)) entry.items.delete(other.url);
    }
  }
  entry.items.set(item.url, item);
  while (entry.items.size > MAX_MEDIA_PER_TAB) {
    entry.items.delete(entry.items.keys().next().value);
  }
  saveTabMedia(tabId, entry);
  notifyTab(tabId, entry);
}

api.webRequest.onHeadersReceived.addListener(
  (details) => {
    if (details.tabId < 0 || details.statusCode < 200 || details.statusCode >= 300 || !/^https?:/i.test(details.url)) return;
    const item = classifyMedia(
      details.url,
      headerValue(details.responseHeaders, 'content-type'),
      resourceSize(details.responseHeaders),
      details.type === 'xmlhttprequest',
    );
    if (item) addMedia(details.tabId, item);
  },
  { urls: ['<all_urls>'], types: ['media', 'xmlhttprequest', 'object', 'other'] },
  ['responseHeaders'],
);

api.tabs.onUpdated.addListener((tabId, change) => {
  if (change.status === 'loading' && change.url) clearTabMedia(tabId);
});
api.tabs.onRemoved.addListener((tabId) => clearTabMedia(tabId));

/** What the panel needs from the app's settings (Options → General → Edit panel for web players). */
function panelSettings(s, tab) {
  let pageHost = '';
  try {
    pageHost = tab && tab.url ? new URL(tab.url).hostname : '';
  } catch {
    // not a web page
  }
  return {
    show: Boolean(s && s.enabled && s.panelShow),
    inPopups: Boolean(s && s.panelInPopups),
    position: (s && s.panelPosition) || 'topRight',
    onHover: Boolean(s && s.panelOnHover),
    excludedSites: (s && s.panelExcludedSites) || [],
    pageHost,
  };
}

/** Sends the chosen media (first) and the page's other media to the app. */
async function downloadMedia(tab, url, title, width, height) {
  if (!tab || !isSupportedUrl(url)) return { ok: false };
  const entry = await tabMedia(tab.id);
  if (entry.protected) return { ok: false, protected: true }; // ground rule: DRM is never downloaded
  const listed = mediaList(entry);
  const chosen = listed.find((item) => item.url === url) || { url, manifest: /\.(m3u8|mpd)([?#]|$)/i.test(url) };
  const items = [chosen, ...listed.filter((item) => item.url !== url).slice(0, 20)].map((item) => ({
    url: item.url,
    mime: item.mime || undefined,
    size: item.size > 0 ? item.size : undefined,
    width: item === chosen && width > 0 ? width : undefined,
    height: item === chosen && height > 0 ? height : undefined,
    manifest: Boolean(item.manifest),
  }));
  const pageUrl = tab.url || '';
  const reply = await send({
    type: 'media',
    pageUrl: isSupportedUrl(pageUrl) ? pageUrl : undefined,
    pageTitle: title || tab.title || undefined,
    items,
    ...(await requestInfo(chosen.url, pageUrl)),
  });
  return { ok: Boolean(reply.ok), error: reply.error };
}

// ---------------------------------------------------------------- context menus

const MENU_LINK = 'novaget-link';
const MENU_ALL = 'novaget-all';
const MENU_MEDIA = 'novaget-media';

async function updateMenus() {
  const s = settings;
  await api.contextMenus.removeAll();
  if (!s || !s.enabled) return;
  if (s.menuDownloadLink) {
    api.contextMenus.create({ id: MENU_LINK, title: api.i18n.getMessage('menuDownloadLink'), contexts: ['link'] });
  }
  if (s.menuDownloadAll) {
    api.contextMenus.create({ id: MENU_ALL, title: api.i18n.getMessage('menuDownloadAll'), contexts: ['page', 'selection'] });
  }
  if (s.menuDownloadVideo) {
    api.contextMenus.create({ id: MENU_MEDIA, title: api.i18n.getMessage('menuDownloadVideo'), contexts: ['video', 'audio'] });
  }
}

/** Runs in the page: every link, image and media source (in the selection, when there is one). */
function collectLinks(selectionOnly, maxLinks) {
  const found = [];
  const seen = new Set();
  const selection = window.getSelection();
  const ranges = [];
  if (selectionOnly && selection) {
    for (let i = 0; i < selection.rangeCount; i++) ranges.push(selection.getRangeAt(i));
  }
  const inSelection = (node) => ranges.length === 0 || ranges.some((range) => range.intersectsNode(node));
  const add = (url, text, kind) => {
    if (!url || found.length >= maxLinks) return;
    let absolute;
    try {
      absolute = new URL(url, document.baseURI).href;
    } catch {
      return;
    }
    if (!/^(https?|ftps?):/i.test(absolute) || seen.has(absolute)) return;
    seen.add(absolute);
    found.push({ url: absolute, text: (text || '').trim().slice(0, 1000), kind });
  };
  document.querySelectorAll('a[href], area[href]').forEach((a) => inSelection(a) && add(a.href, a.textContent || a.title, 'link'));
  document.querySelectorAll('img[src]').forEach((img) => inSelection(img) && add(img.currentSrc || img.src, img.alt, 'image'));
  document.querySelectorAll('video[src], audio[src], source[src]').forEach((m) => inSelection(m) && add(m.src, '', 'media'));
  return found;
}

api.contextMenus.onClicked.addListener(async (info, tab) => {
  const pageUrl = info.pageUrl || (tab && tab.url) || '';
  if (info.menuItemId === MENU_LINK && info.linkUrl) {
    const ok = isSupportedUrl(info.linkUrl) && (await sendDownload({ url: info.linkUrl, referrer: pageUrl, forced: true }));
    if (!ok) {
      api.downloads.download({ url: info.linkUrl }); // never lose the user's download
    }
  } else if (info.menuItemId === MENU_ALL && tab && tab.id !== undefined) {
    const results = await api.scripting.executeScript({
      target: { tabId: tab.id },
      func: collectLinks,
      args: [Boolean(info.selectionText), MAX_LINKS],
    });
    const links = (results && results[0] && results[0].result) || [];
    await send({
      type: 'links',
      pageUrl: isSupportedUrl(pageUrl) ? pageUrl : undefined,
      pageTitle: tab.title,
      links,
      ...(await requestInfo(pageUrl, pageUrl)),
    });
  } else if (info.menuItemId === MENU_MEDIA && tab && tab.id !== undefined) {
    // A player fed from script (blob: source) is offered through the playlist the page loaded.
    const url = isSupportedUrl(info.srcUrl) ? info.srcUrl : (mediaList(await tabMedia(tab.id))[0] || {}).url;
    if (url) await downloadMedia(tab, url);
  }
});

// ---------------------------------------------------------------- messages from the content script and popup

api.runtime.onMessage.addListener((message, sender, sendResponse) => {
  if (!message || typeof message !== 'object') return false;
  switch (message.kind) {
    case 'click':
      lastClick = {
        alt: Boolean(message.alt),
        ctrl: Boolean(message.ctrl),
        shift: Boolean(message.shift),
        insert: Boolean(message.insert),
        at: Date.now(),
      };
      return false;
    case 'status':
      (async () => {
        const reply = await send({ type: 'ping' });
        const s = await getSettings(true);
        sendResponse({ connected: Boolean(reply.ok), version: reply.ok && reply.payload ? reply.payload.version : null, settings: s });
      })();
      return true;
    case 'openApp':
      send({ type: 'openApp' }).then((reply) => sendResponse({ ok: Boolean(reply.ok) }));
      return true;
    case 'getMedia':
      (async () => {
        const tab = sender.tab;
        const entry = tab ? await tabMedia(tab.id) : null;
        sendResponse({
          items: entry ? mediaList(entry) : [],
          protected: Boolean(entry && entry.protected),
          panel: panelSettings(await getSettings(false), tab),
        });
      })();
      return true;
    case 'protected':
      if (sender.tab) {
        tabMedia(sender.tab.id).then((entry) => {
          if (!entry.protected) {
            entry.protected = true;
            saveTabMedia(sender.tab.id, entry);
            notifyTab(sender.tab.id, entry);
          }
        });
      }
      return false;
    case 'downloadMedia':
      downloadMedia(sender.tab, String(message.url || ''), message.title ? String(message.title).slice(0, 2048) : undefined,
        Number(message.width) || 0, Number(message.height) || 0).then(sendResponse);
      return true;
    case 'openOptions':
      send({ type: 'openOptions' });
      return false;
    case 'sendLogin':
      // Only from the popup (no tab): the user chose to hand this site's cookies to NovaGet's Site Grabber.
      if (sender.tab || !isSupportedUrl(message.url)) return false;
      (async () => {
        const cookies = await cookiesFor(message.url);
        const reply = cookies ? await send({ type: 'cookies', url: message.url, cookies }) : { ok: false };
        sendResponse({ ok: Boolean(reply.ok) });
      })();
      return true;
    default:
      return false;
  }
});

api.runtime.onInstalled.addListener(() => getSettings(true));
api.runtime.onStartup.addListener(() => getSettings(true));
