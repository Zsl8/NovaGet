// NovaGet Integration, page context (Chromium): notices when the page asks for DRM (Encrypted Media Extensions)
// so the "Download this video" panel shows the content as protected. It only observes: the page's request goes
// through unchanged, and NovaGet never downloads or decrypts protected media.
'use strict';

(() => {
  const proto = window.Navigator && window.Navigator.prototype;
  const original = proto && proto.requestMediaKeySystemAccess;
  if (typeof original !== 'function' || original.novagetObserved) return;

  const flag = () => {
    try {
      if (document.documentElement) document.documentElement.setAttribute('data-novaget-eme', '');
      document.dispatchEvent(new CustomEvent('novaget-eme'));
    } catch {
      // nothing to flag yet
    }
  };

  const observed = {
    requestMediaKeySystemAccess(...args) {
      flag();
      return original.apply(this, args);
    },
  }.requestMediaKeySystemAccess;
  Object.defineProperty(observed, 'novagetObserved', { value: true });
  try {
    Object.defineProperty(proto, 'requestMediaKeySystemAccess', { value: observed, configurable: true, writable: true, enumerable: true });
  } catch {
    // the page locked it down; the content script still sees 'encrypted' events and mediaKeys
  }
})();
