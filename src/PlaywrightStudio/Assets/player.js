/*
 * Playwright Studio player.
 *
 * Runs one recorded step against the live page. This is what replaces Playwright: finding the
 * element, waiting until it is genuinely ready, doing the thing, and - when it fails - saying
 * which of the four things went wrong rather than just timing out.
 *
 * The studio sends a step, this answers with { ok, error, note, actual }.
 */
(() => {
  'use strict';
  if (window.__pwsPlayerInstalled) return;
  window.__pwsPlayerInstalled = true;

  const POLL = 50;
  const DEFAULT_TIMEOUT = 10000;

  /* ---------------------------------------------------------------- selectors */

  /*
   * The recorder emits Playwright's dialect as well as css: role=button[name="Save"] and
   * text="Save". Neither is valid css, so querySelectorAll cannot run them and they have to be
   * matched by hand. Keeping them is worth the code - they are the selectors that survive a
   * redesign, which is the whole reason the recorder prefers them.
   */

  function findAll(selector, root) {
    root = root || document;
    if (!selector) return [];

    const roleMatch = /^role=([a-zA-Z]+)(?:\[name="((?:[^"\\]|\\.)*)"\])?$/.exec(selector);
    if (roleMatch) return byRole(root, roleMatch[1], unescape(roleMatch[2]));

    const textMatch = /^text="((?:[^"\\]|\\.)*)"$/.exec(selector);
    if (textMatch) return byText(root, unescape(textMatch[1]));

    try { return Array.prototype.slice.call(root.querySelectorAll(selector)); }
    catch (e) { return []; }
  }

  function unescape(s) {
    if (s === undefined || s === null) return null;
    return String(s).replace(/\\"/g, '"').replace(/\\\\/g, '\\');
  }

  function norm(s) { return String(s == null ? '' : s).replace(/\s+/g, ' ').trim(); }

  /* Mirrors recorder.js roleOf() so a selector it recorded is a selector this can find. */
  function roleOf(el) {
    const explicit = el.getAttribute && el.getAttribute('role');
    if (explicit) return explicit.trim().toLowerCase();

    const tag = el.tagName ? el.tagName.toLowerCase() : '';
    const type = (el.getAttribute && (el.getAttribute('type') || '')).toLowerCase();

    if (tag === 'a') return el.hasAttribute('href') ? 'link' : null;
    if (tag === 'button') return 'button';
    if (tag === 'select') return 'combobox';
    if (tag === 'textarea') return 'textbox';
    if (tag === 'img') return 'img';
    if (/^h[1-6]$/.test(tag)) return 'heading';
    if (tag === 'input') {
      if (type === 'checkbox') return 'checkbox';
      if (type === 'radio') return 'radio';
      if (type === 'button' || type === 'submit' || type === 'reset') return 'button';
      if (type === 'search') return 'searchbox';
      return 'textbox';
    }
    return null;
  }

  /* Mirrors recorder.js accName(). Deliberately the same order of preference. */
  function accName(el) {
    const label = el.getAttribute && el.getAttribute('aria-label');
    if (label) return norm(label);

    const by = el.getAttribute && el.getAttribute('aria-labelledby');
    if (by) {
      const parts = by.split(/\s+/)
        .map((id) => document.getElementById(id))
        .filter(Boolean)
        .map((n) => directText(n));
      if (parts.length) return norm(parts.join(' '));
    }

    if (el.id) {
      const tied = document.querySelector('label[for="' + cssEscape(el.id) + '"]');
      if (tied) return directText(tied);
    }

    const wrapping = el.closest && el.closest('label');
    if (wrapping) return directText(wrapping);

    const placeholder = el.getAttribute && el.getAttribute('placeholder');
    if (placeholder) return norm(placeholder);

    const alt = el.getAttribute && el.getAttribute('alt');
    if (alt) return norm(alt);

    const title = el.getAttribute && el.getAttribute('title');
    if (title) return norm(title);

    if (el.tagName === 'INPUT') {
      const v = el.getAttribute('value');
      if (v && /^(button|submit|reset)$/i.test(el.getAttribute('type') || '')) return norm(v);
      return '';
    }

    return directText(el);
  }

  function cssEscape(v) {
    if (window.CSS && CSS.escape) return CSS.escape(v);
    return String(v).replace(/["\\]/g, '\\$&');
  }

  /*
   * innerText bakes in CSS text-transform, so a button styled uppercase would read "SAVE" and
   * never match the recorded "Save". Walking the DOM text avoids that - the same reason
   * recorder.js does it this way.
   */
  function directText(el) {
    let out = '';
    (function walk(node) {
      for (const child of node.childNodes) {
        if (child.nodeType === 3) { out += child.data; continue; }
        if (child.nodeType !== 1) continue;
        if (child.tagName === 'SCRIPT' || child.tagName === 'STYLE') continue;
        let style = null;
        try { style = window.getComputedStyle(child); } catch (e) { }
        if (style && (style.display === 'none' || style.visibility === 'hidden')) continue;
        walk(child);
      }
    })(el);
    return norm(out);
  }

  function byRole(root, role, name) {
    const out = [];
    const all = root.querySelectorAll('*');
    for (const el of all) {
      if (roleOf(el) !== String(role).toLowerCase()) continue;
      if (name !== null && name !== undefined && accName(el) !== name) continue;
      out.push(el);
    }
    return out;
  }

  function byText(root, text) {
    const out = [];
    const all = root.querySelectorAll('*');
    for (const el of all) {
      if (directText(el) !== text) continue;
      // Prefer the innermost element carrying the text, the way a user would point at it.
      if (el.querySelector && Array.prototype.some.call(el.children, (c) => directText(c) === text)) continue;
      out.push(el);
    }
    return out;
  }

  /* ---------------------------------------------------------------- frames */

  /* Only same-origin frames can be reached from inside the page. A cross-origin one throws on
     contentDocument, and there is genuinely nothing to be done about that from here. */
  function resolveRoot(frameChain) {
    let root = document;
    if (!frameChain || !frameChain.length) return { root: root, error: null };

    for (const step of frameChain) {
      const frames = findAll(step, root);
      const frame = frames.find((f) => f.tagName === 'IFRAME' || f.tagName === 'FRAME') || frames[0];
      if (!frame) return { root: null, error: 'The frame "' + step + '" never appeared.' };

      let doc = null;
      try { doc = frame.contentDocument; } catch (e) { doc = null; }
      if (!doc) {
        return {
          root: null,
          error: 'The frame "' + step + '" is on another domain, so the studio cannot reach '
               + 'inside it from the page. Frames on the same domain work.'
        };
      }
      root = doc;
    }
    return { root: root, error: null };
  }

  /* ---------------------------------------------------------------- readiness */

  function visible(el) {
    if (!el || !el.isConnected) return false;
    const rect = el.getBoundingClientRect();
    if (rect.width <= 0 && rect.height <= 0) return false;
    let style = null;
    try { style = window.getComputedStyle(el); } catch (e) { return false; }
    if (!style) return false;
    if (style.visibility === 'hidden' || style.display === 'none') return false;
    if (Number(style.opacity) === 0) return false;
    return true;
  }

  function enabled(el) {
    if (el.disabled) return false;
    return el.getAttribute && el.getAttribute('aria-disabled') !== 'true';
  }

  /*
   * What is actually on top at the element's centre. This is how a failure can say "the cookie
   * banner covered the click" instead of leaving the user with a bare timeout.
   */
  function covering(el) {
    const rect = el.getBoundingClientRect();

    // Probe the part of the element that is actually on screen, rather than its centre. A tall
    // element scrolled half out of view still has a clickable middle, and a centre that falls
    // outside the viewport says nothing about whether something is on top of it.
    const left = Math.max(rect.left, 0);
    const top_ = Math.max(rect.top, 0);
    const right = Math.min(rect.right, window.innerWidth);
    const bottom = Math.min(rect.bottom, window.innerHeight);

    // No overlap with the viewport - or no viewport at all - means this cannot be answered.
    // Saying "not covered" here would be a guess, so the caller treats it as unknown and the
    // step goes ahead rather than failing on something we never measured.
    if (right <= left || bottom <= top_) return null;

    const x = (left + right) / 2;
    const y = (top_ + bottom) / 2;

    let top;
    try { top = document.elementFromPoint(x, y); } catch (e) { return null; }
    if (!top) return null;
    if (top === el || el.contains(top) || top.contains(el)) return null;

    // The studio's own toolbars sit above the page and must never be blamed.
    if (top.closest && top.closest('[data-pws-overlay],[data-pws-runbar]')) return null;
    return top;
  }

  function describe(el) {
    if (!el) return 'something';
    const tag = el.tagName ? el.tagName.toLowerCase() : 'element';
    const id = el.id ? '#' + el.id : '';
    const cls = el.className && typeof el.className === 'string'
      ? '.' + el.className.trim().split(/\s+/).slice(0, 2).join('.')
      : '';
    const text = norm(directText(el)).slice(0, 40);
    return '<' + tag + id + cls + '>' + (text ? ' "' + text + '"' : '');
  }

  function sleep(ms) { return new Promise((r) => setTimeout(r, ms)); }

  /*
   * Waits for an element that is actually usable, then reports precisely why not if it never
   * becomes so. The four outcomes are the ones the studio has always given.
   */
  async function waitFor(step, opts) {
    opts = opts || {};
    const timeout = step.timeoutMs || DEFAULT_TIMEOUT;
    const deadline = Date.now() + timeout;
    const wantHidden = !!opts.wantHidden;

    let matched = 0;
    let lastCover = null;

    while (Date.now() < deadline) {
      const resolved = resolveRoot(step.frames);
      if (resolved.error) {
        await sleep(POLL);
        if (Date.now() >= deadline) return { el: null, error: resolved.error, matched: 0 };
        continue;
      }

      const all = findAll(step.selector, resolved.root);
      matched = all.length;

      if (wantHidden) {
        if (!all.length || !all.some(visible)) return { el: all[0] || null, error: null, matched: matched };
        await sleep(POLL);
        continue;
      }

      const shown = all.filter(visible);
      if (shown.length) {
        const el = shown[0];
        if (!opts.needsInteraction) return { el: el, error: null, matched: matched };

        // Scroll first, then ask what is on top. Checking coverage while the element is below
        // the fold reads its centre as off-screen and wrongly concludes nothing is in the way.
        scrollTo(el);

        const cover = covering(el);
        if (!cover && enabled(el)) return { el: el, error: null, matched: matched };
        lastCover = cover;
      }

      await sleep(POLL);
    }

    // Out of time - work out which of the four it was.
    const resolved = resolveRoot(step.frames);
    if (resolved.error) return { el: null, error: resolved.error, matched: 0 };

    const all = findAll(step.selector, resolved.root);
    if (!all.length) {
      return {
        el: null, matched: 0,
        error: 'Nothing on the page matched ' + step.selector + '.'
      };
    }

    const shown = all.filter(visible);
    if (!shown.length) {
      return {
        el: null, matched: all.length,
        error: all.length + (all.length === 1 ? ' element matched' : ' elements matched')
             + ' but nothing was visible.'
      };
    }

    if (lastCover) {
      return {
        el: null, matched: all.length,
        error: 'The element was there but ' + describe(lastCover) + ' was covering it.'
      };
    }

    if (!enabled(shown[0])) {
      return { el: null, matched: all.length, error: 'The element was visible but still disabled.' };
    }

    return { el: shown[0], error: null, matched: all.length };
  }

  /* ---------------------------------------------------------------- actions */

  function fire(el, type, init) {
    el.dispatchEvent(new Event(type, Object.assign({ bubbles: true, cancelable: true }, init || {})));
  }

  /* Frameworks listen for input and change, not for the value setter. Going through the native
     setter first is what makes React and Blazor notice at all. */
  function setValue(el, value) {
    const proto = el instanceof HTMLTextAreaElement
      ? HTMLTextAreaElement.prototype
      : HTMLInputElement.prototype;
    const setter = Object.getOwnPropertyDescriptor(proto, 'value');

    if (setter && setter.set) setter.set.call(el, value);
    else el.value = value;

    fire(el, 'input');
    fire(el, 'change');
  }

  function mouse(el, type, extra) {
    const rect = el.getBoundingClientRect();
    el.dispatchEvent(new MouseEvent(type, Object.assign({
      bubbles: true,
      cancelable: true,
      view: window,
      clientX: rect.left + rect.width / 2,
      clientY: rect.top + rect.height / 2
    }, extra || {})));
  }

  function scrollTo(el) {
    try { el.scrollIntoView({ block: 'center', inline: 'nearest' }); } catch (e) { el.scrollIntoView(); }
  }

  /* ---------------------------------------------------------------- matching */

  /* The studio's three forms, unchanged: contains by default, =exact, /regex/. */
  function matches(actual, expected) {
    actual = String(actual == null ? '' : actual);
    if (expected === null || expected === undefined || expected === '') return true;
    const want = String(expected);

    if (want.length > 1 && want[0] === '=') return norm(actual) === norm(want.slice(1));

    const re = /^\/(.*)\/([gimsuy]*)$/.exec(want);
    if (re) {
      try { return new RegExp(re[1], re[2]).test(actual); }
      catch (e) { return false; }
    }

    return norm(actual).toLowerCase().indexOf(norm(want).toLowerCase()) >= 0;
  }

  /* ---------------------------------------------------------------- the step */

  async function run(msg) {
    const step = msg.step || msg;
    const action = String(step.action || '');
    const value = step.value == null ? '' : String(step.value);

    try {
      switch (action) {
        case 'Navigate': {
          // Leaving the page kills this script, so answer before going.
          setTimeout(() => { location.href = value; }, 0);
          return { ok: true, note: 'Navigating to ' + value };
        }

        case 'WaitForTimeout': {
          await sleep(Math.max(0, parseInt(value, 10) || 0));
          return { ok: true };
        }

        case 'WaitForUrl': {
          const deadline = Date.now() + (step.timeoutMs || DEFAULT_TIMEOUT);
          while (Date.now() < deadline) {
            if (matches(location.href, value)) return { ok: true, actual: location.href };
            await sleep(POLL);
          }
          return { ok: false, error: 'The url is ' + location.href + ', expected ' + value + '.' };
        }

        case 'AssertUrl': {
          const ok = matches(location.href, value);
          return ok
            ? { ok: true, actual: location.href }
            : { ok: false, error: 'The url is ' + location.href + ', expected ' + value + '.' };
        }

        case 'AssertTitle': {
          const ok = matches(document.title, value);
          return ok
            ? { ok: true, actual: document.title }
            : { ok: false, error: 'The title is "' + document.title + '", expected ' + value + '.' };
        }

        case 'Comment':
          return { ok: true };

        // The studio takes the picture itself, off the back of an ok. Nothing to do in here,
        // and notably no element to wait for.
        case 'Screenshot':
          return { ok: true };
      }

      // Everything below needs an element, so say so plainly rather than hunting for "null".
      if (!step.selector) {
        return { ok: false, error: 'This step has no selector, so there is nothing to act on.' };
      }
      const wantHidden = action === 'AssertHidden' || action === 'WaitForHidden';
      const needsInteraction = /^(Click|DoubleClick|RightClick|Check|Uncheck|Select|Fill|Type|Press|Hover|Upload)$/.test(action);

      if (action === 'AssertCount') {
        const resolved = resolveRoot(step.frames);
        if (resolved.error) return { ok: false, error: resolved.error };
        const count = findAll(step.selector, resolved.root).length;
        const want = parseInt(value, 10);
        return count === want
          ? { ok: true, actual: String(count) }
          : { ok: false, error: 'Found ' + count + ', expected ' + want + '.', actual: String(count) };
      }

      const found = await waitFor(step, { wantHidden: wantHidden, needsInteraction: needsInteraction });
      if (found.error) return { ok: false, error: found.error };

      const el = found.el;

      switch (action) {
        case 'AssertHidden':
        case 'WaitForHidden':
          return { ok: true };

        case 'AssertVisible':
        case 'WaitForSelector':
          return { ok: true };

        case 'AssertText': {
          const actual = directText(el);
          return matches(actual, value)
            ? { ok: true, actual: actual }
            : { ok: false, actual: actual, error: 'Text is "' + actual + '", expected to contain "' + value + '".' };
        }

        case 'AssertValue': {
          const actual = el.value !== undefined ? el.value : directText(el);
          return matches(actual, value)
            ? { ok: true, actual: String(actual) }
            : { ok: false, actual: String(actual), error: 'Value is "' + actual + '", expected "' + value + '".' };
        }

        case 'AssertChecked': {
          const actual = !!el.checked;
          const want = value.toLowerCase() !== 'false';
          return actual === want
            ? { ok: true, actual: String(actual) }
            : { ok: false, actual: String(actual), error: 'It is ' + (actual ? 'checked' : 'not checked') + '.' };
        }

        case 'ScrollIntoView':
          scrollTo(el);
          return { ok: true };

        case 'Focus':
          el.focus();
          return { ok: true };

        case 'Hover':
          scrollTo(el);
          mouse(el, 'mouseover');
          mouse(el, 'mousemove');
          return { ok: true };

        case 'Click':
          scrollTo(el);
          el.click();
          return { ok: true };

        case 'DoubleClick':
          scrollTo(el);
          el.click();
          el.click();
          mouse(el, 'dblclick');
          return { ok: true };

        case 'RightClick':
          scrollTo(el);
          mouse(el, 'contextmenu', { button: 2 });
          return { ok: true };

        case 'Check':
        case 'Uncheck': {
          scrollTo(el);
          const want = action === 'Check';
          if (!!el.checked !== want) el.click();
          return !!el.checked === want
            ? { ok: true }
            : { ok: false, error: 'It would not ' + (want ? 'tick' : 'untick') + '.' };
        }

        case 'Fill':
          scrollTo(el);
          el.focus();
          setValue(el, value);
          return { ok: true };

        case 'Type': {
          scrollTo(el);
          el.focus();
          setValue(el, '');
          for (const ch of value) {
            el.dispatchEvent(new KeyboardEvent('keydown', { key: ch, bubbles: true }));
            setValue(el, (el.value || '') + ch);
            el.dispatchEvent(new KeyboardEvent('keyup', { key: ch, bubbles: true }));
            await sleep(10);
          }
          return { ok: true };
        }

        case 'Press': {
          const parts = value.split('+');
          const key = parts[parts.length - 1];
          const init = {
            key: key,
            bubbles: true,
            cancelable: true,
            ctrlKey: parts.indexOf('Control') >= 0,
            shiftKey: parts.indexOf('Shift') >= 0,
            altKey: parts.indexOf('Alt') >= 0
          };
          el.focus();
          el.dispatchEvent(new KeyboardEvent('keydown', init));
          el.dispatchEvent(new KeyboardEvent('keyup', init));
          if (key === 'Enter' && el.form && typeof el.form.requestSubmit === 'function') {
            el.form.requestSubmit();
          }
          return { ok: true };
        }

        case 'Select': {
          scrollTo(el);
          const wanted = value.split(',').map((v) => v.trim()).filter(Boolean);
          let hit = 0;
          for (const option of el.options || []) {
            // Fall back from the option's value to its visible label, as the runner always has.
            option.selected = wanted.indexOf(option.value) >= 0 || wanted.indexOf(norm(option.text)) >= 0;
            if (option.selected) hit++;
          }
          fire(el, 'input');
          fire(el, 'change');
          return hit
            ? { ok: true }
            : { ok: false, error: 'No option matched "' + value + '".' };
        }

        case 'Upload':
          return {
            ok: false,
            error: 'File uploads cannot be driven from inside the page. Do this step by hand.'
          };

        default:
          return { ok: false, error: 'This player does not know the action "' + action + '".' };
      }
    } catch (err) {
      return { ok: false, error: String((err && err.message) || err) };
    }
  }

  /*
   * Shows the user which element a selector actually picks out. Replaces Playwright's own
   * highlight, and answers the question the studio asks: how many matched?
   */
  function highlight(selector, frames) {
    const resolved = resolveRoot(frames);
    if (resolved.error) return { count: 0, error: resolved.error };

    const all = findAll(selector, resolved.root);
    const shown = all.filter(visible);

    if (shown.length) {
      const el = shown[0];
      scrollTo(el);
      const box = document.createElement('div');
      box.setAttribute('data-pws-overlay', '');
      const r = el.getBoundingClientRect();
      box.style.cssText =
        'position:fixed;pointer-events:none;z-index:2147483646;border:2px solid #56be74;'
        + 'background:rgba(86,190,116,.15);border-radius:3px;transition:opacity .3s;'
        + 'left:' + (r.left - 2) + 'px;top:' + (r.top - 2) + 'px;'
        + 'width:' + (r.width + 4) + 'px;height:' + (r.height + 4) + 'px;';
      document.body.appendChild(box);
      setTimeout(() => { box.style.opacity = '0'; }, 1200);
      setTimeout(() => box.remove(), 1600);
    }

    return { count: all.length, visible: shown.length };
  }

  window.__pwsPlayer = { run: run, findAll: findAll, visible: visible, matches: matches, highlight: highlight };
})();
