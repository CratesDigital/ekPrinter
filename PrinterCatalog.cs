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
using System.Text;

namespace EkPrinter;

/// <summary>
/// The printers installed for this Windows user, read straight from the
/// spooler with EnumPrinters level 2.
///
/// Level 2 rather than System.Drawing's InstalledPrinters, because a calling
/// application needs more than names: the driver and port are how it can
/// guess which printer is the 80 mm receipt printer, and the offline flag is
/// how it can say "the receipt printer is switched off" instead of failing a
/// sale's print with a generic error.
/// </summary>
internal static class PrinterCatalog
{
    private const uint PRINTER_ENUM_LOCAL = 0x2;
    private const uint PRINTER_ENUM_CONNECTIONS = 0x4;
    private const uint PRINTER_ATTRIBUTE_WORK_OFFLINE = 0x400;
    private const uint PRINTER_STATUS_OFFLINE = 0x80;
    private const uint PRINTER_STATUS_ERROR = 0x2;
    private const uint PRINTER_STATUS_PAPER_OUT = 0x10;
    private const uint PRINTER_STATUS_PAPER_JAM = 0x8;
    private const uint PRINTER_STATUS_DOOR_OPEN = 0x400000;
    private const int ERROR_INSUFFICIENT_BUFFER = 122;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PRINTER_INFO_2
    {
        public string? pServerName;
        public string? pPrinterName;
        public string? pShareName;
        public string? pPortName;
        public string? pDriverName;
        public string? pComment;
        public string? pLocation;
        public IntPtr pDevMode;
        public string? pSepFile;
        public string? pPrintProcessor;
        public string? pDatatype;
        public string? pParameters;
        public IntPtr pSecurityDescriptor;
        public uint Attributes;
        public uint Priority;
        public uint DefaultPriority;
        public uint StartTime;
        public uint UntilTime;
        public uint Status;
        public uint cJobs;
        public uint AveragePPM;
    }

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool EnumPrintersW(uint flags, string? name, uint level, IntPtr buffer,
        uint bufferSize, out uint needed, out uint returned);

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetDefaultPrinterW(StringBuilder? buffer, ref uint size);

    public static IReadOnlyList<PrinterInfo> List()
    {
        const uint flags = PRINTER_ENUM_LOCAL | PRINTER_ENUM_CONNECTIONS;

        EnumPrintersW(flags, null, 2, IntPtr.Zero, 0, out var needed, out _);
        if (needed == 0) return Array.Empty<PrinterInfo>();

        var buffer = Marshal.AllocHGlobal((int)needed);
        try
        {
            if (!EnumPrintersW(flags, null, 2, buffer, needed, out _, out var returned))
            {
                var error = Marshal.GetLastWin32Error();
                // A printer added between the two calls; the next request retries.
                if (error == ERROR_INSUFFICIENT_BUFFER) return Array.Empty<PrinterInfo>();
                throw new Win32Exception(error);
            }

            var defaultName = DefaultPrinterName();
            var size = Marshal.SizeOf<PRINTER_INFO_2>();
            var printers = new List<PrinterInfo>((int)returned);

            for (var i = 0; i < returned; i++)
            {
                var info = Marshal.PtrToStructure<PRINTER_INFO_2>(buffer + i * size);
                var name = info.pPrinterName ?? "";
                if (name.Length == 0) continue;

                printers.Add(new PrinterInfo(
                    Name: name,
                    IsDefault: string.Equals(name, defaultName, StringComparison.OrdinalIgnoreCase),
                    Driver: info.pDriverName ?? "",
                    Port: info.pPortName ?? "",
                    Status: Describe(info)));
            }

            return printers.OrderByDescending(p => p.IsDefault).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>The installed printer with this name, matched the way Windows matches it.</summary>
    public static PrinterInfo? Find(string name)
        => List().FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    public static string? DefaultPrinterName()
    {
        uint size = 0;
        GetDefaultPrinterW(null, ref size);
        if (size == 0) return null;

        var buffer = new StringBuilder((int)size);
        return GetDefaultPrinterW(buffer, ref size) ? buffer.ToString() : null;
    }

    /// <summary>
    /// One word a calling application can show. Windows only learns most of
    /// these when it next talks to the printer, so "ready" means "nothing
    /// known to be wrong", not a guarantee.
    /// </summary>
    private static string Describe(PRINTER_INFO_2 info)
    {
        if ((info.Attributes & PRINTER_ATTRIBUTE_WORK_OFFLINE) != 0 || (info.Status & PRINTER_STATUS_OFFLINE) != 0)
            return "offline";
        if ((info.Status & PRINTER_STATUS_PAPER_OUT) != 0) return "paper_out";
        if ((info.Status & PRINTER_STATUS_PAPER_JAM) != 0) return "paper_jam";
        if ((info.Status & PRINTER_STATUS_DOOR_OPEN) != 0) return "door_open";
        if ((info.Status & PRINTER_STATUS_ERROR) != 0) return "error";
        return "ready";
    }
}

public sealed record PrinterInfo(string Name, bool IsDefault, string Driver, string Port, string Status);
