using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace SnapCull.Core;

/// <summary>
/// Minimal TIFF/EXIF reader. Most RAW formats (CR2, NEF, ARW, DNG, ORF, RW2, PEF…) are TIFF files
/// underneath; this walks their IFDs to collect EXIF fields and the locations of embedded JPEG previews.
/// </summary>
internal sealed class TiffParser
{
    const int MaxEntriesPerIfd = 1000;
    const int MaxIfds = 64;
    const int MaxValueBytes = 1 << 16;
    const int MinBlobPreviewBytes = 16 * 1024;

    readonly bool _littleEndian;
    readonly HashSet<long> _visited = [];
    int _iso, _recommendedExposureIndex;

    public Stream Stream { get; }
    /// <summary>Position of the TIFF header in <see cref="Stream"/>; all TIFF offsets are relative to it.</summary>
    public long Base { get; }
    public ExifData Exif { get; } = new();
    /// <summary>Absolute ranges in <see cref="Stream"/> that may hold JPEG data. Not validated yet.</summary>
    public List<ByteRange> JpegRanges { get; } = [];
    /// <summary>Every image directory found, for uncompressed previews and for diagnostics.</summary>
    public List<IfdInfo> Ifds { get; } = [];

    /// <summary>What one IFD (image file directory) describes. Offsets are absolute positions in the stream.</summary>
    public sealed record IfdInfo(long Offset, int Depth, int Width, int Height, int Compression, int Photometric,
        int BitsPerSample, int SamplesPerPixel, int PlanarConfig, uint[] StripOffsets, uint[] StripByteCounts)
    {
        public long DataLength => StripByteCounts.Sum(c => (long)c);

        /// <summary>An 8-bit RGB image stored without compression — what many DNGs and older RAW formats use as a preview.</summary>
        public bool IsUncompressedRgb8 =>
            Compression == 1 && Photometric == 2 && SamplesPerPixel == 3 && BitsPerSample == 8 && PlanarConfig is 0 or 1
            && Width > 0 && Height > 0 && StripOffsets.Length > 0 && StripOffsets.Length == StripByteCounts.Length
            && DataLength >= (long)Width * Height * 3;

        public override string ToString() =>
            $"IFD @{Offset} depth {Depth}: {Width}x{Height}, compression {Compression}, photometric {Photometric}, " +
            $"{SamplesPerPixel}x{BitsPerSample} bit, {StripOffsets.Length} strip(s), {DataLength:N0} bytes";
    }

    TiffParser(Stream stream, long baseOffset, bool littleEndian)
    {
        Stream = stream;
        Base = baseOffset;
        _littleEndian = littleEndian;
    }

    public static TiffParser? Parse(Stream stream, long baseOffset)
    {
        var header = Io.Read(stream, baseOffset, 8);
        if (header is null) return null;

        bool le;
        if (header[0] == 'I' && header[1] == 'I') le = true;
        else if (header[0] == 'M' && header[1] == 'M') le = false;
        else return null;

        var parser = new TiffParser(stream, baseOffset, le);
        // 42 = TIFF; Olympus ORF uses "RO"/"RS", Panasonic RW2 uses 0x55.
        if (parser.U16(header, 2) is not (42 or 0x4F52 or 0x5352 or 0x55)) return null;

        long next = parser.U32(header, 4);
        for (int i = 0; next != 0 && i < 16; i++)
            next = parser.ParseIfd(next, isFirstMainIfd: i == 0, depth: 0);

        // Some cameras write 65535 as ISO above 65535 and put the real value in RecommendedExposureIndex.
        if (parser._iso > 0 && !(parser._iso == 65535 && parser._recommendedExposureIndex > 0))
            parser.Exif.Iso = parser._iso;
        else if (parser._recommendedExposureIndex > 0)
            parser.Exif.Iso = parser._recommendedExposureIndex;

        return parser;
    }

    long ParseIfd(long offset, bool isFirstMainIfd, int depth)
    {
        if (depth > 4 || _visited.Count >= MaxIfds || !_visited.Add(offset)) return 0;

        var countBytes = Io.Read(Stream, Base + offset, 2);
        if (countBytes is null) return 0;
        int count = U16(countBytes, 0);
        if (count == 0 || count > MaxEntriesPerIfd) return 0;
        var entries = Io.Read(Stream, Base + offset + 2, count * 12);
        if (entries is null) return 0;

        int compression = 0, width = 0, height = 0, photometric = -1, bits = 0, samples = 1, planar = 1;
        long jpegOffset = 0, jpegLength = 0;
        uint[]? stripOffsets = null, stripCounts = null;
        var subIfds = new List<long>();
        long exifIfd = 0, gpsIfd = 0;

        for (int i = 0; i < count; i++)
        {
            int e = i * 12;
            ushort tag = U16(entries, e);
            ushort type = U16(entries, e + 2);
            uint n = U32(entries, e + 4);
            int unit = TypeSize(type);
            if (unit == 0 || n == 0) continue;
            long total = (long)unit * n;
            long valueOffset = total <= 4 ? -1 : U32(entries, e + 8);
            var v = new Entry(entries, e, type, n, total, valueOffset);

            switch (tag)
            {
                case 0x0100: width = (int)FirstUInt(v); break;
                case 0x0101: height = (int)FirstUInt(v); break;
                case 0x0102: bits = (int)FirstUInt(v); break;
                case 0x0106: photometric = (int)FirstUInt(v); break;
                case 0x0115: samples = (int)FirstUInt(v); break;
                case 0x011C: planar = (int)FirstUInt(v); break;
                case 0x0103: compression = (int)FirstUInt(v); break;
                case 0x010F: Exif.Make ??= Ascii(v); break;
                case 0x0110: Exif.Model ??= Ascii(v); break;
                case 0x0112:
                    if (isFirstMainIfd && depth == 0)
                    {
                        uint o = FirstUInt(v);
                        Exif.Orientation = o is >= 1 and <= 8 ? (int)o : 1;
                    }
                    break;
                case 0x0111: stripOffsets = UInts(v); break;
                case 0x0117: stripCounts = UInts(v); break;
                case 0x0201: jpegOffset = FirstUInt(v); break;
                case 0x0202: jpegLength = FirstUInt(v); break;
                case 0x014A:
                    if (UInts(v) is { } subs) foreach (var s in subs) subIfds.Add(s);
                    break;
                case 0x8769: exifIfd = FirstUInt(v); break;
                case 0x8825: gpsIfd = FirstUInt(v); break;
                case 0x829A: Exif.ExposureTime ??= Rational(v); break;
                case 0x829D: Exif.FNumber ??= Rational(v); break;
                case 0x8827: if (_iso == 0) _iso = (int)FirstUInt(v); break;
                case 0x8832: if (_recommendedExposureIndex == 0) _recommendedExposureIndex = (int)FirstUInt(v); break;
                case 0x9003: Exif.DateTaken ??= ParseDate(Ascii(v)); break;
                case 0x920A: Exif.FocalLength ??= Rational(v); break;
                case 0xA434: Exif.LensModel ??= Ascii(v); break;
                default:
                    // Some formats store a whole JPEG as a byte blob (e.g. Panasonic RW2's JpgFromRaw, tag 0x2E).
                    if (type is 1 or 7 && total >= MinBlobPreviewBytes && valueOffset >= 0 && StartsWithJpegMarker(valueOffset))
                        JpegRanges.Add(new ByteRange(Base + valueOffset, total));
                    break;
            }
        }

        Ifds.Add(new IfdInfo(Base + offset, depth, width, height, compression, photometric, bits, samples, planar,
            stripOffsets?.Select(o => (uint)Math.Min(uint.MaxValue, Base + o)).ToArray() ?? [], stripCounts ?? []));

        if (jpegOffset > 0 && jpegLength > 0)
            JpegRanges.Add(new ByteRange(Base + jpegOffset, jpegLength));
        // A single compressed strip may be a JPEG preview (CR2 IFD0) or raw data; validation sorts that out later.
        if (compression != 1 && stripOffsets is { Length: 1 } && stripCounts is { Length: 1 } && stripCounts[0] > 0)
            JpegRanges.Add(new ByteRange(Base + stripOffsets[0], stripCounts[0]));

        foreach (var sub in subIfds) ParseIfd(sub, isFirstMainIfd: false, depth + 1);
        if (exifIfd != 0) ParseIfd(exifIfd, isFirstMainIfd: false, depth + 1);
        if (gpsIfd != 0 && Exif.GpsLatitude is null) ParseGpsIfd(gpsIfd);

        var next = Io.Read(Stream, Base + offset + 2 + count * 12, 4);
        return next is null ? 0 : U32(next, 0);
    }

    /// <summary>The GPS IFD reuses small tag numbers (1–6) with their own meanings, so it gets its own reader.</summary>
    void ParseGpsIfd(long offset)
    {
        if (!_visited.Add(offset)) return;
        var countBytes = Io.Read(Stream, Base + offset, 2);
        if (countBytes is null) return;
        int count = U16(countBytes, 0);
        if (count == 0 || count > 100) return;
        var entries = Io.Read(Stream, Base + offset + 2, count * 12);
        if (entries is null) return;

        string? latRef = null, lonRef = null;
        double[]? lat = null, lon = null;
        double? alt = null;
        bool belowSeaLevel = false;
        for (int i = 0; i < count; i++)
        {
            int e = i * 12;
            ushort tag = U16(entries, e), type = U16(entries, e + 2);
            uint n = U32(entries, e + 4);
            int unit = TypeSize(type);
            if (unit == 0 || n == 0) continue;
            long total = (long)unit * n;
            var v = new Entry(entries, e, type, n, total, total <= 4 ? -1 : U32(entries, e + 8));
            switch (tag)
            {
                case 1: latRef = Ascii(v); break;
                case 2: lat = Rationals(v); break;
                case 3: lonRef = Ascii(v); break;
                case 4: lon = Rationals(v); break;
                case 5: belowSeaLevel = FirstUInt(v) == 1; break;
                case 6: alt = Rational(v); break;
            }
        }

        static double? Degrees(double[]? dms) => dms is { Length: >= 1 }
            ? dms[0] + (dms.Length > 1 ? dms[1] / 60 : 0) + (dms.Length > 2 ? dms[2] / 3600 : 0)
            : null;
        if (Degrees(lat) is double la && Degrees(lon) is double lo && !(la == 0 && lo == 0))
        {
            Exif.GpsLatitude = latRef?.StartsWith('S') == true ? -la : la;
            Exif.GpsLongitude = lonRef?.StartsWith('W') == true ? -lo : lo;
            if (alt is double a) Exif.GpsAltitude = belowSeaLevel ? -a : a;
        }
    }

    double[]? Rationals(Entry v)
    {
        if (v.Type is not (5 or 10)) return null;
        var b = ValueBytes(v);
        if (b is null) return null;
        var result = new double[v.Count];
        for (int i = 0; i < result.Length; i++)
        {
            double num = v.Type == 5 ? U32(b, i * 8) : (int)U32(b, i * 8);
            double den = v.Type == 5 ? U32(b, i * 8 + 4) : (int)U32(b, i * 8 + 4);
            result[i] = den == 0 ? 0 : num / den;
        }
        return result;
    }

    readonly record struct Entry(byte[] Entries, int Index, ushort Type, uint Count, long Total, long ValueOffset);

    byte[]? ValueBytes(Entry v)
    {
        if (v.ValueOffset < 0) return v.Entries.AsSpan(v.Index + 8, (int)v.Total).ToArray();
        if (v.Total > MaxValueBytes) return null;
        return Io.Read(Stream, Base + v.ValueOffset, (int)v.Total);
    }

    uint FirstUInt(Entry v)
    {
        var b = ValueBytes(v with { Count = 1, Total = Math.Min(v.Total, TypeSize(v.Type)) });
        if (b is null) return 0;
        return v.Type switch
        {
            3 or 8 => U16(b, 0),
            4 or 9 or 13 => U32(b, 0),
            1 or 6 or 7 => b[0],
            _ => 0,
        };
    }

    uint[]? UInts(Entry v)
    {
        if (v.Type is not (3 or 4 or 13)) return null;
        var b = ValueBytes(v);
        if (b is null) return null;
        var result = new uint[v.Count];
        for (int i = 0; i < result.Length; i++)
            result[i] = v.Type == 3 ? U16(b, i * 2) : U32(b, i * 4);
        return result;
    }

    double? Rational(Entry v)
    {
        if (v.Type is 3 or 4) return FirstUInt(v);
        if (v.Type is not (5 or 10)) return null;
        var b = ValueBytes(v with { Count = 1, Total = 8 });
        if (b is null) return null;
        double num = v.Type == 5 ? U32(b, 0) : (int)U32(b, 0);
        double den = v.Type == 5 ? U32(b, 4) : (int)U32(b, 4);
        return den == 0 ? null : num / den;
    }

    string? Ascii(Entry v)
    {
        if (v.Type is not (1 or 2 or 7)) return null;
        var b = ValueBytes(v);
        if (b is null) return null;
        int end = Array.IndexOf(b, (byte)0);
        var s = Encoding.Latin1.GetString(b, 0, end < 0 ? b.Length : end).Trim();
        return s.Length == 0 ? null : s;
    }

    static DateTime? ParseDate(string? s) =>
        s is not null && DateTime.TryParseExact(s, "yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d
            : null;

    bool StartsWithJpegMarker(long valueOffset) =>
        Io.Read(Stream, Base + valueOffset, 2) is [0xFF, 0xD8];

    static int TypeSize(ushort type) => type switch
    {
        1 or 2 or 6 or 7 => 1,
        3 or 8 => 2,
        4 or 9 or 11 or 13 => 4,
        5 or 10 or 12 => 8,
        _ => 0,
    };

    ushort U16(byte[] b, int i) => _littleEndian
        ? BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(i))
        : BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(i));

    uint U32(byte[] b, int i) => _littleEndian
        ? BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(i))
        : BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(i));
}
