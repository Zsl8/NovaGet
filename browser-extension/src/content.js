// NovaGet Integration content script:
// - tells the background which keys were held when a link was clicked (Options → General prevent/force keys);
// - shows the "Download this video" panel on web players, in a closed shadow root so the site's CSS can't reach it;
// - notices DRM (Encrypted Media Extensions) and then shows the panel disabled as "Protected content".
'use strict';

(() => {
  const api = typeof browser !== 'undefined' ? browser : chrome;

  // ------------------------------------------------------------ prevent/force keys

  let insertHeld = false;
  window.addEventListener('keydown', (e) => {
    if (e.key === 'Insert') insertHeld = true;
  }, true);
  window.addEventListener('keyup', (e) => {
    if (e.key === 'Insert') insertHeld = false;
  }, true);
  window.addEventListener('blur', () => {
    insertHeld = false;
  });

  const report = (e) => {
    const target = e.target && e.target.closest ? e.target.closest('a[href], area[href]') : null;
    if (!target) return;
    try {
      api.runtime.sendMessage({ kind: 'click', alt: e.altKey, ctrl: e.ctrlKey, shift: e.shiftKey, insert: insertHeld });
    } catch {
      // the extension was reloaded; nothing to report to
    }
  };
  window.addEventListener('click', report, true);
  window.addEventListener('auxclick', report, true);

  // ------------------------------------------------------------ "Download this video" panel

  const MIN_WIDTH = 160;
  const MIN_HEIGHT = 90;
  const panels = new Map(); // video element -> panel
  const closedVideos = new WeakSet();
  const watchedVideos = new WeakSet();
  let media = [];
  let settings = null;
  let offSites = [];
  let isProtected = false;
  let pointer = null;
  let layoutQueued = false;

  const t = (key, ...subs) => api.i18n.getMessage(key, subs) || key;

  const message = (body) => {
    try {
      return Promise.resolve(api.runtime.sendMessage(body)).catch(() => null);
    } catch {
      return Promise.resolve(null); // the extension was reloaded
    }
  };

  function hostMatches(pattern, host) {
    pattern = String(pattern || '').trim().toLowerCase();
    host = String(host || '').toLowerCase();
    if (!pattern || !host) return false;
    const bare = pattern.startsWith('*.') ? pattern.slice(2) : null;
    if (bare && (host === bare || host.endsWith(`.${bare}`))) return true;
    const regex = new RegExp(`^${pattern.replace(/[.+^${}()|[\]\\]/g, '\\$&').replace(/\*/g, '.*').replace(/\?/g, '.')}$`, 'i');
    return regex.test(host);
  }

  function formatSize(bytes) {
    if (!(bytes > 0)) return '';
    const units = ['KB', 'MB', 'GB'];
    let value = bytes / 1024;
    let unit = 0;
    while (value >= 1024 && unit < units.length - 1) {
      value /= 1024;
      unit++;
    }
    return `${value >= 100 || unit === 0 ? Math.round(value) : value.toFixed(1)} ${units[unit]}`;
  }

  function extensionOf(url, mime) {
    try {
      const name = new URL(url).pathname.split('/').pop() || '';
      const dot = name.lastIndexOf('.');
      if (dot > 0 && name.length - dot <= 5) return name.slice(dot + 1).toLowerCase();
    } catch {
      // not a URL
    }
    const subtype = String(mime || '').split('/')[1] || '';
    return subtype.replace(/^x-/, '').split(/[;+]/)[0];
  }

  /** "MP4 1920x1080 · 342 MB", "Audio M4A · 3.1 MB", "Streaming video (HLS)". */
  function labelFor(item, video) {
    if (item.manifest) return t(item.kind === 'dash' ? 'panelStreamDash' : 'panelStreamHls');
    const type = (extensionOf(item.url, item.mime) || 'video').toUpperCase();
    let label = item.kind === 'audio' ? `${t('panelAudio')} ${type}` : type;
    if (item.kind !== 'audio' && video && video.currentSrc === item.url && video.videoWidth > 0) {
      label += ` ${video.videoWidth}x${video.videoHeight}`;
    }
    const size = formatSize(item.size);
    return size ? `${label} · ${size}` : label;
  }

  /** The formats for one player: its own source first, then what the page loaded. */
  function formatsFor(video) {
    const list = media.slice();
    const src = video.currentSrc || video.src || '';
    if (/^https?:/i.test(src) && !list.some((item) => item.url === src)) {
      list.unshift({ url: src, mime: '', size: 0, manifest: /\.(m3u8|mpd)([?#]|$)/i.test(src), kind: 'video' });
    }
    return list.sort((a, b) => Number(b.url === src) - Number(a.url === src));
  }

  function panelAllowed() {
    if (!settings || !settings.show) return false;
    if (!settings.inPopups && window.toolbar && !window.toolbar.visible) return false;
    const host = settings.pageHost || location.hostname;
    return !(settings.excludedSites || []).concat(offSites).some((site) => hostMatches(site, host));
  }

  const STYLE = `
    :host { all: initial; }
    .bar { display: inline-flex; align-items: stretch; font: 12px/1 system-ui, "Segoe UI", sans-serif; direction: ltr;
           border-radius: 4px; overflow: hidden; box-shadow: 0 1px 4px rgba(0,0,0,.45); opacity: .92; }
    .bar:hover { opacity: 1; }
    button { all: unset; box-sizing: border-box; cursor: pointer; color: #fff; background: #0b6bcb; padding: 6px 9px;
             font: inherit; white-space: nowrap; }
    button:hover, button:focus-visible { background: #0958a8; }
    button:focus-visible { outline: 2px solid #fff; outline-offset: -3px; }
    .more, .close { padding: 6px 7px; border-left: 1px solid rgba(255,255,255,.3); }
    .bar.disabled button.main, .bar.disabled .more { background: #6b7280; cursor: not-allowed; }
    .menu { position: absolute; top: 100%; margin-top: 2px; min-width: 100%; max-width: 420px; max-height: 300px; overflow: auto;
            background: #fff; color: #111; border: 1px solid #9ca3af; border-radius: 4px; box-shadow: 0 2px 8px rgba(0,0,0,.35);
            font: 12px/1.3 system-ui, "Segoe UI", sans-serif; padding: 3px 0; }
    .menu[hidden] { display: none; }
    .menu button { display: block; width: 100%; color: #111; background: transparent; padding: 5px 12px; }
    .menu button:hover, .menu button:focus-visible { background: #dbeafe; outline: none; }
    .menu hr { border: 0; border-top: 1px solid #e5e7eb; margin: 3px 0; }
    .note { position: absolute; top: 100%; margin-top: 2px; white-space: nowrap; background: #111; color: #fff;
            font: 12px system-ui, "Segoe UI", sans-serif; padding: 4px 8px; border-radius: 3px; }
  `;

  function createPanel(video) {
    const host = document.createElement('novaget-video-panel');
    const root = host.attachShadow({ mode: 'closed' });
    root.innerHTML = `<style>${STYLE}</style>
      <div class="bar" role="toolbar">
        <button class="main" type="button"></button><button class="more" type="button" aria-haspopup="menu">&#9660;</button><button class="close" type="button">&#215;</button>
      </div>
      <div class="menu" role="menu" hidden></div>`;
    const bar = root.querySelector('.bar');
    const main = root.querySelector('.main');
    const more = root.querySelector('.more');
    const close = root.querySelector('.close');
    const menu = root.querySelector('.menu');
    main.textContent = `⬇ ${t('panelDownload')}`;
    more.title = t('panelFormats');
    more.setAttribute('aria-label', t('panelFormats'));
    close.title = t('panelHide');
    close.setAttribute('aria-label', t('panelHide'));
    bar.setAttribute('aria-label', t('extName'));

    const panel = { video, host, bar, main, more, menu, root, note: null };
    const stop = (e) => {
      e.preventDefault();
      e.stopPropagation();
    };

    main.addEventListener('click', (e) => {
      stop(e);
      if (isProtected) return;
      const formats = formatsFor(video);
      if (formats.length === 1) {
        menu.hidden = true;
        download(panel, formats[0]);
      } else {
        toggleMenu(panel);
      }
    });
    more.addEventListener('click', (e) => {
      stop(e);
      toggleMenu(panel);
    });
    close.addEventListener('click', (e) => {
      stop(e);
      closedVideos.add(video);
      removePanel(video);
    });
    root.addEventListener('keydown', (e) => {
      if (e.key === 'Escape') {
        menu.hidden = true;
        main.focus();
      }
    });
    for (const type of ['mousedown', 'mouseup', 'dblclick', 'pointerdown', 'pointerup']) {
      host.addEventListener(type, (e) => e.stopPropagation());
    }

    for (const [name, value] of Object.entries({ position: 'fixed', 'z-index': '2147483647', top: '0', left: '0', display: 'block', margin: '0', padding: '0' })) {
      host.style.setProperty(name, value, 'important');
    }
    return panel;
  }

  function toggleMenu(panel) {
    const { menu, video } = panel;
    if (!menu.hidden) {
      menu.hidden = true;
      return;
    }
    menu.textContent = '';
    if (!isProtected) {
      for (const item of formatsFor(video)) {
        const button = document.createElement('button');
        button.type = 'button';
        button.setAttribute('role', 'menuitem');
        button.textContent = labelFor(item, video);
        button.title = item.url;
        button.addEventListener('click', (e) => {
          e.preventDefault();
          e.stopPropagation();
          menu.hidden = true;
          download(panel, item);
        });
        menu.appendChild(button);
      }
      menu.appendChild(document.createElement('hr'));
    }
    const entries = [
      [t('panelNoSite'), () => {
        const host = (settings && settings.pageHost) || location.hostname;
        offSites = offSites.concat([host]);
        api.storage.local.set({ panelOffSites: offSites });
        refresh();
      }],
      [t('panelSettings'), () => message({ kind: 'openOptions' })],
    ];
    for (const [text, action] of entries) {
      const button = document.createElement('button');
      button.type = 'button';
      button.setAttribute('role', 'menuitem');
      button.textContent = text;
      button.addEventListener('click', (e) => {
        e.preventDefault();
        e.stopPropagation();
        menu.hidden = true;
        action();
      });
      menu.appendChild(button);
    }
    menu.hidden = false;
    const first = menu.querySelector('button');
    if (first) first.focus();
  }

  async function download(panel, item) {
    const isTop = window.top === window;
    const reply = await message({
      kind: 'downloadMedia',
      url: item.url,
      title: isTop ? document.title : undefined, // in an embedded player the tab's title names the file
      width: panel.video.currentSrc === item.url ? panel.video.videoWidth : 0,
      height: panel.video.currentSrc === item.url ? panel.video.videoHeight : 0,
    });
    showNote(panel, reply && reply.ok ? t('panelSent') : reply && reply.protected ? t('panelProtected') : t('panelFailed'));
  }

  function showNote(panel, text) {
    if (panel.note) panel.note.remove();
    const note = document.createElement('div');
    note.className = 'note';
    note.textContent = text;
    note.setAttribute('role', 'status');
    panel.root.appendChild(note);
    panel.note = note;
    setTimeout(() => {
      note.remove();
      if (panel.note === note) panel.note = null;
    }, 2500);
  }

  function removePanel(video) {
    const panel = panels.get(video);
    if (panel) {
      panel.host.remove();
      panels.delete(video);
    }
  }

  function videoUsable(video) {
    if (!video.isConnected || closedVideos.has(video)) return false;
    const rect = video.getBoundingClientRect();
    if (rect.width < MIN_WIDTH || rect.height < MIN_HEIGHT) return false;
    if (rect.bottom < 0 || rect.right < 0 || rect.top > innerHeight || rect.left > innerWidth) return false;
    const style = getComputedStyle(video);
    return style.visibility !== 'hidden' && style.display !== 'none' && Number(style.opacity) > 0.05;
  }

  function hovering(video, panel) {
    if (!pointer) return false;
    const inside = (rect) => pointer.x >= rect.left && pointer.x <= rect.right && pointer.y >= rect.top && pointer.y <= rect.bottom;
    return inside(video.getBoundingClientRect()) || (panel && inside(panel.host.getBoundingClientRect()));
  }

  /** Places each panel at the configured corner of its player (top right, top left, or bottom middle). */
  function layout() {
    layoutQueued = false;
    const fullscreen = document.fullscreenElement;
    for (const [video, panel] of panels) {
      const { host, menu } = panel;
      if (fullscreen === video || !videoUsable(video)) {
        host.style.setProperty('visibility', 'hidden', 'important');
        continue;
      }
      const parent = fullscreen && fullscreen.contains(video) ? fullscreen : document.documentElement;
      if (host.parentNode !== parent) parent.appendChild(host);
      const show = !(settings && settings.onHover) || hovering(video, panel) || !menu.hidden;
      host.style.setProperty('visibility', show ? 'visible' : 'hidden', 'important');
      const rect = video.getBoundingClientRect();
      const bar = panel.bar.getBoundingClientRect();
      const position = (settings && settings.position) || 'topRight';
      let top = rect.top + 8;
      let left = rect.right - bar.width - 8;
      if (position === 'topLeft') {
        left = rect.left + 8;
      } else if (position === 'bottom') {
        top = rect.bottom - bar.height - 52; // above the usual player controls
        left = rect.left + (rect.width - bar.width) / 2;
      }
      host.style.setProperty('top', `${Math.max(0, Math.round(top))}px`, 'important');
      host.style.setProperty('left', `${Math.max(0, Math.round(left))}px`, 'important');
      menu.style.setProperty(position === 'topLeft' ? 'left' : 'right', '0');
    }
  }

  function queueLayout() {
    if (!layoutQueued && panels.size > 0) {
      layoutQueued = true;
      requestAnimationFrame(layout);
    }
  }

  function updateState(panel) {
    const disabled = isProtected;
    panel.bar.classList.toggle('disabled', disabled);
    panel.main.setAttribute('aria-disabled', String(disabled));
    panel.main.title = disabled ? t('panelProtected') : '';
    panel.more.title = disabled ? t('panelProtected') : t('panelFormats');
  }

  /** Adds panels to players with something to download (or disabled ones on protected pages), removes the rest. */
  function refresh() {
    const allowed = panelAllowed();
    const videos = allowed ? [...document.querySelectorAll('video')] : [];
    for (const video of videos) {
      if (video.mediaKeys) markProtected(); // EME is active on this player
      if (!watchedVideos.has(video)) {
        watchedVideos.add(video);
        video.addEventListener('encrypted', markProtected);
        video.addEventListener('loadedmetadata', refresh);
      }
    }
    const wanted = new Set(videos.filter((video) => videoUsable(video) && (isProtected || formatsFor(video).length > 0)));
    for (const video of [...panels.keys()]) {
      if (!wanted.has(video)) removePanel(video);
    }
    for (const video of wanted) {
      if (!panels.has(video)) {
        const panel = createPanel(video);
        panels.set(video, panel);
        document.documentElement.appendChild(panel.host);
      }
      updateState(panels.get(video));
    }
    queueLayout();
  }

  function markProtected() {
    if (isProtected) return;
    isProtected = true;
    message({ kind: 'protected' });
    refresh();
  }

  // DRM: the page-context hook (Chromium) flags EME use; players also fire 'encrypted' and get mediaKeys.
  document.addEventListener('novaget-eme', markProtected, true);
  const emeFlagged = () => document.documentElement && document.documentElement.hasAttribute('data-novaget-eme');

  api.runtime.onMessage.addListener((msg) => {
    if (msg && msg.kind === 'media') {
      media = Array.isArray(msg.items) ? msg.items : [];
      if (msg.protected) isProtected = true;
      refresh();
    }
    return false;
  });

  api.storage.onChanged.addListener((changes, area) => {
    if (area === 'local' && changes.panelOffSites) {
      offSites = changes.panelOffSites.newValue || [];
      refresh();
    }
  });

  async function start() {
    const stored = await api.storage.local.get('panelOffSites').catch(() => ({}));
    offSites = Array.isArray(stored.panelOffSites) ? stored.panelOffSites : [];
    const reply = await message({ kind: 'getMedia' });
    if (reply) {
      media = reply.items || [];
      settings = reply.panel || null;
      isProtected = isProtected || Boolean(reply.protected);
    }
    if (emeFlagged()) markProtected();
    refresh();
    new MutationObserver(() => {
      if (emeFlagged()) markProtected();
      queueRefresh();
    }).observe(document.documentElement, { childList: true, subtree: true, attributes: true, attributeFilter: ['data-novaget-eme', 'src'] });
    setInterval(refresh, 2000);
  }

  let refreshQueued = false;
  function queueRefresh() {
    if (!refreshQueued) {
      refreshQueued = true;
      setTimeout(() => {
        refreshQueued = false;
        refresh();
      }, 300);
    }
  }

  window.addEventListener('scroll', queueLayout, { capture: true, passive: true });
  window.addEventListener('resize', queueLayout, { passive: true });
  document.addEventListener('fullscreenchange', queueLayout);
  document.addEventListener('mousemove', (e) => {
    pointer = { x: e.clientX, y: e.clientY };
    if (settings && settings.onHover) queueLayout();
  }, { capture: true, passive: true });
  document.addEventListener('visibilitychange', () => {
    if (document.visibilityState === 'visible') {
      message({ kind: 'getMedia' }).then((reply) => {
        if (reply) {
          media = reply.items || [];
          settings = reply.panel || settings;
          refresh();
        }
      });
    }
  });

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', start, { once: true });
  } else {
    start();
  }
})();
