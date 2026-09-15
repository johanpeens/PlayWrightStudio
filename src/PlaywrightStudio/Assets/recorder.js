/*
 * Playwright Studio recorder.
 * Injected into every document of the recording browser via AddInitScriptAsync.
 * Watches the user click through a page, turns that into steps, and posts them
 * back to the Blazor app through the __pwsEmit binding.
 */
(() => {
  'use strict';
  if (window.__pwsRecorderInstalled) return;
  window.__pwsRecorderInstalled = true;

  const TOP = window === window.top;
  const MODE_KEY = '__pws_mode';
  const TESTID_ATTRS = ['data-testid', 'data-test-id', 'data-test', 'data-qa', 'data-cy', 'data-automation-id'];
  const PRESS_KEYS = ['Enter', 'Escape', 'ArrowDown', 'ArrowUp', 'PageDown', 'PageUp'];

  /* ---------------------------------------------------------------- channel */

  const queue = [];

  function emit(ev) {
    ev.url = location.href;
    queue.push(ev);
    flush();
  }

  function flush() {
    if (typeof window.__pwsEmit !== 'function') { setTimeout(flush, 100); return; }
    while (queue.length) {
      const ev = queue[0];
      try { window.__pwsEmit(JSON.stringify(ev)); queue.shift(); }
      catch (err) { setTimeout(flush, 250); return; }
    }
  }

  /* ------------------------------------------------------------------ mode */
  /* record | paused | pick:<Action> */

  function getMode() {
    try { return sessionStorage.getItem(MODE_KEY) || 'record'; } catch (e) { return window.__pwsMode || 'record'; }
  }

  function setMode(m) {
    window.__pwsMode = m;
    try { sessionStorage.setItem(MODE_KEY, m); } catch (e) { /* sandboxed */ }
    if (m.indexOf('pick:') !== 0) clearHighlight();
    paint();
    emit({ type: 'mode', value: m });
  }

  window.__pwsSetMode = setMode;

  /* -------------------------------------------------------------- selectors */

  function q(v) { return String(v).replace(/\\/g, '\\\\').replace(/"/g, '\\"'); }

  function esc(v) {
    if (window.CSS && CSS.escape) return CSS.escape(v);
    return String(v).replace(/([^\w-])/g, '\\$1');
  }

  function rootOf(el) {
    const r = el.getRootNode ? el.getRootNode() : document;
    return (r && (r.nodeType === 9 || r.nodeType === 11)) ? r : document;
  }

  function unique(sel, el) {
    try {
      const m = rootOf(el).querySelectorAll(sel);
      return m.length === 1 && m[0] === el;
    } catch (e) { return false; }
  }

  /*
   * An id that is regenerated on every render is worse than no id at all - the step simply
   * never finds its element again. MudBlazor's "mudinput08267ls7" is the common one here;
   * Blazor, Angular Material and the React libraries each have their own flavour.
   */
  function looksGenerated(id) {
    if (!id || id.length > 40) return true;
    if (/^(:r|ember|ext-gen|mud|mat-|cdk-|radix-|react-aria|headlessui-|__)/i.test(id)) return true;
    if (/[0-9a-f]{8,}/i.test(id)) return true;
    if (/\d{4,}/.test(id)) return true;

    // A random token tacked on the end: a long alphanumeric run carrying several digits.
    // Two digits is the threshold, so an honest id like "address2" survives.
    const tail = (id.match(/[a-z0-9]{8,}$/i) || [''])[0];
    if (tail && /[a-z]/i.test(tail) && (tail.match(/\d/g) || []).length >= 2) return true;

    return false;
  }

  function stableClasses(el) {
    const list = Array.from(el.classList || []);
    return list.filter(c =>
      c.length > 2 && c.length < 40 &&
      !/\d{3,}/.test(c) &&
      !/[0-9a-f]{6,}/i.test(c) &&
      !/^(ng-|css-|jsx-|sc-|is-|has-|active|open|show|hover|focus|selected|disabled|invalid|valid|dirty|touched)/.test(c)
    ).slice(0, 2);
  }

  /*
   * innerText is the *rendered* text, so CSS text-transform is baked into it - a MudBlazor
   * button reading "Register" in the markup comes back as "REGISTER". Playwright computes
   * accessible names and text= matches from the DOM text instead, so a selector built from
   * innerText silently matches nothing on replay. Gather raw text, skipping the subtrees
   * innerText would have skipped.
   */
  function directText(el) {
    let out = '';
    (function walk(node) {
      for (const child of node.childNodes) {
        if (child.nodeType === 3) { out += child.data; continue; }
        if (child.nodeType !== 1) continue;
        if (child.tagName === 'SCRIPT' || child.tagName === 'STYLE') continue;
        let style = null;
        try { style = window.getComputedStyle(child); } catch (e) { /* detached */ }
        if (style && (style.display === 'none' || style.visibility === 'hidden')) continue;
        walk(child);
      }
    })(el);
    return out.replace(/\s+/g, ' ').trim();
  }

  function accName(el) {
    if (!el || !el.getAttribute) return '';
    const aria = el.getAttribute('aria-label');
    if (aria && aria.trim()) return aria.trim();
    const labelledBy = el.getAttribute('aria-labelledby');
    if (labelledBy) {
      const txt = labelledBy.split(/\s+/)
        .map(id => { const n = document.getElementById(id); return n ? n.textContent : ''; })
        .join(' ').replace(/\s+/g, ' ').trim();
      if (txt) return txt;
    }
    const tag = el.tagName.toLowerCase();
    const type = (el.getAttribute('type') || '').toLowerCase();
    if (tag === 'input' && ['button', 'submit', 'reset'].indexOf(type) >= 0 && el.value) return String(el.value).trim();
    if (el.labels && el.labels.length) {
      const t = directText(el.labels[0]);
      if (t) return t;
    }
    if (['button', 'a', 'label', 'summary', 'option', 'legend'].indexOf(tag) >= 0) {
      const t = directText(el);
      if (t && t.length <= 80) return t;
    }
    const title = el.getAttribute('title');
    if (title) return title.trim();
    const alt = el.getAttribute('alt');
    if (alt) return alt.trim();
    const ph = el.getAttribute('placeholder');
    if (ph) return ph.trim();
    const t2 = directText(el);
    return (t2 && t2.length <= 60) ? t2 : '';
  }

  function roleOf(el) {
    const explicit = el.getAttribute && el.getAttribute('role');
    if (explicit) return explicit.split(/\s+/)[0];
    const tag = el.tagName.toLowerCase();
    if (/^h[1-6]$/.test(tag)) return 'heading';
    switch (tag) {
      case 'a': return el.hasAttribute('href') ? 'link' : null;
      case 'button': return 'button';
      case 'select': return el.multiple ? 'listbox' : 'combobox';
      case 'textarea': return 'textbox';
      case 'option': return 'option';
      case 'img': return 'img';
      case 'table': return 'table';
      case 'ul': case 'ol': return 'list';
      case 'li': return 'listitem';
      case 'input': {
        const t = (el.getAttribute('type') || 'text').toLowerCase();
        if (t === 'checkbox') return 'checkbox';
        if (t === 'radio') return 'radio';
        if (['submit', 'button', 'reset', 'image'].indexOf(t) >= 0) return 'button';
        if (t === 'range') return 'slider';
        if (t === 'number') return 'spinbutton';
        if (t === 'search') return 'searchbox';
        if (['text', 'email', 'tel', 'url', 'password'].indexOf(t) >= 0) return 'textbox';
        return null;
      }
      default: return null;
    }
  }

  function cssCandidates(el) {
    const out = [];
    const tag = el.tagName.toLowerCase();
    const add = s => { if (s && out.indexOf(s) < 0) out.push(s); };

    for (const a of TESTID_ATTRS) {
      const v = el.getAttribute(a);
      if (v) add('[' + a + '="' + q(v) + '"]');
    }
    const id = el.getAttribute('id');
    if (id && !looksGenerated(id)) add('#' + esc(id));

    const name = el.getAttribute('name');
    const type = (el.getAttribute('type') || '').toLowerCase();
    if (name) {
      // Radios and checkboxes share a name, so the value is what tells them apart.
      if (tag === 'input' && (type === 'radio' || type === 'checkbox')) {
        const v = el.getAttribute('value');
        if (v) add('input[name="' + q(name) + '"][value="' + q(v) + '"]');
      }
      if (tag === 'input' && type) add('input[type="' + q(type) + '"][name="' + q(name) + '"]');
      add(tag + '[name="' + q(name) + '"]');
    }
    // A long aria-label is usually the content itself - a headline, a row summary - and that
    // content is different tomorrow. Short labels name a control and are worth pinning to.
    const aria = el.getAttribute('aria-label');
    if (aria && aria.length <= 80) add(tag + '[aria-label="' + q(aria) + '"]');

    const ph = el.getAttribute('placeholder');
    if (ph) add(tag + '[placeholder="' + q(ph) + '"]');

    if (tag === 'a') {
      const href = el.getAttribute('href');
      if (href && href.length < 120) add('a[href="' + q(href) + '"]');
    }
    const classes = stableClasses(el);
    if (classes.length) add(tag + classes.map(c => '.' + esc(c)).join(''));

    return out.filter(s => unique(s, el));
  }

  /*
   * A regenerated id still beats a bare css path, but it must rank below role= and text=,
   * which survive a re-render. This is the difference between a step that finds its button
   * next time and one that waits ten seconds for an id that no longer exists.
   */
  function weakCandidates(el) {
    const id = el.getAttribute('id');
    if (!id || !looksGenerated(id) || !/^[A-Za-z][\w:.-]*$/.test(id)) return [];
    const sel = '#' + esc(id);
    return unique(sel, el) ? [sel] : [];
  }

  function semanticCandidates(el) {
    const out = [];
    const role = roleOf(el);
    const name = accName(el);
    if (role && name && name.length <= 60) out.push('role=' + role + '[name="' + q(name) + '"]');
    const text = directText(el);
    const tag = el.tagName.toUpperCase();
    const textTags = ['BUTTON', 'A', 'LABEL', 'SPAN', 'LI', 'TD', 'TH', 'H1', 'H2', 'H3', 'H4', 'H5', 'H6', 'P', 'STRONG', 'SUMMARY', 'OPTION'];
    if (text && text.length <= 60 && textTags.indexOf(tag) >= 0) out.push('text="' + q(text) + '"');
    return out;
  }

  function cssPath(el) {
    const parts = [];
    let cur = el;
    while (cur && cur.nodeType === 1 && parts.length < 8) {
      const id = cur.getAttribute('id');
      if (id && !looksGenerated(id)) { parts.unshift('#' + esc(id)); break; }
      let testid = null;
      for (const a of TESTID_ATTRS) {
        const v = cur.getAttribute(a);
        if (v) { testid = '[' + a + '="' + q(v) + '"]'; break; }
      }
      if (testid) { parts.unshift(testid); break; }

      let seg = cur.tagName.toLowerCase();
      const parent = cur.parentElement;
      if (parent) {
        const sibs = Array.from(parent.children).filter(c => c.tagName === cur.tagName);
        if (sibs.length > 1) seg += ':nth-of-type(' + (sibs.indexOf(cur) + 1) + ')';
      }
      parts.unshift(seg);
      cur = parent;
    }
    return parts.join(' > ');
  }

  function selectorFor(el) {
    if (!el || el.nodeType !== 1) return { selector: 'body', alternatives: [] };
    const all = cssCandidates(el).concat(semanticCandidates(el)).concat(weakCandidates(el));
    const path = cssPath(el);
    if (path && all.indexOf(path) < 0) all.push(path);
    const best = all[0] || path || el.tagName.toLowerCase();
    return { selector: best, alternatives: all.filter(s => s !== best).slice(0, 6) };
  }

  /*
   * A cross-origin frame cannot see the <iframe> tag that holds it, but it can always
   * describe itself: its own window.name and url are readable from the inside. That turns
   * a useless bare "iframe" into something that picks out one frame on the parent page.
   */
  function selfFrameSelector() {
    try {
      const name = window.name;
      if (name && name.length <= 100 && /^[\w.:-]+$/.test(name)) return 'iframe[name="' + q(name) + '"]';
    } catch (e) { /* sandboxed */ }
    try {
      // A srcdoc frame reads as about:srcdoc, which is not what the src attribute says.
      const href = location.href;
      if (href && href.length <= 300 && !/^about:/i.test(href)) return 'iframe[src="' + q(href) + '"]';
    } catch (e) { /* sandboxed */ }
    return null;
  }

  function frameChain() {
    const chain = [];
    let w = window;
    let guard = 0;
    while (w !== w.top && guard++ < 5) {
      let fe = null;
      try { fe = w.frameElement; } catch (e) { fe = null; }

      if (fe) {
        chain.unshift(selectorFor(fe).selector);
        try { w = w.parent; } catch (e) { break; }
        continue;
      }

      // Out of reach. Name ourselves if we are the frame the script is running in,
      // otherwise give up rather than emit a selector that matches every iframe.
      chain.unshift((w === window && selfFrameSelector()) || 'iframe');
      break;
    }
    return chain;
  }

  /* ---------------------------------------------------------------- helpers */

  function fromOverlay(e) {
    if (!host) return false;
    const path = e.composedPath ? e.composedPath() : [e.target];
    return path.indexOf(host) >= 0;
  }

  function deepTarget(e) {
    const path = e.composedPath ? e.composedPath() : [e.target];
    for (const n of path) {
      if (n && n.nodeType === 1 && n !== host) return n;
    }
    return e.target;
  }

  function actionable(el) {
    const tags = ['button', 'a', 'input', 'select', 'textarea', 'label', 'summary', 'option'];
    let cur = el;
    for (let i = 0; cur && cur.nodeType === 1 && i < 6; i++) {
      if (tags.indexOf(cur.tagName.toLowerCase()) >= 0) return cur;
      for (const a of TESTID_ATTRS) if (cur.getAttribute(a)) return cur;
      const role = cur.getAttribute('role');
      if (role && ['presentation', 'none'].indexOf(role) < 0) return cur;
      if (cur.hasAttribute('onclick') || cur.tabIndex === 0) return cur;
      cur = cur.parentElement;
    }
    return el;
  }

  function isTextEntry(el) {
    if (!el || el.nodeType !== 1) return false;
    const tag = el.tagName.toLowerCase();
    if (tag === 'textarea') return true;
    if (el.isContentEditable) return true;
    if (tag !== 'input') return false;
    const t = (el.getAttribute('type') || 'text').toLowerCase();
    return ['checkbox', 'radio', 'button', 'submit', 'reset', 'file', 'image', 'range'].indexOf(t) < 0;
  }

  function describe(el, action) {
    const raw = accName(el) || el.getAttribute('name') || el.getAttribute('placeholder')
      || directText(el).slice(0, 40) || el.tagName.toLowerCase();
    const label = String(raw).replace(/\s+/g, ' ').trim().slice(0, 60);
    switch (action) {
      case 'Fill': return 'Fill "' + label + '"';
      case 'Select': return 'Select in "' + label + '"';
      case 'Check': return 'Check "' + label + '"';
      case 'Uncheck': return 'Uncheck "' + label + '"';
      case 'Upload': return 'Upload to "' + label + '"';
      case 'Hover': return 'Hover "' + label + '"';
      case 'DoubleClick': return 'Double-click "' + label + '"';
      case 'RightClick': return 'Right-click "' + label + '"';
      case 'AssertVisible': return 'Expect "' + label + '" to be visible';
      case 'AssertHidden': return 'Expect "' + label + '" to be hidden';
      case 'AssertText': return 'Expect text of "' + label + '"';
      case 'AssertValue': return 'Expect value of "' + label + '"';
      case 'AssertChecked': return 'Expect "' + label + '" checked state';
      case 'WaitForSelector': return 'Wait for "' + label + '"';
      default: return 'Click "' + label + '"';
    }
  }

  function step(el, action, value, extra) {
    const s = selectorFor(el);
    const ev = {
      type: 'step',
      action: action,
      selector: s.selector,
      alternatives: s.alternatives,
      value: (value === undefined || value === null) ? null : String(value),
      description: describe(el, action),
      frames: frameChain()
    };
    if (extra) Object.assign(ev, extra);
    emit(ev);
  }

  /* ----------------------------------------------------------------- capture */

  const pendingFills = new Map();

  function emitFill(el) {
    if (!el || !el.isConnected) return;
    const value = el.isContentEditable ? (el.innerText || '') : (el.value || '');
    const secret = !!(el.getAttribute && (el.getAttribute('type') || '').toLowerCase() === 'password');
    step(el, 'Fill', value, {
      secret: secret,
      field: (el.getAttribute && (el.getAttribute('name') || el.getAttribute('id'))) || 'password'
    });
  }

  function flushPending(el) {
    if (el && pendingFills.has(el)) {
      clearTimeout(pendingFills.get(el));
      pendingFills.delete(el);
      emitFill(el);
      return;
    }
    pendingFills.forEach((timer, node) => { clearTimeout(timer); emitFill(node); });
    pendingFills.clear();
  }

  function onClick(e) {
    if (fromOverlay(e)) return;
    const mode = getMode();
    if (mode.indexOf('pick:') === 0) {
      e.preventDefault(); e.stopPropagation();
      if (e.stopImmediatePropagation) e.stopImmediatePropagation();
      doPick(mode.slice(5), deepTarget(e));
      return;
    }
    if (mode !== 'record') return;

    const el = actionable(deepTarget(e));
    if (!el || el.nodeType !== 1) return;
    flushPending(null);

    const tag = el.tagName.toLowerCase();
    const type = (el.getAttribute('type') || '').toLowerCase();
    let action = 'Click';
    if (tag === 'input' && (type === 'checkbox' || type === 'radio')) {
      // The browser toggles the control before dispatching click, so el.checked is
      // already the state the user just asked for.
      action = (type === 'radio' || el.checked) ? 'Check' : 'Uncheck';
    } else if (e.detail === 2) {
      action = 'DoubleClick';
    }
    step(el, action, null);
  }

  function onContextMenu(e) {
    if (fromOverlay(e) || getMode() !== 'record') return;
    step(actionable(deepTarget(e)), 'RightClick', null);
  }

  function onInput(e) {
    if (fromOverlay(e) || getMode() !== 'record') return;
    const el = deepTarget(e);
    if (!isTextEntry(el)) return;
    if (pendingFills.has(el)) clearTimeout(pendingFills.get(el));
    pendingFills.set(el, setTimeout(() => { pendingFills.delete(el); emitFill(el); }, 450));
  }

  function onChange(e) {
    if (fromOverlay(e) || getMode() !== 'record') return;
    const el = deepTarget(e);
    if (!el || el.nodeType !== 1) return;
    const tag = el.tagName.toLowerCase();
    const type = (el.getAttribute('type') || '').toLowerCase();

    if (tag === 'select') {
      const selected = Array.from(el.selectedOptions || []);
      const s = selectorFor(el);
      emit({
        type: 'step',
        action: 'Select',
        selector: s.selector,
        alternatives: s.alternatives,
        value: selected.map(o => o.value).join(','),
        description: describe(el, 'Select') + ' → ' + selected.map(o => (o.text || '').trim()).join(', '),
        frames: frameChain()
      });
      return;
    }
    if (tag === 'input' && type === 'file') {
      step(el, 'Upload', Array.from(el.files || []).map(f => f.name).join(';'));
      return;
    }
    if (isTextEntry(el)) flushPending(el);
  }

  function onKeyDown(e) {
    if (fromOverlay(e)) return;
    const mode = getMode();
    if (mode.indexOf('pick:') === 0) {
      if (e.key === 'Escape') { e.preventDefault(); setMode('record'); }
      return;
    }
    if (mode !== 'record') return;
    if (PRESS_KEYS.indexOf(e.key) < 0) return;
    const el = deepTarget(e);
    flushPending(isTextEntry(el) ? el : null);
    let key = e.key;
    if (e.ctrlKey) key = 'Control+' + key;
    const target = (el && el.nodeType === 1) ? el : document.body;
    const s = selectorFor(target);
    emit({
      type: 'step', action: 'Press', selector: s.selector, alternatives: s.alternatives,
      value: key, description: 'Press ' + key, frames: frameChain()
    });
  }

  function doPick(action, el) {
    if (!el || el.nodeType !== 1) return;
    let value = null;
    if (action === 'AssertText') value = directText(el).slice(0, 200);
    else if (action === 'AssertValue') value = (el.value !== undefined && el.value !== null) ? String(el.value) : directText(el).slice(0, 200);
    else if (action === 'AssertChecked') value = el.checked ? 'true' : 'false';
    step(el, action, value);
    setMode('record');
  }

  /* ---------------------------------------------------------------- overlay */

  let host = null, shadow = null, highlight = null;

  const OVERLAY_HTML = [
    '<style>',
    ':host { all: initial; }',
    '.bar { position: fixed; right: 16px; bottom: 16px; z-index: 2147483647; display: flex; align-items: center;',
    '  gap: 6px; font: 500 12px/1.2 -apple-system, "Segoe UI", system-ui, sans-serif; background: #15171c;',
    '  color: #e8eaed; padding: 7px 9px; border-radius: 999px; box-shadow: 0 6px 24px rgba(0,0,0,.45);',
    '  border: 1px solid #2c3036; user-select: none; }',
    '.chip { display: flex; align-items: center; gap: 6px; padding: 3px 10px 3px 8px; border-radius: 999px;',
    '  background: #2b1216; color: #ff6b81; font-weight: 700; letter-spacing: .04em; }',
    '.chip.paused { background: #241f10; color: #ffc857; }',
    '.chip.pick { background: #10222b; color: #4fc3f7; }',
    '.dot { width: 8px; height: 8px; border-radius: 50%; background: currentColor; }',
    '.chip.rec .dot { animation: pws-pulse 1.1s ease-in-out infinite; }',
    '@keyframes pws-pulse { 0%,100% { opacity: 1 } 50% { opacity: .2 } }',
    'button { font: inherit; cursor: pointer; color: #cfd3d8; background: #22262c; border: 1px solid #32373f;',
    '  border-radius: 7px; padding: 5px 9px; }',
    'button:hover { background: #2c313a; color: #fff; }',
    '.menu { position: fixed; right: 16px; bottom: 62px; display: none; flex-direction: column; gap: 2px;',
    '  background: #15171c; border: 1px solid #2c3036; border-radius: 10px; padding: 5px;',
    '  box-shadow: 0 6px 24px rgba(0,0,0,.45); min-width: 200px; }',
    '.menu.open { display: flex; }',
    '.menu button { text-align: left; background: transparent; border: none; padding: 7px 10px; border-radius: 6px; }',
    '.menu button:hover { background: #262b33; }',
    '.menu .sep { height: 1px; background: #2c3036; margin: 4px 2px; }',
    '.hint { position: fixed; left: 50%; transform: translateX(-50%); top: 14px; display: none; background: #10222b;',
    '  color: #9fe6ff; border: 1px solid #1d4a5c; padding: 7px 14px; border-radius: 999px;',
    '  font: 500 12px/1.2 system-ui, sans-serif; box-shadow: 0 6px 24px rgba(0,0,0,.4); z-index: 2147483647; }',
    '.hint.on { display: block; }',
    '.hl { position: fixed; pointer-events: none; border: 2px solid #4fc3f7; background: rgba(79,195,247,.14);',
    '  border-radius: 3px; display: none; z-index: 2147483646; }',
    '.hl.on { display: block; }',
    '.tag { position: absolute; top: -20px; left: -2px; background: #4fc3f7; color: #04222e; padding: 1px 6px;',
    '  border-radius: 3px; font: 700 10px/1.5 ui-monospace, monospace; white-space: nowrap; max-width: 60vw;',
    '  overflow: hidden; text-overflow: ellipsis; }',
    '.cmt { position: fixed; right: 16px; bottom: 62px; display: none; gap: 6px; background: #15171c;',
    '  border: 1px solid #2c3036; border-radius: 10px; padding: 6px; }',
    '.cmt.open { display: flex; }',
    '.cmt input { font: inherit; background: #0f1115; color: #e8eaed; border: 1px solid #32373f;',
    '  border-radius: 6px; padding: 6px 9px; width: 230px; outline: none; }',
    '</style>',
    '<div class="hl"><span class="tag"></span></div>',
    '<div class="hint"></div>',
    '<div class="menu">',
    '  <button data-pick="AssertVisible">✔ Assert element visible</button>',
    '  <button data-pick="AssertHidden">✖ Assert element hidden</button>',
    '  <button data-pick="AssertText">≡ Assert text</button>',
    '  <button data-pick="AssertValue">⌨ Assert input value</button>',
    '  <button data-pick="AssertChecked">☑ Assert checked state</button>',
    '  <div class="sep"></div>',
    '  <button data-pick="WaitForSelector">⏱ Wait for element</button>',
    '  <button data-pick="Hover">☝ Hover element</button>',
    '</div>',
    '<div class="cmt"><input type="text" placeholder="Note for this point in the test..." /></div>',
    '<div class="bar">',
    '  <span class="chip rec"><span class="dot"></span><span class="label">REC</span></span>',
    '  <button class="pause" title="Pause / resume capture">⏸</button>',
    '  <button class="assert" title="Add an assertion by picking an element">✔ Assert</button>',
    '  <button class="comment" title="Add a note">✎</button>',
    '</div>'
  ].join('\n');

  function closeMenus() {
    if (!shadow) return;
    shadow.querySelector('.menu').classList.remove('open');
    shadow.querySelector('.cmt').classList.remove('open');
  }

  function paint() {
    if (!shadow) return;
    const mode = getMode();
    const chip = shadow.querySelector('.chip');
    const label = shadow.querySelector('.label');
    const hint = shadow.querySelector('.hint');
    const pause = shadow.querySelector('.pause');
    chip.className = 'chip';
    if (mode === 'record') { chip.classList.add('rec'); label.textContent = 'REC'; pause.textContent = '⏸'; }
    else if (mode === 'paused') { chip.classList.add('paused'); label.textContent = 'PAUSED'; pause.textContent = '▶'; }
    else { chip.classList.add('pick'); label.textContent = 'PICK'; }
    if (mode.indexOf('pick:') === 0) {
      hint.classList.add('on');
      hint.textContent = 'Click an element to add "' + mode.slice(5) + '"  •  Esc to cancel';
    } else {
      hint.classList.remove('on');
    }
  }

  function buildOverlay() {
    if (!TOP) return;
    if (host && host.isConnected) return;
    const parent = document.body || document.documentElement;
    if (!parent) return;
    host = document.createElement('div');
    host.setAttribute('data-pws-overlay', '');
    host.style.cssText = 'all:initial;position:fixed;top:0;left:0;width:0;height:0;z-index:2147483647;';
    shadow = host.attachShadow({ mode: 'open' });
    shadow.innerHTML = OVERLAY_HTML;
    parent.appendChild(host);

    highlight = shadow.querySelector('.hl');

    shadow.querySelector('.pause').addEventListener('click', () => {
      setMode(getMode() === 'paused' ? 'record' : 'paused');
      closeMenus();
    });
    shadow.querySelector('.assert').addEventListener('click', () => {
      shadow.querySelector('.cmt').classList.remove('open');
      shadow.querySelector('.menu').classList.toggle('open');
    });
    shadow.querySelector('.comment').addEventListener('click', () => {
      shadow.querySelector('.menu').classList.remove('open');
      const box = shadow.querySelector('.cmt');
      box.classList.toggle('open');
      if (box.classList.contains('open')) box.querySelector('input').focus();
    });
    shadow.querySelectorAll('.menu button').forEach(b => {
      b.addEventListener('click', () => {
        closeMenus();
        setMode('pick:' + b.getAttribute('data-pick'));
      });
    });
    const input = shadow.querySelector('.cmt input');
    input.addEventListener('keydown', ev => {
      ev.stopPropagation();
      if (ev.key === 'Enter' && input.value.trim()) {
        emit({ type: 'step', action: 'Comment', value: input.value.trim(), description: input.value.trim(), frames: [] });
        input.value = '';
        closeMenus();
      } else if (ev.key === 'Escape') {
        input.value = '';
        closeMenus();
      }
    });
    paint();
  }

  function drawHighlight(el) {
    if (!highlight || !el || !el.getBoundingClientRect) return;
    const r = el.getBoundingClientRect();
    highlight.style.left = r.left + 'px';
    highlight.style.top = r.top + 'px';
    highlight.style.width = r.width + 'px';
    highlight.style.height = r.height + 'px';
    highlight.classList.add('on');
    const tag = highlight.querySelector('.tag');
    if (tag) tag.textContent = selectorFor(el).selector;
  }

  function clearHighlight() {
    if (highlight) highlight.classList.remove('on');
  }

  function onMouseMove(e) {
    if (!TOP || getMode().indexOf('pick:') !== 0) return;
    if (fromOverlay(e)) { clearHighlight(); return; }
    drawHighlight(deepTarget(e));
  }

  /* ------------------------------------------------------------ SPA routing */

  let lastHref = location.href;

  /* Only the page itself navigates. An iframe settling from about:blank to its own url -
   * about:srcdoc, an ad, a consent modal - is not a step in the journey. */
  function navigable(href) {
    return /^(https?|file):/i.test(href || '');
  }

  function checkNav() {
    if (!TOP) return;
    if (location.href === lastHref) return;
    lastHref = location.href;
    if (!navigable(location.href)) return;
    emit({ type: 'nav', value: location.href });
  }

  ['pushState', 'replaceState'].forEach(fn => {
    const orig = history[fn];
    if (typeof orig !== 'function') return;
    history[fn] = function () {
      const r = orig.apply(this, arguments);
      setTimeout(checkNav, 0);
      return r;
    };
  });
  window.addEventListener('popstate', () => setTimeout(checkNav, 0));

  /* ------------------------------------------------------------------ wire */

  document.addEventListener('click', onClick, true);
  document.addEventListener('contextmenu', onContextMenu, true);
  document.addEventListener('input', onInput, true);
  document.addEventListener('change', onChange, true);
  document.addEventListener('keydown', onKeyDown, true);
  document.addEventListener('mousemove', onMouseMove, true);
  // Leaving a field settles its value, so the Fill lands before whatever comes next.
  document.addEventListener('blur', e => {
    if (fromOverlay(e) || getMode() !== 'record') return;
    const el = deepTarget(e);
    if (isTextEntry(el)) flushPending(el);
  }, true);
  window.addEventListener('beforeunload', () => flushPending(null));

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', buildOverlay);
  } else {
    buildOverlay();
  }
  setInterval(buildOverlay, 1000);
  setInterval(checkNav, 500);
  flush();
})();
