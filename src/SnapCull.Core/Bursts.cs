namespace SnapCull.Core;

/// <summary>What burst detection needs to know about one photo.</summary>
/// <param name="Index">The caller's index for the photo; returned in the groups.</param>
/// <param name="Camera">Which camera took it (photos from two bodies are never grouped together).</param>
/// <param name="Time">Capture time including fractions of a second, if known.</param>
/// <param name="SortName">File name, to order frames shot within the same instant.</param>
/// <param name="Signature">A tiny picture fingerprint from <see cref="BurstDetector.Signature"/>, or null if not loaded.</param>
public sealed record BurstFrame(int Index, string Camera, DateTime? Time, string SortName, byte[]? Signature);

/// <summary>
/// Finds bursts: runs of photos from the same camera taken in quick succession of the same scene.
/// </summary>
public static class BurstDetector
{
    /// <summary>Frames this close together (in seconds) can belong to one burst.</summary>
    public const double DefaultGapSeconds = 1.0;
    public static readonly double[] GapChoices = [0.5, 1, 2, 3];

    /// <summary>
    /// Neighbouring frames whose fingerprints differ more than this (0–1) are a new scene, not the same burst.
    /// Measured with <c>SnapCull.Probe --scene-diffs</c> on 73 camera samples: a photo against itself moved 3%,
    /// 15% brighter and slightly blurred (like the next frame of a burst) differs by at most 0.060 (median 0.026);
    /// different photos differ by 0.080 or more in 99% of 2,556 pairs (median 0.163). Erring towards "new scene"
    /// is deliberate: splitting a burst is harmless, while merging two scenes could reject a good photo of something else.
    /// </summary>
    public const double SameSceneLimit = 0.075;

    const int SignatureSide = 16;

    /// <summary>Groups photos into bursts of two or more frames, each in shooting order. Photos without a capture time are skipped.</summary>
    public static List<List<int>> Detect(IReadOnlyList<BurstFrame> frames, double gapSeconds = DefaultGapSeconds)
    {
        var ordered = frames.Where(f => f.Time is not null)
            .OrderBy(f => f.Camera, StringComparer.Ordinal)
            .ThenBy(f => f.Time)
            .ThenBy(f => f.SortName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var groups = new List<List<int>>();
        List<BurstFrame>? current = null;
        foreach (var frame in ordered)
        {
            var previous = current?[^1];
            bool continues = previous is not null
                && previous.Camera == frame.Camera
                && (frame.Time!.Value - previous.Time!.Value).TotalSeconds <= gapSeconds + 1e-6
                && SameScene(previous.Signature, frame.Signature);

            if (continues) current!.Add(frame);
            else
            {
                if (current is { Count: >= 2 }) groups.Add(current.Select(f => f.Index).ToList());
                current = [frame];
            }
        }
        if (current is { Count: >= 2 }) groups.Add(current.Select(f => f.Index).ToList());
        return groups;
    }

    static bool SameScene(byte[]? a, byte[]? b) => a is null || b is null || Difference(a, b) <= SameSceneLimit;

    /// <summary>
    /// A 16 × 16 fingerprint of a picture, per colour channel, each normalised for brightness and contrast
    /// so that exposure changes between frames don't matter, only what's in the picture and its colours.
    /// </summary>
    /// <param name="pixels">8-bit greyscale (<paramref name="bytesPerPixel"/> = 1) or BGRA (4) pixels.</param>
    public static byte[] Signature(ReadOnlySpan<byte> pixels, int width, int height, int stride, int bytesPerPixel = 1)
    {
        int channels = bytesPerPixel >= 3 ? 3 : 1;
        var result = new byte[SignatureSide * SignatureSide * channels];
        var cells = new double[SignatureSide * SignatureSide];
        for (int ch = 0; ch < channels; ch++)
        {
            for (int cy = 0; cy < SignatureSide; cy++)
            for (int cx = 0; cx < SignatureSide; cx++)
            {
                int x0 = cx * width / SignatureSide, x1 = Math.Max(x0 + 1, (cx + 1) * width / SignatureSide);
                int y0 = cy * height / SignatureSide, y1 = Math.Max(y0 + 1, (cy + 1) * height / SignatureSide);
                double sum = 0; int n = 0;
                for (int y = y0; y < y1 && y < height; y++)
                    for (int x = x0; x < x1 && x < width; x++) { sum += pixels[y * stride + x * bytesPerPixel + ch]; n++; }
                cells[cy * SignatureSide + cx] = n > 0 ? sum / n : 0;
            }
            double mean = cells.Average();
            double std = Math.Sqrt(cells.Average(c => (c - mean) * (c - mean)));
            if (std < 1) std = 1;
            for (int i = 0; i < cells.Length; i++)
                result[ch * cells.Length + i] = (byte)Math.Clamp(128 + (cells[i] - mean) / std * 40, 0, 255);
        }
        return result;
    }

    /// <summary>How different two fingerprints are: 0 = identical, about 0.3+ = unrelated pictures.</summary>
    public static double Difference(byte[] a, byte[] b)
    {
        int n = Math.Min(a.Length, b.Length);
        if (n == 0) return 1;
        long total = 0;
        for (int i = 0; i < n; i++) total += Math.Abs(a[i] - b[i]);
        return total / (255.0 * n);
    }
}

/// <summary>
/// Sharpness of one burst frame, tile by tile (1 / edge width, NaN where a tile has no edges), separately for
/// vertical edges (<paramref name="Across"/>) and horizontal edges (<paramref name="Down"/>), plus how much is clipped.
/// </summary>
public sealed record FrameMeasure(double[] Across, double[] Down, double HighlightClipping, double ShadowClipping);

/// <param name="Rank">1 = best.</param>
/// <param name="Relative">Score compared with the best frame (best = 1).</param>
/// <param name="Note">A short reason, for example "a little softer than the best".</param>
public sealed record FrameRank(int Rank, double Score, double Relative, string Note);

/// <param name="Frames">One entry per input frame, in input order.</param>
/// <param name="SubjectX">Where the subject is, 0–1 across the picture: the spot burst review zooms to.</param>
public sealed record BurstRanking(IReadOnlyList<FrameRank> Frames, double SubjectX, double SubjectY);

/// <summary>
/// Picks the best frame of a burst. Frames are compared where the subject is: the tiles that are sharpest
/// in any frame of the burst (a bird's eye, a runner's face). A frame wins when its detail there is crispest,
/// which also catches motion blur and camera shake. Frames with noticeably more clipping lose a few points.
/// </summary>
public static class BurstRanker
{
    /// <summary>Grid used for bursts: finer than the focus check, because frames differ in small areas.</summary>
    public const int Grid = 16;
    /// <summary>Long side, in pixels, that burst frames are analysed at.</summary>
    public const int AnalysisLongSide = 2048;

    public static FrameMeasure Measure(ReadOnlySpan<byte> gray, int width, int height, int stride, double highlightClipping, double shadowClipping)
    {
        var (across, down) = FocusAnalyzer.TileWidthsByDirection(gray, width, height, stride, Grid);
        static double[] Sharpness(double[] widths) => widths.Select(w => double.IsNaN(w) ? double.NaN : 1 / w).ToArray();
        return new FrameMeasure(Sharpness(across), Sharpness(down), highlightClipping, shadowClipping);
    }

    /// <summary>Subject tiles in one direction: the 10% (at least 4) that reach the highest sharpness in any frame.</summary>
    static List<(int Tile, double Max)> SubjectTiles(IReadOnlyList<double[]> perFrame)
    {
        int tiles = perFrame.Min(t => t.Length);
        return Enumerable.Range(0, tiles)
            .Select(t => (Tile: t, Max: perFrame.Max(f => double.IsNaN(f[t]) ? 0 : f[t])))
            .Where(t => t.Max > 0)
            .OrderByDescending(t => t.Max)
            .Take(Math.Max(4, tiles / 10))
            .ToList();
    }

    public static BurstRanking Rank(IReadOnlyList<FrameMeasure> frames)
    {
        if (frames.Count == 0) return new BurstRanking([], 0.5, 0.5);

        // Each direction is compared separately; a frame is only as sharp as its worse direction, so a
        // sideways shake counts even when it leaves horizontal edges crisp. In the same scene, a tile whose
        // edges have vanished in one frame has been smeared, so it counts as 0 there.
        var across = SubjectTiles(frames.Select(f => f.Across).ToList());
        var down = SubjectTiles(frames.Select(f => f.Down).ToList());

        double subjectX = 0.5, subjectY = 0.5;
        var subject = across.Concat(down).ToList();
        if (subject.Count > 0)
        {
            double weight = subject.Sum(s => s.Max);
            subjectX = subject.Sum(s => (s.Tile % Grid + 0.5) / Grid * s.Max) / weight;
            subjectY = subject.Sum(s => (s.Tile / Grid + 0.5) / Grid * s.Max) / weight;
        }

        static double DirectionScore(double[] tiles, List<(int Tile, double Max)> subjectTiles) =>
            subjectTiles.Average(s => double.IsNaN(tiles[s.Tile]) ? 0 : tiles[s.Tile]);

        double minHighlights = frames.Min(f => f.HighlightClipping);
        double minShadows = frames.Min(f => f.ShadowClipping);
        var scores = frames.Select(f =>
        {
            double sharp = (across.Count > 0, down.Count > 0) switch
            {
                (true, true) => Math.Min(DirectionScore(f.Across, across), DirectionScore(f.Down, down)),
                (true, false) => DirectionScore(f.Across, across),
                (false, true) => DirectionScore(f.Down, down),
                _ => 0,
            };
            double highlightPenalty = Math.Clamp((f.HighlightClipping - minHighlights) * 4, 0, 0.2);
            double shadowPenalty = Math.Clamp((f.ShadowClipping - minShadows) * 2, 0, 0.1);
            return (Score: sharp * (1 - highlightPenalty - shadowPenalty), Highlights: highlightPenalty, Shadows: shadowPenalty);
        }).ToList();

        double best = scores.Max(s => s.Score);
        var order = scores.Select((s, i) => (s.Score, i)).OrderByDescending(x => x.Score).ThenBy(x => x.i).Select(x => x.i).ToList();
        var ranks = new FrameRank[frames.Count];
        for (int r = 0; r < order.Count; r++)
        {
            int i = order[r];
            double relative = best > 0 ? scores[i].Score / best : 1;
            ranks[i] = new FrameRank(r + 1, scores[i].Score, relative, Note(r + 1, relative, scores[i].Highlights, scores[i].Shadows));
        }
        return new BurstRanking(ranks, subjectX, subjectY);
    }

    static string Note(int rank, double relative, double highlightPenalty, double shadowPenalty)
    {
        var note = rank == 1 ? "sharpest frame"
            : relative >= 0.97 ? "about as sharp as the best"
            : relative >= 0.90 ? "a little softer than the best"
            : "clearly softer than the best";
        if (highlightPenalty >= 0.03) note += ", more blown highlights";
        else if (shadowPenalty >= 0.03) note += ", more crushed shadows";
        return note;
    }
}
