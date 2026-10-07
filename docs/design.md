# ekPrinter — design

Status: **0.1, untested on hardware.** The design below is what the code
does; the points marked *to verify* are what the first runs against real
printers have to confirm.

## What it replaces, and why

Web applications that print silently — no browser print dialog, straight to
the receipt printer, cash drawer open — have mostly used QZ Tray. QZ Tray
trusts a site by a signed certificate: without one bought from its vendor, or a
self-signed one copied into every workstation's install folder with
administrator rights, it shows a trust prompt on every print.

ekPrinter trusts a site the way [ekSigner](https://github.com/CratesDigital/ekSigner)
does: the operator reads a six-digit code off the agent's own page and enters
it on the site, once. The browser stamps every request with the site's
`Origin`, which a web page cannot forge, so nothing needs signing and nothing
prompts per print.

It also prints HTML with Chromium (WebView2) instead of a second, lesser
renderer, which is what makes Arabic and other right-to-left receipts come out
joined and in the right order.

## Shape

```
  web application page (https://shop.example.com)
        │  fetch, CORS + Private/Local Network Access
        ▼
  ekPrinter   http://127.0.0.1:8421   (tray app, per Windows user)
        ├── HTML  ──► WebView2 (off-screen) ──► PrintAsync ──► spooler ──► printer
        └── raw   ──► winspool WritePrinter (RAW / XPS_PASS) ──────────► printer
```

A per-user tray app, not a service: printers, the tray and the operator all
live in the interactive session; a service runs in session 0, which has none
of them. Installing per user also needs no administrator rights.

## Transport

`http://127.0.0.1:8421`, not HTTPS. Loopback is a *potentially trustworthy
origin*, so an HTTPS page may call it without mixed-content blocking — there
is no certificate to install or accept.

Port 8421 is unassigned by IANA (8418–8422 are free) and has no common
unofficial user; it sits next to ekSigner's 8420. It can be changed in the
config file.

Bound to `127.0.0.1` only. Binding `0.0.0.0` would let anyone on the shop's
network print to the till and open its cash drawer.

## Security model

Three layers, in the order a request meets them:

**Host check.** A request whose `Host` is not `127.0.0.1:<port>` or
`localhost:<port>` is refused with `421`. This defeats DNS rebinding: a site
can point its own hostname at 127.0.0.1, and its page then reaches the agent
as *same-origin* — but the `Host` header still carries the site's name.

**CORS only where a site needs it.** `Access-Control-Allow-Origin` is echoed
for every origin on the site-facing endpoints (`/v1/ping`, `/v1/pair`,
`/v1/printers`, `/v1/print/*`) — it has to be, or a not-yet-paired site could
not pair. It is **never** sent on the agent's own page, because that page holds
the pairing code: with the header, any web page could fetch it and pair itself.
The agent page also refuses to be framed.

**Pairing.** Every printing endpoint checks the caller's `Origin` against the
paired list. Pairing takes the six-digit code, from a cryptographic RNG, and is
rate-limited: five wrong codes, five minutes of `429`. The origin paired is
the request's `Origin` header; `null` (sandboxed frames, `file:` pages) is
never accepted. Removing a site, and printing a test page, can only be done
from the agent's own page.

Requests with no `Origin` at all are allowed: they come from a process on the
same computer (curl, a script), and the boundary here is the Windows user, as
it is for ekSigner. See [SECURITY.md](../SECURITY.md).

## HTML printing

One off-screen WebView2, one job at a time.

**Loading.** The document is not written into a blank page. It is served from
memory at a URL **on the calling site** — the request's `base_url` (default:
the site's root) with an `ekprinter_job=<random>` query marker — and that one
URL is intercepted with `WebResourceRequested` before it reaches the network.
So the page has the site's own origin: relative image paths, the site's fonts
and stylesheets resolve exactly as they did in the operator's browser, with no
`<base>` rewriting and no CORS failures on fonts. The query marker keeps every
job's URL unique without changing relative resolution, which ignores the query.

A site may only stand in for itself: `base_url` must be on the caller's origin.

**What the page may do.** Page scripts are disabled
(`IsScriptEnabled = false`); the agent's own measuring script still runs
through `ExecuteScriptAsync`. Top-level navigation other than the job's own
URL is cancelled; new windows, context menus, dev tools and accelerator keys
are off. Sub-resources load over http(s) only — never `file:`, never loopback,
so a job cannot call the agent's API or another local service.

The WebView2 profile is the agent's own (`%LOCALAPPDATA%\ekPrinter\WebView2`),
so it carries no cookies; anything behind a sign-in must be embedded.

**Before printing:** wait for `load`; switch to print media through the
DevTools protocol (`Emulation.setEmulatedMedia`) so the print stylesheet
applies; wait up to 5 s for `document.fonts.status === "loaded"`, because the
load event does not wait for web fonts and an Arabic fallback font changes
where every line wraps.

**Page size.** The caller states the page and margins; CSS `@page` is not
read, because WebView2's `PrintAsync` always applies explicit margins
(defaulting to 1 cm) and an explicit size is the only deterministic contract.
Margins default to 0 rather than WebView2's 1 cm.

**Fit to content.** With `page.width_mm` and no `height_mm`, the document's
root is constrained to the printable width (page width less side margins),
its height measured in CSS px (1 px = 1/96 in), and the page made that tall
plus the vertical margins and 1 mm of slack, clamped to 20–3000 mm. A thermal
printer then gets one page exactly as long as the receipt and cuts once,
under the last line, instead of at its driver's default paper length.
*To verify on real drivers:* that each common thermal driver (Xprinter, Epson
TM, Rongta, Sunmi) accepts a custom page of arbitrary length and cuts at its
end. Where one does not, the fallback is to render to an image and send it as
an ESC/POS raster with a cut command through the raw path.

`PrintAsync` resolves once the spooler has the job, not once paper is out.

**Recovery.** If the WebView2 browser process exits, the WebView is discarded
and the next job builds a new one. A renderer crash needs nothing: the next
navigation starts a new renderer.

## Raw printing

`OpenPrinter` → `StartDocPrinter` → `WritePrinter` → `EndDocPrinter`, by
printer name, through the spooler. Datatype `RAW`; a v4 class driver refuses
`RAW` with `ERROR_INVALID_DATATYPE` and wants `XPS_PASS` for the same
pass-through, so that is retried automatically. One raw job at a time, so a
receipt and a drawer kick from two tabs cannot interleave.

## Threads

Kestrel serves requests on the thread pool. WebView2 is an STA COM component
bound to the thread that created it, and the tray icon needs a message loop,
so both live on one dedicated STA thread (`UiThread`), with a hidden,
off-screen tool window as WebView2's parent. HTML jobs are marshalled onto it;
`await`s inside a job resume there through the WinForms synchronization
context. The window is shown, off-screen, rather than hidden, because a hidden
parent tells WebView2 it is invisible and an invisible WebView2 may stop
rendering. *To verify:* that printing from the off-screen window is reliable
on Windows 10 and 11.

## Files

| Where | What |
|---|---|
| `%LOCALAPPDATA%\Programs\ekPrinter` | The install (overwritten on upgrade) |
| `%APPDATA%\ekPrinter\agent-config.json` | Port, pairing code, paired sites, run-at-login. Kept on uninstall |
| `%LOCALAPPDATA%\ekPrinter\WebView2` | The renderer's profile, rebuilt on demand |
| `%LOCALAPPDATA%\ekPrinter\agent-error.log` | Fatal startup errors |
| `HKCU\…\CurrentVersion\Run\ekPrinter` | Autostart, written by the agent so the tray menu can change it |

The last 50 jobs are kept in memory for the agent page — time, site, printer,
kind and result. **Never the content**: receipts carry customer names and
amounts, and a copy on the till would be a second set of books nobody secures.
