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
/// The last jobs, shown on the agent's page so "it didn't print" can be
/// answered over the phone: did the request arrive, from which site, to which
/// printer, and what Windows said.
///
/// Records that a job happened, never what was printed. Receipts carry
/// customer names and amounts; keeping them on the till would be a second copy
/// of the business's books in a place nobody backs up or secures.
///
/// In memory only. It is a support aid for the current session, not an audit
/// trail.
/// </summary>
public sealed class JobLog
{
    private const int Capacity = 50;

    private readonly object _gate = new();
    private readonly LinkedList<JobRecord> _records = new();

    public void Add(JobRecord record)
    {
        lock (_gate)
        {
            _records.AddFirst(record);
            while (_records.Count > Capacity) _records.RemoveLast();
        }
    }

    public IReadOnlyList<JobRecord> Snapshot()
    {
        lock (_gate)
        {
            return _records.ToList();
        }
    }
}

public sealed record JobRecord(
    string Id,
    DateTime At,
    string Origin,
    string Printer,
    string Kind,
    bool Ok,
    string Message);
