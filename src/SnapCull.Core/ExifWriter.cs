using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace SnapCull.Core;

/// <summary>
/// Writes a small, standard EXIF block (a little-endian TIFF structure) for converted photos:
/// camera, lens, exposure settings, capture time and, optionally, GPS location.
/// Orientation is always 1 because converted pixels are already the right way up.
/// </summary>
public static class ExifWriter
{
    const ushort Ascii = 2, Short = 3, Long = 4, RationalType = 5, Byte = 1, Undefined = 7;

    sealed record Field(ushort Tag, ushort Type, uint Count, byte[] Value, int PointsToIfd = -1);

    public static byte[] Build(ExifData exif, bool includeGps, string software = "SnapCull")
    {
        var ifd0 = new List<Field>();
        var exifIfd = new List<Field>();
        var gpsIfd = new List<Field>();

        AddAscii(ifd0, 0x010F, exif.Make);
        AddAscii(ifd0, 0x0110, exif.Model);
        ifd0.Add(new Field(0x0112, Short, 1, U16(1)));
        AddAscii(ifd0, 0x0131, software);
        AddAscii(ifd0, 0x0132, FormatDate(exif.DateTaken));

        exifIfd.Add(new Field(0x9000, Undefined, 4, "0232"u8.ToArray()));
        if (exif.ExposureTime is double t && t > 0) exifIfd.Add(new Field(0x829A, RationalType, 1, ExposureRational(t)));
        if (exif.FNumber is double f && f > 0) exifIfd.Add(new Field(0x829D, RationalType, 1, Rational(f, 10)));
        if (exif.Iso is int iso && iso > 0) exifIfd.Add(new Field(0x8827, Short, 1, U16((ushort)Math.Min(iso, ushort.MaxValue))));
        AddAscii(exifIfd, 0x9003, FormatDate(exif.DateTaken));
        if (exif.FocalLength is double fl && fl > 0) exifIfd.Add(new Field(0x920A, RationalType, 1, Rational(fl, 10)));
        AddAscii(exifIfd, 0xA434, exif.LensModel);

        bool gps = includeGps && exif.HasGps;
        if (gps)
        {
            double lat = exif.GpsLatitude!.Value, lon = exif.GpsLongitude!.Value;
            gpsIfd.Add(new Field(0x0000, Byte, 4, [2, 3, 0, 0]));
            gpsIfd.Add(new Field(0x0001, Ascii, 2, [(byte)(lat < 0 ? 'S' : 'N'), 0]));
            gpsIfd.Add(new Field(0x0002, RationalType, 3, Dms(Math.Abs(lat))));
            gpsIfd.Add(new Field(0x0003, Ascii, 2, [(byte)(lon < 0 ? 'W' : 'E'), 0]));
            gpsIfd.Add(new Field(0x0004, RationalType, 3, Dms(Math.Abs(lon))));
            if (exif.GpsAltitude is double alt)
            {
                gpsIfd.Add(new Field(0x0005, Byte, 1, [(byte)(alt < 0 ? 1 : 0)]));
                gpsIfd.Add(new Field(0x0006, RationalType, 1, Rational(Math.Abs(alt), 100)));
            }
        }

        // IFD order in the file: IFD0, EXIF IFD, GPS IFD. IFD0 points at the other two.
        var ifds = new List<List<Field>> { ifd0, exifIfd };
        ifd0.Add(new Field(0x8769, Long, 1, new byte[4], PointsToIfd: 1));
        if (gps)
        {
            ifds.Add(gpsIfd);
            ifd0.Add(new Field(0x8825, Long, 1, new byte[4], PointsToIfd: 2));
        }
        foreach (var list in ifds) list.Sort((a, b) => a.Tag.CompareTo(b.Tag));

        // Work out where each IFD starts, then fill in the pointers and write.
        var starts = new int[ifds.Count];
        int pos = 8;
        for (int i = 0; i < ifds.Count; i++)
        {
            starts[i] = pos;
            pos += IfdSize(ifds[i]);
        }

        var output = new byte[pos];
        "II"u8.CopyTo(output);
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(2), 42);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(4), (uint)starts[0]);
        for (int i = 0; i < ifds.Count; i++)
            WriteIfd(output, starts[i], ifds[i], starts);
        return output;
    }

    static int IfdSize(List<Field> fields)
    {
        int size = 2 + fields.Count * 12 + 4;
        foreach (var f in fields)
            if (f.Value.Length > 4) size += (f.Value.Length + 1) & ~1;
        return size;
    }

    static void WriteIfd(byte[] output, int start, List<Field> fields, int[] ifdStarts)
    {
        var span = output.AsSpan();
        BinaryPrimitives.WriteUInt16LittleEndian(span[start..], (ushort)fields.Count);
        int entry = start + 2;
        int data = start + 2 + fields.Count * 12 + 4;
        foreach (var f in fields)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(span[entry..], f.Tag);
            BinaryPrimitives.WriteUInt16LittleEndian(span[(entry + 2)..], f.Type);
            BinaryPrimitives.WriteUInt32LittleEndian(span[(entry + 4)..], f.Count);
            if (f.PointsToIfd >= 0)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(span[(entry + 8)..], (uint)ifdStarts[f.PointsToIfd]);
            }
            else if (f.Value.Length <= 4)
            {
                f.Value.CopyTo(span[(entry + 8)..]);
            }
            else
            {
                BinaryPrimitives.WriteUInt32LittleEndian(span[(entry + 8)..], (uint)data);
                f.Value.CopyTo(span[data..]);
                data += (f.Value.Length + 1) & ~1;
            }
            entry += 12;
        }
        BinaryPrimitives.WriteUInt32LittleEndian(span[entry..], 0); // no next IFD
    }

    static void AddAscii(List<Field> list, ushort tag, string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var bytes = Encoding.Latin1.GetBytes(text.Trim() + "\0");
        list.Add(new Field(tag, Ascii, (uint)bytes.Length, bytes));
    }

    static string? FormatDate(DateTime? d) => d?.ToString("yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture);

    static byte[] U16(ushort v) { var b = new byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(b, v); return b; }

    static byte[] Rational(double value, uint denominator)
    {
        var b = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(b, (uint)Math.Round(value * denominator));
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), denominator);
        return b;
    }

    // 1/250 s is stored as 1/250, 2.5 s as 25/10, the way cameras do.
    static byte[] ExposureRational(double seconds)
    {
        var b = new byte[8];
        if (seconds < 1)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(b, 1);
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), (uint)Math.Round(1 / seconds));
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(b, (uint)Math.Round(seconds * 10));
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), 10);
        }
        return b;
    }

    static byte[] Dms(double degrees)
    {
        int d = (int)degrees;
        double minutesTotal = (degrees - d) * 60;
        int m = (int)minutesTotal;
        double s = (minutesTotal - m) * 60;
        return [.. Rational(d, 1), .. Rational(m, 1), .. Rational(s, 10000)];
    }
}

/// <summary>Puts an EXIF block (from <see cref="ExifWriter"/>) into encoded JPEG, PNG and WebP files.</summary>
public static class MetadataEmbedder
{
    /// <summary>Adds an "Exif" APP1 segment right after the start-of-image marker.</summary>
    public static byte[] EmbedInJpeg(byte[] jpeg, byte[] tiff)
    {
        if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8 || tiff.Length + 8 > ushort.MaxValue) return jpeg;
        int length = 2 + 6 + tiff.Length;
        byte[] segment = [0xFF, 0xE1, (byte)(length >> 8), (byte)length, .. "Exif\0\0"u8, .. tiff];
        return [0xFF, 0xD8, .. segment, .. jpeg.AsSpan(2)];
    }

    /// <summary>Adds an "eXIf" chunk right after the IHDR header chunk.</summary>
    public static byte[] EmbedInPng(byte[] png, byte[] tiff)
    {
        const int ihdrEnd = 8 + 4 + 4 + 13 + 4; // signature + IHDR (length, type, 13 bytes of data, CRC)
        if (png.Length < ihdrEnd || !png.AsSpan(12, 4).SequenceEqual("IHDR"u8)) return png;
        var chunk = new byte[12 + tiff.Length];
        BinaryPrimitives.WriteUInt32BigEndian(chunk, (uint)tiff.Length);
        "eXIf"u8.CopyTo(chunk.AsSpan(4));
        tiff.CopyTo(chunk, 8);
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(8 + tiff.Length), Crc32(chunk.AsSpan(4, 4 + tiff.Length)));
        return [.. png.AsSpan(0, ihdrEnd), .. chunk, .. png.AsSpan(ihdrEnd)];
    }

    /// <summary>
    /// Adds an "EXIF" chunk to a WebP file. Simple WebP files have to be turned into the "extended" layout
    /// first, which starts with a VP8X chunk holding the canvas size and a flag saying EXIF is present.
    /// </summary>
    public static byte[] EmbedInWebp(byte[] webp, byte[] tiff, int width, int height)
    {
        if (webp.Length < 20 || !webp.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !webp.AsSpan(8, 4).SequenceEqual("WEBP"u8)) return webp;
        var body = new List<byte>();
        bool extended = webp.AsSpan(12, 4).SequenceEqual("VP8X"u8);
        if (extended)
        {
            body.AddRange(webp.AsSpan(12).ToArray());
            body[8] |= 0x08; // EXIF flag (first byte of VP8X data)
        }
        else
        {
            var vp8x = new byte[18];
            "VP8X"u8.CopyTo(vp8x);
            BinaryPrimitives.WriteUInt32LittleEndian(vp8x.AsSpan(4), 10);
            vp8x[8] = 0x08;
            WriteUInt24(vp8x.AsSpan(12), width - 1);
            WriteUInt24(vp8x.AsSpan(15), height - 1);
            body.AddRange(vp8x);
            body.AddRange(webp.AsSpan(12).ToArray());
        }

        var size = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(size, (uint)tiff.Length);
        body.AddRange("EXIF"u8.ToArray());
        body.AddRange(size);
        body.AddRange(tiff);
        if (tiff.Length % 2 == 1) body.Add(0);

        var riffSize = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(riffSize, (uint)(4 + body.Count));
        return [.. "RIFF"u8, .. riffSize, .. "WEBP"u8, .. body];
    }

    static void WriteUInt24(Span<byte> s, int v)
    {
        s[0] = (byte)v; s[1] = (byte)(v >> 8); s[2] = (byte)(v >> 16);
    }

    static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n =>
    {
        uint c = (uint)n;
        for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint c = 0xFFFFFFFF;
        foreach (var b in data) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFF;
    }
}
