using System.Buffers.Binary;

namespace SnapCull.Core.Tests;

public class FormatTests
{
    static byte[] ExifTiff(string make, string model, ushort orientation, string date = "2026:01:02 03:04:05")
    {
        var t = new TiffBuilder();
        uint exif = t.AddIfd([t.Ascii(0x9003, date), t.Short(0x8827, 200)]);
        t.SetFirstIfd(t.AddIfd([t.Ascii(0x010F, make), t.Ascii(0x0110, model), t.Short(0x0112, orientation), t.Long(0x8769, exif)]));
        return t.ToArray();
    }

    static byte[] FullBox(string type, byte version, params byte[][] content) =>
        Fake.Box(type, [version, 0, 0, 0, .. content.SelectMany(c => c)]);

    static byte[] U16(ushort v) { var b = new byte[2]; BinaryPrimitives.WriteUInt16BigEndian(b, v); return b; }

    /// <summary>A minimal HEIC: ftyp, meta (iinf with an "Exif" item, iloc pointing at it), mdat.</summary>
    static byte[] Heic(byte[] tiff)
    {
        byte[] exifItem = [0, 0, 0, 6, .. "Exif\0\0"u8, .. tiff];
        byte[] Build(uint exifOffset)
        {
            var ftyp = Fake.Box("ftyp", "heic"u8.ToArray(), Fake.BE32(0), "mif1heic"u8.ToArray());
            var iinf = FullBox("iinf", 0, U16(2),
                FullBox("infe", 2, U16(1), U16(0), "hvc1"u8.ToArray(), [0]),
                FullBox("infe", 2, U16(2), U16(0), "Exif"u8.ToArray(), [0]));
            // iloc v0: offset_size=4, length_size=4, base_offset_size=0; two items with one extent each.
            var iloc = FullBox("iloc", 0, [0x44, 0x00], U16(2),
                U16(1), U16(0), U16(1), Fake.BE32(0), Fake.BE32(10),
                U16(2), U16(0), U16(1), Fake.BE32(exifOffset), Fake.BE32((uint)exifItem.Length));
            var meta = FullBox("meta", 0, iinf, iloc);
            return [.. ftyp, .. meta, .. Fake.Box("mdat", new byte[10], exifItem)];
        }
        var probe = Build(0);
        return Build((uint)(probe.Length - exifItem.Length));
    }

    [Fact]
    public void Heic_exif_is_found_through_the_item_table()
    {
        var file = Heic(ExifTiff("Apple", "iPhone 15 Pro", 6));

        var exif = PreviewExtractor.ReadExif(new MemoryStream(file));
        Assert.Equal("Apple iPhone 15 Pro", exif?.CameraText);
        Assert.Equal(6, exif!.Orientation);
        Assert.Equal(200, exif.Iso);

        // HEIF pictures are handed to Windows' codec whole, with the EXIF attached.
        var preview = PreviewExtractor.Extract(new MemoryStream(file));
        Assert.Equal(file, preview!.Image);
        Assert.Equal(6, preview.Exif.Orientation);
    }

    [Fact]
    public void Png_exif_comes_from_the_eXIf_chunk()
    {
        var tiff = ExifTiff("Google", "Pixel 8", 1);
        byte[] Chunk(string type, byte[] data) => [.. Fake.BE32((uint)data.Length), .. System.Text.Encoding.ASCII.GetBytes(type), .. data, 0, 0, 0, 0];
        byte[] png = [0x89, .. "PNG\r\n\x1a\n"u8, .. Chunk("IHDR", new byte[13]), .. Chunk("eXIf", tiff), .. Chunk("IDAT", new byte[20]), .. Chunk("IEND", [])];

        Assert.Equal("Google Pixel 8", PreviewExtractor.ReadExif(new MemoryStream(png))?.CameraText);
    }

    [Fact]
    public void Webp_exif_comes_from_the_EXIF_chunk_with_or_without_prefix()
    {
        foreach (var prefix in new[] { Array.Empty<byte>(), "Exif\0\0"u8.ToArray() })
        {
            byte[] data = [.. prefix, .. ExifTiff("SONY", "ILCE-7M4", 8)];
            var lenLe = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(lenLe, (uint)data.Length);
            byte[] chunk = [.. "EXIF"u8, .. lenLe, .. data, .. (data.Length % 2 == 1 ? new byte[] { 0 } : [])];
            byte[] vp8 = [.. "VP8 "u8, 4, 0, 0, 0, 1, 2, 3, 4];
            byte[] body = [.. "WEBP"u8, .. vp8, .. chunk];
            var size = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(size, (uint)body.Length);
            byte[] webp = [.. "RIFF"u8, .. size, .. body];

            var exif = PreviewExtractor.ReadExif(new MemoryStream(webp));
            Assert.Equal("SONY ILCE-7M4", exif?.CameraText);
            Assert.Equal(8, exif!.Orientation);
        }
    }

    [Fact]
    public void Plain_tiff_images_are_decoded_whole_not_treated_as_raw()
    {
        var tiff = ExifTiff("EPSON", "Perfection V600", 1);
        var preview = PreviewExtractor.Extract(new MemoryStream(tiff), plainImage: true);

        Assert.Equal(tiff, preview!.Image);
        Assert.Equal((0, 0), (preview.Width, preview.Height)); // size comes from the Windows codec
    }

    [Fact]
    public void Minolta_mrw_reads_exif_from_the_TTW_block_and_finds_the_preview()
    {
        var tiff = ExifTiff("KONICA MINOLTA", "DYNAX 7D", 1);
        byte[] Block(string name, byte[] data) => [.. System.Text.Encoding.ASCII.GetBytes(name), .. Fake.BE32((uint)data.Length), .. data];
        byte[] blocks = [.. Block("\0PRD", new byte[24]), .. Block("\0TTW", tiff), .. Block("\0WBG", new byte[12])];
        byte[] mrw = [.. "\0MRM"u8, .. Fake.BE32((uint)blocks.Length), .. blocks, .. Fake.Bytes(2000, 1), .. Fake.Jpeg(1600, 1200), .. Fake.Bytes(2000, 2)];

        var preview = PreviewExtractor.Extract(new MemoryStream(mrw));
        Assert.Equal((1600, 1200), (preview!.Width, preview.Height));
        Assert.Equal("KONICA MINOLTA DYNAX 7D", preview.Exif.CameraText);
    }

    [Fact]
    public void Canon_crw_camera_info_comes_from_the_ciff_heap()
    {
        static byte[] Le16(int v) => [(byte)v, (byte)(v >> 8)];
        static byte[] Le32(long v) => [(byte)v, (byte)(v >> 8), (byte)(v >> 16), (byte)(v >> 24)];

        byte[] header = [.. "II"u8, .. Le32(26), .. "HEAPCCDR"u8, .. new byte[12]];
        byte[] makeModel = [.. "Canon\0Canon EOS 10D\0"u8];
        byte[] time = [.. Le32(1_000_000_000), 0, 0, 0, 0, 0, 0, 0, 0];
        byte[] spec = [.. Le32(3072), .. Le32(2048), 0, 0, 0x80, 0x3F, .. Le32(90)];
        byte[] data = [.. makeModel, .. time, .. spec];
        int tableAt = data.Length;
        byte[] Record(int tag, int size, int offset) => [.. Le16(tag), .. Le32(size), .. Le32(offset)];
        byte[] table = [.. Le16(3),
            .. Record(0x080A, makeModel.Length, 0),
            .. Record(0x180E, time.Length, makeModel.Length),
            .. Record(0x1810, spec.Length, makeModel.Length + time.Length)];
        byte[] crw = [.. header, .. data, .. table, .. Le32(tableAt)];
        byte[] withPreview = [.. crw[..26], .. Fake.Jpeg(1536, 1024), .. crw[26..]];
        // The preview sits before the records, so shift the record offsets by its length.
        int shift = withPreview.Length - crw.Length;
        byte[] file = [.. header, .. Fake.Jpeg(1536, 1024), .. data, .. Le16(3),
            .. Record(0x080A, makeModel.Length, shift),
            .. Record(0x180E, time.Length, shift + makeModel.Length),
            .. Record(0x1810, spec.Length, shift + makeModel.Length + time.Length),
            .. Le32(shift + tableAt)];

        var exif = PreviewExtractor.ReadExif(new MemoryStream(crw))!;
        Assert.Equal("Canon EOS 10D", exif.CameraText);
        Assert.Equal(new DateTime(2001, 9, 9, 1, 46, 40), exif.DateTaken);
        Assert.Equal(6, exif.Orientation);

        var preview = PreviewExtractor.Extract(new MemoryStream(file));
        Assert.Equal((1536, 1024), (preview!.Width, preview.Height));
        Assert.Equal("Canon EOS 10D", preview.Exif.CameraText);
    }

    [Fact]
    public void ReadExif_is_cheap_and_matches_Extract_for_raw_and_jpeg()
    {
        var t = new TiffBuilder();
        var preview = Fake.Jpeg(4000, 3000);
        uint at = t.AddBlob(preview);
        uint exif = t.AddIfd([t.Ascii(0x9003, "2025:07:08 09:10:11")]);
        t.SetFirstIfd(t.AddIfd([t.Ascii(0x010F, "NIKON CORPORATION"), t.Ascii(0x0110, "NIKON Z 8"), t.Long(0x8769, exif),
            t.Long(0x0201, at), t.Long(0x0202, (uint)preview.Length)]));

        Assert.Equal(new DateTime(2025, 7, 8, 9, 10, 11), PreviewExtractor.ReadExif(new MemoryStream(t.ToArray()))?.DateTaken);

        var jpeg = Fake.Jpeg(4000, 3000, exifTiff: ExifTiff("Canon", "Canon EOS R8", 1, "2024:12:24 18:00:00"));
        Assert.Equal(new DateTime(2024, 12, 24, 18, 0, 0), PreviewExtractor.ReadExif(new MemoryStream(jpeg))?.DateTaken);
    }

    [Fact]
    public void Camera_heif_pairs_with_its_raw()
    {
        using var dir = new TempDir();
        dir.File("IMG_0001.CR3");
        dir.File("IMG_0001.HIF");
        dir.File("IMG_0002.heic");
        dir.File("scan.tif");

        var photos = FolderScanner.Scan(dir.Path);

        Assert.Equal(3, photos.Count);
        Assert.True(photos.Single(p => p.BaseName == "IMG_0001").IsPair);
        Assert.Equal(Path.Combine(dir.Path, "IMG_0002.heic"), photos.Single(p => p.BaseName == "IMG_0002").ImagePath);
    }

    [Fact]
    public void Subfolders_are_included_only_when_asked_and_pair_within_their_own_folder()
    {
        using var dir = new TempDir();
        dir.File("IMG_0001.NEF");
        Directory.CreateDirectory(Path.Combine(dir.Path, "Day 2"));
        dir.File(Path.Combine("Day 2", "IMG_0001.NEF"));
        dir.File(Path.Combine("Day 2", "IMG_0001.JPG"));
        var hidden = Directory.CreateDirectory(Path.Combine(dir.Path, ".cache"));
        hidden.Attributes |= FileAttributes.Hidden;
        dir.File(Path.Combine(".cache", "IMG_0009.NEF"));

        Assert.Single(FolderScanner.Scan(dir.Path));

        var all = FolderScanner.Scan(dir.Path, includeSubfolders: true);
        Assert.Equal(2, all.Count);
        Assert.False(all[0].IsPair);
        Assert.True(all[1].IsPair);
        Assert.Equal(Path.Combine(dir.Path, "Day 2", "IMG_0001.xmp"), all[1].SidecarPath);
    }
}

public class ImageAnalysisTests
{
    /// <summary>
    /// A grey picture of blocks with crisp edges (like real subject detail), optionally blurred: <paramref name="passes"/>
    /// of a 3×3 box blur, or of a sideways-only blur for <paramref name="motion"/>, plus optional sensor-like noise.
    /// </summary>
    static byte[] Pattern(int size, int passes, bool motion = false, int noise = 0)
    {
        var img = new byte[size * size];
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
            img[y * size + x] = (byte)((x / 16 + y / 24) % 2 == 0 ? 60 : 190);
        for (int pass = 0; pass < passes; pass++)
        {
            var copy = (byte[])img.Clone();
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                int sum = 0, n = 0;
                for (int dy = motion ? 0 : -1; dy <= (motion ? 0 : 1); dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    sum += copy[Math.Clamp(y + dy, 0, size - 1) * size + Math.Clamp(x + dx, 0, size - 1)];
                    n++;
                }
                img[y * size + x] = (byte)(sum / n);
            }
        }
        var rnd = new Random(42);
        if (noise > 0)
            for (int i = 0; i < img.Length; i++) img[i] = (byte)Math.Clamp(img[i] + rnd.Next(-noise, noise + 1), 0, 255);
        return img;
    }

    static double Score(byte[] img) => FocusAnalyzer.Score(img, 512, 512, 512)!.Value;

    [Fact]
    public void Blur_lowers_the_focus_score_past_the_thresholds()
    {
        double sharp = Score(Pattern(512, 0)), soft = Score(Pattern(512, 2)), blurry = Score(Pattern(512, 12));

        Assert.True(sharp > soft && soft > blurry, $"sharp {sharp:0}, soft {soft:0}, blurry {blurry:0}");
        Assert.Equal(FocusVerdict.Sharp, FocusAnalyzer.Verdict(sharp));
        Assert.NotEqual(FocusVerdict.Sharp, FocusAnalyzer.Verdict(soft));
        Assert.Equal(FocusVerdict.Blurry, FocusAnalyzer.Verdict(blurry));
    }

    [Fact]
    public void Sideways_shake_is_caught_even_though_horizontal_edges_stay_sharp()
    {
        double shaken = Score(Pattern(512, 6, motion: true));
        Assert.NotEqual(FocusVerdict.Sharp, FocusAnalyzer.Verdict(shaken));
    }

    [Fact]
    public void Noise_does_not_make_a_blurred_photo_look_sharp()
    {
        double noisyBlurred = Score(Pattern(512, 12, noise: 8));
        Assert.NotEqual(FocusVerdict.Sharp, FocusAnalyzer.Verdict(noisyBlurred));
        Assert.Equal(FocusVerdict.Sharp, FocusAnalyzer.Verdict(Score(Pattern(512, 0, noise: 8))));
    }

    [Fact]
    public void Focus_score_ignores_overall_brightness_and_contrast()
    {
        var normal = Pattern(512, 1);
        var dim = normal.Select(v => (byte)(v / 4)).ToArray();
        double a = Score(normal), b = Score(dim);
        Assert.InRange(b, a * 0.8, a * 1.2);
    }

    [Fact]
    public void Flat_pictures_have_no_focus_score()
    {
        Assert.Null(FocusAnalyzer.Score(new byte[256 * 256], 256, 256, 256));
    }

    [Fact]
    public void Histogram_counts_channels_and_clipping()
    {
        // 4 pixels: pure white, pure black, mid grey, pure red (BGRA order).
        byte[] px = [255, 255, 255, 255, 0, 0, 0, 255, 128, 128, 128, 255, 0, 0, 255, 255];
        var h = Histogram.FromBgra(px, 4, 1, 16);

        Assert.Equal(2, h.Red[255]);
        Assert.Equal(1, h.Green[128]);
        Assert.Equal(0.5, h.HighlightClipping);
        Assert.Equal(0.25, h.ShadowClipping);
        Assert.Equal(4, h.Luminance.Sum());
    }
}
