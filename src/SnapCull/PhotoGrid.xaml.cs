using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace SnapCull;

/// <summary>One row of photos in the grid.</summary>
public sealed class GridRow(IReadOnlyList<PhotoItem> items)
{
    public IReadOnlyList<PhotoItem> Items { get; } = items;
}

/// <summary>
/// All photos as a grid of thumbnails whose size can be changed. Supports keyboard navigation and
/// multi-selection (Ctrl/Shift + click, Shift + arrows, Ctrl+A). Only rows on screen are drawn.
/// </summary>
public partial class PhotoGrid : UserControl
{
    public const double MinCellSize = 100, MaxCellSize = 420;
    /// <summary>Above this size the filmstrip thumbnails (256 px) look soft, so sharper ones are loaded for visible photos.</summary>
    const double LargeThumbnailFrom = 250;

    public static readonly DependencyProperty CellSizeProperty = DependencyProperty.Register(nameof(CellSize), typeof(double), typeof(PhotoGrid),
        new PropertyMetadata(190.0, (d, _) => ((PhotoGrid)d).Relayout(force: false)));
    public static readonly DependencyProperty ImageHeightProperty =
        DependencyProperty.Register(nameof(ImageHeight), typeof(GridLength), typeof(PhotoGrid), new PropertyMetadata(new GridLength(137)));
    public static readonly DependencyProperty ShowNamesProperty =
        DependencyProperty.Register(nameof(ShowNames), typeof(bool), typeof(PhotoGrid), new PropertyMetadata(true));

    List<PhotoItem> _items = [];
    List<GridRow> _rows = [];
    readonly HashSet<PhotoItem> _selected = [];
    PhotoItem? _current, _anchor;
    int _columns = 1;
    ScrollViewer? _scroller;

    public PhotoGrid() => InitializeComponent();

    public double CellSize { get => (double)GetValue(CellSizeProperty); set => SetValue(CellSizeProperty, Math.Clamp(value, MinCellSize, MaxCellSize)); }
    public GridLength ImageHeight { get => (GridLength)GetValue(ImageHeightProperty); private set => SetValue(ImageHeightProperty, value); }
    public bool ShowNames { get => (bool)GetValue(ShowNamesProperty); private set => SetValue(ShowNamesProperty, value); }

    public LargeThumbnailLoader? LargeThumbnails { get; set; }
    public PhotoItem? CurrentItem => _current;
    public int Columns => _columns;

    /// <summary>The photos ratings apply to: the multi-selection if there is one, otherwise the current photo.</summary>
    public IReadOnlyList<PhotoItem> SelectedItems =>
        _selected.Count > 1 ? _items.Where(_selected.Contains).ToList() : _current is null ? [] : [_current];

    public event Action<PhotoItem>? OpenRequested;
    public event Action<PhotoItem?>? CurrentChanged;
    public event Action? SelectionChanged;
    public event Action<double>? CellSizeChanged;

    /// <summary>Shows these photos (the current filter's), keeping the current photo and selection where possible.</summary>
    public void SetItems(IReadOnlyList<PhotoItem> items)
    {
        _items = items.ToList();
        var present = _items.ToHashSet();
        foreach (var gone in _selected.Where(s => !present.Contains(s)).ToList())
        {
            gone.IsGridSelected = false;
            _selected.Remove(gone);
        }
        if (_current is not null && !present.Contains(_current))
        {
            _current.IsGridCurrent = false;
            _current = null;
        }
        Relayout(force: true);
    }

    void Relayout(bool force)
    {
        double size = CellSize;
        ImageHeight = new GridLength(Math.Round(size * 0.72));
        ShowNames = size >= 140;

        double available = Math.Max(1, Rows.ActualWidth - 16 - SystemParameters.VerticalScrollBarWidth);
        int columns = Math.Max(1, (int)(available / (size + 8)));
        if (!force && columns == _columns && _rows.Count > 0) { RequestLargeThumbnails(); return; }

        _columns = columns;
        _rows = _items.Chunk(columns).Select(c => new GridRow(c)).ToList();
        Rows.ItemsSource = _rows;
        if (_current is not null) ScrollTo(_current);
        RequestLargeThumbnails();
    }

    // ── Current photo and selection ──

    public void SetCurrent(PhotoItem? item, bool clearSelection = true, bool scroll = true)
    {
        if (_current is not null) _current.IsGridCurrent = false;
        _current = item;
        if (item is not null) item.IsGridCurrent = true;
        if (clearSelection) { ClearSelection(); _anchor = item; }
        if (scroll && item is not null) ScrollTo(item);
        CurrentChanged?.Invoke(item);
    }

    /// <summary>Moves the current photo by columns and rows; with <paramref name="extend"/>, selects everything in between.</summary>
    public void Move(int dx, int dy, bool extend)
    {
        if (_items.Count == 0) return;
        int index = _current is null ? 0 : Math.Max(0, _items.IndexOf(_current));
        var target = _items[Math.Clamp(index + dx + dy * _columns, 0, _items.Count - 1)];
        if (extend)
        {
            _anchor ??= _current ?? target;
            SelectRange(_anchor, target);
            SetCurrent(target, clearSelection: false);
        }
        else
        {
            SetCurrent(target);
        }
    }

    public void SelectAll()
    {
        foreach (var item in _items) Select(item, true);
        SelectionChanged?.Invoke();
    }

    public void ClearSelection()
    {
        foreach (var item in _selected) item.IsGridSelected = false;
        _selected.Clear();
        SelectionChanged?.Invoke();
    }

    public bool HasMultiSelection => _selected.Count > 1;

    void Select(PhotoItem item, bool on)
    {
        item.IsGridSelected = on;
        if (on) _selected.Add(item); else _selected.Remove(item);
    }

    void SelectRange(PhotoItem from, PhotoItem to)
    {
        foreach (var item in _selected) item.IsGridSelected = false;
        _selected.Clear();
        int a = _items.IndexOf(from), b = _items.IndexOf(to);
        if (a < 0 || b < 0) return;
        for (int i = Math.Min(a, b); i <= Math.Max(a, b); i++) Select(_items[i], true);
        SelectionChanged?.Invoke();
    }

    void Cell_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PhotoItem item) return;
        e.Handled = true;
        if (e.ClickCount == 2) { OpenRequested?.Invoke(item); return; }

        var mods = Keyboard.Modifiers;
        if ((mods & ModifierKeys.Shift) != 0)
        {
            SelectRange(_anchor ?? _current ?? item, item);
            SetCurrent(item, clearSelection: false, scroll: false);
        }
        else if ((mods & ModifierKeys.Control) != 0)
        {
            // Ctrl+click adds or removes a photo; the photo you were on joins the selection first.
            if (_selected.Count == 0 && _current is not null && !ReferenceEquals(_current, item)) Select(_current, true);
            Select(item, !_selected.Contains(item));
            _anchor = item;
            SetCurrent(item, clearSelection: false, scroll: false);
            SelectionChanged?.Invoke();
        }
        else
        {
            SetCurrent(item, scroll: false);
        }
    }

    void ScrollTo(PhotoItem item)
    {
        int index = _items.IndexOf(item);
        if (index < 0 || _rows.Count == 0) return;
        Rows.ScrollIntoView(_rows[Math.Min(_rows.Count - 1, index / _columns)]);
    }

    // ── Size ──

    /// <summary>Makes thumbnails bigger (+1) or smaller (−1).</summary>
    public void ChangeSize(int direction) => SetSize(CellSize * (direction > 0 ? 1.2 : 1 / 1.2));

    void SetSize(double size)
    {
        CellSize = size;
        CellSizeChanged?.Invoke(CellSize);
        if (_current is not null) ScrollTo(_current);
    }

    void Rows_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        // Ctrl + wheel (or a touchpad pinch) changes the thumbnail size, like zooming.
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
        e.Handled = true;
        SetSize(CellSize * Math.Pow(1.1, e.Delta / 120.0));
    }

    void Rows_SizeChanged(object sender, SizeChangedEventArgs e) => Relayout(force: false);

    void Rows_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        _scroller = e.OriginalSource as ScrollViewer ?? _scroller;
        RequestLargeThumbnails();
    }

    /// <summary>Asks for sharper thumbnails for the photos on screen when thumbnails are shown big.</summary>
    void RequestLargeThumbnails()
    {
        if (LargeThumbnails is null || CellSize < LargeThumbnailFrom || _rows.Count == 0) return;
        double rowHeight = Math.Round(CellSize * 0.72) + (ShowNames ? 20 : 0) + 12;
        double offset = _scroller?.VerticalOffset ?? 0, viewport = _scroller?.ViewportHeight ?? Rows.ActualHeight;
        int first = Math.Max(0, (int)(offset / rowHeight) - 1);
        int last = Math.Min(_rows.Count - 1, (int)((offset + viewport) / rowHeight) + 1);
        var visible = new List<PhotoItem>();
        for (int r = first; r <= last; r++) visible.AddRange(_rows[r].Items);
        LargeThumbnails.Request(visible);
    }
}
