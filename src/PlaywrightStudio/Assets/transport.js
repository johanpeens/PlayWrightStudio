/*
 * Playwright Studio transport.
 *
 * Opens a socket back to the studio and hangs window.__pwsEmit off it. recorder.js and runbar.js
 * were written against that binding when Playwright provided it, so they need no changes - the
 * channel just comes from a different place now.
 *
 * Inbound is new: the studio can push the recorder's mode, drive the run bar, and hand the
 * player one step at a time.
 */
(() => {
  'use strict';
  if (window.__pwsTransportInstalled) return;
  window.__pwsTransportInstalled = true;

  /* Playwright still owns the channel when the studio launched this browser itself. Leaving its
     binding alone means both worlds keep working while the new one is being built. */
  const PLAYWRIGHT_OWNS_CHANNEL = typeof window.__pwsEmit === 'function';

  const TOP = window === window.top;
  const ORIGIN = scriptOrigin();
  const PAGE_KEY = '__pws_page_id';
  const TOKEN_KEY = '__pws_token';

  let socket = null;
  let ready = false;
  let closed = false;
  let backoff = 500;
  const pending = [];

  /* ---------------------------------------------------------------- identity */

  /* The studio addresses this page by an id that has to survive navigation, or every click
     through a journey would look like a new tab arriving. sessionStorage is per-tab, which is
     exactly the scope wanted. */
  function pageId() {
    try {
      let id = sessionStorage.getItem(PAGE_KEY);
      if (!id) {
        id = 'p' + Math.random().toString(36).slice(2, 10) + Date.now().toString(36).slice(-4);
        sessionStorage.setItem(PAGE_KEY, id);
      }
      return id;
    } catch (e) {
      /* A sandboxed frame cannot use sessionStorage. It gets a per-load id and will not be
         claimable, which is fine - only the top document is ever claimed. */
      return 'p-ephemeral-' + Math.random().toString(36).slice(2, 10);
    }
  }

  /*
   * When the studio opens a tab for you it puts a one-time token on the url, so it can tell
   * which of your tabs is the one it just asked for. Kept in sessionStorage because the journey
   * navigates and the token has to outlive the first page.
   */
  function claimToken() {
    try {
      const fromUrl = new URLSearchParams(location.search).get('__pws');
      if (fromUrl) {
        sessionStorage.setItem(TOKEN_KEY, fromUrl);
        return fromUrl;
      }
      return sessionStorage.getItem(TOKEN_KEY) || '';
    } catch (e) {
      return '';
    }
  }

  /* Where the studio lives, worked out from this script's own src so the page never has to be
     told. Falls back to the page's own origin for the bookmarklet case. */
  function scriptOrigin() {
    try {
      const me = document.currentScript && document.currentScript.src;
      if (me) return new URL(me, location.href).origin;
    } catch (e) { /* ignore */ }
    if (window.__pwsStudioOrigin) return window.__pwsStudioOrigin;
    return location.origin;
  }

  /* ---------------------------------------------------------------- socket */

  function connect() {
    if (PLAYWRIGHT_OWNS_CHANNEL || closed) return;
    if (!TOP) return;   // one socket per tab, owned by the top document

    let url;
    try {
      url = new URL('/pws/socket', ORIGIN);
      url.protocol = url.protocol === 'https:' ? 'wss:' : 'ws:';
      url.searchParams.set('id', pageId());
    } catch (e) {
      return;
    }

    try { socket = new WebSocket(url.toString()); }
    catch (e) { retry(); return; }

    socket.onopen = () => {
      ready = true;
      backoff = 500;
      hello();
      while (pending.length) send(pending.shift());
    };

    socket.onmessage = (e) => {
      let msg;
      try { msg = JSON.parse(e.data); } catch (err) { return; }
      handle(msg);
    };

    socket.onclose = () => { ready = false; socket = null; retry(); };
    socket.onerror = () => { try { socket && socket.close(); } catch (e) { /* ignore */ } };
  }

  function retry() {
    if (closed || PLAYWRIGHT_OWNS_CHANNEL) return;
    setTimeout(connect, backoff);
    backoff = Math.min(backoff * 2, 10000);   // the studio may simply be stopped
  }

  function send(json) {
    if (ready && socket) {
      try { socket.send(json); return true; }
      catch (e) { /* fall through to queueing */ }
    }
    pending.push(json);
    if (pending.length > 500) pending.shift();   // a long-disconnected page should not grow forever
    return false;
  }

  /* Tells the studio what this page is, so it can be listed as claimable. Repeated on every
     navigation, which is how the list stays accurate. */
  function hello() {
    send(JSON.stringify({
      type: 'hello',
      id: pageId(),
      token: claimToken(),
      url: location.href,
      title: document.title || location.hostname
    }));
  }

  /* ---------------------------------------------------------------- outbound */

  if (!PLAYWRIGHT_OWNS_CHANNEL) {
    window.__pwsEmit = function (json) { send(json); };
  }

  /* A frame cannot open its own socket, so it hands events to the top document instead. */
  if (!TOP && !PLAYWRIGHT_OWNS_CHANNEL) {
    window.__pwsEmit = function (json) {
      try { window.top.postMessage({ __pws: true, payload: json }, '*'); }
      catch (e) { /* cross-origin top - nothing can be done from here */ }
    };
  }

  if (TOP && !PLAYWRIGHT_OWNS_CHANNEL) {
    window.addEventListener('message', (e) => {
      const d = e.data;
      if (d && d.__pws === true && typeof d.payload === 'string') send(d.payload);
    });
  }

  /* ---------------------------------------------------------------- inbound */

  function handle(msg) {
    if (!msg || typeof msg.type !== 'string') return;

    switch (msg.type) {
      case 'mode':
        if (typeof window.__pwsSetMode === 'function') window.__pwsSetMode(msg.value);
        break;

      case 'runbar':
        if (window.__pwsRunBar && typeof window.__pwsRunBar[msg.method] === 'function') {
          window.__pwsRunBar[msg.method](msg.text);
        }
        break;

      case 'step':
        /* player.js answers this. It may not have loaded yet on a very early message. */
        if (window.__pwsPlayer && typeof window.__pwsPlayer.run === 'function') {
          window.__pwsPlayer.run(msg).then(
            (result) => send(JSON.stringify({ type: 'stepResult', id: msg.id, result: result })),
            (err) => send(JSON.stringify({
              type: 'stepResult',
              id: msg.id,
              result: { ok: false, error: String((err && err.message) || err) }
            })));
        } else {
          send(JSON.stringify({
            type: 'stepResult',
            id: msg.id,
            result: { ok: false, error: 'The player is not loaded on this page.' }
          }));
        }
        break;

      case 'highlight': {
        let out = { count: 0 };
        try {
          if (window.__pwsPlayer && window.__pwsPlayer.highlight) {
            out = window.__pwsPlayer.highlight(msg.selector, msg.frames);
          }
        } catch (e) { out = { count: 0, error: String(e && e.message || e) }; }
        send(JSON.stringify({ type: 'highlightResult', id: msg.id, result: out }));
        break;
      }

      case 'tape':
        if (window.__pwsTape) {
          if (msg.action === 'start') {
            window.__pwsTape.start().then(
              (ok) => send(JSON.stringify({ type: 'tapeResult', id: msg.id, started: ok })),
              () => send(JSON.stringify({ type: 'tapeResult', id: msg.id, started: false })));
          } else {
            const events = window.__pwsTape.finish();
            send(JSON.stringify({ type: 'tapeResult', id: msg.id, events: events }));
          }
        } else {
          send(JSON.stringify({ type: 'tapeResult', id: msg.id, started: false, events: [] }));
        }
        break;

      case 'shot':
        if (window.__pwsShot && typeof window.__pwsShot.take === 'function') {
          window.__pwsShot.take().then(
            (data) => send(JSON.stringify({ type: 'shotResult', id: msg.id, data: data })),
            () => send(JSON.stringify({ type: 'shotResult', id: msg.id, data: null })));
        } else {
          send(JSON.stringify({ type: 'shotResult', id: msg.id, data: null }));
        }
        break;
    }
  }

  /* ---------------------------------------------------------------- lifecycle */

  window.__pwsTransport = {
    send: send,
    hello: hello,
    origin: ORIGIN,
    pageId: pageId,
    get connected() { return ready; }
  };

  if (TOP && !PLAYWRIGHT_OWNS_CHANNEL) {
    connect();

    /* The url changes without a reload in a single-page app, and the studio's page list should
       follow. Cheap enough at this interval. */
    let lastUrl = location.href;
    setInterval(() => {
      if (location.href === lastUrl) return;
      lastUrl = location.href;
      hello();
    }, 1000);

    window.addEventListener('pagehide', () => { closed = true; try { socket && socket.close(); } catch (e) { } });
  }
})();
