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
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace EkPrinter;

/// <summary>
/// Turns a rendered page into a TSPL print job, for printers whose Windows
/// driver places a browser's page wrongly.
///
/// The values here were proven on an Xprinter XP-370B with an 80 mm roll
/// (2026-11-05): a ruler and text drawn to a bitmap and sent this way started
/// at 0 on the left edge, upright, black on white. Through the driver the same
/// printer lost the left of every page, whatever its settings.
/// </summary>
internal static class TsplRaster
{
    /// <summary>203 dpi, the resolution of the printers this mode is for.</summary>
    public const int DotsPerMm = 8;

    /// <summary>What the XP-370B printed before running out of head, on an 80 mm roll.</summary>
    public const double PrintableWidthMm = 75;

    /// <summary>The roll. Stated as tested: a narrower SIZE may move the image across the head.</summary>
    private const int PaperWidthMm = 80;

    private const int MinHeightMm = 20;

    /// <summary>
    /// Blank paper under the content, so a tear cannot clip the last line. The page
    /// is taller than the image, and DIRECTION 1 prints the bottom of the page first.
    /// </summary>
    private const int BottomSpaceMm = 5;

    /// <summary>
    /// Lighter than this prints nothing. A thermal head prints a dot or no dot,
    /// and a cut-off above the middle keeps thin strokes of small text whole.
    /// </summary>
    private const int WhiteAbove = 150;

    public static byte[] Build(byte[] png, int copies, out double heightMm)
    {
        using var stream = new MemoryStream(png, writable: false);
        using var source = new Bitmap(stream);

        var heightDots = source.Height;
        var rowBytes = (source.Width + 7) / 8;
        var bitmap = ToOneBit(source, rowBytes);

        var sizeMm = Math.Max(MinHeightMm, (int)Math.Ceiling(heightDots / (double)DotsPerMm) + BottomSpaceMm);
        heightMm = sizeMm;

        var invariant = CultureInfo.InvariantCulture;
        var header = string.Create(invariant,
            $"SIZE {PaperWidthMm} mm,{sizeMm} mm\r\nGAP 0,0\r\nDIRECTION 1\r\nSET TEAR ON\r\nCLS\r\nBITMAP 0,0,{rowBytes},{heightDots},0,");
        var footer = string.Create(invariant, $"\r\nPRINT {copies}\r\n");

        using var job = new MemoryStream(header.Length + bitmap.Length + footer.Length);
        job.Write(Encoding.ASCII.GetBytes(header));
        job.Write(bitmap);
        job.Write(Encoding.ASCII.GetBytes(footer));
        return job.ToArray();
    }

    /// <summary>One bit per dot, most significant first, in TSPL's sense: 1 is white.</summary>
    private static byte[] ToOneBit(Bitmap source, int rowBytes)
    {
        var width = source.Width;
        var height = source.Height;

        var locked = source.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        int stride;
        byte[] pixels;
        try
        {
            stride = Math.Abs(locked.Stride);
            pixels = new byte[stride * height];
            Marshal.Copy(locked.Scan0, pixels, 0, pixels.Length);
        }
        finally
        {
            source.UnlockBits(locked);
        }

        var bits = new byte[rowBytes * height];
        Array.Fill(bits, (byte)0xFF);

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var at = y * stride + x * 4;
                int blue = pixels[at], green = pixels[at + 1], red = pixels[at + 2], alpha = pixels[at + 3];

                // Anything see-through sits on white paper.
                var grey = (red * 299 + green * 587 + blue * 114) / 1000;
                grey = (grey * alpha + 255 * (255 - alpha)) / 255;

                if (grey <= WhiteAbove)
                    bits[y * rowBytes + x / 8] &= (byte)~(0x80 >> (x % 8));
            }
        }

        return bits;
    }
}
