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
using System.Drawing;
using System.Windows.Forms;

namespace EkPrinter;

/// <summary>
/// The tray icon — the only visible sign that a windowless agent is running.
/// Without it the agent looks exactly like one that failed to start, and the
/// only way to stop it is Task Manager.
///
/// Built on <see cref="UiThread"/>, which owns the message loop.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly AgentConfig _config;
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _startupItem;
    private readonly System.Windows.Forms.Timer _refresh;
    private bool _settingCheck;

    public TrayIcon(AgentConfig config)
    {
        _config = config;

        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem("Open agent page", null, (_, _) => Open("/")));
        menu.Items.Add(new ToolStripSeparator());

        _startupItem = new ToolStripMenuItem("Run when Windows starts")
        {
            CheckOnClick = true,
            Checked = _config.RunAtLogin,
        };
        _startupItem.CheckedChanged += (_, _) => ToggleStartup();
        menu.Items.Add(_startupItem);

        menu.Items.Add(new ToolStripSeparator());
        // Ends the message loop; Program.cs then stops the HTTP server.
        menu.Items.Add(new ToolStripMenuItem("Quit", null, (_, _) => Application.ExitThread()));

        _icon = new NotifyIcon
        {
            Icon = LoadIcon(),
            ContextMenuStrip = menu,
            Visible = true,
            Text = "ekPrinter",
        };
        _icon.DoubleClick += (_, _) => Open("/");

        UpdateTooltip();
        _refresh = new System.Windows.Forms.Timer { Interval = 10_000 };
        _refresh.Tick += (_, _) => UpdateTooltip();
        _refresh.Start();
    }

    /// <summary>
    /// The icon compiled into this executable, so there is one copy of the image.
    /// A missing tray icon is not worth failing to start over.
    /// </summary>
    private static Icon LoadIcon()
    {
        try
        {
            var path = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(path))
            {
                var icon = Icon.ExtractAssociatedIcon(path);
                if (icon is not null) return icon;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[tray] could not read the application icon: {ex.Message}");
        }

        return SystemIcons.Application;
    }

    private void ToggleStartup()
    {
        if (_settingCheck) return;

        var wanted = _startupItem.Checked;
        if (!Autostart.Set(wanted))
        {
            // Put the tick back where reality is rather than leaving a menu that
            // lies about what happens at login. Guarded, because assigning
            // Checked raises this handler again.
            _settingCheck = true;
            _startupItem.Checked = Autostart.IsEnabled();
            _settingCheck = false;

            MessageBox.Show(
                "Windows would not let the startup setting be changed.",
                "ekPrinter", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _config.RunAtLogin = wanted;
        _config.Save();
    }

    /// <summary>The shell caps tooltip text at 63 characters, so this stays terse.</summary>
    private void UpdateTooltip()
    {
        string state;
        if (HtmlPrinter.RuntimeVersion() is null)
        {
            state = "WebView2 missing";
        }
        else
        {
            var paired = _config.PairedSnapshot().Count;
            state = paired switch
            {
                0 => "not paired yet",
                1 => "ready, 1 site paired",
                _ => $"ready, {paired} sites paired",
            };
        }

        _icon.Text = $"ekPrinter — {state}";
    }

    private void Open(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo($"http://127.0.0.1:{_config.Port}{path}") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[tray] could not open a browser: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _refresh.Dispose();
        _icon.Visible = false;
        _icon.Dispose();
    }
}
