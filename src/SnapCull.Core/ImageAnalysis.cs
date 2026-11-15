namespace SnapCull.Core;

/// <summary>
/// Estimates how sharp a photo is, to flag shots that are probably out of focus or shaken.
/// </summary>
/// <remarks>
/// <para>
/// Works like a photographer's eye: it looks at real edges (a step from dark to light) and measures how many
/// pixels wide each edge is. In a sharp photo, edges are about 1–1.5 px wide; missed focus or camera shake
/// spreads them over 2, 3 or more pixels. The picture is analysed at <see cref="AnalysisLongSide"/> px on the
/// long side, close to what you see when you zoom in.
/// </para>
/// <para>
/// Edges are measured separately in the horizontal and vertical direction, and the worse direction counts,
/// so motion blur (which only smears edges across the direction of movement) is caught too. Only clear edges
/// count, which keeps sensor noise from looking like detail. The picture is split into a grid and the sharpest
/// few tiles decide, because only the subject needs to be in focus: a portrait with a blurred background is sharp.
/// </para>
/// </remarks>
public static class FocusAnalyzer
{
    /// <summary>Long side, in pixels, that photos are analysed at.</summary>
    public const int AnalysisLongSide = 2400;

    const int Grid = 8;
    const int TopTiles = 3;
    const int Window = 8;          // an edge must rise within this many pixels
    const int MinEdgesPerDirection = 8;
    /// <summary>Edge width assumed for a direction whose edges were smeared away entirely.</summary>
    const double SmearedWidth = 4;

    /// <summary>
    /// Scores from this up look sharp when zoomed in; below <see cref="BlurryBelow"/> they are probably not usable.
    /// </summary>
    /// <remarks>
    /// Calibrated with <c>SnapCull.Probe --focus</c> on 72 camera samples from raw.pixls.us and blurred copies of them,
    /// analysed at 2400 px. Originals: median 84, 92% Sharp. Slight blur (σ ≈ 0.8 px): median 64, 54% flagged.
    /// Blurred (σ ≈ 1.4 px): max 58, none Sharp, 78% Blurry. Very blurred (σ ≈ 2.4 px): all Blurry.
    /// Sideways shake (σ ≈ 1.4 px in one direction): max 67, 96% flagged.
    /// </remarks>
    public const double SharpAbove = 65;
    public const double BlurryBelow = 50;

    /// <summary>Returns a sharpness score from 0 (completely blurred) to 100, or null if the picture has no clear edges to judge.</summary>
    public static double? Score(ReadOnlySpan<byte> gray, int width, int height, int stride) =>
        Analyze(gray, width, height, stride)?.Score;

    /// <summary>Score plus the typical edge width (in pixels at <see cref="AnalysisLongSide"/>) of the sharpest part of the photo.</summary>
    public static (double Score, double EdgeWidth)? Analyze(ReadOnlySpan<byte> gray, int width, int height, int stride)
    {
        if (width < 64 || height < 64) return null;
        var (across, down) = TileWidthsByDirection(gray, width, height, stride, Grid);

        // Each direction is judged by its sharpest tiles; the worse direction decides. A sideways shake leaves
        // horizontal edges crisp but smears vertical ones, so the photo still counts as blurred.
        static double? Best(double[] widths)
        {
            var valid = widths.Where(w => !double.IsNaN(w)).Order().ToList();
            return valid.Count == 0 ? null : valid.Take(TopTiles).Average();
        }
        var a = Best(across);
        var d = Best(down);
        if (a is null && d is null) return null;
        double edgeWidth = Math.Max(a ?? 0, d ?? 0);

        // Strong shake can smear edges in one direction away completely. Real photos virtually always have some
        // edges both ways, so a photo with plenty of edges one way and none the other is treated as smeared.
        int validAcross = across.Count(w => !double.IsNaN(w)), validDown = down.Count(w => !double.IsNaN(w));
        if ((validAcross == 0 && validDown >= 6) || (validDown == 0 && validAcross >= 6))
            edgeWidth = Math.Max(edgeWidth, SmearedWidth);
        return (ScoreFromWidth(edgeWidth), edgeWidth);
    }

    /// <summary>1 px wide edges score 100, 2 px 75, 3 px 50, 5 px or wider 0.</summary>
    public static double ScoreFromWidth(double edgeWidth) => Math.Clamp(100 * (5 - edgeWidth) / 4, 0, 100);

    public static FocusVerdict Verdict(double score) =>
        score >= SharpAbove ? FocusVerdict.Sharp : score < BlurryBelow ? FocusVerdict.Blurry : FocusVerdict.Soft;

    /// <summary>
    /// Sharpness of each tile in a <paramref name="grid"/> × <paramref name="grid"/> split (row by row), as 1 / edge width:
    /// higher is sharper. Tiles without enough clear edges (sky, walls) are NaN.
    /// </summary>
    public static double[] TileSharpness(ReadOnlySpan<byte> gray, int width, int height, int stride, int grid) =>
        TileEdgeWidths(gray, width, height, stride, grid).Select(w => double.IsNaN(w) ? double.NaN : 1 / w).ToArray();

    /// <summary>Typical edge width in each tile (the worse of the two directions); NaN without enough edges.</summary>
    public static double[] TileEdgeWidths(ReadOnlySpan<byte> gray, int width, int height, int stride, int grid)
    {
        var (across, down) = TileWidthsByDirection(gray, width, height, stride, grid);
        var result = new double[across.Length];
        for (int i = 0; i < result.Length; i++)
            result[i] = (double.IsNaN(across[i]), double.IsNaN(down[i])) switch
            {
                (false, false) => Math.Max(across[i], down[i]), // motion blur shows up in one direction only
                (false, true) => across[i],
                (true, false) => down[i],
                _ => double.NaN,
            };
        return result;
    }

    /// <summary>
    /// Typical edge width in each tile, separately for edges met moving across rows (vertical edges) and moving down
    /// columns (horizontal edges). NaN where a tile has too few edges in that direction.
    /// </summary>
    public static (double[] Across, double[] Down) TileWidthsByDirection(ReadOnlySpan<byte> gray, int width, int height, int stride, int grid)
    {
        int minRise = MinRise(gray, width, height, stride);
        var resultAcross = new double[grid * grid];
        var resultDown = new double[grid * grid];
        int tileW = width / grid, tileH = height / grid;
        var across = new List<double>(512);
        var down = new List<double>(512);

        for (int ty = 0; ty < grid; ty++)
        for (int tx = 0; tx < grid; tx++)
        {
            int x0 = tx * tileW, x1 = tx == grid - 1 ? width : x0 + tileW;
            int y0 = ty * tileH, y1 = ty == grid - 1 ? height : y0 + tileH;
            across.Clear();
            down.Clear();

            // Edges met when moving across a row (vertical edges), every second row.
            for (int y = y0; y < y1; y += 2)
            {
                int row = y * stride;
                for (int x = Math.Max(x0, 3); x < Math.Min(x1, width - Window); x++)
                    if (EdgeRatio(gray, row + x, 1, minRise) is double r) across.Add(r);
            }
            // Edges met when moving down a column (horizontal edges), every second column.
            for (int x = x0; x < x1; x += 2)
                for (int y = Math.Max(y0, 3); y < Math.Min(y1, height - Window); y++)
                    if (EdgeRatio(gray, y * stride + x, stride, minRise) is double r) down.Add(r);

            resultAcross[ty * grid + tx] = across.Count >= MinEdgesPerDirection ? 1 / Percentile(across, 0.75) : double.NaN;
            resultDown[ty * grid + tx] = down.Count >= MinEdgesPerDirection ? 1 / Percentile(down, 0.75) : double.NaN;
        }
        return (resultAcross, resultDown);
    }

    /// <summary>
    /// If the pixel at <paramref name="at"/> is the steepest step of an edge running along <paramref name="step"/>,
    /// returns step / total rise of the edge (1 = the edge happens in one pixel, 0.5 = over two pixels…).
    /// </summary>
    static double? EdgeRatio(ReadOnlySpan<byte> g, int at, int step, int minRise)
    {
        int d = Math.Abs(g[at + step] - g[at]);
        if (d * 4 < minRise) return null;
        // Must be the steepest step nearby, so each edge is counted once.
        if (Math.Abs(g[at] - g[at - step]) > d || Math.Abs(g[at + 2 * step] - g[at + step]) >= d) return null;

        int lo = 255, hi = 0;
        for (int k = -3; k <= Window - 3; k++)
        {
            int v = g[at + k * step];
            if (v < lo) lo = v;
            if (v > hi) hi = v;
        }
        int rise = hi - lo;
        return rise >= minRise ? (double)d / rise : null;
    }

    /// <summary>An edge must rise at least this much to count: 20% of the photo's tonal range, between 12 and 40 levels.</summary>
    static int MinRise(ReadOnlySpan<byte> g, int width, int height, int stride)
    {
        var histogram = new int[256];
        int samples = 0;
        for (int y = 0; y < height; y += 4)
            for (int x = 0; x < width; x += 4) { histogram[g[y * stride + x]]++; samples++; }
        int Level(double fraction)
        {
            int target = (int)(samples * fraction), seen = 0;
            for (int v = 0; v < 256; v++) if ((seen += histogram[v]) > target) return v;
            return 255;
        }
        return Math.Clamp((int)((Level(0.98) - Level(0.02)) * 0.2), 12, 40);
    }

    static double Percentile(List<double> values, double p)
    {
        values.Sort();
        return values[Math.Clamp((int)(values.Count * p), 0, values.Count - 1)];
    }
}

public enum FocusVerdict { Sharp, Soft, Blurry }

/// <summary>Brightness distribution of a photo, for the histogram overlay.</summary>
public sealed class Histogram
{
    public int[] Red { get; } = new int[256];
    public int[] Green { get; } = new int[256];
    public int[] Blue { get; } = new int[256];
    public int[] Luminance { get; } = new int[256];
    /// <summary>Share of pixels (0–1) with at least one channel at full brightness.</summary>
    public double HighlightClipping { get; private set; }
    /// <summary>Share of pixels (0–1) that are completely black.</summary>
    public double ShadowClipping { get; private set; }

    /// <summary>Builds a histogram from 32-bit BGRA/BGR32 pixels.</summary>
    public static Histogram FromBgra(ReadOnlySpan<byte> pixels, int width, int height, int stride)
    {
        var h = new Histogram();
        long highlights = 0, shadows = 0, total = 0;
        for (int y = 0; y < height; y++)
        {
            var row = pixels.Slice(y * stride, width * 4);
            for (int x = 0; x < row.Length; x += 4)
            {
                int b = row[x], g = row[x + 1], r = row[x + 2];
                h.Red[r]++; h.Green[g]++; h.Blue[b]++;
                h.Luminance[(r * 54 + g * 183 + b * 19) >> 8]++; // Rec. 709 weights
                if (r == 255 || g == 255 || b == 255) highlights++;
                if (r == 0 && g == 0 && b == 0) shadows++;
                total++;
            }
        }
        if (total > 0)
        {
            h.HighlightClipping = (double)highlights / total;
            h.ShadowClipping = (double)shadows / total;
        }
        return h;
    }
}