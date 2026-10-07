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

using System.Drawing;
using System.Globalization;
using System.Text;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace EkPrinter;

/// <summary>
/// Prints an HTML document silently to a named printer, using WebView2 — the
/// Chromium engine Windows already ships for Edge.
///
/// Chromium because the document was designed in a browser and has to come out
/// of the printer looking like it did there: Arabic joined and shaped,
/// right-to-left runs in the right order, web fonts, SVG barcodes. A second,
/// lesser HTML renderer is exactly what makes other print agents' Arabic
/// receipts come out wrong.
///
/// How a job is loaded matters more than it looks. The document is not written
/// into a blank page. It is served, from memory, at a URL ON the calling site
/// (its own address plus a job marker), intercepted before it reaches the
/// network. So the page has the site's origin: relative image paths, the
/// site's own fonts and stylesheets all resolve exactly as they did in the
/// operator's browser, with no &lt;base&gt; rewriting and no CORS surprises.
///
/// What the page can do is narrowed hard, because it is the caller's HTML
/// running on the till:
///   - page scripts are off; the agent's own measuring script still runs
///   - it cannot navigate anywhere, open windows or show a context menu
///   - sub-resources load only over http(s), never from file:, and never from
///     loopback — the job cannot reach this agent or anything else on the PC
///
/// The WebView2 profile is the agent's own, not the operator's browser
/// profile, so it carries no cookies: anything the document needs must be
/// reachable without a sign-in, or be embedded in it.
/// </summary>
internal sealed class HtmlPrinter
{
    private const double MmPerInch = 25.4;
    private const double CssPxPerInch = 96;

    /// <summary>
    /// A fitted page is never shorter than this. A near-empty document would
    /// otherwise produce a page some drivers reject outright.
    /// </summary>
    private const double MinFittedHeightMm = 20;

    /// <summary>
    /// Nor longer. Thermal drivers cap a page's length (commonly 1–3 m); past
    /// this a "receipt" is more likely a runaway layout than a real sale.
    /// </summary>
    private const double MaxFittedHeightMm = 3000;

    private static readonly TimeSpan LoadTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan FontTimeout = TimeSpan.FromSeconds(5);

    private readonly UiThread _ui;
    private readonly string _profileDir;

    /// <summary>One job at a time: there is one WebView2, and it shows one document.</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    // Touched only on the UI thread.
    private CoreWebView2Environment? _environment;
    private WebView2? _view;
    private ActiveJob? _active;

    public HtmlPrinter(UiThread ui, string dataDir)
    {
        _ui = ui;
        _profileDir = Path.Combine(dataDir, "WebView2");
    }

    /// <summary>
    /// The installed WebView2 Runtime's version, or null when there is none.
    /// Windows 11 and updated Windows 10 ship it; an old or stripped image may not.
    /// </summary>
    public static string? RuntimeVersion()
    {
        try
        {
            return CoreWebView2Environment.GetAvailableBrowserVersionString();
        }
        catch
        {
            return null;
        }
    }

    public async Task<HtmlPrintResult> PrintAsync(HtmlJob job, CancellationToken cancellation)
    {
        await _gate.WaitAsync(cancellation);
        try
        {
            // The gate is held until the UI side has completely finished, even
            // if the caller has given up. Releasing it early would let the next
            // job navigate the WebView out from under one still printing.
            return await _ui.InvokeAsync(() => PrintOnUiThreadAsync(job));
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<HtmlPrintResult> PrintOnUiThreadAsync(HtmlJob job)
    {
        var core = await EnsureWebViewAsync();
        var environment = _environment!;

        _active = new ActiveJob(job.DocumentUri, Encoding.UTF8.GetBytes(job.Html));
        try
        {
            await LoadAsync(core, job.DocumentUri);
            await EmulatePrintMediaAsync(core);
            await WaitForFontsAsync(core);

            var settings = environment.CreatePrintSettings();
            settings.PrinterName = job.Printer;
            settings.Copies = job.Copies;
            settings.ShouldPrintBackgrounds = job.Backgrounds;
            settings.ShouldPrintHeaderAndFooter = false;
            settings.ScaleFactor = 1.0;
            settings.Orientation = CoreWebView2PrintOrientation.Portrait;
            settings.ColorMode = job.Color ? CoreWebView2PrintColorMode.Color : CoreWebView2PrintColorMode.Grayscale;

            // Margins are always stated, never left to WebView2's 1 cm default:
            // an explicit page size means the caller has laid the document out
            // for exact paper, and a centimetre it did not ask for would push
            // an 80 mm receipt's right edge off the roll.
            settings.MarginTop = job.Margins.Top / MmPerInch;
            settings.MarginRight = job.Margins.Right / MmPerInch;
            settings.MarginBottom = job.Margins.Bottom / MmPerInch;
            settings.MarginLeft = job.Margins.Left / MmPerInch;

            double? fittedHeightMm = null;
            if (job.Page is { } page)
            {
                var heightMm = page.HeightMm;
                if (heightMm is null)
                {
                    heightMm = await MeasureHeightMmAsync(core, page.WidthMm, job.Margins);
                    fittedHeightMm = heightMm;
                }

                settings.MediaSize = CoreWebView2PrintMediaSize.Custom;
                settings.PageWidth = page.WidthMm / MmPerInch;
                settings.PageHeight = heightMm.Value / MmPerInch;
            }

            var status = await core.PrintAsync(settings);
            return status switch
            {
                CoreWebView2PrintStatus.Succeeded => new HtmlPrintResult(fittedHeightMm),
                CoreWebView2PrintStatus.PrinterUnavailable => throw new PrintFailure(
                    "printer_unavailable", $"Windows reports the printer \"{job.Printer}\" is unavailable."),
                _ => throw new PrintFailure("print_failed", "Windows did not accept the print job."),
            };
        }
        finally
        {
            _active = null;
        }
    }

    // ── Loading ──────────────────────────────────────────────────────

    private static async Task LoadAsync(CoreWebView2 core, Uri documentUri)
    {
        var completed = new TaskCompletionSource<CoreWebView2NavigationCompletedEventArgs>();
        ulong navigationId = 0;

        void Started(object? sender, CoreWebView2NavigationStartingEventArgs e)
        {
            if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) && uri == documentUri)
                navigationId = e.NavigationId;
        }

        // Matched on the navigation id, so a late completion from some earlier
        // navigation cannot be mistaken for this document having loaded.
        void Completed(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            if (navigationId != 0 && e.NavigationId == navigationId)
                completed.TrySetResult(e);
        }

        core.NavigationStarting += Started;
        core.NavigationCompleted += Completed;
        try
        {
            core.Navigate(documentUri.AbsoluteUri);

            var finished = await Task.WhenAny(completed.Task, Task.Delay(LoadTimeout));
            if (finished != completed.Task)
            {
                core.Stop();
                throw new PrintFailure("load_timeout", "The document took too long to load.");
            }

            var result = completed.Task.Result;
            if (!result.IsSuccess)
                throw new PrintFailure("load_failed", $"The document did not load ({result.WebErrorStatus}).");
        }
        finally
        {
            core.NavigationStarting -= Started;
            core.NavigationCompleted -= Completed;
        }
    }

    /// <summary>
    /// Lay the page out with its print stylesheet before measuring it, so a
    /// fitted page's height is the printed height and not the on-screen one —
    /// the two differ wherever the document hides or resizes things for print.
    /// Best effort: failing this only makes the measurement approximate.
    /// </summary>
    private static async Task EmulatePrintMediaAsync(CoreWebView2 core)
    {
        try
        {
            await core.CallDevToolsProtocolMethodAsync("Emulation.setEmulatedMedia", "{\"media\":\"print\"}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[print] could not switch to print media: {ex.Message}");
        }
    }

    /// <summary>
    /// The load event does not wait for web fonts. Printing before they arrive
    /// gets the fallback font — and for Arabic, a fallback with different
    /// widths, which changes where every line wraps.
    /// </summary>
    private static async Task WaitForFontsAsync(CoreWebView2 core)
    {
        var deadline = DateTime.UtcNow + FontTimeout;
        while (DateTime.UtcNow < deadline)
        {
            var status = await core.ExecuteScriptAsync("document.fonts ? document.fonts.status : 'loaded'");
            // "null" means the script did not run at all; waiting longer will not change that.
            if (status is "\"loaded\"" or "null") return;
            await Task.Delay(50);
        }
    }

    /// <summary>
    /// The page height that fits the whole document on one sheet: what a
    /// receipt printer needs to cut once, under the last line, instead of at
    /// whatever length its driver's default paper happens to be.
    ///
    /// The document is first constrained to the printable width, so it wraps
    /// here exactly as it will on paper; otherwise a wider window would make it
    /// shorter than it prints.
    /// </summary>
    private static async Task<double> MeasureHeightMmAsync(CoreWebView2 core, double pageWidthMm, Margins margins)
    {
        var contentWidthMm = Math.Max(10, pageWidthMm - margins.Left - margins.Right);
        var script = $$"""
            (() => {
              const root = document.documentElement;
              root.style.width = '{{contentWidthMm.ToString("0.###", CultureInfo.InvariantCulture)}}mm';
              const body = document.body;
              return Math.ceil(Math.max(
                root.scrollHeight,
                root.getBoundingClientRect().height,
                body ? body.scrollHeight : 0,
                body ? body.getBoundingClientRect().bottom : 0));
            })()
            """;

        var raw = await core.ExecuteScriptAsync(script);
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var cssPx) || cssPx <= 0)
            throw new PrintFailure("measure_failed", "Could not measure the document to fit the page to it.");

        // One millimetre of slack: a fitted page exactly as tall as its content
        // can tip the last line onto a second sheet through rounding alone.
        var heightMm = cssPx / CssPxPerInch * MmPerInch + margins.Top + margins.Bottom + 1;
        return Math.Clamp(Math.Ceiling(heightMm), MinFittedHeightMm, MaxFittedHeightMm);
    }

    // ── The WebView itself ───────────────────────────────────────────

    private async Task<CoreWebView2> EnsureWebViewAsync()
    {
        if (_view?.CoreWebView2 is { } existing) return existing;

        if (RuntimeVersion() is null)
            throw new PrintFailure("webview2_missing",
                "The Microsoft Edge WebView2 Runtime is not installed on this computer.");

        Directory.CreateDirectory(_profileDir);
        _environment ??= await CoreWebView2Environment.CreateAsync(browserExecutableFolder: null, userDataFolder: _profileDir);

        // Wide enough that nothing is laid out for a phone; the page width that
        // actually matters is set per job when it is measured, and by the
        // printer's paper when it is printed.
        var view = new WebView2 { Location = new Point(0, 0), Size = new Size(1024, 1024) };
        _ui.Host.Controls.Add(view);
        await view.EnsureCoreWebView2Async(_environment);

        var core = view.CoreWebView2;
        var settings = core.Settings;
        settings.IsScriptEnabled = false;
        settings.IsWebMessageEnabled = false;
        settings.AreHostObjectsAllowed = false;
        settings.AreDefaultContextMenusEnabled = false;
        settings.AreDefaultScriptDialogsEnabled = false;
        settings.AreDevToolsEnabled = false;
        settings.IsStatusBarEnabled = false;
        settings.IsZoomControlEnabled = false;
        settings.AreBrowserAcceleratorKeysEnabled = false;

        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += OnResourceRequested;
        core.NavigationStarting += OnNavigationStarting;
        core.FrameNavigationStarting += OnFrameNavigationStarting;
        core.NewWindowRequested += (_, e) => e.Handled = true;
        core.ProcessFailed += OnProcessFailed;

        _view = view;
        return core;
    }

    /// <summary>
    /// Serves the job's document from memory, and refuses what the job may not
    /// load. Runs on the UI thread, like every other WebView2 event.
    /// </summary>
    private void OnResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        var environment = _environment!;
        Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var uri);

        if (_active is { } job && uri is not null && uri == job.DocumentUri)
        {
            e.Response = environment.CreateWebResourceResponse(
                new MemoryStream(job.Document, writable: false), 200, "OK",
                "Content-Type: text/html; charset=utf-8\r\nCache-Control: no-store");
            return;
        }

        if (uri is null || !MayLoad(uri))
        {
            e.Response = environment.CreateWebResourceResponse(null, 403, "Forbidden", "");
        }
        // Otherwise: an ordinary image, font or stylesheet, fetched from the network as usual.
    }

    /// <summary>
    /// The only top-level navigation allowed is the job's own document. A meta
    /// refresh or a link the HTML tries to follow goes nowhere.
    /// </summary>
    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        var isJob = _active is { } job
                    && Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri)
                    && uri == job.DocumentUri;
        if (!isJob) e.Cancel = true;
    }

    private void OnFrameNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) || !(MayLoad(uri) || uri.Scheme == "about"))
            e.Cancel = true;
    }

    /// <summary>
    /// http(s) only, and nothing on this computer. Blocking loopback keeps a
    /// job from calling this agent's own API, or any other local service.
    /// </summary>
    private static bool MayLoad(Uri uri)
        => (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
           && !uri.IsLoopback
           && !string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// If the browser process dies, this WebView is finished; drop it so the
    /// next job builds a fresh one rather than every print failing until the
    /// agent is restarted. A renderer crash needs nothing: the next navigation
    /// starts a new renderer.
    /// </summary>
    private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        Console.Error.WriteLine($"[print] WebView2 process failed: {e.ProcessFailedKind}");
        if (e.ProcessFailedKind != CoreWebView2ProcessFailedKind.BrowserProcessExited) return;

        var view = _view;
        _view = null;
        _environment = null;
        try { view?.Dispose(); } catch { /* already gone */ }
    }

    private sealed record ActiveJob(Uri DocumentUri, byte[] Document);
}

/// <summary>A sheet of paper, in millimetres. A null height means "fit to the content".</summary>
public sealed record PageSize(double WidthMm, double? HeightMm);

public sealed record Margins(double Top, double Right, double Bottom, double Left)
{
    public static readonly Margins None = new(0, 0, 0, 0);
}

/// <summary>
/// One HTML job. <see cref="DocumentUri"/> is where the document pretends to
/// live; it is never fetched.
/// </summary>
public sealed record HtmlJob(
    string Printer,
    string Html,
    Uri DocumentUri,
    int Copies,
    PageSize? Page,
    Margins Margins,
    bool Color,
    bool Backgrounds);

/// <summary>The page height used when it was fitted to the content, so a caller can log it.</summary>
public sealed record HtmlPrintResult(double? FittedHeightMm);

/// <summary>A job that failed for a reason worth telling the caller in words.</summary>
public sealed class PrintFailure : Exception
{
    public string Code { get; }

    public PrintFailure(string code, string message) : base(message)
    {
        Code = code;
    }
}
