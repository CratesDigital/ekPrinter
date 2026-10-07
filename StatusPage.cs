// Copyright 2026 Eickter Software & Supplies
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using System.Net;

namespace EkPrinter;

/// <summary>
/// The agent's front page, served on 127.0.0.1: the pairing code, the paired
/// sites, the printers with a test button each, and the last jobs.
///
/// The installed agent has no console and no window, so this is where the
/// operator reads the pairing code — and where whoever is helping them over the
/// phone looks first when something did not print.
///
/// Self-contained: no external asset, nothing from a caller.
/// </summary>
internal static class StatusPage
{
    public static string Html(AgentConfig config, JobLog jobs)
    {
        var runtime = HtmlPrinter.RuntimeVersion();
        var runtimeBlock = runtime is null
            ? "<div class=\"bad\">The Microsoft Edge WebView2 Runtime is not installed, so documents cannot be "
              + "printed (raw printing still works). Install it from "
              + "<a href=\"https://developer.microsoft.com/microsoft-edge/webview2/\">Microsoft</a>, "
              + "then restart ekPrinter.</div>"
            : "";

        var pairedOrigins = config.PairedSnapshot();
        var paired = pairedOrigins.Count == 0
            ? "<p class=\"muted\">No site paired yet — enter the code above on the site you want to print from.</p>"
            : "<ul class=\"rows\">" + string.Join("", pairedOrigins.Select(o =>
                  "<li><span class=\"mono\">" + Esc(o) + "</span>"
                  + "<button class=\"btn small\" data-unpair=\"" + Esc(o) + "\">Remove</button></li>")) + "</ul>";

        string printers;
        try
        {
            var list = PrinterCatalog.List();
            printers = list.Count == 0
                ? "<p class=\"muted\">Windows has no printers installed for this user.</p>"
                : "<ul class=\"rows\">" + string.Join("", list.Select(p =>
                      "<li><span><strong>" + Esc(p.Name) + "</strong>"
                      + (p.IsDefault ? " <span class=\"tag\">default</span>" : "")
                      + (p.Status == "ready" ? "" : " <span class=\"tag warn\">" + Esc(p.Status.Replace('_', ' ')) + "</span>")
                      + "<br><span class=\"muted mono\">" + Esc(p.Driver) + " · " + Esc(p.Port) + "</span></span>"
                      + "<button class=\"btn small\" data-test=\"" + Esc(p.Name) + "\""
                      + (runtime is null ? " disabled" : "") + ">Test print</button></li>")) + "</ul>";
        }
        catch (Exception ex)
        {
            printers = "<div class=\"bad\">Could not read the printer list: " + Esc(ex.Message) + "</div>";
        }

        var recent = jobs.Snapshot();
        var jobRows = recent.Count == 0
            ? "<p class=\"muted\">Nothing printed since the agent started.</p>"
            : "<table><thead><tr><th>Time</th><th>Site</th><th>Printer</th><th>Kind</th><th>Result</th></tr></thead><tbody>"
              + string.Join("", recent.Select(j =>
                  "<tr><td class=\"mono\">" + j.At.ToLocalTime().ToString("HH:mm:ss") + "</td>"
                  + "<td>" + Esc(j.Origin) + "</td><td>" + Esc(j.Printer) + "</td><td>" + Esc(j.Kind) + "</td>"
                  + "<td class=\"" + (j.Ok ? "ok" : "err") + "\">" + Esc(j.Message) + "</td></tr>"))
              + "</tbody></table>";

        return $$"""
            <!doctype html>
            <html lang="en">
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width, initial-scale=1">
              <title>ekPrinter</title>
              <style>
                :root { color-scheme: light dark; }
                body { font-family: system-ui, -apple-system, "Segoe UI", sans-serif;
                       margin: 0; padding: 32px 16px; background: #f8fafc; color: #0f172a; }
                .card { background: #fff; border: 1px solid #e2e8f0; border-radius: 14px;
                        padding: 28px; width: 100%; max-width: 620px; margin: 0 auto; box-sizing: border-box;
                        box-shadow: 0 8px 24px rgba(15,23,42,.06); }
                h1 { font-size: 18px; margin: 0 0 2px; }
                h2 { font-size: 13px; text-transform: uppercase; letter-spacing: .06em;
                     color: #64748b; margin: 26px 0 8px; }
                p  { font-size: 13px; color: #475569; margin: 0 0 10px; line-height: 1.55; }
                .muted { color: #94a3b8; }
                .mono { font-family: ui-monospace, Consolas, monospace; font-size: 12px; }
                .addr { font-family: ui-monospace, Consolas, monospace; font-size: 12px; color: #94a3b8; }
                .code { font-family: ui-monospace, Consolas, monospace; font-size: 34px;
                        font-weight: 700; letter-spacing: .22em; color: #0f172a;
                        background: #f1f5f9; border: 1px solid #e2e8f0; border-radius: 10px;
                        padding: 14px; text-align: center; }
                .rows { list-style: none; margin: 0; padding: 0; font-size: 13px; }
                .rows li { display: flex; justify-content: space-between; align-items: center; gap: 12px;
                           padding: 9px 0; border-bottom: 1px solid #f1f5f9; }
                .tag { font-size: 11px; font-weight: 600; padding: 1px 7px; border-radius: 999px;
                       background: #eff6ff; color: #1d4ed8; border: 1px solid #bfdbfe; }
                .tag.warn { background: #fffbeb; color: #92400e; border-color: #fde68a; }
                .bad { font-size: 13px; padding: 11px 13px; border-radius: 8px; margin-bottom: 12px;
                       background: #fef2f2; color: #991b1b; border: 1px solid #fecaca; }
                table { width: 100%; border-collapse: collapse; font-size: 12px; }
                th { text-align: start; color: #64748b; font-weight: 600; padding: 6px 4px; border-bottom: 1px solid #e2e8f0; }
                td { padding: 6px 4px; border-bottom: 1px solid #f1f5f9; vertical-align: top; overflow-wrap: anywhere; }
                td.ok { color: #166534; } td.err { color: #991b1b; }
                .btn { font: inherit; font-size: 13px; font-weight: 600; padding: 8px 14px; flex-shrink: 0;
                       border-radius: 8px; border: 1px solid #cbd5e1; background: #fff; color: #0f172a; cursor: pointer; }
                .btn.small { font-size: 12px; padding: 5px 10px; }
                .btn:disabled { opacity: .5; cursor: default; }
                #notice { display: none; font-size: 13px; padding: 10px 12px; border-radius: 8px; margin-top: 12px; }
                @media (prefers-color-scheme: dark) {
                  body { background: #0f172a; color: #e2e8f0; }
                  .card { background: #1e293b; border-color: #334155; }
                  .code { background: #0f172a; border-color: #334155; color: #e2e8f0; }
                  p { color: #94a3b8; }
                  .rows li, td { border-color: #334155; } th { border-color: #475569; }
                  .btn { background: #0f172a; border-color: #475569; color: #e2e8f0; }
                }
              </style>
            </head>
            <body>
              <div class="card">
                <h1>ekPrinter</h1>
                <p class="addr">Running on http://127.0.0.1:{{config.Port}}</p>
                {{runtimeBlock}}

                <h2>Pairing code</h2>
                <div class="code">{{config.PairingCode}}</div>
                <p style="margin-top:10px;">
                  On the site you print from, open its <strong>printer settings</strong> and enter this
                  code. Only a site you pair can print to this computer or open its cash drawer.
                </p>

                <h2>Paired sites</h2>
                {{paired}}

                <h2>Printers</h2>
                {{printers}}
                <div id="notice"></div>

                <h2>Recent jobs</h2>
                {{jobRows}}

                <h2>Leave this running</h2>
                <p>
                  ekPrinter starts automatically when you sign in to Windows. Close this tab — it keeps
                  running. To come back, open
                  <a href="http://127.0.0.1:{{config.Port}}/">127.0.0.1:{{config.Port}}</a>
                  or double-click the printer icon by the clock.
                </p>
              </div>

              <script>
                const notice = document.getElementById('notice');

                function show(ok, text) {
                  notice.style.display = 'block';
                  notice.style.background = ok ? '#f0fdf4' : '#fef2f2';
                  notice.style.color = ok ? '#166534' : '#991b1b';
                  notice.style.border = '1px solid ' + (ok ? '#bbf7d0' : '#fecaca');
                  notice.textContent = text;
                }

                async function post(path, body) {
                  const r = await fetch(path, {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify(body),
                  });
                  return r.json().catch(() => ({ ok: false, message: 'The agent did not answer.' }));
                }

                document.querySelectorAll('[data-test]').forEach(b => b.addEventListener('click', async () => {
                  b.disabled = true;
                  const d = await post('/v1/test-print', { printer: b.dataset.test });
                  show(d.ok, d.ok ? 'Test page sent to ' + b.dataset.test + '.' : (d.message || 'Printing failed.'));
                  b.disabled = false;
                }));

                document.querySelectorAll('[data-unpair]').forEach(b => b.addEventListener('click', async () => {
                  if (!confirm('Stop ' + b.dataset.unpair + ' from printing to this computer?')) return;
                  await post('/v1/unpair', { origin: b.dataset.unpair });
                  location.reload();
                }));
              </script>
            </body>
            </html>
            """;
    }

    /// <summary>
    /// The test page: Arabic and English side by side, because whether Arabic
    /// comes out joined and right-to-left is the first thing to check on a new
    /// till, and a ruler so the operator can see whether the paper width is right.
    /// </summary>
    public static string TestPageHtml(string printer)
    {
        var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        return $$"""
            <!doctype html>
            <html lang="en">
            <head>
              <meta charset="utf-8">
              <title>ekPrinter test page</title>
              <style>
                body { font-family: "Segoe UI", Tahoma, sans-serif; margin: 0; padding: 4mm; color: #000; }
                h1 { font-size: 16px; margin: 0 0 2mm; }
                p { font-size: 12px; margin: 0 0 1.5mm; }
                .ar { direction: rtl; text-align: right; font-size: 14px; }
                .ruler { display: flex; margin-top: 3mm; border-top: 1px solid #000; }
                .ruler span { flex: 0 0 10mm; border-left: 1px solid #000; height: 3mm; font-size: 8px; padding-left: 1px; box-sizing: border-box; }
              </style>
            </head>
            <body>
              <h1>ekPrinter test page</h1>
              <p class="ar">صفحة اختبار — إذا كانت الحروف العربية متصلة ومن اليمين إلى اليسار فالطباعة سليمة.</p>
              <p>Printer: {{Esc(printer)}}</p>
              <p>Printed: {{now}}</p>
              <div class="ruler"><span>0</span><span>10</span><span>20</span><span>30</span><span>40</span><span>50</span><span>60</span><span>70</span><span>80</span><span>90</span><span>100</span><span>110</span><span>120</span><span>130</span><span>140</span><span>150</span><span>160</span><span>170</span><span>180</span><span>190</span></div>
              <p style="margin-top:2mm;">Each mark is 10 mm. The last full mark shows the printable width.</p>
            </body>
            </html>
            """;
    }

    private static string Esc(string value) => WebUtility.HtmlEncode(value);
}
