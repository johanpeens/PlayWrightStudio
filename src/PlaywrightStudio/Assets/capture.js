/*
 * Playwright Studio capture.
 *
 * Two jobs, both needed because there is no browser process to ask any more:
 *   __pwsShot   - photograph the page, using html2canvas
 *   __pwsTape   - record DOM mutations with rrweb, so a run can be scrubbed afterwards
 *
 * Both libraries are loaded from the studio, never from a CDN, and both are fetched lazily so a
 * page that never records anything pays nothing for them.
 */
(() => {
  'use strict';
  if (window.__pwsCaptureInstalled) return;
  window.__pwsCaptureInstalled = true;

  const ORIGIN = (window.__pwsTransport && window.__pwsTransport.origin) || location.origin;

  /* ---------------------------------------------------------------- loading */

  const loaded = {};

  function loadScript(path) {
    if (loaded[path]) return loaded[path];

    loaded[path] = new Promise((resolve, reject) => {
      const el = document.createElement('script');
      el.src = ORIGIN + path;
      el.async = true;
      el.onload = () => resolve(true);
      el.onerror = () => reject(new Error('Could not load ' + path + ' from the studio.'));
      (document.head || document.documentElement).appendChild(el);
    });

    return loaded[path];
  }

  /* ---------------------------------------------------------------- screenshots */

  async function take() {
    if (typeof window.html2canvas !== 'function') {
      await loadScript('/lib/html2canvas/html2canvas.min.js');
    }
    if (typeof window.html2canvas !== 'function') return null;

    /* Only what is on screen. A full-page render of a long document is slow enough to stall a
       run, and the interesting part is almost always the viewport. */
    const canvas = await window.html2canvas(document.documentElement, {
      logging: false,
      useCORS: true,
      backgroundColor: null,
      scale: 1,
      width: Math.min(document.documentElement.clientWidth, window.innerWidth),
      height: Math.min(document.documentElement.clientHeight, window.innerHeight),
      x: window.scrollX,
      y: window.scrollY,
      /* The studio's own toolbars are not part of the page under test and must never appear
         in a run report or a manual. */
      ignoreElements: (el) =>
        !!(el.hasAttribute && (el.hasAttribute('data-pws-overlay') || el.hasAttribute('data-pws-runbar')))
    });

    return canvas.toDataURL('image/png');
  }

  window.__pwsShot = {
    take: function () {
      return take().catch(() => null);   // a missing picture must never fail a step
    }
  };

  /* ---------------------------------------------------------------- session tape */

  let stop = null;
  let events = [];

  async function start() {
    if (stop) return true;

    if (!window.rrweb) await loadScript('/lib/rrweb/rrweb.min.js');
    if (!window.rrweb || typeof window.rrweb.record !== 'function') return false;

    events = [];
    stop = window.rrweb.record({
      emit: (e) => {
        events.push(e);
        /* A long run would otherwise grow without limit. Keeping the tail is the right half:
           what broke is at the end. */
        if (events.length > 6000) events.splice(0, 1000);
      },
      /* Anything typed into a password field stays out of the tape. The studio already warns
         that secrets live in the scenario file; it should not put them here too. */
      maskAllInputs: false,
      maskInputOptions: { password: true },
      blockClass: 'pws-no-record',
      recordCanvas: false,
      collectFonts: false
    }) || null;

    return !!stop;
  }

  function finish() {
    if (stop) { try { stop(); } catch (e) { /* already stopped */ } }
    stop = null;
    const out = events;
    events = [];
    return out;
  }

  window.__pwsTape = { start: start, finish: finish, get recording() { return !!stop; } };
})();
