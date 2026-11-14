namespace SnapCull.Core;

/// <summary>Where a JPEG sits in a buffer and how big the picture is.</summary>
/// <param name="Length">Total length in bytes including the end marker, or -1 when only the header was parsed.</param>
public readonly record struct JpegInfo(int Start, int Length, int Width, int Height);

/// <summary>
/// Walks JPEG marker segments without decoding any pixels. Used to validate and measure the
/// preview JPEGs that cameras embed in RAW files, and to find JPEGs inside unknown containers.
/// </summary>
public static class JpegScanner
{
    /// <summary>
    /// Parses the JPEG that starts at <paramref name="start"/>. Returns null if it isn't a baseline or
    /// progressive JPEG (lossless JPEG — used for raw sensor data in CR2/DNG — is rejected on purpose).
    /// With <paramref name="scanToEnd"/> false, stops as soon as the image size is known.
    /// </summary>
    public static JpegInfo? Parse(ReadOnlySpan<byte> d, int start, bool scanToEnd)
    {
        if (start < 0 || start + 4 > d.Length || d[start] != 0xFF || d[start + 1] != 0xD8) return null;

        int p = start + 2;
        int width = 0, height = 0;
        bool haveFrame = false, inScan = false;

        while (true)
        {
            if (inScan)
            {
                // Entropy-coded data: skip until a real marker (0xFF followed by something other than 0x00 or RSTn).
                int idx = d[p..].IndexOf((byte)0xFF);
                if (idx < 0) return null;
                p += idx;
                int q = p + 1;
                while (q < d.Length && d[q] == 0xFF) q++;
                if (q >= d.Length) return null;
                byte m = d[q];
                if (m == 0x00 || m is >= 0xD0 and <= 0xD7) { p = q + 1; continue; }
                if (m == 0xD9) return new JpegInfo(start, q + 1 - start, width, height);
                inScan = false; // progressive JPEGs have more segments (DHT, SOS…) between scans
                p = q - 1;
                continue;
            }

            if (p + 2 > d.Length || d[p] != 0xFF) return null;
            while (p + 1 < d.Length && d[p + 1] == 0xFF) p++; // fill bytes
            if (p + 2 > d.Length) return null;
            byte marker = d[p + 1];
            p += 2;

            if (marker is 0x01 or >= 0xD0 and <= 0xD7) continue; // standalone markers
            if (marker < 0xC0 || marker is 0xD8 or 0xD9) return null;
            if (p + 2 > d.Length) return null;
            int len = (d[p] << 8) | d[p + 1];
            if (len < 2) return null;

            switch (marker)
            {
                case 0xC0 or 0xC1 or 0xC2: // baseline, extended, progressive
                    if (len < 8 || p + 7 > d.Length) return null;
                    height = (d[p + 3] << 8) | d[p + 4];
                    width = (d[p + 5] << 8) | d[p + 6];
                    if (width == 0 || height == 0) return null;
                    haveFrame = true;
                    if (!scanToEnd) return new JpegInfo(start, -1, width, height);
                    break;
                case 0xC3 or 0xC5 or 0xC6 or 0xC7 or 0xC9 or 0xCA or 0xCB or 0xCD or 0xCE or 0xCF:
                    return null; // lossless, hierarchical or arithmetic-coded: raw data, not a viewable preview
                case 0xDA:
                    if (!haveFrame) return null;
                    inScan = true;
                    break;
            }

            p += len;
            if (p > d.Length) return null;
        }
    }

    /// <summary>Finds the TIFF block inside a JPEG's "Exif" APP1 segment. Returns (offset, length) or null.</summary>
    public static (int Offset, int Length)? FindExifSegment(ReadOnlySpan<byte> d)
    {
        if (d.Length < 4 || d[0] != 0xFF || d[1] != 0xD8) return null;
        int p = 2;
        while (p + 4 <= d.Length)
        {
            if (d[p] != 0xFF) return null;
            byte marker = d[p + 1];
            if (marker == 0xFF) { p++; continue; }
            if (marker is 0xDA or 0xD9) return null;
            int len = (d[p + 2] << 8) | d[p + 3];
            if (len < 2) return null;
            if (marker == 0xE1 && len >= 8 && p + 10 <= d.Length && d.Slice(p + 4, 6).SequenceEqual("Exif\0\0"u8))
            {
                int dataStart = p + 10;
                int dataLength = Math.Min(len - 8, d.Length - dataStart);
                return dataLength > 8 ? (dataStart, dataLength) : null;
            }
            p += 2 + len;
        }
        return null;
    }
}
