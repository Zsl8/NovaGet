// NovaGet Integration content script: tells the background which keys were held when a link was clicked, so the
// "prevent capture" and "force capture" keys from Options → General work.
'use strict';

(() => {
  const api = typeof browser !== 'undefined' ? browser : chrome;
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
})();
