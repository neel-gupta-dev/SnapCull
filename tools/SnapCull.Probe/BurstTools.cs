using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SnapCull;
using SnapCull.Core;

/// <summary>Probe commands for bursts: build a realistic test burst, and run burst detection and ranking on a folder.</summary>
static class BurstTools
{
    /// <summary>
    /// Writes frames of a fake burst from one photo: each frame gets a different blur (focus blur or motion blur)
    /// and a small shift, 0.1 s apart, with real camera EXIF. Frame <paramref name="sharpFrame"/> (1-based) is left sharp.
    /// A different scene and a later single shot are added to check they aren't grouped in.
    /// </summary>
    public static void MakeBurst(string source, string otherScene, string outDir, int sharpFrame = 4)
    {
        Directory.CreateDirectory(outDir);
        var start = new DateTime(2026, 11, 16, 9, 30, 0);
        // (blur radius x, blur radius y, shift) per frame; frame 4 is the sharp one.
        (int Rx, int Ry, int Shift)[] frames = [(2, 2, 0), (1, 1, 3), (5, 0, 6), (0, 0, 8), (1, 1, 11), (3, 0, 14), (3, 3, 16), (2, 1, 18)];
        frames[3] = (0, 0, 8);
        if (sharpFrame - 1 != 3) (frames[3], frames[sharpFrame - 1]) = (frames[sharpFrame - 1], frames[3]);

        var (pixels, w, h, stride, exif) = Load(source, 3000);
        for (int i = 0; i < frames.Length; i++)
        {
            var (rx, ry, shift) = frames[i];
            var frame = Shift(pixels, w, h, stride, shift);
            if (rx > 0) BoxBlur(frame, w, h, stride, rx, horizontal: true);
            if (ry > 0) BoxBlur(frame, w, h, stride, ry, horizontal: false);
            var time = start.AddSeconds(i * 0.1);
            Save(Path.Combine(outDir, $"BURST_{i + 1:00}.jpg"), frame, w, h, stride, exif, time);
            Console.WriteLine($"BURST_{i + 1:00}.jpg  {time:HH:mm:ss.fff}  blur {rx},{ry}  shift {shift}px{(rx == 0 && ry == 0 ? "   <- sharp" : "")}");
        }

        // A different subject 0.3 s later (same camera): must start a new group, not join the burst.
        var (op, ow, oh, os, _) = Load(otherScene, 3000);
        for (int i = 0; i < 2; i++)
        {
            var time = start.AddSeconds(0.8 + i * 0.1);
            Save(Path.Combine(outDir, $"OTHER_{i + 1:00}.jpg"), Shift(op, ow, oh, os, i * 4), ow, oh, os, exif, time);
        }
        // A single shot 20 s later: must stay on its own.
        Save(Path.Combine(outDir, "SINGLE.jpg"), pixels, w, h, stride, exif, start.AddSeconds(20));
        Console.WriteLine($"Wrote {frames.Length} burst frames, 2 frames of another scene and 1 single shot to {outDir}");
    }

    /// <summary>Runs the app's burst detection and ranking on a folder and prints the groups and ranks.</summary>
    public static void Analyse(string folder, double gap)
    {
        var files = Directory.EnumerateFiles(folder).Where(PhotoFormats.IsSupported).OrderBy(f => f).ToList();
        var frames = files.Select((f, i) =>
        {
            var exif = PreviewExtractor.ReadExif(f);
            var thumb = ImageDecoder.TryLoad(PhotoFiles.FromPath(f), PreviewSize.Thumbnail, 256)!.Bitmap;
            var (g, tw, th, ts) = ImageDecoder.Pixels(thumb, 64, PixelFormats.Bgra32);
            return new BurstFrame(i, exif?.CameraKey ?? "", exif?.PreciseTime, Path.GetFileName(f), BurstDetector.Signature(g, tw, th, ts, bytesPerPixel: 4));
        }).ToList();

        for (int i = 1; i < frames.Count; i++)
            Console.WriteLine($"  difference {frames[i - 1].SortName} -> {frames[i].SortName}: " +
                              $"{BurstDetector.Difference(frames[i - 1].Signature!, frames[i].Signature!):0.000}");

        var groups = BurstDetector.Detect(frames, gap);
        Console.WriteLine($"{groups.Count} burst(s) with frames up to {gap} s apart:");
        foreach (var group in groups)
        {
            var measures = group.Select(i =>
            {
                var bitmap = ImageDecoder.TryLoad(PhotoFiles.FromPath(files[i]), PreviewSize.Largest, BurstRanker.AnalysisLongSide)!.Bitmap;
                var (g, w, h, s) = ImageDecoder.Pixels(bitmap, BurstRanker.AnalysisLongSide, PixelFormats.Gray8);
                var hist = ImageDecoder.Histogram(bitmap);
                return BurstRanker.Measure(g, w, h, s, hist.HighlightClipping, hist.ShadowClipping);
            }).ToList();
            var ranking = BurstRanker.Rank(measures);
            Console.WriteLine($"  Burst of {group.Count}, subject at ({ranking.SubjectX:0.00}, {ranking.SubjectY:0.00}):");
            for (int k = 0; k < group.Count; k++)
            {
                var r = ranking.Frames[k];
                Console.WriteLine($"    {Path.GetFileName(files[group[k]]),-16} #{r.Rank,-3} {r.Relative,6:0.0%}  {r.Note}");
            }
        }
    }

    /// <summary>
    /// Calibrates <see cref="BurstDetector.SameSceneLimit"/>: fingerprint differences between all pairs of different
    /// photos, versus each photo and a copy moved 3%, 15% brighter and slightly blurred (like the next frame of a burst).
    /// </summary>
    public static void SceneDiffs(string folder)
    {
        var sigs = new List<byte[]>();
        var same = new List<double>();
        foreach (var f in Directory.EnumerateFiles(folder).Where(PhotoFormats.IsSupported).OrderBy(f => f))
        {
            var thumb = ImageDecoder.TryLoad(PhotoFiles.FromPath(f), PreviewSize.Thumbnail, 256)?.Bitmap;
            if (thumb is null) continue;
            var (px, w, h, s) = ImageDecoder.Pixels(thumb, 256, PixelFormats.Bgra32);
            var a = BurstDetector.Signature(px, w, h, s, 4);
            var moved = Shift(px, w, h, s, Math.Max(1, w * 3 / 100));
            for (int i = 0; i < moved.Length; i++) if (i % 4 != 3) moved[i] = (byte)Math.Min(255, moved[i] * 1.15);
            BoxBlur(moved, w, h, s, 1, horizontal: true);
            same.Add(BurstDetector.Difference(a, BurstDetector.Signature(moved, w, h, s, 4)));
            sigs.Add(a);
        }
        var different = new List<double>();
        for (int i = 0; i < sigs.Count; i++)
            for (int j = i + 1; j < sigs.Count; j++) different.Add(BurstDetector.Difference(sigs[i], sigs[j]));

        static string Stats(List<double> v)
        {
            v.Sort();
            return $"min {v[0]:0.000}  1% {v[(int)(v.Count * 0.01)]:0.000}  median {v[v.Count / 2]:0.000}  99% {v[(int)(v.Count * 0.99)]:0.000}  max {v[^1]:0.000}";
        }
        Console.WriteLine($"Same scene, next frame ({same.Count}):      {Stats(same)}");
        Console.WriteLine($"Different photos ({different.Count} pairs): {Stats(different)}");
        Console.WriteLine($"Current limit: {BurstDetector.SameSceneLimit}");
    }

    static (byte[] Pixels, int W, int H, int Stride, ExifData Exif) Load(string path, int longSide)
    {
        var loaded = ImageDecoder.TryLoad(PhotoFiles.FromPath(path), PreviewSize.Largest, longSide)!;
        var (pixels, w, h, stride) = ImageDecoder.Pixels(loaded.Bitmap, longSide, PixelFormats.Bgra32);
        return (pixels, w, h, stride, loaded.Exif);
    }

    // Moves the picture sideways by a few pixels, like a subject or camera moving between frames.
    static byte[] Shift(byte[] src, int w, int h, int stride, int dx)
    {
        var dst = new byte[src.Length];
        for (int y = 0; y < h; y++)
        {
            int row = y * stride;
            Buffer.BlockCopy(src, row, dst, row + dx * 4, (w - dx) * 4);
            Buffer.BlockCopy(src, row, dst, row, dx * 4);
        }
        return dst;
    }

    // Two passes of a box blur in one direction (horizontal = motion blur when used alone).
    static void BoxBlur(byte[] px, int w, int h, int stride, int r, bool horizontal)
    {
        var tmp = new byte[px.Length];
        for (int pass = 0; pass < 2; pass++)
        {
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            for (int c = 0; c < 3; c++)
            {
                int sum = 0, n = 0;
                for (int d = -r; d <= r; d++)
                {
                    int xx = horizontal ? Math.Clamp(x + d, 0, w - 1) : x, yy = horizontal ? y : Math.Clamp(y + d, 0, h - 1);
                    sum += px[yy * stride + xx * 4 + c]; n++;
                }
                tmp[y * stride + x * 4 + c] = (byte)(sum / n);
            }
            for (int i = 3; i < px.Length; i += 4) tmp[i] = 255;
            Buffer.BlockCopy(tmp, 0, px, 0, px.Length);
        }
    }

    static void Save(string path, byte[] pixels, int w, int h, int stride, ExifData exif, DateTime time)
    {
        var bitmap = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        var encoder = new JpegBitmapEncoder { QualityLevel = 92 };
        encoder.Frames.Add(BitmapFrame.Create(new FormatConvertedBitmap(bitmap, PixelFormats.Bgr24, null, 0)));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        var meta = exif.Clone();
        meta.DateTaken = time.AddTicks(-(time.Ticks % TimeSpan.TicksPerSecond));
        meta.SubSecond = (time.Ticks % TimeSpan.TicksPerSecond) / (double)TimeSpan.TicksPerSecond;
        File.WriteAllBytes(path, MetadataEmbedder.EmbedInJpeg(stream.ToArray(), ExifWriter.Build(meta, includeGps: false)));
    }
}
