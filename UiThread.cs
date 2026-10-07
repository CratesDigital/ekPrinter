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
using System.Windows.Forms;

namespace EkPrinter;

/// <summary>
/// The one thread that owns windows: the tray icon and the hidden form the
/// WebView2 renderer lives in.
///
/// WebView2 is an STA COM component bound to the thread that created it, and
/// NotifyIcon needs a message loop. Kestrel serves requests on the thread
/// pool, so every HTML job is marshalled here through <see cref="InvokeAsync"/>.
/// Top-level statements in Program.cs cannot carry [STAThread], which is why
/// this is a thread of its own rather than the main one.
/// </summary>
internal sealed class UiThread
{
    private readonly ManualResetEventSlim _ready = new();
    private HostForm? _host;
    private Thread? _thread;

    /// <summary>The hidden form. Valid once <see cref="Start"/> has returned.</summary>
    public Control Host => _host ?? throw new InvalidOperationException("The UI thread has not started.");

    /// <summary>
    /// Starts the message loop and returns once the host form exists, so a print
    /// request arriving the moment Kestrel is up has somewhere to go.
    /// <paramref name="buildTray"/> runs on the UI thread.
    /// </summary>
    public void Start(Func<IDisposable> buildTray)
    {
        _thread = new Thread(() => Loop(buildTray)) { IsBackground = false, Name = "ekPrinter UI" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait();
    }

    /// <summary>Blocks until the operator quits from the tray.</summary>
    public void Join() => _thread?.Join();

    private void Loop(Func<IDisposable> buildTray)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // Creating the first control installs the WinForms synchronization
        // context on this thread, which is what makes an `await` inside a
        // marshalled job resume here rather than on the thread pool.
        _host = new HostForm();
        _host.Show();

        using var tray = buildTray();
        _ready.Set();

        Application.Run();

        _host.Dispose();
    }

    /// <summary>
    /// Runs <paramref name="work"/> on the UI thread and completes with its
    /// result. Continuations run asynchronously, so a Kestrel request thread
    /// never ends up executing on the UI thread.
    /// </summary>
    public Task<T> InvokeAsync<T>(Func<Task<T>> work)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Host.BeginInvoke(new Action(async () =>
        {
            try
            {
                done.SetResult(await work());
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
        }));
        return done.Task;
    }

    /// <summary>
    /// A real top-level window, because WebView2 needs a parent HWND to render
    /// into, but one the operator never sees: off-screen, no taskbar button,
    /// and a tool window so Alt+Tab skips it. Shown rather than hidden because
    /// a hidden parent tells WebView2 it is invisible, and an invisible
    /// WebView2 is allowed to stop rendering.
    /// </summary>
    private sealed class HostForm : Form
    {
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_NOACTIVATE = 0x08000000;

        public HostForm()
        {
            Text = "ekPrinter";
            ShowInTaskbar = false;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Location = new Point(-32000, -32000);
            Size = new Size(1024, 1024);
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
                return cp;
            }
        }
    }
}
