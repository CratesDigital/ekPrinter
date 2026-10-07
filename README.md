# ekPrinter

**Silent printing from web applications to Windows printers.** A small tray
agent on the till: your web page sends it a document, and it prints straight
to the receipt, label or office printer — no print dialog, no per-print trust
prompt, and Arabic that comes out right.

[العربية](README.ar.md) · [HTTP API](docs/api.md) · [Design notes](docs/design.md) · [Security](SECURITY.md)

## The problem it solves

A point of sale cannot show the browser's print dialog for every receipt, and
the browser will not print silently on its own. The usual answer is a local
agent, and the usual agent trusts a site by a signed certificate: buy one, or
copy a self-signed one into every till's install folder with administrator
rights, or watch a trust prompt appear on every print.

ekPrinter trusts a site the way pairing a device works. The operator reads a
six-digit code off the agent's page and types it into the site, once. From
then on that site — and only that site — can print. There is no certificate to
buy, install or renew.

It renders HTML with **Chromium** (the WebView2 engine that ships with
Windows), the same engine that drew the page on screen. Arabic letters stay
joined, right-to-left runs stay in order, web fonts and SVG barcodes print as
they look. And for receipt printers it can make the page exactly as long as
the receipt, so the printer cuts under the last line.

## Set-up, once per computer

1. **Install.** Download `ekPrinter-Setup.exe` from
   [Releases](https://github.com/CratesDigital/ekPrinter/releases) and run it.
   No administrator rights; it installs under the user's own profile and
   starts at every login.
2. **Pair it.** The agent's page opens by itself after installing, showing a
   six-digit code. Enter it in the application you print from. To see the code
   again, open <http://127.0.0.1:8421/> or double-click the tray icon.
3. **Print a test page** from the agent's page, to check the printer and that
   Arabic comes out joined.

One install prints for every site paired with it.

> **Windows will warn that the publisher is unrecognised.** Code signing is
> not in place yet; verify the download against the SHA-256 published on the
> release.

**Requirements:** Windows 10 or 11, 64-bit, with the Microsoft Edge WebView2
Runtime (included in Windows 11 and updated Windows 10; the installer warns if
it is missing). Raw printing works without it.

## Integrating your application

Five endpoints. **[Full contract in `docs/api.md`](docs/api.md).**

```
GET  /v1/ping          is the agent there, is this site paired
POST /v1/pair          six-digit code in, this origin paired
GET  /v1/printers      installed printers, default, status
POST /v1/print/html    HTML document → printer (page size, fit-to-content)
POST /v1/print/raw     bytes → printer (ESC/POS, cash drawer, ZPL/TSPL)
```

```js
await fetch('http://127.0.0.1:8421/v1/print/html', {
  method: 'POST',
  headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify({
    printer: 'XP-80C',
    html: '<!doctype html>' + document.documentElement.outerHTML,
    base_url: location.href,
    page: { width_mm: 80 },          // no height: fit the page to the receipt
    margins_mm: { top: 2, right: 2, bottom: 2, left: 2 },
  }),
});
```

Three things bite integrators, all covered in `docs/api.md`: Chrome asks the
operator once per site before a page may reach `127.0.0.1`; your
Content-Security-Policy must allow `connect-src http://127.0.0.1:8421`; and
the document prints without scripts or cookies, so render it fully and embed
anything behind a sign-in.

## While it is running

The agent sits in the notification area. Right-click for:

| | |
|---|---|
| **Open agent page** | pairing code, paired sites, printers, test print, recent jobs (also double-click) |
| **Run when Windows starts** | on by default; the operator can turn it off |
| **Quit** | stops the agent |

Starting it again while it runs opens its page instead of a second copy.

## Run from source

```powershell
dotnet run
```

Listens on `http://127.0.0.1:8421`, puts an icon in the tray, and opens its
page on first run. Builds need the .NET 8 SDK on Windows.

## Where things live

`%APPDATA%\ekPrinter\agent-config.json` holds the port, the pairing code and
the paired sites; it survives upgrades and uninstalls. The renderer's profile
is under `%LOCALAPPDATA%\ekPrinter`. Details in [design notes](docs/design.md#files).

## Licence

Apache-2.0. See [LICENSE](LICENSE) and [NOTICE](NOTICE).

ekPrinter is an independent project, written from scratch. It is not
affiliated with or derived from QZ Tray.
