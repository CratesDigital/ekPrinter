# Security

ekPrinter listens on a port and can print to the till's printers — and open
its cash drawer, which is wired to the receipt printer. This document states
what it defends against, what it does not, and how to report something.

## Reporting a vulnerability

Email **info@eickter.com** with "ekPrinter" in the subject. Please do not open
a public issue for anything that would let a third party print or open a
drawer.

We will acknowledge within a week. This is a small team and there is no bounty
programme; credit in the release notes is offered and usually taken.

## What the design defends

**The agent is not reachable from the network.** It binds `127.0.0.1`, never
`0.0.0.0`, and the installer adds no firewall rule.

**Requests must be addressed to loopback by name.** Anything whose `Host` is
not `127.0.0.1:<port>` or `localhost:<port>` is refused. Without this, a site
could point its own hostname at 127.0.0.1 (DNS rebinding) and reach the agent
as same-origin, past every CORS rule.

**The agent page cannot be read or framed by another site.** It holds the
pairing code, so it is served without `Access-Control-Allow-Origin` and with
`X-Frame-Options: DENY`. A web page cannot fetch the code and pair itself, and
cannot trick a click on "Remove" through an invisible frame.

**Web origins are allow-listed by an out-of-band code.** A site can print only
after the operator has read a six-digit code off the agent's own page and
entered it on that site. The origin paired is the browser-supplied `Origin`
header, which a page cannot forge; `null` is never accepted.

**Pairing attempts are rate-limited.** Five wrong codes and `/v1/pair` refuses
everything for five minutes, which puts the expected time to guess a code past
a year and a half.

**Removing a site and printing a test page are agent-page-only.** No site,
paired or not, can unpair another or print a test page.

**Printed documents are contained.** The caller's HTML renders with page
scripts off, cannot navigate or open windows, and can load sub-resources only
over http(s) — never `file:` and never loopback, so a document cannot call the
agent's API or another local service. A document may only claim to live on
its own site's origin.

**What was printed is not kept.** The agent page lists recent jobs — time,
site, printer, result — in memory only, never their content.

## What it does not defend

**Any program running as the same Windows user can call the agent.** Loopback
carries no process identity. Requests with no `Origin` header (curl, a local
script) are accepted. The boundary is the Windows user account: malware
already running as the operator can print through the spooler directly anyway.

**A paired site is trusted to print what it likes**, including raw bytes — a
paired site can open the cash drawer whenever it chooses. Pair only the
applications you use, and remove any you stop using from the agent page.

**Raw jobs are not inspected.** ESC/POS and label-printer commands are passed
through untouched; the agent cannot tell a receipt from a firmware command.

**The config file is readable by the user who owns it.** It holds the pairing
code, the paired origins and the port; no secrets beyond that.

## Scope

In scope: the agent (`*.cs`), its installer (`installer/`), and the release
workflow. Out of scope: the applications that call it, the WebView2 Runtime,
and printer drivers.
