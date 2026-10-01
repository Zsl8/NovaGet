'use strict';

const api = typeof browser !== 'undefined' ? browser : chrome;
const $ = (id) => document.getElementById(id);

for (const element of document.querySelectorAll('[data-i18n]')) {
  element.textContent = api.i18n.getMessage(element.dataset.i18n);
}

async function currentTab() {
  const [tab] = await api.tabs.query({ active: true, currentWindow: true });
  return tab;
}

async function currentHost() {
  const tab = await currentTab();
  try {
    const url = new URL(tab && tab.url);
    return /^(https?|ftps?):$/.test(url.protocol) ? url.hostname : null;
  } catch {
    return null;
  }
}

/** NovaGet asked for this site's login (Site Grabber → "Log in via browser…"). */
function loginWanted(settings, host) {
  const wanted = (settings && Array.isArray(settings.loginRequests)) ? settings.loginRequests : [];
  const bare = (h) => String(h || '').toLowerCase().replace(/^www\./, '');
  return Boolean(host) && wanted.some((w) => bare(w) === bare(host) || bare(host).endsWith(`.${bare(w)}`));
}

async function init() {
  const host = await currentHost();
  const { captureOff, offSites } = await api.storage.local.get(['captureOff', 'offSites']);
  const sites = Array.isArray(offSites) ? offSites : [];
  $('captureAll').checked = !captureOff;
  $('captureSite').checked = host ? !sites.includes(host) : false;
  $('captureSite').disabled = !host;
  $('siteLabel').textContent = host ? api.i18n.getMessage('popupCaptureSite', [host]) : api.i18n.getMessage('popupNoSite');

  $('captureAll').addEventListener('change', (e) => api.storage.local.set({ captureOff: !e.target.checked }));
  $('captureSite').addEventListener('change', async (e) => {
    const { offSites: current } = await api.storage.local.get('offSites');
    const list = new Set(Array.isArray(current) ? current : []);
    if (e.target.checked) list.delete(host); else list.add(host);
    await api.storage.local.set({ offSites: [...list] });
  });
  $('open').addEventListener('click', async () => {
    await api.runtime.sendMessage({ kind: 'openApp' });
    window.close();
  });

  const status = $('status');
  status.textContent = api.i18n.getMessage('popupChecking');
  const reply = await api.runtime.sendMessage({ kind: 'status' });
  if (reply && reply.connected && loginWanted(reply.settings, host)) {
    const button = $('sendLogin');
    button.hidden = false;
    button.addEventListener('click', async () => {
      const tab = await currentTab();
      const sent = await api.runtime.sendMessage({ kind: 'sendLogin', url: tab && tab.url });
      button.disabled = true;
      button.textContent = api.i18n.getMessage(sent && sent.ok ? 'popupLoginSent' : 'popupLoginFailed');
    });
  }

  if (reply && reply.connected) {
    status.className = 'status ok';
    status.textContent = reply.settings && !reply.settings.enabled
      ? api.i18n.getMessage('popupDisabled')
      : api.i18n.getMessage('popupConnected', [reply.version || '']);
  } else {
    status.className = 'status off';
    status.textContent = api.i18n.getMessage('popupNotConnected');
  }
}

init();
