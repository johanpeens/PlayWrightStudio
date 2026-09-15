/*
 * Run bar. Injected into every document of a headed run so you can see where the run is up to
 * and stop it without going back to the studio. Lives in a shadow root, like the recorder's
 * toolbar, so it cannot inherit or leak the page's styles.
 */
(() => {
  'use strict';
  if (window.__pwsRunBarInstalled) return;
  window.__pwsRunBarInstalled = true;
  if (window !== window.top) return;          // the page itself, not its frames

  let host = null, shadow = null, label = null;
  let text = 'Starting…';
  let stopping = false;

  const HTML = [
    '<style>',
    ':host { all: initial; }',
    '.bar { position: fixed; top: 14px; left: 50%; transform: translateX(-50%); z-index: 2147483647;',
    '  display: flex; align-items: center; gap: 10px; pointer-events: auto;',
    '  font: 500 12px/1.2 -apple-system, "Segoe UI", system-ui, sans-serif;',
    '  background: #12151b; color: #e8eaed; padding: 7px 9px 7px 12px; border-radius: 999px;',
    '  box-shadow: 0 6px 24px rgba(0,0,0,.45); border: 1px solid #2c3036; user-select: none; }',
    '.dot { width: 8px; height: 8px; border-radius: 50%; background: #4f9cf9; flex: none;',
    '  animation: pws-run 1.1s ease-in-out infinite; }',
    '@keyframes pws-run { 0%,100% { opacity: 1 } 50% { opacity: .25 } }',
    '.label { white-space: nowrap; max-width: 46vw; overflow: hidden; text-overflow: ellipsis; }',
    'button { font: 600 12px/1 inherit; cursor: pointer; color: #fff; background: #c0243c;',
    '  border: 0; border-radius: 999px; padding: 6px 13px; letter-spacing: .02em; }',
    'button:hover { background: #a81e33; }',
    'button:disabled { background: #4a4f57; cursor: default; }',
    '.bar.done .dot { animation: none; background: #3ecf8e; }',
    '</style>',
    '<div class="bar"><span class="dot"></span><span class="label"></span>',
    '<button type="button">Stop</button></div>'
  ].join('\n');

  function build() {
    if (host && host.isConnected) return;
    const parent = document.body || document.documentElement;
    if (!parent) return;

    host = document.createElement('div');
    host.setAttribute('data-pws-runbar', '');
    host.style.cssText = 'all:initial;position:fixed;top:0;left:0;width:0;height:0;z-index:2147483647;';
    shadow = host.attachShadow({ mode: 'open' });
    shadow.innerHTML = HTML;
    parent.appendChild(host);

    label = shadow.querySelector('.label');
    shadow.querySelector('button').addEventListener('click', stop);
    paint();
  }

  function paint() {
    if (!label) return;
    label.textContent = text;
    const button = shadow.querySelector('button');
    button.disabled = stopping;
    button.textContent = stopping ? 'Stopping…' : 'Stop';
  }

  function stop() {
    if (stopping) return;
    stopping = true;
    text = 'Stopping after this step…';
    paint();
    try { window.__pwsRunStop(''); } catch (e) { /* binding gone */ }
  }

  function dismiss() {
    if (host) { host.remove(); host = null; shadow = null; label = null; }
  }

  // Called from the runner as the run moves along.
  window.__pwsRunBar = {
    // Nothing is shown until a run says so - this script also rides along in the recorder
    // session, where a bar would just be in the way.
    begin(next) {
      text = next || 'Starting…';
      stopping = false;
      dismiss();
      build();
    },
    dismiss,
    update(next) { text = next; paint(); },
    finish(next) {
      text = next;
      stopping = true;
      paint();
      if (shadow) shadow.querySelector('.bar').classList.add('done');
    },
    // The run needs to click things; get out of the way when asked.
    hide() { if (host) host.style.display = 'none'; },
    show() { if (host) host.style.display = ''; }
  };

  // Rebuild only while a run is on, so the bar survives a page that rewrites its own body.
  setInterval(() => { if (host) build(); }, 1000);
})();
