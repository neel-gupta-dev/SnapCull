using System.Buffers.Binary;

namespace SnapCull.Core.Tests;

public class ExifWriterTests
{
    static ExifData Sample() => new()
    {
        Make = "Canon", Model = "Canon EOS R6", LensModel = "RF24-105mm F4 L IS USM",
        DateTaken = new DateTime(2026, 11, 14, 17, 30, 12), ExposureTime = 1.0 / 250, FNumber = 2.8, Iso = 400, FocalLength = 50,
        Orientation = 6, GpsLatitude = -33.8568, GpsLongitude = 151.2153, GpsAltitude = 12.5,
    };

    [Fact]
    public void Written_exif_reads_back_with_the_same_values()
    {
        var tiff = ExifWriter.Build(Sample(), includeGps: true);
        var exif = TiffParser.Parse(new MemoryStream(tiff), 0)!.Exif;

        Assert.Equal("Canon EOS R6", exif.CameraText);
        Assert.Equal("RF24-105mm F4 L IS USM", exif.LensModel);
        Assert.Equal(new DateTime(2026, 11, 14, 17, 30, 12), exif.DateTaken);
        Assert.Equal(1.0 / 250, exif.ExposureTime!.Value, 6);
        Assert.Equal(2.8, exif.FNumber!.Value, 3);
        Assert.Equal(400, exif.Iso);
        Assert.Equal(50, exif.FocalLength!.Value, 3);
        Assert.Equal(1, exif.Orientation); // pixels are already upright after converting
        Assert.Equal(-33.8568, exif.GpsLatitude!.Value, 4);
        Assert.Equal(151.2153, exif.GpsLongitude!.Value, 4);
        Assert.Equal(12.5, exif.GpsAltitude!.Value, 2);
    }

    [Fact]
    public void Gps_is_left_out_when_asked()
    {
        var exif = TiffParser.Parse(new MemoryStream(ExifWriter.Build(Sample(), includeGps: false)), 0)!.Exif;
        Assert.False(exif.HasGps);
        Assert.Equal("Canon EOS R6", exif.CameraText);
    }

    [Fact]
    public void Long_exposures_are_written_like_cameras_write_them()
    {
        var exif = TiffParser.Parse(new MemoryStream(ExifWriter.Build(new ExifData { ExposureTime = 2.5 }, false)), 0)!.Exif;
        Assert.Equal("2.5 s", exif.ShutterText);
    }

    [Fact]
    public void Gps_is_read_from_camera_files()
    {
        var t = new TiffBuilder();
        byte[] Rationals(params (uint N, uint D)[] values) => values.SelectMany(v => new[] { v.N, v.D }).SelectMany(x => BitConverter.GetBytes(x)).ToArray();
        uint gps = t.AddIfd(
        [
            t.Ascii(1, "N"), new TiffEntry(2, 5, 3, Rationals((51, 1), (30, 1), (2630, 100))),
            t.Ascii(3, "W"), new TiffEntry(4, 5, 3, Rationals((0, 1), (7, 1), (3984, 100))),
        ]);
        t.SetFirstIfd(t.AddIfd([t.Ascii(0x010F, "SONY"), t.Long(0x8825, gps)]));

        var exif = TiffParser.Parse(new MemoryStream(t.ToArray()), 0)!.Exif;

        Assert.Equal(51.5073, exif.GpsLatitude!.Value, 4);
        Assert.Equal(-0.1277, exif.GpsLongitude!.Value, 4);
    }
}

public class MetadataEmbedderTests
{
    static readonly byte[] Tiff = ExifWriter.Build(new ExifData { Make = "NIKON CORPORATION", Model = "NIKON Z f", Iso = 250 }, false);

    [Fact]
    public void Jpeg_gets_an_exif_segment_that_reads_back()
    {
        var jpeg = MetadataEmbedder.EmbedInJpeg(Fake.Jpeg(1200, 800), Tiff);

        Assert.Equal("NIKON Z f", PreviewExtractor.ReadExif(new MemoryStream(jpeg))?.CameraText);
        Assert.Equal((1200, 800), (JpegScanner.Parse(jpeg, 0, true)!.Value.Width, 800));
    }

    [Fact]
    public void Png_gets_an_eXIf_chunk_with_a_valid_crc()
    {
        byte[] ihdr = [0, 0, 0, 13, .. "IHDR"u8, .. new byte[13], 0, 0, 0, 0];
        byte[] iend = [0, 0, 0, 0, .. "IEND"u8, 0xAE, 0x42, 0x60, 0x82];
        byte[] png = [0x89, .. "PNG\r\n\x1a\n"u8, .. ihdr, .. iend];

        var withExif = MetadataEmbedder.EmbedInPng(png, Tiff);

        Assert.Equal("NIKON Z f", PreviewExtractor.ReadExif(new MemoryStream(withExif))?.CameraText);
        // Check the CRC of the new chunk the same way image viewers do.
        int start = 8 + 25;
        int length = BinaryPrimitives.ReadInt32BigEndian(withExif.AsSpan(start));
        uint stored = BinaryPrimitives.ReadUInt32BigEndian(withExif.AsSpan(start + 8 + length));
        Assert.Equal(Crc32(withExif.AsSpan(start + 4, 4 + length)), stored);
    }

    [Fact]
    public void Simple_webp_is_upgraded_to_the_extended_layout_with_exif()
    {
        byte[] vp8 = [.. "VP8 "u8, 10, 0, 0, 0, .. new byte[10]];
        var size = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(size, (uint)(4 + vp8.Length));
        byte[] webp = [.. "RIFF"u8, .. size, .. "WEBP"u8, .. vp8];

        var result = MetadataEmbedder.EmbedInWebp(webp, Tiff, 1920, 1280);

        Assert.True(result.AsSpan(12, 4).SequenceEqual("VP8X"u8));
        Assert.Equal(0x08, result[20] & 0x08); // EXIF flag
        Assert.Equal(1919, result[24] | result[25] << 8 | result[26] << 16);
        Assert.Equal(result.Length - 8, (int)BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(4)));
        Assert.Equal("NIKON Z f", PreviewExtractor.ReadExif(new MemoryStream(result))?.CameraText);
    }

    static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint c = 0xFFFFFFFF;
        foreach (var b in data)
        {
            c ^= b;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
        }
        return c ^ 0xFFFFFFFF;
    }
}

public class ConvertPlanTests
{
    static PhotoFiles Photo(string name) => PhotoFiles.FromPath(Path.Combine("C:\\Photos", name));

    [Theory]
    [InlineData("{name}", "IMG_0042.jpg")]
    [InlineData("{date}_{name}", "2026-11-14_IMG_0042.jpg")]
    [InlineData("Wedding_{seq}", "Wedding_0007.jpg")]
    [InlineData("{date} {time} {camera}", "2026-11-14 173012 Canon EOS R6.jpg")]
    [InlineData("bad:name?<{name}>", "bad_name__IMG_0042_.jpg")]
    [InlineData("", "IMG_0042.jpg")]
    public void Names_follow_the_pattern(string pattern, string expected)
    {
        var exif = new ExifData { Make = "Canon", Model = "Canon EOS R6", DateTaken = new DateTime(2026, 11, 14, 17, 30, 12) };
        Assert.Equal(expected, ConvertPlan.FileName(pattern, Photo("IMG_0042.CR3"), exif, 7, ConvertFormat.Jpeg));
    }

    [Fact]
    public void Photos_without_a_date_still_get_a_name()
    {
        Assert.Equal("nodate_IMG_1.webp", ConvertPlan.FileName("{date}_{name}", Photo("IMG_1.NEF"), null, 1, ConvertFormat.WebP));
    }

    [Fact]
    public void Paths_never_clash_with_existing_or_planned_files()
    {
        using var dir = new TempDir();
        dir.File("IMG_1.jpg");
        var planned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var first = ConvertPlan.UniquePath(dir.Path, "IMG_1.jpg", planned);
        var second = ConvertPlan.UniquePath(dir.Path, "IMG_1.jpg", planned);

        Assert.Equal(Path.Combine(dir.Path, "IMG_1 (1).jpg"), first);
        Assert.Equal(Path.Combine(dir.Path, "IMG_1 (2).jpg"), second);
    }

    [Theory]
    [InlineData(ConvertResize.Original, 0, 6000, 6000)]
    [InlineData(ConvertResize.LongEdge, 2048, 6000, 2048)]
    [InlineData(ConvertResize.LongEdge, 8000, 6000, 6000)] // never enlarges
    [InlineData(ConvertResize.Percent, 50, 6000, 3000)]
    [InlineData(ConvertResize.Percent, 100, 1616, 1616)]
    public void Output_size_is_computed_from_the_long_side(ConvertResize mode, int value, int source, int expected)
    {
        Assert.Equal(expected, ConvertPlan.TargetLongSide(source, new ConvertOptions { Resize = mode, ResizeValue = value }));
    }
}
