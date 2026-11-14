namespace SnapCull.Core;

/// <summary>
/// Estimates how sharp a photo is, to flag shots that are probably out of focus or shaken.
/// </summary>
/// <remarks>
/// Works on an 8-bit greyscale copy of the preview, about 1000 px on the long side. The picture is split
/// into a grid of tiles and each tile gets a "local sharpness" value: the strength of its fine detail
/// (the variance of the Laplacian) relative to its overall contrast. Only the sharpest tiles count, because
/// in a good photo only the subject needs to be in focus; a portrait with a blurred background is still sharp.
/// Comparing detail with contrast keeps dark or low-contrast photos from being mistaken for blurry ones.
/// </remarks>
public static class FocusAnalyzer
{
    const int Grid = 8;
    const double MinTileContrast = 6;   // standard deviation in grey levels; flatter tiles (sky, walls) are ignored
    const int TopTiles = 3;

    /// <summary>
    /// Scores from this up look sharp at screen size; below <see cref="BlurryBelow"/> they are probably not usable.
    /// Calibrated on 70+ camera sample files from raw.pixls.us and blurred copies of them
    /// (run tools/SnapCull.Probe with --focus to see the numbers).
    /// </summary>
    /// <remarks>
    /// On those samples: sharp originals scored a median of 84 (90% above 56); copies with even a slight blur
    /// (σ ≈ 1.4 px at 1024 px wide) scored at most 32, and clearly blurred copies at most 23.
    /// </remarks>
    public const double SharpAbove = 45;
    public const double BlurryBelow = 33;

    /// <summary>Returns a sharpness score from 0 (completely blurred) to 100, or null if the picture has no detail to judge.</summary>
    public static double? Score(ReadOnlySpan<byte> gray, int width, int height, int stride)
    {
        if (width < 64 || height < 64) return null;
        int tileW = width / Grid, tileH = height / Grid;
        var tiles = new List<double>(Grid * Grid);

        for (int ty = 0; ty < Grid; ty++)
        for (int tx = 0; tx < Grid; tx++)
        {
            int x0 = Math.Max(1, tx * tileW), x1 = Math.Min(width - 1, (tx + 1) * tileW);
            int y0 = Math.Max(1, ty * tileH), y1 = Math.Min(height - 1, (ty + 1) * tileH);
            double sum = 0, sumSq = 0, lapSum = 0, lapSq = 0;
            int n = 0;
            for (int y = y0; y < y1; y++)
            {
                int row = y * stride;
                for (int x = x0; x < x1; x++)
                {
                    int c = gray[row + x];
                    int lap = gray[row + x - 1] + gray[row + x + 1] + gray[row - stride + x] + gray[row + stride + x] - 4 * c;
                    sum += c; sumSq += c * c;
                    lapSum += lap; lapSq += lap * lap;
                    n++;
                }
            }
            if (n == 0) continue;
            double mean = sum / n, contrast = Math.Sqrt(Math.Max(0, sumSq / n - mean * mean));
            if (contrast < MinTileContrast) continue;
            double lapMean = lapSum / n, lapStd = Math.Sqrt(Math.Max(0, lapSq / n - lapMean * lapMean));
            tiles.Add(lapStd / contrast);
        }

        if (tiles.Count == 0) return null;
        tiles.Sort();
        double best = tiles.TakeLast(Math.Min(TopTiles, tiles.Count)).Average();
        // Map the ratio (about 0.2 for heavy blur, 1–3 for crisp detail) onto 0–100 with a curve that
        // keeps separating sharp shots from each other instead of capping them all at 100.
        return 100 * (1 - Math.Exp(-best));
    }

    public static FocusVerdict Verdict(double score) =>
        score >= SharpAbove ? FocusVerdict.Sharp : score < BlurryBelow ? FocusVerdict.Blurry : FocusVerdict.Soft;
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
