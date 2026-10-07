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

using System.Text.Json;

namespace EkPrinter;

/// <summary>
/// Agent settings, persisted in the operator's roaming profile.
///
/// The paired origins are the security boundary. An agent that prints
/// whatever it is asked, for whoever asks, lets any web page waste paper and —
/// worse — open the cash drawer, which is wired to the receipt printer. Nothing
/// prints for an origin the operator has not explicitly paired.
/// </summary>
public sealed class AgentConfig
{
    /// <summary>
    /// 8421: unassigned by IANA (8418–8422 are free), with no common unofficial
    /// user, and next to ekSigner's 8420 so the two agents read as a pair.
    /// </summary>
    public int Port { get; set; } = 8421;

    /// <summary>
    /// Start with Windows. On by default — a print agent that has to be
    /// launched by hand every morning is one that is missing at the first sale.
    /// Kept here rather than read back from the registry so an operator who
    /// deliberately turns it off stays off across upgrades.
    /// </summary>
    public bool RunAtLogin { get; set; } = true;

    /// <summary>
    /// Origins allowed to print, e.g. https://dev.ekpos.net. A list: one till
    /// can print for more than one web application.
    /// </summary>
    public List<string> PairedOrigins { get; set; } = new();

    /// <summary>Shown on the agent's own page; the operator types it into the site once.</summary>
    public string PairingCode { get; set; } = "";

    /// <summary>
    /// The roaming profile, not the install directory. An installer overwrites
    /// its own directory on upgrade, so config kept beside the executable would
    /// cost the operator their pairing every time the agent updated.
    /// </summary>
    private static string ConfigDir => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ekPrinter");

    private static string Path => System.IO.Path.Combine(ConfigDir, "agent-config.json");

    /// <summary>
    /// Machine-local working files: the WebView2 profile and the error log.
    /// Local rather than roaming, because a browser profile is large, is
    /// rebuilt on demand, and has no business following the user between PCs.
    /// </summary>
    public static string DataDir => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ekPrinter");

    public static AgentConfig Load()
    {
        if (File.Exists(Path))
        {
            try
            {
                var loaded = JsonSerializer.Deserialize<AgentConfig>(File.ReadAllText(Path));
                if (loaded is not null)
                {
                    if (string.IsNullOrWhiteSpace(loaded.PairingCode))
                    {
                        loaded.PairingCode = NewPairingCode();
                        loaded.Save();
                    }
                    return loaded;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[config] {Path} is unreadable, starting fresh: {ex.Message}");
            }
        }

        var fresh = new AgentConfig { PairingCode = NewPairingCode() };
        fresh.Save();
        return fresh;
    }

    private readonly object _gate = new();

    /// <summary>
    /// Serialised: Kestrel serves requests concurrently, and the status page's
    /// "Remove" can race a pairing from a site.
    /// </summary>
    public void Save()
    {
        lock (_gate)
        {
            Directory.CreateDirectory(ConfigDir);
            File.WriteAllText(Path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    public bool IsPaired(string? origin)
    {
        if (string.IsNullOrEmpty(origin)) return false;
        lock (_gate)
        {
            return PairedOrigins.Any(o => string.Equals(o, origin, StringComparison.OrdinalIgnoreCase));
        }
    }

    public IReadOnlyList<string> PairedSnapshot()
    {
        lock (_gate)
        {
            return PairedOrigins.ToList();
        }
    }

    public void Pair(string origin)
    {
        lock (_gate)
        {
            if (PairedOrigins.Any(o => string.Equals(o, origin, StringComparison.OrdinalIgnoreCase))) return;
            PairedOrigins.Add(origin);
        }
        Save();
    }

    /// <summary>Returns whether anything was removed.</summary>
    public bool Unpair(string origin)
    {
        int removed;
        lock (_gate)
        {
            removed = PairedOrigins.RemoveAll(o => string.Equals(o, origin, StringComparison.OrdinalIgnoreCase));
        }
        if (removed > 0) Save();
        return removed > 0;
    }

    /// <summary>
    /// Six digits, from a cryptographic RNG. Short enough to read off a screen
    /// and type.
    /// </summary>
    private static string NewPairingCode()
        => System.Security.Cryptography.RandomNumberGenerator.GetInt32(100_000, 1_000_000).ToString();
}
