using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace ClaudeWidget.Services;

/// <summary>
/// Renders a rounded-pill progress bar (matching claude.ai's own Usage page
/// look) as a small PNG, returned as a "data:image/png;base64,..." URI so it
/// can be dropped straight into an AdaptiveCard <c>Image</c> element.
/// </summary>
/// <remarks>
/// The Windows 11 Widgets board's bundled AdaptiveCards renderer does not
/// support the <c>ProgressBar</c>/<c>ProgressRing</c> element types (they
/// silently leave the card stuck in the loading skeleton, regardless of
/// declared schema version). <c>Image</c> is a basic, universally-supported
/// element, so we render the bar ourselves, pixel by pixel, with a small
/// internal supersampling pass for anti-aliased rounded ends — no
/// System.Drawing / GDI+ dependency (which is best avoided in a packaged
/// MSIX app), just raw RGBA bytes deflate-compressed into a minimal PNG.
/// </remarks>
public static class ProgressBarImage
{
    private const int Width = 480;
    private const int Height = 20;
    private const int Supersample = 4; // 4x4 subsamples per output pixel for smooth AA edges.

    /// <summary>Semi-transparent neutral track color that reads fine on both light and dark card backgrounds.</summary>
    private const string DefaultTrackHex = "#40808080";

    public static string BuildDataUri(double percent, string fillHex, string? trackHex = null)
    {
        double pct = Math.Clamp(percent, 0, 100) / 100.0;
        int fillWidth = (int)Math.Round(Width * pct);

        (byte fr, byte fg, byte fb, byte fa) = ParseHex(fillHex, 255);
        (byte tr, byte tg, byte tb, byte ta) = ParseHex(trackHex ?? DefaultTrackHex, 64);

        byte[] pixels = Render(fillWidth, (fr, fg, fb, fa), (tr, tg, tb, ta));
        byte[] png = PngEncoder.Encode(pixels, Width, Height);
        return "data:image/png;base64," + Convert.ToBase64String(png);
    }

    /// <summary>
    /// Source height (px) for the separator rule bitmap. The card renders it
    /// with <c>"size": "Stretch"</c>, which scales height proportionally to
    /// this source's aspect ratio (<see cref="Width"/>:this) — a 480x1
    /// source scales down to a sub-pixel, invisible sliver at typical widget
    /// widths, so this needs to be tall enough that the scaled result is a
    /// few pixels thick.
    /// </summary>
    private const int SeparatorHeight = 4;

    /// <summary>
    /// A plain solid-color horizontal rule, rendered the same way as the
    /// progress bars (raw PNG, no GDI+) so it's guaranteed to render as a
    /// visible fixed-height Image in the Widgets board host — unlike a
    /// <c>Container</c>'s <c>backgroundImage</c>, which collapses to zero
    /// height there.
    /// </summary>
    public static string BuildSeparatorDataUri(string colorHex)
    {
        (byte r, byte g, byte b, byte a) = ParseHex(colorHex, 255);
        var buffer = new byte[Width * SeparatorHeight * 4];
        for (int i = 0; i < Width * SeparatorHeight; i++)
        {
            int idx = i * 4;
            buffer[idx] = r;
            buffer[idx + 1] = g;
            buffer[idx + 2] = b;
            buffer[idx + 3] = a;
        }
        byte[] png = PngEncoder.Encode(buffer, Width, SeparatorHeight);
        return "data:image/png;base64," + Convert.ToBase64String(png);
    }

    private static byte[] Render(int fillWidth, (byte r, byte g, byte b, byte a) fill, (byte r, byte g, byte b, byte a) track)
    {
        var buffer = new byte[Width * Height * 4];
        float trackRadius = Height / 2f;
        float fillRadius = Math.Min(trackRadius, fillWidth / 2f);
        float trackRightCx = Width - trackRadius;
        float fillRightCx = Math.Max(fillRadius, fillWidth - fillRadius);

        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                // Accumulate premultiplied color across the subsample grid, then
                // unpremultiply once at the end — gives smooth anti-aliased edges
                // against full transparency outside the pill shape.
                float accR = 0, accG = 0, accB = 0, accA = 0;

                for (int sy = 0; sy < Supersample; sy++)
                {
                    float py = y + (sy + 0.5f) / Supersample;
                    for (int sx = 0; sx < Supersample; sx++)
                    {
                        float px = x + (sx + 0.5f) / Supersample;

                        bool inTrack = InsideCapsule(px, py, trackRadius, trackRadius, trackRightCx, Height / 2f);
                        bool inFill = fillWidth > 0 && InsideCapsule(px, py, fillRadius, fillRadius, fillRightCx, Height / 2f);

                        (byte r, byte g, byte b, byte a) src = inFill ? fill : inTrack ? track : ((byte)0, (byte)0, (byte)0, (byte)0);
                        float a = src.a / 255f;
                        accR += src.r * a;
                        accG += src.g * a;
                        accB += src.b * a;
                        accA += a;
                    }
                }

                int samples = Supersample * Supersample;
                float outA = accA / samples;
                byte finalA = (byte)Math.Round(outA * 255f);
                byte finalR = 0, finalG = 0, finalB = 0;
                if (outA > 0.0001f)
                {
                    finalR = (byte)Math.Clamp(Math.Round(accR / samples / outA), 0, 255);
                    finalG = (byte)Math.Clamp(Math.Round(accG / samples / outA), 0, 255);
                    finalB = (byte)Math.Clamp(Math.Round(accB / samples / outA), 0, 255);
                }

                int idx = (y * Width + x) * 4;
                buffer[idx] = finalR;
                buffer[idx + 1] = finalG;
                buffer[idx + 2] = finalB;
                buffer[idx + 3] = finalA;
            }
        }

        return buffer;
    }

    /// <summary>
    /// A horizontal "stadium" (pill) shape: a full-height rectangle between
    /// the two cap centers, capped with quarter/half circles of (possibly
    /// different) radii on each end.
    /// </summary>
    private static bool InsideCapsule(float px, float py, float leftRadius, float leftCx, float rightCx, float cy)
    {
        if (px < leftCx) return Dist(px, py, leftCx, cy) <= leftRadius;
        if (px > rightCx) return Dist(px, py, rightCx, cy) <= leftRadius;
        return py >= cy - leftRadius && py <= cy + leftRadius;
    }

    private static float Dist(float x1, float y1, float x2, float y2)
    {
        float dx = x1 - x2, dy = y1 - y2;
        return MathF.Sqrt(dx * dx + dy * dy);
    }

    private static (byte r, byte g, byte b, byte a) ParseHex(string hex, byte defaultAlpha)
    {
        hex = hex.TrimStart('#');
        try
        {
            if (hex.Length == 8)
            {
                byte a = Convert.ToByte(hex.Substring(0, 2), 16);
                byte r = Convert.ToByte(hex.Substring(2, 2), 16);
                byte g = Convert.ToByte(hex.Substring(4, 2), 16);
                byte b = Convert.ToByte(hex.Substring(6, 2), 16);
                return (r, g, b, a);
            }
            if (hex.Length == 6)
            {
                byte r = Convert.ToByte(hex.Substring(0, 2), 16);
                byte g = Convert.ToByte(hex.Substring(2, 2), 16);
                byte b = Convert.ToByte(hex.Substring(4, 2), 16);
                return (r, g, b, defaultAlpha);
            }
        }
        catch { }
        return (128, 128, 128, defaultAlpha);
    }

    /// <summary>Minimal single-pass 8-bit RGBA PNG encoder (no external image library needed).</summary>
    private static class PngEncoder
    {
        private static readonly byte[] Signature = { 137, 80, 78, 71, 13, 10, 26, 10 };

        public static byte[] Encode(byte[] rgba, int width, int height)
        {
            using var ms = new MemoryStream();
            ms.Write(Signature);

            WriteChunk(ms, "IHDR", BuildIhdr(width, height));
            WriteChunk(ms, "IDAT", BuildIdat(rgba, width, height));
            WriteChunk(ms, "IEND", Array.Empty<byte>());

            return ms.ToArray();
        }

        private static byte[] BuildIhdr(int width, int height)
        {
            var ihdr = new byte[13];
            BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0, 4), width);
            BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4, 4), height);
            ihdr[8] = 8;  // bit depth
            ihdr[9] = 6;  // color type: RGBA
            ihdr[10] = 0; // compression
            ihdr[11] = 0; // filter
            ihdr[12] = 0; // interlace
            return ihdr;
        }

        private static byte[] BuildIdat(byte[] rgba, int width, int height)
        {
            int stride = width * 4;
            using var raw = new MemoryStream();
            for (int y = 0; y < height; y++)
            {
                raw.WriteByte(0); // filter type: None
                raw.Write(rgba, y * stride, stride);
            }

            using var compressed = new MemoryStream();
            using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            {
                raw.Position = 0;
                raw.CopyTo(zlib);
            }
            return compressed.ToArray();
        }

        private static void WriteChunk(MemoryStream ms, string type, byte[] data)
        {
            Span<byte> lenBuf = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(lenBuf, data.Length);
            ms.Write(lenBuf);

            byte[] typeBytes = Encoding.ASCII.GetBytes(type);
            ms.Write(typeBytes);
            ms.Write(data);

            uint crc = Crc32(typeBytes, data);
            Span<byte> crcBuf = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(crcBuf, crc);
            ms.Write(crcBuf);
        }

        private static readonly uint[] CrcTable = BuildCrcTable();

        private static uint[] BuildCrcTable()
        {
            var table = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++)
                    c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
                table[n] = c;
            }
            return table;
        }

        private static uint Crc32(byte[] typeBytes, byte[] data)
        {
            uint crc = 0xFFFFFFFF;
            crc = UpdateCrc(crc, typeBytes);
            crc = UpdateCrc(crc, data);
            return crc ^ 0xFFFFFFFF;
        }

        private static uint UpdateCrc(uint crc, byte[] buf)
        {
            foreach (byte b in buf)
                crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
            return crc;
        }
    }
}
