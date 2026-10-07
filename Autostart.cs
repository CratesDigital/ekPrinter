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

using Microsoft.Win32;

namespace EkPrinter;

/// <summary>
/// Whether the agent starts when the operator signs in to Windows.
///
/// The HKCU Run key rather than a Startup-folder shortcut, because the operator
/// has to be able to turn it off from the tray menu. It must also be the ONLY
/// autostart mechanism: ekSigner once had both, so the agent was always already
/// running and every manual launch became a second instance.
/// </summary>
internal static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "ekPrinter";

    private static string? ExecutablePath => Environment.ProcessPath;

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string value && !string.IsNullOrWhiteSpace(value);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Returns whether the change stuck, so the caller can re-read rather than assume.</summary>
    public static bool Set(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (key is null) return false;

            if (enabled)
            {
                var path = ExecutablePath;
                if (string.IsNullOrEmpty(path)) return false;
                // Quoted: the install path contains spaces.
                key.SetValue(ValueName, $"\"{path}\"");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }

            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[autostart] could not update the Run key: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Bring the registry into line with the stored preference. Called on every
    /// start, because a reinstall to another directory leaves a stale path that
    /// silently stops the agent starting at login.
    /// </summary>
    public static void Apply(AgentConfig config)
    {
        if (config.RunAtLogin)
        {
            Set(true);
        }
        else if (IsEnabled())
        {
            Set(false);
        }
    }
}
