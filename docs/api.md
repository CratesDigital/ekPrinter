# ekPrinter HTTP API

ekPrinter listens on **`http://127.0.0.1:8421`** on the computer with the
printers. Your web application calls it from the operator's browser with
`fetch`. Every request and response body is JSON; every response has an `ok`
field.

## Before you start: four things that look like "the agent is not running"

1. **Call `127.0.0.1`, not `localhost`.** Both are accepted, but `127.0.0.1`
   avoids a DNS lookup and any IPv6 surprises. Any other hostname is refused
   with `421` (that is the DNS-rebinding defence, see [design](design.md)).
2. **Chrome asks once per site.** Recent Chrome versions show a one-time
   permission prompt before a public page may reach a loopback address
   ("Local Network Access"). If the operator blocks it, every call fails with a
   network error. The agent also answers the older Private Network Access
   preflight (`Access-Control-Allow-Private-Network: true`).
3. **Send an `Origin` header and pair first.** Browsers send it for you on
   `fetch`. Everything except `/v1/ping` and `/v1/pair` answers `403
   not_paired` until the operator has paired your site.
4. **Allow the agent in your Content-Security-Policy** —
   `connect-src http://127.0.0.1:8421` — or the browser blocks the call before
   it leaves the page.

## Errors

A failed call answers with an HTTP error status and:

```json
{ "ok": false, "error": "printer_not_found", "message": "There is no printer called \"XP-80\" on this computer." }
```

`error` is a stable code for your code to branch on; `message` is an English
sentence for a log or a developer. Show your own words to the operator.

| Status | `error` | Meaning |
|---|---|---|
| 400 | `bad_request`, `bad_base_url`, `bad_page`, `bad_margins`, `bad_origin` | The request is malformed; `message` says how |
| 403 | `not_paired` | This origin is not paired |
| 403 | `wrong_code` | Pairing code incorrect |
| 404 | `printer_not_found` | No printer by that name, or no default printer |
| 409 | `printer_unavailable` | Windows reports the printer unavailable |
| 429 | `too_many_attempts` | Five wrong pairing codes; wait `retry_after_seconds` |
| 500 | `print_failed`, `load_failed`, `load_timeout`, `measure_failed` | The job failed; `message` has Windows' reason |
| 503 | `webview2_missing` | The WebView2 Runtime is not installed: HTML printing is unavailable, raw printing still works |

---

## `GET /v1/ping`

Open to every origin. Is the agent there, and is this site paired?

```json
{ "ok": true, "agent": "ekprinter", "version": "0.1.0", "paired": false, "webview2": "131.0.2903.70" }
```

`webview2` is the runtime version, or `null` when it is missing (HTML printing
will answer `503`).

## `POST /v1/pair`

Open to every origin. Exchanges the six-digit code shown on the agent's page
(`http://127.0.0.1:8421/`) for a permanent pairing of the calling origin.

```json
{ "code": "482913" }
```
```json
{ "ok": true, "origin": "https://shop.example.com" }
```

The origin paired is the request's `Origin` header — never something in the
body. Five wrong codes and pairing is refused for five minutes (`429`). The
operator removes a paired site from the agent's own page.

## `GET /v1/printers`

Paired origins only. The printers installed for this Windows user.

```json
{
  "ok": true,
  "printers": [
    { "name": "XP-80C", "default": true, "driver": "XP-80C", "port": "USB001", "status": "ready" },
    { "name": "HP LaserJet M404", "default": false, "driver": "HP Universal Printing PCL 6", "port": "IP_192.168.1.20", "status": "offline" }
  ]
}
```

`status` is one of `ready`, `offline`, `paper_out`, `paper_jam`, `door_open`,
`error`. Windows only learns most of these when it next talks to the printer,
so `ready` means "nothing known to be wrong". Store the `name` your user picks:
it is what the print calls take.

## `POST /v1/print/html`

Paired origins only. Renders an HTML document with the Chromium engine
(WebView2) and prints it silently.

```json
{
  "printer": "XP-80C",
  "html": "<!doctype html><html dir=\"rtl\">…</html>",
  "base_url": "https://shop.example.com/sales/1001/receipt",
  "copies": 1,
  "page": { "width_mm": 80 },
  "margins_mm": { "top": 2, "right": 2, "bottom": 2, "left": 2 },
  "color": false,
  "backgrounds": true
}
```

| Field | Default | |
|---|---|---|
| `printer` | Windows' default printer | An installed printer's name |
| `html` | — | Required. A complete document |
| `base_url` | your origin + `/` | Where the document "lives": relative URLs resolve against it. Must be on **your own origin** |
| `copies` | 1 | 1–20 |
| `page` | the printer's default paper | `width_mm` (20–1000) and `height_mm` (20–3000). **Leave out `height_mm` to fit the page to the content** — what a receipt printer needs to cut under the last line |
| `margins_mm` | all 0 | Each 0–100 |
| `color` | true | false prints grayscale |
| `backgrounds` | true | Print CSS backgrounds and colours |

```json
{ "ok": true, "job": "3f9a1c0b7d2e", "fitted_height_mm": 143 }
```

The reply comes once Windows has accepted the job into its queue — not once
the paper is out. `fitted_height_mm` is set when the height was fitted.

**Things to know about the document:**

- **State the page and margins.** The agent does not read CSS `@page`; it
  prints exactly the page you pass. Without `page` the printer's default paper
  is used.
- **Scripts do not run.** Render everything before you send it: serialize the
  DOM after your barcodes and charts are drawn. Canvas content is lost when a
  DOM is serialized — draw barcodes as SVG or `<img>` with a `data:` URL.
- **No cookies.** The agent fetches images, fonts and stylesheets itself, not
  through the operator's browser session. Anything behind a sign-in must be
  embedded in the HTML (a `data:` URL) or it will be missing.
- **Loads only over http(s) and never from the computer itself.** `file:`
  and loopback addresses are refused.
- Web fonts are waited for (up to 5 seconds) before printing, so Arabic is
  printed in its real font and wraps as it did on screen.

## `POST /v1/print/raw`

Paired origins only. Sends bytes to the printer untouched: ESC/POS for receipt
printers, ZPL/TSPL/EPL for label printers.

```json
{ "printer": "XP-80C", "data": "G3AAGfo=", "title": "Open drawer" }
```
```json
{ "ok": true, "job": "a71c9e04b5d3", "bytes": 5 }
```

`data` is base64. The example is the ESC/POS cash-drawer kick `ESC p 0 25 250`
(`1B 70 00 19 FA`) — pin 2; use `1B 70 01 19 FA` for pin 5.

The printer must have a driver that passes raw data through, which nearly every
receipt and label printer driver does (the "Generic / Text Only" driver works
for most ESC/POS printers too).

---

## A minimal integration

```js
const AGENT = 'http://127.0.0.1:8421';

async function agent(path, body) {
  const r = await fetch(AGENT + path, body === undefined ? {} : {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  });
  return r.json();
}

const ping = await agent('/v1/ping');           // throws if the agent is not running
if (!ping.paired) {
  await agent('/v1/pair', { code: prompt('ekPrinter pairing code') });
}

const { printers } = await agent('/v1/printers');

await agent('/v1/print/html', {
  printer: printers[0].name,
  html: '<!doctype html>' + document.documentElement.outerHTML,
  base_url: location.href,
  page: { width_mm: 80 },
  margins_mm: { top: 2, right: 2, bottom: 2, left: 2 },
});
```
