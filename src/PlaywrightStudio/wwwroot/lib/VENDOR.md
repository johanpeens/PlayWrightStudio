# Vendored JavaScript

Nothing here is fetched at runtime. Every file was downloaded once, committed, and is served by
the studio itself — so the app works offline, behind a corporate proxy, and on a machine that
cannot reach a CDN. Adding a `<script src="https://some-cdn/...">` anywhere in this project is a
bug, not a shortcut.

Upgrading is a deliberate act: download the new file, replace it here, update the version below,
and re-run the verification in the plan.

| Library | Version | Licence | Global | Source |
| --- | --- | --- | --- | --- |
| html2canvas | 1.4.1 | MIT | `html2canvas` | `https://cdnjs.cloudflare.com/ajax/libs/html2canvas/1.4.1/html2canvas.min.js` |
| rrweb | 2.1.6 | MIT | `rrweb` | `https://cdn.jsdelivr.net/npm/rrweb@2.1.6/dist/rrweb.umd.min.cjs` |
| rrweb (styles) | 2.1.6 | MIT | — | `https://cdn.jsdelivr.net/npm/rrweb@2.1.6/dist/style.min.css` |

## What each is for

**html2canvas** renders the live DOM to a canvas, which is how a run takes screenshots now that
there is no browser process to ask. It draws the page rather than capturing pixels, so
cross-origin iframes and some canvas content come out blank — good enough for a run report and a
manual, not a substitute for a real screenshot.

**rrweb** records DOM mutations during a run so it can be replayed and scrubbed afterwards. It
replaces the old mp4 video and `trace.zip`, and unlike a video you can pause it and inspect the
DOM at the moment something broke. It is not a video file and cannot be turned into one.

## Size

About 465 KB in total, against the 92 MB Playwright node driver and 157 MB ffmpeg download that
this architecture removes.

## Note on rrweb's source

rrweb is not published on cdnjs, so it was taken from jsdelivr's npm mirror. That only affects
where it was fetched from once — the file lives here now and is never requested again.
