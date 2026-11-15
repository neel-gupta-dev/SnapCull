using System.Diagnostics;
using System.Windows.Media;
using System.Windows.Threading;
using SnapCull.Core;

namespace SnapCull;

/// <summary>A run of frames shot in quick succession, and (once analysed) which one is best.</summary>
public sealed class BurstGroup(int number, IReadOnlyList<PhotoItem> frames)
{
    public int Number { get; } = number;
    /// <summary>The frames in shooting order.</summary>
    public IReadOnlyList<PhotoItem> Frames { get; } = frames;
    public BurstRanking? Ranking { get; set; }
    public bool IsRanked => Ranking is not null;
    public PhotoItem? Best => Ranking is null ? null : Frames[Ranking.Frames.Select((f, i) => (f.Rank, i)).Min().i];

    /// <summary>The frames best first (or in shooting order until ranked).</summary>
    public IEnumerable<PhotoItem> FramesBestFirst => Ranking is null
        ? Frames
        : Frames.Select((f, i) => (f, Ranking.Frames[i].Rank)).OrderBy(x => x.Rank).Select(x => x.f);
}

/// <summary>Finds bursts among the loaded photos and ranks their frames in the background.</summary>
public static class BurstService
{
    /// <summary>Groups photos into bursts using capture times (with fractions of a second) and thumbnail fingerprints.</summary>
    public static async Task<List<BurstGroup>> DetectAsync(IReadOnlyList<PhotoItem> items, double gapSeconds)
    {
        var inputs = items.Select((p, i) => (Item: p, Index: i, Exif: p.Exif, Thumb: p.Thumbnail)).ToList();
        var groups = await Task.Run(() =>
        {
            var frames = inputs.Select(x =>
            {
                byte[]? signature = null;
                if (x.Thumb is not null)
                {
                    var (pixels, w, h, stride) = ImageDecoder.Pixels(x.Thumb, 64, PixelFormats.Bgra32);
                    signature = BurstDetector.Signature(pixels, w, h, stride, bytesPerPixel: 4);
                }
                return new BurstFrame(x.Index, x.Exif?.CameraKey ?? "", x.Exif?.PreciseTime, x.Item.DisplayName, signature);
            }).ToList();
            return BurstDetector.Detect(frames, gapSeconds);
        });

        var bursts = groups.Select((g, n) => new BurstGroup(n + 1, g.Select(i => items[i]).ToList())).ToList();
        foreach (var item in items) item.Burst = null;
        foreach (var burst in bursts)
            foreach (var frame in burst.Frames) frame.Burst = burst;
        return bursts;
    }

    /// <summary>
    /// Ranks every burst, starting with the one nearest <paramref name="focus"/>. Results are applied on the UI thread
    /// as each burst finishes; <paramref name="ranked"/> is called after each.
    /// </summary>
    public static Task RankAllAsync(IReadOnlyList<BurstGroup> bursts, Func<BurstGroup?> focus, Dispatcher dispatcher,
        Action<BurstGroup> ranked, CancellationToken ct) => Task.Factory.StartNew(() =>
    {
        Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
        var remaining = bursts.ToList();
        while (remaining.Count > 0 && !ct.IsCancellationRequested)
        {
            // The burst you're looking at first, then the rest in order.
            var wanted = dispatcher.Invoke(focus);
            var next = wanted is not null && remaining.Contains(wanted) ? wanted : remaining[0];
            remaining.Remove(next);

            var ranking = Rank(next, ct);
            if (ranking is null || ct.IsCancellationRequested) continue;
            dispatcher.InvokeAsync(() =>
            {
                next.Ranking = ranking;
                for (int i = 0; i < next.Frames.Count; i++) next.Frames[i].BurstRank = ranking.Frames[i];
                ranked(next);
            }, DispatcherPriority.Background);
        }
    }, ct, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    static BurstRanking? Rank(BurstGroup burst, CancellationToken ct)
    {
        var measures = new FrameMeasure?[burst.Frames.Count];
        try
        {
            Parallel.For(0, burst.Frames.Count, new ParallelOptions { MaxDegreeOfParallelism = 2, CancellationToken = ct }, i =>
            {
                var loaded = ImageDecoder.TryLoad(burst.Frames[i].Files, PreviewSize.Largest, BurstRanker.AnalysisLongSide);
                if (loaded is null) return;
                var (gray, w, h, stride) = ImageDecoder.Pixels(loaded.Bitmap, BurstRanker.AnalysisLongSide, PixelFormats.Gray8);
                var histogram = ImageDecoder.Histogram(loaded.Bitmap);
                measures[i] = BurstRanker.Measure(gray, w, h, stride, histogram.HighlightClipping, histogram.ShadowClipping);
            });
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            Debug.WriteLine($"SnapCull: burst {burst.Number} could not be ranked: {ex}");
            return null;
        }

        // Frames that couldn't be read are ranked last with an empty measure.
        var empty = new double[BurstRanker.Grid * BurstRanker.Grid];
        var filled = measures.Select(m => m ?? new FrameMeasure(empty, empty, 1, 1)).ToList();
        return BurstRanker.Rank(filled);
    }
}
