using System.Buffers.Binary;

namespace SnapCull.Core;

public enum PreviewSize
{
    /// <summary>The biggest embedded preview (usually full resolution) — for the main viewer and 100% zoom.</summary>
    Largest,
    /// <summary>The smallest embedded preview that still looks right — for the filmstrip.</summary>
    Thumbnail,
}

/// <param name="Image">The encoded image (JPEG for RAW previews; HEIF, PNG, TIFF or WebP for those files), ready to decode.</param>
/// <param name="Width">Pixel width before EXIF orientation is applied, or 0 if not known without decoding.</param>
/// <param name="Height">Pixel height before EXIF orientation is applied, or 0 if not known without decoding.</param>
public sealed record PreviewResult(byte[] Image, int Width, int Height, ExifData Exif)
{
    public bool IsJpeg => Image.Length > 2 && Image[0] == 0xFF && Image[1] == 0xD8;
}

/// <summary>
/// Pulls the JPEG previews that cameras embed in RAW files, so photos can be shown instantly
/// without demosaicing the raw sensor data. Also handles JPEG, HEIF, PNG, TIFF and WebP files.
/// </summary>
public static class PreviewExtractor
{
    const int HeaderProbeBytes = 1 << 20;
    const long MaxScanBytes = 512L << 20;
    const long MaxImageBytes = 1L << 30;
    const int MinThumbnailSide = 160;
    const int MinGoodPreviewSide = 1000;
    const double AspectTolerance = 0.03;

    static readonly byte[] CanonUuid = Convert.FromHexString("85C0B687820F11E08111F4CE462B6A48");
    static readonly byte[] CanonPreviewUuid = Convert.FromHexString("EAF42B5E1C984B88B9FBB7DC406E4D16");
    static readonly string[] HeifBrands = ["heic", "heix", "hevc", "hevx", "heim", "heis", "mif1", "msf1", "avif", "avis", "MiHE", "MiAF"];

    enum Container { Jpeg, Raf, Cr3, Crw, Heif, Png, Webp, Mrw, Tiff, Unknown }

    sealed record Candidate(long Offset, int Length, int Width, int Height, byte[]? Buffer = null, int BufferOffset = 0,
        Func<Stream, byte[]>? Build = null)
    {
        public long Area => (long)Width * Height;
        public int LongSide => Math.Max(Width, Height);
        public double Aspect => (double)Width / Height;

        public byte[] ReadBytes(Stream s) =>
            Build is not null ? Build(s)
            : Buffer is not null ? Buffer.AsSpan(BufferOffset, Length).ToArray()
            : Io.Read(s, Offset, Length) ?? throw new EndOfStreamException("Preview is outside the file.");
    }

    /// <summary>Uncompressed 8-bit RGB previews (common in DNG, Mamiya, Epson, Leica M8) become candidates too, converted to BMP when chosen.</summary>
    static IEnumerable<Candidate> RgbCandidates(TiffParser tiff) =>
        tiff.Ifds.Where(i => i.IsUncompressedRgb8 && (long)i.Width * i.Height <= 64_000_000)
                 .Select(i => new Candidate(i.StripOffsets[0], 0, i.Width, i.Height, Build: s => RgbToBmp(s, i)));

    static byte[] RgbToBmp(Stream s, TiffParser.IfdInfo ifd)
    {
        int w = ifd.Width, h = ifd.Height;
        var rgb = new byte[(long)w * h * 3];
        int filled = 0;
        for (int i = 0; i < ifd.StripOffsets.Length && filled < rgb.Length; i++)
        {
            int count = (int)Math.Min(ifd.StripByteCounts[i], rgb.Length - filled);
            var strip = Io.Read(s, ifd.StripOffsets[i], count) ?? throw new EndOfStreamException("Preview strip is outside the file.");
            strip.CopyTo(rgb, filled);
            filled += count;
        }

        // 24-bit BMP: rows are BGR, bottom-up, padded to 4 bytes.
        int stride = (w * 3 + 3) & ~3;
        var bmp = new byte[54 + stride * h];
        bmp[0] = (byte)'B'; bmp[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(2), bmp.Length);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(10), 54);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(14), 40);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(18), w);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(22), h);
        BinaryPrimitives.WriteInt16LittleEndian(bmp.AsSpan(26), 1);
        BinaryPrimitives.WriteInt16LittleEndian(bmp.AsSpan(28), 24);
        for (int y = 0; y < h; y++)
        {
            int src = y * w * 3, dst = 54 + (h - 1 - y) * stride;
            for (int x = 0; x < w; x++, src += 3, dst += 3)
            {
                bmp[dst] = rgb[src + 2];
                bmp[dst + 1] = rgb[src + 1];
                bmp[dst + 2] = rgb[src];
            }
        }
        return bmp;
    }

    static FileStream Open(string path) => new(path, FileMode.Open, FileAccess.Read,
        FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.RandomAccess);

    public static PreviewResult? Extract(string path, PreviewSize size = PreviewSize.Largest)
    {
        using var fs = Open(path);
        return Extract(fs, size, PhotoFormats.IsOtherImage(path));
    }

    /// <param name="plainImage">True for files that are already a finished picture (e.g. a .tif from a scanner) rather than RAW.</param>
    public static PreviewResult? Extract(Stream stream, PreviewSize size = PreviewSize.Largest, bool plainImage = false)
    {
        var kind = Detect(stream);
        if (kind is Container.Tiff && plainImage) return FromImageFile(stream, kind);
        return kind switch
        {
            Container.Jpeg => FromJpegFile(stream, size),
            Container.Raf => FromRaf(stream),
            Container.Cr3 => FromCr3(stream, size),
            Container.Heif or Container.Png or Container.Webp => FromImageFile(stream, kind),
            Container.Mrw => FromMrw(stream, size),
            Container.Crw => FromCandidates(stream, size, [], CiffParser.Read(stream)),
            Container.Tiff => FromTiff(stream, size),
            Container.Unknown when stream.CanSeek && stream.Length >= 16 => FromScan(stream, size, new ExifData()),
            _ => null,
        };
    }

    /// <summary>Reads just the EXIF fields (camera, settings, capture time, orientation) without loading any preview.</summary>
    public static ExifData? ReadExif(string path)
    {
        using var fs = Open(path);
        return ReadExif(fs);
    }

    public static ExifData? ReadExif(Stream s)
    {
        switch (Detect(s))
        {
            case Container.Jpeg:
                return ReadJpegExif(Io.Read(s, 0, (int)Math.Min(s.Length, 256 * 1024))!)?.Exif;
            case Container.Raf when RafPreviewRange(s) is { } r:
                var head = Io.Read(s, r.Offset, (int)Math.Min(r.Length, 256 * 1024));
                return head is null ? null : ReadJpegExif(head)?.Exif;
            case Container.Cr3:
                var exif = new ExifData();
                foreach (var box in Bmff.Boxes(s, 0, s.Length))
                    if (box.Type == "moov") ReadCr3Moov(s, box, exif, [], [], scanThumbnail: false);
                return exif;
            case Container.Heif:
            case Container.Png:
            case Container.Webp:
                return ReadImageExif(s, Detect(s));
            case Container.Mrw:
                return MrwTiff(s)?.Exif;
            case Container.Crw:
                return CiffParser.Read(s);
            case Container.Tiff:
                return TiffParser.Parse(s, 0)?.Exif;
            default:
                return null;
        }
    }

    static Container Detect(Stream s)
    {
        if (!s.CanSeek || s.Length < 16) return Container.Unknown;
        var h = Io.Read(s, 0, 16)!;
        var span = h.AsSpan();
        if (h[0] == 0xFF && h[1] == 0xD8) return Container.Jpeg;
        if (span.StartsWith("FUJIFILMCCD-RAW "u8)) return Container.Raf;
        if (h[0] == 0x89 && span.Slice(1, 3).SequenceEqual("PNG"u8)) return Container.Png;
        if (span.StartsWith("RIFF"u8) && span.Slice(8, 4).SequenceEqual("WEBP"u8)) return Container.Webp;
        if (span.StartsWith("\0MRM"u8)) return Container.Mrw;
        if (CiffParser.IsCiff(span)) return Container.Crw;
        if (span.Slice(4, 4).SequenceEqual("ftyp"u8))
        {
            if (span.Slice(8, 4).SequenceEqual("crx "u8)) return Container.Cr3;
            return IsHeif(s) ? Container.Heif : Container.Unknown;
        }
        if ((h[0] == 'I' && h[1] == 'I') || (h[0] == 'M' && h[1] == 'M')) return Container.Tiff;
        return Container.Unknown;
    }

    static bool IsHeif(Stream s)
    {
        foreach (var box in Bmff.Boxes(s, 0, s.Length))
        {
            if (box.Type != "ftyp") return false;
            var brands = Io.Read(s, box.ContentStart, (int)Math.Min(box.Length, 64));
            if (brands is null) return false;
            for (int i = 0; i + 4 <= brands.Length; i += 4)
            {
                if (i == 4) continue; // minor version
                var brand = System.Text.Encoding.ASCII.GetString(brands, i, 4);
                if (HeifBrands.Contains(brand)) return true;
            }
            return false;
        }
        return false;
    }

    // CR2, NEF, ARW, DNG, ORF, RW2, PEF, SRW, 3FR, IIQ… are all TIFF-based.
    static PreviewResult? FromTiff(Stream s, PreviewSize size)
    {
        var tiff = TiffParser.Parse(s, 0);
        if (tiff is null) return FromScan(s, size, new ExifData());
        var candidates = Validate(s, tiff.JpegRanges);
        candidates.AddRange(RgbCandidates(tiff));
        return FromCandidates(s, size, candidates, tiff.Exif);
    }

    /// <summary>Picks the best candidate; if none is good enough, also scans the whole file (some makers hide previews in maker notes).</summary>
    static PreviewResult? FromCandidates(Stream s, PreviewSize size, List<Candidate> candidates, ExifData exif)
    {
        var best = Choose(candidates, size);
        if (best is null || (size == PreviewSize.Largest && best.LongSide < MinGoodPreviewSide))
        {
            candidates.AddRange(ScanForJpegs(s, 0, s.Length));
            best = Choose(candidates, size);
        }
        return best is null ? null : new PreviewResult(best.ReadBytes(s), best.Width, best.Height, exif);
    }

    static PreviewResult? FromCr3(Stream s, PreviewSize size)
    {
        var exif = new ExifData();
        var ranges = new List<ByteRange>();
        var found = new List<Candidate>();

        foreach (var box in Bmff.Boxes(s, 0, s.Length))
        {
            if (box.Type == "moov")
                ReadCr3Moov(s, box, exif, ranges, found, scanThumbnail: true);
            else if (box.Type == "uuid" && Bmff.HasUuid(s, box, CanonPreviewUuid))
                found.AddRange(ScanForJpegs(s, box.ContentStart + 16, box.End)); // PRVW: ~1620×1080
        }

        var candidates = Validate(s, ranges);
        candidates.AddRange(found);
        return FromCandidates(s, size, candidates, exif);
    }

    static void ReadCr3Moov(Stream s, Bmff.Box moov, ExifData exif, List<ByteRange> ranges, List<Candidate> found, bool scanThumbnail)
    {
        bool firstTrack = true;
        foreach (var box in Bmff.Boxes(s, moov.ContentStart, moov.End))
        {
            if (box.Type == "uuid" && Bmff.HasUuid(s, box, CanonUuid))
            {
                foreach (var child in Bmff.Boxes(s, box.ContentStart + 16, box.End))
                {
                    if (child.Type is "CMT1" or "CMT2") // CMT1 = TIFF IFD0, CMT2 = EXIF IFD
                    {
                        int len = (int)Math.Min(child.Length, 1 << 20);
                        if (Io.Read(s, child.ContentStart, len) is { } bytes && TiffParser.Parse(new MemoryStream(bytes), 0) is { } t)
                            exif.MergeFrom(t.Exif);
                    }
                    else if (child.Type == "THMB" && scanThumbnail)
                    {
                        found.AddRange(ScanForJpegs(s, child.ContentStart, child.End)); // 160×120
                    }
                }
            }
            else if (box.Type == "trak" && firstTrack)
            {
                // Track 1 of a CR3 is a full-resolution JPEG.
                firstTrack = false;
                if (Bmff.FindPath(s, box, "mdia", "minf", "stbl") is { } stbl && FirstSample(s, stbl) is { } range)
                    ranges.Add(range);
            }
        }
    }

    static ByteRange? FirstSample(Stream s, Bmff.Box stbl)
    {
        long size = 0, offset = 0;
        foreach (var b in Bmff.Boxes(s, stbl.ContentStart, stbl.End))
        {
            switch (b.Type)
            {
                case "stsz" when Io.Read(s, b.ContentStart, 12) is { } d:
                    uint fixedSize = BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(4));
                    uint count = BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(8));
                    if (fixedSize != 0) size = fixedSize;
                    else if (count > 0 && Io.Read(s, b.ContentStart + 12, 4) is { } first)
                        size = BinaryPrimitives.ReadUInt32BigEndian(first);
                    break;
                case "co64" when Io.Read(s, b.ContentStart, 16) is { } d:
                    if (BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(4)) > 0)
                        offset = (long)BinaryPrimitives.ReadUInt64BigEndian(d.AsSpan(8));
                    break;
                case "stco" when Io.Read(s, b.ContentStart, 12) is { } d:
                    if (BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(4)) > 0)
                        offset = BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(8));
                    break;
            }
        }
        return size > 0 && offset > 0 ? new ByteRange(offset, size) : null;
    }

    static ByteRange? RafPreviewRange(Stream s)
    {
        var header = Io.Read(s, 0, 92);
        if (header is null) return null;
        return new ByteRange(BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(84)), BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(88)));
    }

    static PreviewResult? FromRaf(Stream s)
    {
        var candidates = RafPreviewRange(s) is { } r ? Validate(s, [r]) : [];
        if (candidates.Count == 0) return FromScan(s, PreviewSize.Largest, new ExifData());

        var c = candidates[0];
        var bytes = c.ReadBytes(s);
        var exif = ReadJpegExif(bytes)?.Exif ?? new ExifData();
        return new PreviewResult(bytes, c.Width, c.Height, exif);
    }

    // Minolta MRW: a chain of blocks, one of which ("\0TTW") is a TIFF with the EXIF data.
    static TiffParser? MrwTiff(Stream s)
    {
        var header = Io.Read(s, 0, 8);
        if (header is null) return null;
        long end = Math.Min(s.Length, 8 + (long)BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4)));
        long pos = 8;
        while (pos + 8 <= end)
        {
            var block = Io.Read(s, pos, 8)!;
            long length = BinaryPrimitives.ReadUInt32BigEndian(block.AsSpan(4));
            if (block.AsSpan(0, 4).SequenceEqual("\0TTW"u8)) return TiffParser.Parse(s, pos + 8);
            pos += 8 + length;
        }
        return null;
    }

    static PreviewResult? FromMrw(Stream s, PreviewSize size)
    {
        var tiff = MrwTiff(s);
        var candidates = tiff is null ? [] : Validate(s, tiff.JpegRanges);
        if (tiff is not null) candidates.AddRange(RgbCandidates(tiff));
        return FromCandidates(s, size, candidates, tiff?.Exif ?? new ExifData());
    }

    static PreviewResult? FromJpegFile(Stream s, PreviewSize size)
    {
        if (s.Length > MaxImageBytes) return null;
        var head = Io.Read(s, 0, (int)Math.Min(s.Length, 256 * 1024))!;
        var exifTiff = ReadJpegExif(head);
        var exif = exifTiff?.Exif ?? new ExifData();

        // For thumbnails, use the little JPEG inside the EXIF block if it has the same shape as the photo.
        if (size == PreviewSize.Thumbnail && exifTiff is not null && JpegScanner.Parse(head, 0, false) is { } main)
        {
            double aspect = (double)main.Width / main.Height;
            var thumb = Validate(exifTiff.Stream, exifTiff.JpegRanges)
                .Where(c => c.LongSide >= MinThumbnailSide && SameAspect(c.Aspect, aspect))
                .MinBy(c => c.Area);
            if (thumb is not null)
                return new PreviewResult(thumb.ReadBytes(exifTiff.Stream), thumb.Width, thumb.Height, exif);
        }

        var all = Io.Read(s, 0, (int)s.Length)!;
        return JpegScanner.Parse(all, 0, scanToEnd: false) is { } info
            ? new PreviewResult(all, info.Width, info.Height, exif)
            : null;
    }

    /// <summary>HEIF, PNG, WebP and TIFF images are handed to Windows' own decoders whole.</summary>
    static PreviewResult? FromImageFile(Stream s, Container kind)
    {
        if (s.Length > MaxImageBytes) return null;
        var exif = ReadImageExif(s, kind) ?? new ExifData();
        return new PreviewResult(Io.Read(s, 0, (int)s.Length)!, 0, 0, exif);
    }

    static ExifData? ReadImageExif(Stream s, Container kind)
    {
        switch (kind)
        {
            case Container.Tiff:
                return TiffParser.Parse(s, 0)?.Exif;
            case Container.Heif:
                return Bmff.FindHeifExif(s) is { } r ? TiffParser.Parse(s, r.Offset)?.Exif : null;
            case Container.Png:
                // Chunks: length, type, data, CRC. EXIF lives in "eXIf".
                for (long pos = 8; pos + 12 <= s.Length;)
                {
                    var h = Io.Read(s, pos, 8)!;
                    long len = BinaryPrimitives.ReadUInt32BigEndian(h);
                    if (h.AsSpan(4, 4).SequenceEqual("eXIf"u8)) return TiffParser.Parse(s, pos + 8)?.Exif;
                    if (h.AsSpan(4, 4).SequenceEqual("IEND"u8)) break;
                    pos += 12 + len;
                }
                return null;
            case Container.Webp:
                // RIFF chunks: fourcc, little-endian size, data (padded to even). EXIF lives in "EXIF".
                for (long pos = 12; pos + 8 <= s.Length;)
                {
                    var h = Io.Read(s, pos, 8)!;
                    long len = BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(4));
                    if (h.AsSpan(0, 4).SequenceEqual("EXIF"u8))
                    {
                        bool prefixed = Io.Read(s, pos + 8, 6) is { } p && p.AsSpan().SequenceEqual("Exif\0\0"u8);
                        return TiffParser.Parse(s, pos + 8 + (prefixed ? 6 : 0))?.Exif;
                    }
                    pos += 8 + len + (len & 1);
                }
                return null;
            default:
                return null;
        }
    }

    static PreviewResult? FromScan(Stream s, PreviewSize size, ExifData exif)
    {
        var best = Choose(ScanForJpegs(s, 0, s.Length), size);
        return best is null ? null : new PreviewResult(best.ReadBytes(s), best.Width, best.Height, exif);
    }

    static TiffParser? ReadJpegExif(byte[] jpeg) =>
        JpegScanner.FindExifSegment(jpeg) is var (offset, length)
            ? TiffParser.Parse(new MemoryStream(jpeg, offset, length, writable: false), 0)
            : null;

    /// <summary>Keeps only the ranges that really start with a viewable JPEG, and measures them.</summary>
    static List<Candidate> Validate(Stream s, IEnumerable<ByteRange> ranges)
    {
        var list = new List<Candidate>();
        foreach (var r in ranges.Distinct())
        {
            if (r.Length < 4 || r.Length > int.MaxValue || r.Offset < 0 || r.Offset >= s.Length) continue;
            // Some cameras write a length that runs a few bytes past the end of the file.
            long length = Math.Min(r.Length, s.Length - r.Offset);
            var head = Io.Read(s, r.Offset, (int)Math.Min(length, HeaderProbeBytes));
            if (head is not null && JpegScanner.Parse(head, 0, scanToEnd: false) is { } info)
                list.Add(new Candidate(r.Offset, (int)length, info.Width, info.Height));
        }
        return list;
    }

    /// <summary>Brute-force search for complete JPEGs in a region of the file.</summary>
    static List<Candidate> ScanForJpegs(Stream s, long start, long end)
    {
        var list = new List<Candidate>();
        long length = Math.Min(end - start, MaxScanBytes);
        if (length < 4) return list;
        var data = Io.Read(s, start, (int)length);
        if (data is null) return list;

        ReadOnlySpan<byte> soi = [0xFF, 0xD8, 0xFF];
        int pos = 0;
        while (pos < data.Length - 3)
        {
            int i = data.AsSpan(pos).IndexOf(soi);
            if (i < 0) break;
            int at = pos + i;
            if (JpegScanner.Parse(data, at, scanToEnd: true) is { } info)
            {
                list.Add(new Candidate(start + at, info.Length, info.Width, info.Height, data, at));
                pos = at + info.Length; // skip nested thumbnails inside this JPEG
            }
            else
            {
                pos = at + 2;
            }
        }
        return list;
    }

    static Candidate? Choose(List<Candidate> candidates, PreviewSize size)
    {
        if (candidates.Count == 0) return null;
        var largest = candidates.MaxBy(c => c.Area)!;
        if (size == PreviewSize.Largest) return largest;

        // Tiny EXIF thumbnails are often 4:3 with black bars on a 3:2 photo — skip those.
        return candidates
            .Where(c => c.LongSide >= MinThumbnailSide && SameAspect(c.Aspect, largest.Aspect))
            .MinBy(c => c.Area) ?? largest;
    }

    static bool SameAspect(double a, double b) => Math.Abs(a - b) / b < AspectTolerance;
}
