namespace SnapCull.Core.Tests;

public class JpegScannerTests
{
    [Fact]
    public void Measures_a_baseline_jpeg_and_finds_its_end()
    {
        var jpeg = Fake.Jpeg(6000, 4000);
        var info = JpegScanner.Parse(jpeg, 0, scanToEnd: true);

        Assert.NotNull(info);
        Assert.Equal(6000, info.Value.Width);
        Assert.Equal(4000, info.Value.Height);
        Assert.Equal(jpeg.Length, info.Value.Length);
    }

    [Fact]
    public void Follows_progressive_jpegs_across_multiple_scans()
    {
        var jpeg = Fake.Jpeg(1620, 1080, frameMarker: 0xC2);
        var info = JpegScanner.Parse(jpeg, 0, scanToEnd: true);

        Assert.Equal(jpeg.Length, info?.Length);
    }

    [Fact]
    public void Finds_a_jpeg_in_the_middle_of_other_data()
    {
        var jpeg = Fake.Jpeg(800, 600);
        byte[] data = [.. Fake.Bytes(100, 1), .. jpeg, .. Fake.Bytes(100, 2)];

        var info = JpegScanner.Parse(data, 100, scanToEnd: true);

        Assert.Equal(new JpegInfo(100, jpeg.Length, 800, 600), info);
    }

    [Fact]
    public void Rejects_lossless_jpeg_which_is_raw_sensor_data()
    {
        Assert.Null(JpegScanner.Parse(Fake.Jpeg(6000, 4000, frameMarker: 0xC3), 0, scanToEnd: false));
    }

    [Fact]
    public void Rejects_truncated_and_non_jpeg_data()
    {
        var jpeg = Fake.Jpeg(100, 100);
        Assert.Null(JpegScanner.Parse(jpeg.AsSpan(0, jpeg.Length - 10), 0, scanToEnd: true));
        Assert.Null(JpegScanner.Parse(Fake.Bytes(500, 3), 0, scanToEnd: true));
    }

    [Fact]
    public void Finds_the_exif_block()
    {
        var tiff = new TiffBuilder();
        tiff.SetFirstIfd(tiff.AddIfd([tiff.Ascii(0x010F, "Canon")]));
        var tiffBytes = tiff.ToArray();
        var jpeg = Fake.Jpeg(100, 100, exifTiff: tiffBytes);

        var segment = JpegScanner.FindExifSegment(jpeg);

        Assert.NotNull(segment);
        Assert.Equal(tiffBytes, jpeg.AsSpan(segment.Value.Offset, segment.Value.Length).ToArray());
    }
}
