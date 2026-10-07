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

namespace EkPrinter;

/// <summary>
/// Rate-limits pairing attempts.
///
/// The pairing code is six digits, and pairing is what stands between a web
/// page and the till's printers — including the cash drawer, which opens on a
/// command sent to the receipt printer. Without a limit, a page left open in
/// the operator's browser could work through all 900,000 codes over loopback
/// in minutes.
///
/// Five attempts, then five minutes of nothing. A genuine operator who mistyped
/// gets an obvious message and a short wait; the expected time to guess a code
/// goes past a year and a half.
///
/// Deliberately does NOT issue a new code when the allowance runs out: each
/// guess is still 1 in 900,000 either way, and an operator staring at a code
/// that silently stopped working has been handed a mystery.
///
/// In memory on purpose: the counter only has to outlive the attack, and
/// anything able to restart the agent is already running as the operator.
/// </summary>
public sealed class PairingGuard
{
    private const int MaxAttempts = 5;
    private static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(5);

    private readonly object _gate = new();
    private int _failed;
    private DateTime _lockedUntil = DateTime.MinValue;

    /// <summary>
    /// How long pairing stays refused, or null if it is open. Read under the
    /// same lock that writes it, so a burst of parallel guesses cannot race
    /// past the counter.
    /// </summary>
    public TimeSpan? LockedFor
    {
        get
        {
            lock (_gate)
            {
                var remaining = _lockedUntil - DateTime.UtcNow;
                return remaining > TimeSpan.Zero ? remaining : null;
            }
        }
    }

    public void Succeeded()
    {
        lock (_gate)
        {
            _failed = 0;
            _lockedUntil = DateTime.MinValue;
        }
    }

    public void Failed()
    {
        lock (_gate)
        {
            if (++_failed < MaxAttempts) return;

            _failed = 0;
            _lockedUntil = DateTime.UtcNow + Cooldown;
        }
    }
}
