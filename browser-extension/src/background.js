// NovaGet Integration: hands downloads and links to the NovaGet app through its native messaging host.
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
  } else if (info.menuItemId === MENU_MEDIA && info.srcUrl) {
    if (!isSupportedUrl(info.srcUrl)) return; // blob: sources are handled by the video panel
    await send({
      type: 'media',
      pageUrl: isSupportedUrl(pageUrl) ? pageUrl : undefined,
      pageTitle: tab && tab.title,
      items: [{ url: info.srcUrl }],
      ...(await requestInfo(info.srcUrl, pageUrl)),
    });
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
    default:
      return false;
  }
});

api.runtime.onInstalled.addListener(() => getSettings(true));
api.runtime.onStartup.addListener(() => getSettings(true));
