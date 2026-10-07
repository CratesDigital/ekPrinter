# Changelog

Notable changes to ekPrinter. Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/);
versions follow [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Fixed
- Pages as small as 5 × 5 mm are accepted, so small barcode labels print;
  the minimum was 20 mm.

## [0.1.0] — 2026-10-07

### Added
- First version of the agent: a per-user Windows tray app on
  `http://127.0.0.1:8421`.
- Pairing by a six-digit code shown on the agent's page, rate-limited to five
  attempts per five minutes.
- `GET /v1/printers`: installed printers with driver, port and status.
- `POST /v1/print/html`: silent HTML printing through WebView2, with explicit
  page size and margins, and a page fitted to the content's height for
  receipt printers.
- `POST /v1/print/raw`: bytes straight to the spooler (ESC/POS, cash drawer,
  label languages), falling back from `RAW` to `XPS_PASS` for v4 drivers.
- Agent page: pairing code, paired sites with removal, printers with a test
  page (English and Arabic), and the last 50 jobs.
- Inno Setup installer (per user, no administrator rights) and a GitHub
  Actions workflow that builds it on every push and releases it on a `v*` tag.

### Security
- Requests not addressed to `127.0.0.1` or `localhost` are refused (DNS
  rebinding).
- CORS headers are sent only on the endpoints a site calls, never on the agent
  page that shows the pairing code; the agent page cannot be framed.
