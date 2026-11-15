namespace SnapCull.Core.Tests;

public class BurstDetectorTests
{
    static readonly DateTime T0 = new(2026, 11, 15, 10, 0, 0);

    static byte[] Scene(int seed)
    {
        var gray = new byte[64 * 64];
        var rnd = new Random(seed);
        for (int i = 0; i < gray.Length; i++) gray[i] = (byte)rnd.Next(256);
        // Blur a little so the fingerprint isn't pure noise.
        return BurstDetector.Signature(gray, 64, 64, 64);
    }

    static BurstFrame Frame(int index, double seconds, byte[] signature, string camera = "Canon|R6") =>
        new(index, camera, T0.AddSeconds(seconds), $"IMG_{index:0000}", signature);

    [Fact]
    public void Groups_quick_frames_of_the_same_scene_and_camera()
    {
        var bird = Scene(1);
        var other = Scene(2);
        var frames = new[]
        {
            Frame(0, 0.00, bird), Frame(1, 0.10, bird), Frame(2, 0.25, bird),   // burst 1
            Frame(3, 0.40, other), Frame(4, 0.55, other),                       // new scene right after: burst 2
            Frame(5, 5.00, bird),                                               // single shot later
            Frame(6, 0.05, bird, camera: "SONY|ILCE-1"),                        // second camera at the same moment
            new BurstFrame(7, "Canon|R6", null, "IMG_0007", bird),             // no capture time
        };

        var groups = BurstDetector.Detect(frames);

        Assert.Equal(2, groups.Count);
        Assert.Contains(groups, g => g.SequenceEqual([0, 1, 2]));
        Assert.Contains(groups, g => g.SequenceEqual([3, 4]));
    }

    [Fact]
    public void Frames_in_the_same_second_are_ordered_by_file_name_without_sub_second_times()
    {
        var s = Scene(3);
        var frames = new[] { Frame(2, 0, s) with { SortName = "IMG_0102" }, Frame(1, 0, s) with { SortName = "IMG_0101" }, Frame(3, 1, s) with { SortName = "IMG_0103" } };

        Assert.Equal([1, 2, 3], BurstDetector.Detect(frames).Single());
    }

    [Fact]
    public void A_larger_gap_joins_slower_sequences()
    {
        var s = Scene(4);
        var frames = new[] { Frame(0, 0, s), Frame(1, 1.8, s), Frame(2, 3.5, s) };

        Assert.Empty(BurstDetector.Detect(frames, gapSeconds: 1));
        Assert.Single(BurstDetector.Detect(frames, gapSeconds: 2));
    }

    [Fact]
    public void Fingerprints_ignore_brightness_but_not_content()
    {
        var gray = new byte[64 * 64];
        for (int y = 0; y < 64; y++) for (int x = 0; x < 64; x++) gray[y * 64 + x] = (byte)(x * 3 + (y / 8 % 2) * 40);
        var darker = gray.Select(v => (byte)(v / 2 + 10)).ToArray();
        var flipped = gray.Reverse().ToArray();

        var a = BurstDetector.Signature(gray, 64, 64, 64);
        Assert.True(BurstDetector.Difference(a, BurstDetector.Signature(darker, 64, 64, 64)) < 0.03);
        Assert.True(BurstDetector.Difference(a, BurstDetector.Signature(flipped, 64, 64, 64)) > BurstDetector.SameSceneLimit);
    }
}

public class BurstRankerTests
{
    const int Size = 512;

    /// <summary>A soft background with a detailed "subject" patch near the top-left; the patch gets <paramref name="blur"/> passes of blur.</summary>
    static byte[] Picture(int blur, int seed = 7)
    {
        var img = new byte[Size * Size];
        var rnd = new Random(seed);
        for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
                img[y * Size + x] = (byte)(90 + 40 * Math.Sin(x / 60.0) + 30 * Math.Cos(y / 45.0));
        for (int y = 96; y < 224; y++)
            for (int x = 96; x < 224; x++)
                img[y * Size + x] = (byte)(((x / 3 + y / 5) % 2 == 0 ? 50 : 200) + rnd.Next(-4, 5));
        for (int pass = 0; pass < blur; pass++)
        {
            var copy = (byte[])img.Clone();
            for (int y = 90; y < 230; y++)
                for (int x = 90; x < 230; x++)
                {
                    int sum = 0;
                    for (int dy = -1; dy <= 1; dy++) for (int dx = -1; dx <= 1; dx++) sum += copy[(y + dy) * Size + x + dx];
                    img[y * Size + x] = (byte)(sum / 9);
                }
        }
        return img;
    }

    static FrameMeasure Measure(byte[] img, double highlights = 0, double shadows = 0) =>
        BurstRanker.Measure(img, Size, Size, Size, highlights, shadows);

    [Fact]
    public void The_sharpest_frame_wins_and_the_subject_is_located()
    {
        var frames = new[] { Measure(Picture(2)), Measure(Picture(0)), Measure(Picture(5)), Measure(Picture(1)) };

        var ranking = BurstRanker.Rank(frames);

        Assert.Equal(new[] { 3, 1, 4, 2 }, ranking.Frames.Select(f => f.Rank));
        Assert.Equal(1.0, ranking.Frames[1].Relative, 6);
        Assert.Equal("sharpest frame", ranking.Frames[1].Note);
        Assert.Contains("softer", ranking.Frames[2].Note);
        Assert.InRange(ranking.SubjectX, 0.2, 0.45); // the detailed patch spans 0.19–0.44 of the width
        Assert.InRange(ranking.SubjectY, 0.2, 0.45);
    }

    [Fact]
    public void Equally_sharp_frames_with_more_blown_highlights_rank_lower()
    {
        var ranking = BurstRanker.Rank([Measure(Picture(0), highlights: 0.08), Measure(Picture(0), highlights: 0.0)]);

        Assert.Equal(2, ranking.Frames[0].Rank);
        Assert.Contains("blown highlights", ranking.Frames[0].Note);
    }

    [Fact]
    public void Sub_second_times_are_read_and_written()
    {
        var t = new TiffBuilder();
        uint exif = t.AddIfd([t.Ascii(0x9003, "2026:11:15 10:00:01"), t.Ascii(0x9291, "45")]);
        t.SetFirstIfd(t.AddIfd([t.Ascii(0x010F, "Canon"), t.Long(0x8769, exif)]));
        var read = TiffParser.Parse(new MemoryStream(t.ToArray()), 0)!.Exif;

        Assert.Equal(0.45, read.SubSecond!.Value, 6);
        Assert.Equal(new DateTime(2026, 11, 15, 10, 0, 1, 450), read.PreciseTime);

        var written = TiffParser.Parse(new MemoryStream(ExifWriter.Build(read, false)), 0)!.Exif;
        Assert.Equal(read.PreciseTime, written.PreciseTime);
    }
}
