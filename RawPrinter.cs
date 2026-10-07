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

using System.ComponentModel;
using System.Runtime.InteropServices;

namespace EkPrinter;

/// <summary>
/// Sends bytes to a printer untouched — ESC/POS for receipt printers (the
/// cash-drawer kick is one of these), ZPL/TSPL/EPL for label printers.
///
/// Goes through the Windows spooler by printer name rather than opening a COM
/// or USB port, so it works for any printer Windows already knows, shared or
/// local, without the agent having to know how it is attached.
/// </summary>
internal sealed class RawPrinter
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DOC_INFO_1
    {
        public string pDocName;
        public string? pOutputFile;
        public string pDatatype;
    }

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool OpenPrinterW(string name, out IntPtr handle, IntPtr defaults);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool ClosePrinter(IntPtr handle);

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int StartDocPrinterW(IntPtr handle, int level, ref DOC_INFO_1 info);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool EndDocPrinter(IntPtr handle);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool StartPagePrinter(IntPtr handle);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool EndPagePrinter(IntPtr handle);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool WritePrinter(IntPtr handle, byte[] buffer, int count, out int written);

    private const int ERROR_INVALID_DATATYPE = 1804;

    /// <summary>
    /// One job at a time. Two raw jobs interleaved on one receipt printer — a
    /// receipt and a drawer kick from a second tab — would garble both.
    /// </summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task PrintAsync(string printer, byte[] data, string title, CancellationToken cancellation)
    {
        await _gate.WaitAsync(cancellation);
        try
        {
            // The spooler calls block on a slow or network printer; keep them
            // off Kestrel's request thread.
            await Task.Run(() => Send(printer, data, title), cancellation);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static void Send(string printer, byte[] data, string title)
    {
        if (!OpenPrinterW(printer, out var handle, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Windows could not open the printer \"{printer}\".");

        try
        {
            // RAW is what a v3 driver — nearly every receipt and label printer —
            // passes straight through. A v4 (class) driver refuses it and wants
            // XPS_PASS for the same pass-through, so that is the fallback rather
            // than something a caller should have to know about.
            if (!StartDoc(handle, title, "RAW", out var error))
            {
                if (error != ERROR_INVALID_DATATYPE || !StartDoc(handle, title, "XPS_PASS", out error))
                    throw new Win32Exception(error, $"The printer \"{printer}\" refused the job.");
            }

            try
            {
                if (!StartPagePrinter(handle))
                    throw new Win32Exception(Marshal.GetLastWin32Error());

                if (!WritePrinter(handle, data, data.Length, out var written))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                if (written != data.Length)
                    throw new IOException($"Only {written} of {data.Length} bytes reached the printer.");

                EndPagePrinter(handle);
            }
            finally
            {
                EndDocPrinter(handle);
            }
        }
        finally
        {
            ClosePrinter(handle);
        }
    }

    private static bool StartDoc(IntPtr handle, string title, string datatype, out int error)
    {
        var info = new DOC_INFO_1 { pDocName = title, pOutputFile = null, pDatatype = datatype };
        if (StartDocPrinterW(handle, 1, ref info) != 0)
        {
            error = 0;
            return true;
        }
        error = Marshal.GetLastWin32Error();
        return false;
    }
}
