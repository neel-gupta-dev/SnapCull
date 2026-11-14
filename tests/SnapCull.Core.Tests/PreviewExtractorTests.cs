namespace SnapCull.Core.Tests;

public class PreviewExtractorTests
{
    /// <summary>
    /// Shaped like a CR2/NEF: IFD0 holds lossless raw data in a strip, a SubIFD holds the full-size preview,
    /// IFD1 holds a letterboxed 160×120 thumbnail, and another IFD a small correctly-shaped preview.
    /// </summary>
    static byte[] BuildTiffRaw(bool bigEndian = false)
    {
        var t = new TiffBuilder(bigEndian);
        var full = Fake.Jpeg(6000, 4000);
        var small = Fake.Jpeg(240, 160);
        var letterboxed = Fake.Jpeg(160, 120);
        var raw = Fake.Jpeg(6000, 4000, frameMarker: 0xC3, dataBytes: 5000);

        uint fullAt = t.AddBlob(full), smallAt = t.AddBlob(small), boxAt = t.AddBlob(letterboxed), rawAt = t.AddBlob(raw);

        uint exif = t.AddIfd(
        [
            t.Rational(0x829A, 1, 250), t.Rational(0x829D, 28, 10), t.Short(0x8827, 400),
            t.Ascii(0x9003, "2026:11:02 17:43:12"), t.Rational(0x920A, 50, 1), t.Ascii(0xA434, "RF24-105mm F4 L IS USM"),
        ]);
        uint sub1 = t.AddIfd([t.Long(0x0201, fullAt), t.Long(0x0202, (uint)full.Length)]);
        uint sub2 = t.AddIfd([t.Short(0x0103, 6), t.Long(0x0111, smallAt), t.Long(0x0117, (uint)small.Length)]);
        uint ifd1 = t.AddIfd([t.Long(0x0201, boxAt), t.Long(0x0202, (uint)letterboxed.Length)]);
        uint ifd0 = t.AddIfd(
        [
            t.Ascii(0x010F, "Canon"), t.Ascii(0x0110, "Canon EOS R6"), t.Short(0x0112, 6),
            t.Short(0x0103, 6), t.Long(0x0111, rawAt), t.Long(0x0117, (uint)raw.Length),
            new TiffEntry(0x014A, 4, 2, [.. t.Long(0, sub1).Value, .. t.Long(0, sub2).Value]),
            t.Long(0x8769, exif),
        ], next: ifd1);
        t.SetFirstIfd(ifd0);
        return t.ToArray();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Tiff_raw_largest_preview_skips_raw_data_and_reads_exif(bool bigEndian)
    {
        var result = PreviewExtractor.Extract(new MemoryStream(BuildTiffRaw(bigEndian)), PreviewSize.Largest);

        Assert.NotNull(result);
        Assert.Equal((6000, 4000), (result.Width, result.Height));
        Assert.Equal(0xFF, result.Image[0]);
        Assert.Equal(0xD9, result.Image[^1]);

        var exif = result.Exif;
        Assert.Equal("Canon EOS R6", exif.CameraText);
        Assert.Equal("RF24-105mm F4 L IS USM", exif.LensModel);
        Assert.Equal(6, exif.Orientation);
        Assert.Equal(400, exif.Iso);
        Assert.Equal(2.8, exif.FNumber!.Value, 3);
        Assert.Equal(1.0 / 250, exif.ExposureTime!.Value, 6);
        Assert.Equal(new DateTime(2026, 11, 2, 17, 43, 12), exif.DateTaken);
        Assert.Equal("50 mm  ·  f/2.8  ·  1/250 s  ·  ISO 400", exif.SettingsText);
    }

    [Fact]
    public void Tiff_raw_thumbnail_is_the_smallest_preview_with_the_right_shape()
    {
        var result = PreviewExtractor.Extract(new MemoryStream(BuildTiffRaw()), PreviewSize.Thumbnail);

        Assert.Equal((240, 160), (result!.Width, result.Height)); // not the letterboxed 160×120
    }

    [Fact]
    public void Cr3_uses_the_full_size_jpeg_from_track_one()
    {
        var full = Fake.Jpeg(6000, 4000);
        var prvw = Fake.Jpeg(1620, 1080);
        var thmb = Fake.Jpeg(160, 120);

        var cmt1 = new TiffBuilder();
        cmt1.SetFirstIfd(cmt1.AddIfd([cmt1.Ascii(0x010F, "Canon"), cmt1.Ascii(0x0110, "Canon EOS R5"), cmt1.Short(0x0112, 8)]));
        var cmt2 = new TiffBuilder();
        cmt2.SetFirstIfd(cmt2.AddIfd([cmt2.Short(0x8827, 800), cmt2.Rational(0x829D, 4, 1)]));

        byte[] Build(ulong mdatOffset)
        {
            var ftyp = Fake.Box("ftyp", "crx "u8.ToArray(), Fake.BE32(1), "crx isom"u8.ToArray());
            var canon = Fake.Box("uuid", Convert.FromHexString("85C0B687820F11E08111F4CE462B6A48"),
                Fake.Box("CMT1", cmt1.ToArray()), Fake.Box("CMT2", cmt2.ToArray()), Fake.Box("THMB", new byte[12], thmb));
            var stbl = Fake.Box("stbl",
                Fake.Box("stsz", Fake.BE32(0), Fake.BE32(0), Fake.BE32(1), Fake.BE32((uint)full.Length)),
                Fake.Box("co64", Fake.BE32(0), Fake.BE32(1), Fake.BE64(mdatOffset)));
            var moov = Fake.Box("moov", canon, Fake.Box("trak", Fake.Box("mdia", Fake.Box("minf", stbl))));
            var preview = Fake.Box("uuid", Convert.FromHexString("EAF42B5E1C984B88B9FBB7DC406E4D16"), new byte[8],
                Fake.Box("PRVW", new byte[16], prvw));
            return [.. ftyp, .. moov, .. preview, .. Fake.Box("mdat", full)];
        }

        var probe = Build(0);
        var file = Build((ulong)(probe.Length - full.Length)); // the JPEG starts right after the mdat header

        var largest = PreviewExtractor.Extract(new MemoryStream(file), PreviewSize.Largest);
        Assert.Equal((6000, 4000), (largest!.Width, largest.Height));
        Assert.Equal(full, largest.Image);
        Assert.Equal("Canon EOS R5", largest.Exif.CameraText);
        Assert.Equal(8, largest.Exif.Orientation);
        Assert.Equal(800, largest.Exif.Iso);

        var thumb = PreviewExtractor.Extract(new MemoryStream(file), PreviewSize.Thumbnail);
        Assert.Equal((1620, 1080), (thumb!.Width, thumb.Height));
    }

    [Fact]
    public void Raf_uses_the_jpeg_named_in_the_header_and_its_exif()
    {
        var exif = new TiffBuilder();
        exif.SetFirstIfd(exif.AddIfd([exif.Ascii(0x010F, "FUJIFILM"), exif.Ascii(0x0110, "X-T5"), exif.Short(0x0112, 6)]));
        var jpeg = Fake.Jpeg(1920, 1280, exifTiff: exif.ToArray());

        var header = new byte[148];
        "FUJIFILMCCD-RAW 0201FF383501"u8.CopyTo(header);
        Fake.BE32((uint)header.Length).CopyTo(header, 84);
        Fake.BE32((uint)jpeg.Length).CopyTo(header, 88);
        byte[] file = [.. header, .. jpeg, .. Fake.Bytes(4000, 5)];

        var result = PreviewExtractor.Extract(new MemoryStream(file));

        Assert.Equal((1920, 1280), (result!.Width, result.Height));
        Assert.Equal("FUJIFILM X-T5", result.Exif.CameraText);
        Assert.Equal(6, result.Exif.Orientation);
    }

    [Fact]
    public void Plain_jpeg_uses_the_exif_thumbnail_for_the_filmstrip()
    {
        var thumb = Fake.Jpeg(160, 120);
        var tiff = new TiffBuilder();
        uint thumbAt = tiff.AddBlob(thumb);
        uint ifd1 = tiff.AddIfd([tiff.Long(0x0201, thumbAt), tiff.Long(0x0202, (uint)thumb.Length)]);
        tiff.SetFirstIfd(tiff.AddIfd([tiff.Ascii(0x010F, "NIKON CORPORATION"), tiff.Ascii(0x0110, "NIKON Z 6_2"), tiff.Short(0x0112, 3)], next: ifd1));
        var file = Fake.Jpeg(4000, 3000, exifTiff: tiff.ToArray());

        var largest = PreviewExtractor.Extract(new MemoryStream(file), PreviewSize.Largest);
        Assert.Equal((4000, 3000), (largest!.Width, largest.Height));
        Assert.Equal(file, largest.Image);
        Assert.Equal(3, largest.Exif.Orientation);
        Assert.Equal("NIKON Z 6_2", largest.Exif.CameraText);

        var small = PreviewExtractor.Extract(new MemoryStream(file), PreviewSize.Thumbnail);
        Assert.Equal((160, 120), (small!.Width, small.Height));
    }

    [Fact]
    public void Unknown_formats_fall_back_to_scanning_for_the_biggest_jpeg()
    {
        byte[] file = [.. "XYZW-unknown-raw"u8, .. Fake.Bytes(3000, 7), .. Fake.Jpeg(160, 107),
                       .. Fake.Bytes(3000, 8), .. Fake.Jpeg(2048, 1365), .. Fake.Bytes(3000, 9)];

        var result = PreviewExtractor.Extract(new MemoryStream(file));

        Assert.Equal((2048, 1365), (result!.Width, result.Height));
    }

    [Fact]
    public void Returns_null_when_there_is_no_preview()
    {
        Assert.Null(PreviewExtractor.Extract(new MemoryStream([.. "XYZW-unknown-raw"u8, .. Fake.Bytes(5000, 10)])));
    }

    /// <summary>
    /// Runs against real camera files when the SNAPCULL_SAMPLES environment variable points at a folder of them.
    /// Free samples: https://raw.pixls.us
    /// </summary>
    [Fact]
    public void Real_sample_files_are_read_without_errors()
    {
        var folder = Environment.GetEnvironmentVariable("SNAPCULL_SAMPLES");
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;

        // Some formats have no embedded preview at all (phone DNGs, cinema DNGs, Kodak DCR…); the app renders those
        // with Windows' RAW codec instead, so here we only insist that nothing throws and the camera is identified.
        // tools/SnapCull.Probe shows what the app will actually display.
        var failures = new List<string>();
        foreach (var path in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Where(PhotoFormats.IsSupported))
        {
            try
            {
                PreviewExtractor.Extract(path, PreviewSize.Largest);
                PreviewExtractor.Extract(path, PreviewSize.Thumbnail);
                var exif = PreviewExtractor.ReadExif(path);
                if (exif?.CameraText is null && !path.EndsWith(".x3f", StringComparison.OrdinalIgnoreCase) && !path.EndsWith(".mos", StringComparison.OrdinalIgnoreCase))
                    failures.Add($"{Path.GetFileName(path)}: camera not identified");
            }
            catch (Exception ex)
            {
                failures.Add($"{Path.GetFileName(path)}: {ex.GetType().Name}: {ex.Message}");
            }
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }
}
