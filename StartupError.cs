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

using System.Runtime.InteropServices;

namespace EkPrinter;

/// <summary>
/// A windowless agent that fails at startup fails invisibly: no console, no
/// window, just an agent that never appears. So a fatal startup error gets a
/// dialog the operator cannot miss, and a line in a log for whoever they call.
/// </summary>
internal static class StartupError
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    private const uint IconError = 0x10;

    public static void Report(string message)
    {
        Console.Error.WriteLine(message);

        try
        {
            Directory.CreateDirectory(AgentConfig.DataDir);
            File.AppendAllText(
                Path.Combine(AgentConfig.DataDir, "agent-error.log"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
        }
        catch
        {
            // If even the log cannot be written, the dialog is still worth showing.
        }

        try { MessageBoxW(IntPtr.Zero, message, "ekPrinter", IconError); }
        catch { /* nothing left to fall back to */ }
    }
}
