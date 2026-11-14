using System.IO;
using System.Text.Json;
using System.Threading.Channels;
using System.Windows.Threading;
using SnapCull.Core;

namespace SnapCull;

/// <summary>
/// Keeps the last few screen-sized photos decoded, so flipping back and forth is instant.
/// Caches the decode task itself, so prefetching and showing the same photo never decode twice.
/// Use from the UI thread only.
/// </summary>
public sealed class ImageCache(int capacity)
{
    readonly LinkedList<KeyValuePair<PhotoItem, Task<ImageDecoder.Loaded?>>> _lru = new();
    readonly Dictionary<PhotoItem, LinkedListNode<KeyValuePair<PhotoItem, Task<ImageDecoder.Loaded?>>>> _map = [];

    /// <summary>Long side, in pixels, that photos are decoded at for the fit-to-screen view.</summary>
    public int LongSide { get; set; } = 2560;

    public Task<ImageDecoder.Loaded?> Get(PhotoItem item)
    {
        if (_map.TryGetValue(item, out var node))
        {
            _lru.Remove(node);
            _lru.AddFirst(node);
            return node.Value.Value;
        }

        var files = item.Files;
        int longSide = LongSide;
        var task = Task.Run(() => ImageDecoder.TryLoad(files, PreviewSize.Largest, longSide));
        _map[item] = _lru.AddFirst(new KeyValuePair<PhotoItem, Task<ImageDecoder.Loaded?>>(item, task));

        while (_lru.Count > capacity)
        {
            var last = _lru.Last!;
            _lru.RemoveLast();
            _map.Remove(last.Value.Key);
        }
        return task;
    }

    public void Clear()
    {
        _lru.Clear();
        _map.Clear();
    }
}

/// <summary>
/// Background work for every photo in the folder, always working outward from the photo being viewed:
/// first all filmstrip thumbnails, then (optionally) a focus check of each photo.
/// </summary>
public sealed class BackgroundLoader
{
    const int ThumbnailLongSide = 256;
    const int FocusLongSide = 1024;

    readonly IReadOnlyList<PhotoItem> _items;
    readonly Dictionary<PhotoItem, int> _index;
    readonly Dispatcher _dispatcher;
    readonly CancellationTokenSource _cts = new();
    readonly bool[] _thumbTaken, _focusTaken;
    readonly bool _checkFocus;
    readonly Lock _lock = new();
    int _focus, _focusDone;

    /// <summary>Raised on the UI thread as focus checks complete: (checked so far, total).</summary>
    public event Action<int, int>? FocusProgress;

    public BackgroundLoader(IReadOnlyList<PhotoItem> items, Dispatcher dispatcher, bool checkFocus)
    {
        _items = items;
        _dispatcher = dispatcher;
        _checkFocus = checkFocus;
        _thumbTaken = new bool[items.Count];
        _focusTaken = new bool[items.Count];
        _index = items.Select((item, i) => (item, i)).ToDictionary(x => x.item, x => x.i);
        // After re-sorting, photos keep what was already loaded for them.
        for (int i = 0; i < items.Count; i++)
        {
            _thumbTaken[i] = items[i].Thumbnail is not null || items[i].Error is not null;
            if (items[i].FocusChecked) { _focusTaken[i] = true; _focusDone++; }
        }
    }

    public void Start()
    {
        int workers = Math.Clamp(Environment.ProcessorCount / 2, 2, 4);
        for (int i = 0; i < workers; i++)
            Task.Factory.StartNew(Work, TaskCreationOptions.LongRunning);
    }

    public void Focus(PhotoItem item)
    {
        if (_index.TryGetValue(item, out int i)) _focus = i;
    }

    public void Cancel() => _cts.Cancel();

    static int NearestFree(bool[] taken, int focus)
    {
        int n = taken.Length, f = Math.Clamp(focus, 0, Math.Max(0, n - 1));
        for (int d = 0; f + d < n || f - d >= 0; d++)
        {
            if (f + d < n && !taken[f + d]) { taken[f + d] = true; return f + d; }
            if (d > 0 && f - d >= 0 && !taken[f - d]) { taken[f - d] = true; return f - d; }
        }
        return -1;
    }

    (int Index, bool IsFocusJob) Next()
    {
        lock (_lock)
        {
            int i = NearestFree(_thumbTaken, _focus);
            if (i >= 0) return (i, false);
            return _checkFocus ? (NearestFree(_focusTaken, _focus), true) : (-1, false);
        }
    }

    void Work()
    {
        Thread.CurrentThread.Priority = ThreadPriority.BelowNormal; // the photo you're looking at comes first
        var ct = _cts.Token;
        while (!ct.IsCancellationRequested)
        {
            var (i, isFocusJob) = Next();
            if (i < 0) return;
            var item = _items[i];

            if (!isFocusJob)
            {
                var loaded = ImageDecoder.TryLoad(item.Files, PreviewSize.Thumbnail, ThumbnailLongSide);
                if (ct.IsCancellationRequested) return;
                _dispatcher.InvokeAsync(() =>
                {
                    item.Thumbnail = loaded?.Bitmap;
                    if (loaded is not null) item.Exif ??= loaded.Exif;
                    item.Error = loaded is null ? ImageDecoder.MissingCodecHint(item.Files) ?? "No preview found in this file" : null;
                }, DispatcherPriority.Background);
            }
            else
            {
                double? score = null;
                try
                {
                    if (ImageDecoder.TryLoad(item.Files, PreviewSize.Largest, FocusLongSide) is { } loaded)
                        score = ImageDecoder.FocusScore(loaded.Bitmap);
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { }
                if (ct.IsCancellationRequested) return;
                _dispatcher.InvokeAsync(() =>
                {
                    if (!item.FocusChecked) { item.FocusScore = score; item.FocusChecked = true; }
                    FocusProgress?.Invoke(++_focusDone, _items.Count);
                }, DispatcherPriority.Background);
            }
        }
    }
}

/// <summary>
/// Saves ratings to .xmp sidecars on a background thread, in order, so rating never waits on the disk.
/// </summary>
public sealed class SidecarWriter
{
    readonly Channel<(string? Path, CullState State, TaskCompletionSource? Flushed)> _queue =
        Channel.CreateUnbounded<(string?, CullState, TaskCompletionSource?)>(new UnboundedChannelOptions { SingleReader = true });

    public event Action<string>? Error;

    public SidecarWriter() => Task.Run(ProcessAsync);

    public void Enqueue(string xmpPath, CullState state) => _queue.Writer.TryWrite((xmpPath, state, null));

    /// <summary>Completes once everything queued before this call has been written.</summary>
    public Task FlushAsync()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Writer.TryWrite((null, default, tcs));
        return tcs.Task;
    }

    async Task ProcessAsync()
    {
        await foreach (var (path, state, flushed) in _queue.Reader.ReadAllAsync())
        {
            if (flushed is not null) { flushed.TrySetResult(); continue; }
            try
            {
                XmpSidecar.Write(path!, state);
            }
            catch (Exception ex)
            {
                Error?.Invoke($"Couldn't save the rating for {Path.GetFileName(path)}: {ex.Message}");
            }
        }
    }
}

public sealed class AppSettings
{
    static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SnapCull", "settings.json");

    public string? LastFolder { get; set; }
    public bool AutoAdvance { get; set; } = true;
    public bool ShowInfo { get; set; } = true;
    public bool ShowFilmstrip { get; set; } = true;
    public bool ShowHistogram { get; set; }
    public bool CheckFocus { get; set; } = true;
    public bool IncludeSubfolders { get; set; }
    public bool SortByCaptureTime { get; set; }
    /// <summary>The last Convert settings, so the dialog opens the way it was used (except the destination).</summary>
    public ConvertOptions? LastConvert { get; set; }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
