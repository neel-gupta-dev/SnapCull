namespace SnapCull.Core.Tests;

public class FolderScannerTests
{
    [Fact]
    public void Pairs_raw_and_jpeg_files_with_the_same_name()
    {
        using var dir = new TempDir();
        dir.File("IMG_0001.CR3");
        dir.File("IMG_0001.JPG");
        dir.File("IMG_0002.nef");
        dir.File("IMG_0001.xmp");
        dir.File("notes.txt");
        dir.File("._IMG_0003.ARW");

        var photos = FolderScanner.Scan(dir.Path);

        Assert.Equal(new[] { "IMG_0001", "IMG_0002" }, photos.Select(p => p.BaseName));
        Assert.True(photos[0].IsPair);
        Assert.Equal(Path.Combine(dir.Path, "IMG_0001.xmp"), photos[0].SidecarPath);
        Assert.Equal(Path.Combine(dir.Path, "IMG_0002.nef"), photos[1].PrimaryPath);
    }
}

public class ExportServiceTests
{
    [Fact]
    public void Copies_the_raw_jpeg_and_sidecar_together()
    {
        using var source = new TempDir();
        using var dest = new TempDir();
        source.File("IMG_1.CR3");
        source.File("IMG_1.JPG");
        source.File("IMG_1.xmp");
        var photo = FolderScanner.Scan(source.Path).Single();

        var result = ExportService.Run(new ExportRequest([photo], dest.Path, ExportMode.Copy));

        Assert.Equal(1, result.Exported);
        Assert.True(File.Exists(Path.Combine(dest.Path, "IMG_1.CR3")));
        Assert.True(File.Exists(Path.Combine(dest.Path, "IMG_1.JPG")));
        Assert.True(File.Exists(Path.Combine(dest.Path, "IMG_1.xmp")));
        Assert.True(File.Exists(photo.RawPath)); // originals stay
    }

    [Fact]
    public void Never_overwrites_a_different_file_with_the_same_name()
    {
        using var source = new TempDir();
        using var dest = new TempDir();
        source.File("IMG_1.CR3", "new photo"u8.ToArray());
        source.File("IMG_1.xmp");
        dest.File("IMG_1.CR3", "an older, different photo"u8.ToArray());
        var photo = FolderScanner.Scan(source.Path).Single();

        var result = ExportService.Run(new ExportRequest([photo], dest.Path, ExportMode.Copy));

        Assert.Equal(1, result.Exported);
        Assert.Equal("an older, different photo", File.ReadAllText(Path.Combine(dest.Path, "IMG_1.CR3")));
        Assert.Equal("new photo", File.ReadAllText(Path.Combine(dest.Path, "IMG_1 (1).CR3")));
        Assert.True(File.Exists(Path.Combine(dest.Path, "IMG_1 (1).xmp"))); // sidecar keeps matching its photo
    }

    [Fact]
    public void Skips_photos_already_exported_but_refreshes_their_ratings()
    {
        using var source = new TempDir();
        using var dest = new TempDir();
        source.File("IMG_1.CR3", "photo"u8.ToArray());
        source.File("IMG_1.xmp", "new rating"u8.ToArray());
        dest.File("IMG_1.CR3", "photo"u8.ToArray());
        dest.File("IMG_1.xmp", "old rating"u8.ToArray());
        var photo = FolderScanner.Scan(source.Path).Single();

        var result = ExportService.Run(new ExportRequest([photo], dest.Path, ExportMode.Copy));

        Assert.Equal((0, 1), (result.Exported, result.Skipped));
        Assert.Equal("new rating", File.ReadAllText(Path.Combine(dest.Path, "IMG_1.xmp")));
        Assert.False(File.Exists(Path.Combine(dest.Path, "IMG_1 (1).CR3")));
    }

    [Fact]
    public void Move_removes_the_originals()
    {
        using var source = new TempDir();
        using var dest = new TempDir();
        source.File("IMG_1.ARW");
        source.File("IMG_1.xmp");
        var photo = FolderScanner.Scan(source.Path).Single();

        var result = ExportService.Run(new ExportRequest([photo], Path.Combine(dest.Path, "Picks"), ExportMode.Move));

        Assert.Single(result.Succeeded);
        Assert.False(File.Exists(photo.RawPath));
        Assert.False(File.Exists(photo.SidecarPath));
        Assert.True(File.Exists(Path.Combine(dest.Path, "Picks", "IMG_1.ARW")));
        Assert.True(File.Exists(Path.Combine(dest.Path, "Picks", "IMG_1.xmp")));
    }
}

public class ResolutionTests
{
    [Fact]
    public void Resolution_is_shown_the_way_the_photo_is_viewed()
    {
        Assert.Equal("6000 × 4000 px · 24.0 MP", new ExifData { PixelWidth = 6000, PixelHeight = 4000 }.ResolutionText);
        Assert.Equal("4000 × 6000 px · 24.0 MP", new ExifData { PixelWidth = 6000, PixelHeight = 4000, Orientation = 6 }.ResolutionText);
        Assert.Null(new ExifData().ResolutionText);
    }

    [Fact]
    public void Exif_pixel_size_wins_over_everything_else()
    {
        var t = new TiffBuilder();
        uint exif = t.AddIfd([t.Long(0xA002, 3360), t.Long(0xA003, 2240)]);
        t.SetFirstIfd(t.AddIfd([t.Long(0x0100, 6720), t.Long(0x0101, 4480), t.Long(0x8769, exif)]));
        var parsed = TiffParser.Parse(new MemoryStream(t.ToArray()), 0)!;
        parsed.UseLargestImageAsSize([(6720, 4480)]);

        Assert.Equal((3360, 2240), (parsed.Exif.PixelWidth!.Value, parsed.Exif.PixelHeight!.Value));
    }

    [Theory]
    [InlineData(7360, 4912, 7360, 4912)] // full-size preview nearly as big as the sensor data: the preview is the photo size
    [InlineData(1616, 1080, 6048, 4024)] // small preview: the sensor data size is used
    public void Raw_size_comes_from_the_sensor_data_or_a_full_size_preview(int previewW, int previewH, int expectedW, int expectedH)
    {
        var t = new TiffBuilder();
        uint raw = t.AddIfd([t.Long(0x0100, previewW == 7360 ? 7392u : 6048u), t.Long(0x0101, previewW == 7360 ? 4950u : 4024u)]);
        t.SetFirstIfd(t.AddIfd([t.Ascii(0x010F, "PENTAX"), new TiffEntry(0x014A, 4, 1, BitConverter.GetBytes(raw))]));
        var parsed = TiffParser.Parse(new MemoryStream(t.ToArray()), 0)!;
        parsed.UseLargestImageAsSize([(previewW, previewH)]);

        Assert.Equal((expectedW, expectedH), (parsed.Exif.PixelWidth!.Value, parsed.Exif.PixelHeight!.Value));
    }

    [Fact]
    public void Plain_jpeg_size_comes_from_its_frame_header()
    {
        var jpeg = Fake.Jpeg(4032, 3024);
        Assert.Equal("4032 × 3024 px · 12.2 MP", PreviewExtractor.Extract(new MemoryStream(jpeg))!.Exif.ResolutionText);
    }
}

public class ExifDataTests
{
    [Theory]
    [InlineData(1.0 / 250, "1/250 s")]
    [InlineData(1.0 / 8000, "1/8000 s")]
    [InlineData(0.5, "0.5 s")]
    [InlineData(2.0, "2 s")]
    [InlineData(30.0, "30 s")]
    public void Formats_shutter_speeds_like_a_camera(double seconds, string expected)
    {
        Assert.Equal(expected, ExifData.FormatShutter(seconds));
    }

    [Theory]
    [InlineData("Canon", "Canon EOS R6", "Canon EOS R6")]
    [InlineData("NIKON CORPORATION", "NIKON Z 6_2", "NIKON Z 6_2")]
    [InlineData("SONY", "ILCE-7M4", "SONY ILCE-7M4")]
    [InlineData(null, "X-T5", "X-T5")]
    [InlineData("OLYMPUS CORPORATION", "E-M10MarkIV", "OLYMPUS E-M10MarkIV")]
    [InlineData("OLYMPUS IMAGING CORP.", "E-M5", "OLYMPUS E-M5")]
    [InlineData("NIKON CORPORATION", "COOLPIX P1000", "NIKON COOLPIX P1000")]
    [InlineData("LEICA CAMERA AG", "LEICA M10", "LEICA M10")]
    [InlineData("SAMSUNG TECHWIN", "GX-1L", "SAMSUNG GX-1L")]
    [InlineData("OM Digital Solutions", "OM-1", "OM-1")]
    [InlineData("OM Digital Solutions", "TG-7", "OM System TG-7")]
    [InlineData("Xiaomi", "MI 8", "Xiaomi MI 8")]
    [InlineData("RICOH IMAGING COMPANY, LTD.", "PENTAX K-1", "PENTAX K-1")]
    [InlineData("EASTMAN KODAK COMPANY", "KODAK P880 ZOOM DIGITAL CAMERA", "KODAK P880 ZOOM DIGITAL CAMERA")]
    public void Does_not_repeat_the_brand(string? make, string model, string expected)
    {
        Assert.Equal(expected, new ExifData { Make = make, Model = model }.CameraText);
    }
}
