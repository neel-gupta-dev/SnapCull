using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SnapCull.Core;

namespace SnapCull;

/// <summary>One frame in the burst review grid.</summary>
public sealed class BurstCell(PhotoItem item, int? rank, string rankText) : INotifyPropertyChanged
{
    BitmapSource? _crop;
    bool _selected;

    public event PropertyChangedEventHandler? PropertyChanged;
    public PhotoItem Item { get; } = item;
    public bool IsBest => rank == 1;
    public string RankText { get; } = rankText;
    public BitmapSource? Crop { get => _crop; set { _crop = value; Changed(); } }
    public bool IsSelected { get => _selected; set { _selected = value; Changed(); } }

    void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// Shows every frame of a burst side by side, best first, as 100% crops centred on the subject,
/// so the sharpest frame is easy to see.
/// </summary>
public partial class BurstReview : UserControl
{
    public static readonly DependencyProperty CellWidthProperty =
        DependencyProperty.Register(nameof(CellWidth), typeof(double), typeof(BurstReview), new PropertyMetadata(300.0));
    public static readonly DependencyProperty CellHeightProperty =
        DependencyProperty.Register(nameof(CellHeight), typeof(double), typeof(BurstReview), new PropertyMetadata(200.0));

    readonly ObservableCollection<BurstCell> _cells = [];
    CancellationTokenSource? _cropCts;
    int _selected, _columns = 1;
    bool _suppressGap;

    public BurstReview()
    {
        InitializeComponent();
        Cells.ItemsSource = _cells;
        _suppressGap = true;
        foreach (var gap in BurstDetector.GapChoices) GapCombo.Items.Add($"{gap:0.#} s");
        _suppressGap = false;
    }

    public double CellWidth { get => (double)GetValue(CellWidthProperty); set => SetValue(CellWidthProperty, value); }
    public double CellHeight { get => (double)GetValue(CellHeightProperty); set => SetValue(CellHeightProperty, value); }

    public BurstGroup? Burst { get; private set; }
    public PhotoItem? SelectedItem => _selected >= 0 && _selected < _cells.Count ? _cells[_selected].Item : null;

    public event Action<PhotoItem>? OpenRequested;
    public event Action? CloseRequested;
    public event Action? KeepBestRequested;
    public event Action<double>? GapChanged;

    public void Show(BurstGroup burst, double gapSeconds, PhotoItem? select)
    {
        Burst = burst;
        _suppressGap = true;
        GapCombo.SelectedIndex = Math.Max(0, Array.IndexOf(BurstDetector.GapChoices, gapSeconds));
        _suppressGap = false;

        var ranking = burst.Ranking;
        _cells.Clear();
        foreach (var item in burst.FramesBestFirst)
        {
            int i = IndexIn(burst, item);
            var rank = ranking?.Frames[i];
            var text = rank is null ? $"Frame {i + 1} of {burst.Frames.Count}" : $"#{rank.Rank} · {rank.Note} · {rank.Relative:0%}";
            _cells.Add(new BurstCell(item, rank?.Rank, text));
        }
        TitleText.Text = ranking is null
            ? $"Burst of {burst.Frames.Count} frames · finding the best one…"
            : $"Burst of {burst.Frames.Count} frames · best first";
        KeepBestButton.IsEnabled = ranking is not null;

        int index = select is null ? 0 : Math.Max(0, _cells.ToList().FindIndex(c => ReferenceEquals(c.Item, select)));
        Select(index);
        Layout();
        LoadCrops();
    }

    /// <summary>Re-shows the same burst, e.g. after its ranking arrived.</summary>
    public void Refresh(double gapSeconds)
    {
        if (Burst is { } burst) Show(burst, gapSeconds, SelectedItem);
    }

    public void Stop()
    {
        _cropCts?.Cancel();
        _cells.Clear();
        Burst = null;
    }

    static int IndexIn(BurstGroup burst, PhotoItem item)
    {
        for (int i = 0; i < burst.Frames.Count; i++)
            if (ReferenceEquals(burst.Frames[i], item)) return i;
        return 0;
    }

    // ── Selection ──

    public void MoveSelection(int dx, int dy) => Select(Math.Clamp(_selected + dx + dy * _columns, 0, Math.Max(0, _cells.Count - 1)));

    void Select(int index)
    {
        for (int i = 0; i < _cells.Count; i++) _cells[i].IsSelected = i == index;
        _selected = index;
    }

    void Cells_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not BurstCell cell) return;
        Select(_cells.IndexOf(cell));
        if (e.ClickCount == 2) OpenRequested?.Invoke(cell.Item);
        e.Handled = true;
    }

    // ── Layout: as many cells as fit, 3:2, as big as possible ──

    void GridHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        Layout();
        if (_cells.Count > 0) LoadCrops();
    }

    void Layout()
    {
        int n = Math.Max(1, _cells.Count);
        double w = Math.Max(100, GridHost.ActualWidth), h = Math.Max(100, GridHost.ActualHeight);
        double bestCell = 0; int bestCols = 1;
        for (int cols = 1; cols <= n; cols++)
        {
            int rows = (n + cols - 1) / cols;
            double cell = Math.Min(w / cols, h / rows * 1.5);
            if (cell > bestCell) { bestCell = cell; bestCols = cols; }
        }
        _columns = bestCols;
        CellWidth = Math.Floor(bestCell);
        CellHeight = Math.Floor(bestCell / 1.5);
    }

    // ── 100% crops around the subject ──

    async void LoadCrops()
    {
        _cropCts?.Cancel();
        var cts = _cropCts = new CancellationTokenSource();
        var ranking = Burst?.Ranking;
        double sx = ranking?.SubjectX ?? 0.5, sy = ranking?.SubjectY ?? 0.5;
        var dpi = VisualTreeHelper.GetDpi(this);
        int cropW = Math.Max(16, (int)((CellWidth - 14) * dpi.DpiScaleX));
        int cropH = Math.Max(16, (int)((CellHeight - 14) * dpi.DpiScaleY));
        var cells = _cells.ToList();

        using var gate = new SemaphoreSlim(2);
        var tasks = cells.Select(async cell =>
        {
            await gate.WaitAsync();
            try
            {
                if (cts.IsCancellationRequested) return;
                var files = cell.Item.Files;
                var crop = await Task.Run(() => CropAt(files, sx, sy, cropW, cropH));
                if (!cts.IsCancellationRequested) cell.Crop = crop;
            }
            finally { gate.Release(); }
        });
        try { await Task.WhenAll(tasks); } catch (OperationCanceledException) { }
    }

    static BitmapSource? CropAt(PhotoFiles files, double x, double y, int width, int height)
    {
        var full = ImageDecoder.TryLoad(files, PreviewSize.Largest, maxLongSide: null)?.Bitmap;
        if (full is null) return null;
        int w = Math.Min(width, full.PixelWidth), h = Math.Min(height, full.PixelHeight);
        int left = Math.Clamp((int)(x * full.PixelWidth - w / 2.0), 0, full.PixelWidth - w);
        int top = Math.Clamp((int)(y * full.PixelHeight - h / 2.0), 0, full.PixelHeight - h);
        var crop = new CroppedBitmap(full, new Int32Rect(left, top, w, h));
        var copy = new WriteableBitmap(crop); // detach from the full-size bitmap so it can be freed
        copy.Freeze();
        return copy;
    }

    // ── Buttons ──

    void GapCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressGap || GapCombo.SelectedIndex < 0) return;
        GapChanged?.Invoke(BurstDetector.GapChoices[GapCombo.SelectedIndex]);
    }

    void KeepBest_Click(object sender, RoutedEventArgs e) => KeepBestRequested?.Invoke();
    void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();
}
