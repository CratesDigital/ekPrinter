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

using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using EkPrinter;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

var config = AgentConfig.Load();

// One agent per user session; a second could not bind the port anyway.
// Starting an agent that is already running is a request to SEE it, so open
// its page and leave quietly rather than raising an error dialog.
using var instanceLock = new Mutex(initiallyOwned: true, @"Local\ekprinter", out var isFirstInstance);
if (!isFirstInstance)
{
    OpenPage(config.Port);
    return 0;
}

Autostart.Apply(config);

var pairing = new PairingGuard();
var jobs = new JobLog();
var ui = new UiThread();
var html = new HtmlPrinter(ui, AgentConfig.DataDir);
var raw = new RawPrinter();

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
// Loopback only. Binding 0.0.0.0 would let anyone on the shop's network print
// to the till and open its cash drawer.
builder.WebHost.UseUrls($"http://127.0.0.1:{config.Port}");
// An A4 invoice with an embedded logo is a few hundred KB; this leaves room
// for image-heavy documents without accepting anything absurd.
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 16 * 1024 * 1024);

var app = builder.Build();

// ── Host check ───────────────────────────────────────────────────
//
// DNS rebinding: a site can point its own hostname at 127.0.0.1, and its page
// then reaches this port as *same-origin* — no CORS, and able to read the
// agent page and its pairing code. The Host header still carries the site's
// name, so anything not addressed to loopback by name is refused.
app.Use(async (context, next) =>
{
    var host = context.Request.Host.Value ?? "";
    if (host != $"127.0.0.1:{config.Port}" && host != $"localhost:{config.Port}")
    {
        context.Response.StatusCode = StatusCodes.Status421MisdirectedRequest;
        return;
    }
    await next();
});

// ── CORS + Private Network Access ────────────────────────────────
//
// Loopback is a "potentially trustworthy origin", so an HTTPS page may call
// http://127.0.0.1 without mixed-content blocking — no certificate to install.
//
// Chrome sends `Access-Control-Request-Private-Network: true` on the preflight
// for a public → loopback request and refuses the call unless the answer says
// `Access-Control-Allow-Private-Network: true`. Without it the failure looks
// exactly like the agent not running.
//
// Only the endpoints a site calls get CORS headers. The agent's own page holds
// the pairing code: answer it with Access-Control-Allow-Origin and any web page
// can fetch it, read the code, and pair itself.
var crossOriginPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    "/v1/ping", "/v1/pair", "/v1/printers", "/v1/print/html", "/v1/print/raw",
};

app.Use(async (context, next) =>
{
    if (!crossOriginPaths.Contains(context.Request.Path.Value ?? ""))
    {
        // The agent page cannot be framed either, so its buttons cannot be
        // clicked through an invisible frame on someone else's page.
        context.Response.Headers["X-Frame-Options"] = "DENY";
        context.Response.Headers["Content-Security-Policy"] = "frame-ancestors 'none'";
        await next();
        return;
    }

    var origin = context.Request.Headers.Origin.ToString();

    // Echoed for every origin, paired or not, on these endpoints. CORS is not
    // the boundary here — Allowed() is. Gating this header on pairing makes
    // pairing impossible: /v1/pair's own preflight comes from an origin that
    // is not paired yet.
    if (!string.IsNullOrEmpty(origin))
    {
        context.Response.Headers["Access-Control-Allow-Origin"] = origin;
        context.Response.Headers["Vary"] = "Origin";
    }
    context.Response.Headers["Access-Control-Allow-Headers"] = "Content-Type";
    context.Response.Headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS";
    context.Response.Headers["Access-Control-Allow-Private-Network"] = "true";

    if (HttpMethods.IsOptions(context.Request.Method))
    {
        context.Response.StatusCode = StatusCodes.Status204NoContent;
        return;
    }

    await next();
});

// The agent's own page, served from this port. Both spellings of loopback,
// because the operator types one or the other into the address bar.
bool IsSelf(string origin) =>
    origin == $"http://127.0.0.1:{config.Port}" || origin == $"http://localhost:{config.Port}";

// Printing requires a paired origin. No Origin at all means a process on this
// machine (curl, a script) — the browser is the untrusted caller, not the shell.
bool Allowed(HttpContext ctx)
{
    var origin = ctx.Request.Headers.Origin.ToString();
    return string.IsNullOrEmpty(origin) || IsSelf(origin) || config.IsPaired(origin);
}

// The agent page's own actions: removing a site, printing a test page. No site
// can do these, paired or not.
bool FromAgentPage(HttpContext ctx)
{
    var origin = ctx.Request.Headers.Origin.ToString();
    return string.IsNullOrEmpty(origin) || IsSelf(origin);
}

IResult Error(int status, string code, string message) =>
    Results.Json(new { ok = false, error = code, message }, statusCode: status);

IResult Forbidden() => Error(403, "not_paired", "This site is not paired with ekPrinter.");

// ── Discovery ────────────────────────────────────────────────────

app.MapGet("/v1/ping", (HttpContext ctx) => Results.Json(new
{
    ok = true,
    agent = "ekprinter",
    version = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0",
    // Whether THIS caller is paired, so a site can skip its pairing box.
    paired = config.IsPaired(ctx.Request.Headers.Origin.ToString()),
    // Without the runtime only raw printing works; a site should say so
    // rather than offer HTML printing that will fail.
    webview2 = HtmlPrinter.RuntimeVersion(),
}));

app.MapGet("/v1/printers", (HttpContext ctx) =>
{
    if (!Allowed(ctx)) return Forbidden();

    try
    {
        var printers = PrinterCatalog.List().Select(p => new
        {
            name = p.Name,
            @default = p.IsDefault,
            driver = p.Driver,
            port = p.Port,
            status = p.Status,
        });
        return Results.Json(new { ok = true, printers });
    }
    catch (Exception ex)
    {
        return Error(500, "printers_unreadable", ex.Message);
    }
});

// ── Printing ─────────────────────────────────────────────────────

app.MapPost("/v1/print/html", async (HttpContext ctx) =>
{
    if (!Allowed(ctx)) return Forbidden();

    var request = await ReadJson<HtmlPrintRequest>(ctx);
    if (request is null || string.IsNullOrWhiteSpace(request.Html))
        return Error(400, "bad_request", "html is required.");

    var origin = ctx.Request.Headers.Origin.ToString();
    var printer = ResolvePrinter(request.Printer);
    if (printer is null) return PrinterNotFound(request.Printer);

    if (!TryDocumentUri(origin, request.BaseUrl, out var documentUri, out var uriProblem))
        return Error(400, "bad_base_url", uriProblem);

    if (!TryPage(request.Page, out var page, out var pageProblem))
        return Error(400, "bad_page", pageProblem);
    if (!TryMargins(request.MarginsMm, out var margins, out var marginProblem))
        return Error(400, "bad_margins", marginProblem);

    var job = new HtmlJob(
        Printer: printer,
        Html: request.Html,
        DocumentUri: documentUri,
        Copies: Math.Clamp(request.Copies ?? 1, 1, 20),
        Page: page,
        Margins: margins,
        Color: request.Color ?? true,
        Backgrounds: request.Backgrounds ?? true);

    var id = NewJobId();
    try
    {
        var result = await html.PrintAsync(job, ctx.RequestAborted);
        jobs.Add(new JobRecord(id, DateTime.UtcNow, OriginLabel(origin), printer, "document", true,
            result.FittedHeightMm is { } h ? $"printed, {h:0} mm long" : "printed"));
        return Results.Json(new { ok = true, job = id, fitted_height_mm = result.FittedHeightMm });
    }
    catch (PrintFailure ex)
    {
        jobs.Add(new JobRecord(id, DateTime.UtcNow, OriginLabel(origin), printer, "document", false, ex.Message));
        var status = ex.Code switch
        {
            "webview2_missing" => 503,
            "printer_unavailable" => 409,
            _ => 500,
        };
        return Error(status, ex.Code, ex.Message);
    }
    catch (Exception ex)
    {
        jobs.Add(new JobRecord(id, DateTime.UtcNow, OriginLabel(origin), printer, "document", false, ex.Message));
        return Error(500, "print_failed", ex.Message);
    }
});

app.MapPost("/v1/print/raw", async (HttpContext ctx) =>
{
    if (!Allowed(ctx)) return Forbidden();

    var request = await ReadJson<RawPrintRequest>(ctx);
    if (request is null || string.IsNullOrWhiteSpace(request.Data))
        return Error(400, "bad_request", "data (base64) is required.");

    byte[] bytes;
    try
    {
        bytes = Convert.FromBase64String(request.Data);
    }
    catch (FormatException)
    {
        return Error(400, "bad_request", "data is not valid base64.");
    }
    if (bytes.Length == 0) return Error(400, "bad_request", "data is empty.");

    var origin = ctx.Request.Headers.Origin.ToString();
    var printer = ResolvePrinter(request.Printer);
    if (printer is null) return PrinterNotFound(request.Printer);

    var id = NewJobId();
    try
    {
        await raw.PrintAsync(printer, bytes, Title(request.Title, "ekPrinter raw job"), ctx.RequestAborted);
        jobs.Add(new JobRecord(id, DateTime.UtcNow, OriginLabel(origin), printer, "raw", true, $"sent {bytes.Length} bytes"));
        return Results.Json(new { ok = true, job = id, bytes = bytes.Length });
    }
    catch (Exception ex)
    {
        jobs.Add(new JobRecord(id, DateTime.UtcNow, OriginLabel(origin), printer, "raw", false, ex.Message));
        return Error(500, "print_failed", ex.Message);
    }
});

// ── Pairing ──────────────────────────────────────────────────────

app.MapPost("/v1/pair", async (HttpContext ctx) =>
{
    // Checked before the code is even read: six digits is only a secret while
    // guessing is expensive.
    if (pairing.LockedFor is { } remaining)
    {
        return Results.Json(new
        {
            ok = false,
            error = "too_many_attempts",
            retry_after_seconds = (int)Math.Ceiling(remaining.TotalSeconds),
            message = $"Too many incorrect pairing codes. Try again in {Math.Ceiling(remaining.TotalMinutes)} minute(s).",
        }, statusCode: StatusCodes.Status429TooManyRequests);
    }

    var origin = ctx.Request.Headers.Origin.ToString();

    // A real web origin only. "null" is what a sandboxed frame or a file://
    // page sends, and pairing it would trust every such page at once.
    if (!Uri.TryCreate(origin, UriKind.Absolute, out var originUri)
        || (originUri.Scheme != Uri.UriSchemeHttps && originUri.Scheme != Uri.UriSchemeHttp)
        || IsSelf(origin))
    {
        return Error(400, "bad_origin", "Pairing must be started from the site you are pairing with.");
    }

    var request = await ReadJson<PairRequest>(ctx);
    if (request is null || request.Code?.Trim() != config.PairingCode)
    {
        pairing.Failed();
        return Error(403, "wrong_code", "That pairing code is not correct.");
    }

    pairing.Succeeded();
    config.Pair(origin);
    return Results.Json(new { ok = true, origin });
});

app.MapPost("/v1/unpair", async (HttpContext ctx) =>
{
    if (!FromAgentPage(ctx)) return Error(403, "forbidden", "Sites are removed from the agent's own page.");

    var request = await ReadJson<UnpairRequest>(ctx);
    if (request is null || string.IsNullOrWhiteSpace(request.Origin))
        return Error(400, "bad_request", "origin is required.");

    return Results.Json(new { ok = config.Unpair(request.Origin) });
});

// ── The agent's own page ─────────────────────────────────────────

app.MapGet("/", () => Results.Content(StatusPage.Html(config, jobs), "text/html; charset=utf-8"));

app.MapPost("/v1/test-print", async (HttpContext ctx) =>
{
    if (!FromAgentPage(ctx)) return Error(403, "forbidden", "Test pages are printed from the agent's own page.");

    var request = await ReadJson<TestPrintRequest>(ctx);
    var printer = ResolvePrinter(request?.Printer);
    if (printer is null) return PrinterNotFound(request?.Printer);

    var id = NewJobId();
    try
    {
        // The printer's own default paper: the test is of the printer and its
        // driver as Windows has them set up, not of a page size we chose.
        await html.PrintAsync(new HtmlJob(
            printer, StatusPage.TestPageHtml(printer), new Uri($"https://ekprinter.invalid/test/{id}"),
            Copies: 1, Page: null, Margins: Margins.None, Color: true, Backgrounds: true), ctx.RequestAborted);
        jobs.Add(new JobRecord(id, DateTime.UtcNow, "agent page", printer, "test page", true, "printed"));
        return Results.Json(new { ok = true, job = id });
    }
    catch (Exception ex)
    {
        jobs.Add(new JobRecord(id, DateTime.UtcNow, "agent page", printer, "test page", false, ex.Message));
        return Error(500, ex is PrintFailure pf ? pf.Code : "print_failed", ex.Message);
    }
});

// ── Start ────────────────────────────────────────────────────────

try
{
    await app.StartAsync();
}
catch (Exception ex)
{
    // Almost always the port: a second copy of the agent is caught by the
    // mutex above, so this means another program holds it.
    StartupError.Report(
        $"ekPrinter could not start on port {config.Port}.\n\n"
        + $"{ex.Message}\n\n"
        + "Another program may be using that port. Change \"Port\" in\n"
        + "%APPDATA%\\ekPrinter\\agent-config.json and start ekPrinter again.");
    return 1;
}

ui.Start(() => new TrayIcon(config));

// Show the page once, on the run that follows installation, where the operator
// still has to read the pairing code. After that the agent starts at every
// login and stays quiet; an app that opens a browser tab each morning gets
// uninstalled.
if (config.PairedSnapshot().Count == 0)
{
    OpenPage(config.Port);
}

// Blocks until Quit in the tray menu ends the message loop.
ui.Join();
await app.StopAsync();
return 0;

// ── Helpers ──────────────────────────────────────────────────────

static void OpenPage(int port)
{
    try
    {
        // UseShellExecute hands the URL to the default browser.
        Process.Start(new ProcessStartInfo($"http://127.0.0.1:{port}/") { UseShellExecute = true });
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[start] could not open a browser: {ex.Message}");
    }
}

// A malformed body is the caller's mistake, answered with a 400 — not a 500
// with a stack trace in the agent's log.
static async Task<T?> ReadJson<T>(HttpContext ctx) where T : class
{
    try
    {
        return await ctx.Request.ReadFromJsonAsync<T>();
    }
    catch (Exception ex) when (ex is JsonException or InvalidOperationException or NotSupportedException or BadHttpRequestException)
    {
        return null;
    }
}

// An empty name means Windows' default printer, so a site can print before
// the operator has chosen one. Otherwise the name has to match an installed
// printer; returning Windows' own spelling keeps the job log consistent.
static string? ResolvePrinter(string? requested)
{
    if (string.IsNullOrWhiteSpace(requested)) return PrinterCatalog.DefaultPrinterName();
    return PrinterCatalog.Find(requested.Trim())?.Name;
}

static IResult PrinterNotFound(string? requested) => Results.Json(new
{
    ok = false,
    error = "printer_not_found",
    message = string.IsNullOrWhiteSpace(requested)
        ? "No printer was named and Windows has no default printer."
        : $"There is no printer called \"{requested}\" on this computer.",
}, statusCode: 404);

// Where the document pretends to live. Same origin as the caller, so relative
// paths and the site's own fonts resolve as they did in the browser; the job
// marker in the query keeps every job's address unique, and leaves relative
// resolution (which ignores the query) untouched.
static bool TryDocumentUri(string origin, string? baseUrl, out Uri documentUri, out string problem)
{
    documentUri = null!;
    problem = "";

    Uri? callerOrigin = null;
    if (!string.IsNullOrEmpty(origin) && !Uri.TryCreate(origin, UriKind.Absolute, out callerOrigin))
    {
        problem = "The request's Origin is not a valid address.";
        return false;
    }

    Uri target;
    if (string.IsNullOrWhiteSpace(baseUrl))
    {
        target = callerOrigin is not null && callerOrigin.Scheme is "http" or "https"
            ? new Uri(callerOrigin, "/")
            : new Uri("https://ekprinter.invalid/");
    }
    else
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var parsed) || parsed.Scheme is not ("http" or "https"))
        {
            problem = "base_url must be an absolute http(s) address.";
            return false;
        }
        // A site may only stand in for itself. Otherwise a paired site could
        // print a document that loads as another site's page.
        if (callerOrigin is not null && callerOrigin.Scheme is "http" or "https"
            && !string.Equals(parsed.GetLeftPart(UriPartial.Authority), callerOrigin.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase))
        {
            problem = "base_url must be on the same site as the page that sent the job.";
            return false;
        }
        target = parsed;
    }

    var uriBuilder = new UriBuilder(target) { Fragment = "" };
    var marker = "ekprinter_job=" + Guid.NewGuid().ToString("N");
    uriBuilder.Query = string.IsNullOrEmpty(uriBuilder.Query) ? marker : uriBuilder.Query.TrimStart('?') + "&" + marker;
    documentUri = uriBuilder.Uri;
    return true;
}

static bool TryPage(PageRequest? requested, out PageSize? page, out string problem)
{
    page = null;
    problem = "";
    if (requested is null) return true;

    if (requested.WidthMm is not (>= 20 and <= 1000))
    {
        problem = "page.width_mm must be between 20 and 1000.";
        return false;
    }
    if (requested.HeightMm is { } h && h is not (>= 20 and <= 3000))
    {
        problem = "page.height_mm must be between 20 and 3000, or omitted to fit the content.";
        return false;
    }

    page = new PageSize(requested.WidthMm.Value, requested.HeightMm);
    return true;
}

static bool TryMargins(MarginsRequest? requested, out Margins margins, out string problem)
{
    margins = Margins.None;
    problem = "";
    if (requested is null) return true;

    double[] values = [requested.Top ?? 0, requested.Right ?? 0, requested.Bottom ?? 0, requested.Left ?? 0];
    if (values.Any(v => v is < 0 or > 100))
    {
        problem = "margins_mm values must be between 0 and 100.";
        return false;
    }

    margins = new Margins(values[0], values[1], values[2], values[3]);
    return true;
}

static string Title(string? requested, string fallback)
{
    var title = (requested ?? "").Trim();
    return title.Length == 0 ? fallback : title.Length > 120 ? title[..120] : title;
}

static string OriginLabel(string origin) => string.IsNullOrEmpty(origin) ? "this computer" : origin;

static string NewJobId() => Guid.NewGuid().ToString("N")[..12];

internal sealed record HtmlPrintRequest(
    [property: JsonPropertyName("printer")] string? Printer,
    [property: JsonPropertyName("html")] string? Html,
    [property: JsonPropertyName("base_url")] string? BaseUrl,
    [property: JsonPropertyName("copies")] int? Copies,
    [property: JsonPropertyName("page")] PageRequest? Page,
    [property: JsonPropertyName("margins_mm")] MarginsRequest? MarginsMm,
    [property: JsonPropertyName("color")] bool? Color,
    [property: JsonPropertyName("backgrounds")] bool? Backgrounds);

internal sealed record PageRequest(
    [property: JsonPropertyName("width_mm")] double? WidthMm,
    [property: JsonPropertyName("height_mm")] double? HeightMm);

internal sealed record MarginsRequest(
    [property: JsonPropertyName("top")] double? Top,
    [property: JsonPropertyName("right")] double? Right,
    [property: JsonPropertyName("bottom")] double? Bottom,
    [property: JsonPropertyName("left")] double? Left);

internal sealed record RawPrintRequest(
    [property: JsonPropertyName("printer")] string? Printer,
    [property: JsonPropertyName("data")] string? Data,
    [property: JsonPropertyName("title")] string? Title);

internal sealed record PairRequest([property: JsonPropertyName("code")] string? Code);

internal sealed record UnpairRequest([property: JsonPropertyName("origin")] string? Origin);

internal sealed record TestPrintRequest([property: JsonPropertyName("printer")] string? Printer);
