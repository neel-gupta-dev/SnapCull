using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using SnapCull.Core;

namespace SnapCull;

public sealed record Shortcut(string Keys, string Action);

public partial class MainWindow : Window
{
    enum ViewFilter { All, Picks, Unflagged, Rejects, Stars1, Stars2, Stars3, Stars4, Stars5, Blurry, Sharp }

    static readonly (ViewFilter Filter, string Label)[] Filters =
    [
        (ViewFilter.All, "All photos"),
        (ViewFilter.Picks, "Picks"),
        (ViewFilter.Unflagged, "Not yet flagged"),
        (ViewFilter.Rejects, "Rejects"),
        (ViewFilter.Stars1, "★ and up"),
        (ViewFilter.Stars2, "★★ and up"),
        (ViewFilter.Stars3, "★★★ and up"),
        (ViewFilter.Stars4, "★★★★ and up"),
        (ViewFilter.Stars5, "★★★★★"),
        (ViewFilter.Blurry, "Blurry or soft"),
        (ViewFilter.Sharp, "Sharp"),
    ];

    static readonly Shortcut[] Shortcuts =
    [
        new("← →", "Previous / next photo"),
        new("Home / End", "First / last photo"),
        new("P", "Pick"),
        new("X", "Reject"),
        new("U", "Clear pick / reject"),
        new("1 – 5", "Star rating"),
        new("0", "Clear stars"),
        new("6 7 8 9", "Red / yellow / green / blue label"),
        new("Shift + key", "Rate, doing the opposite of auto-advance"),
        new("A", "Turn auto-advance on / off"),
        new("Z or click", "Zoom to 100% at that spot (drag to pan)"),
        new("C", "Compare two photos side by side"),
        new("Tab", "Compare: switch side · otherwise filmstrip on/off"),
        new("H", "Histogram and clipping"),
        new("I", "Camera info and focus score"),
        new("Mouse wheel", "Previous / next photo"),
        new("Right-click", "Open in editor, show in Explorer, copy path"),
        new("F or F11", "Full screen"),
        new("Ctrl + Z", "Undo"),
        new("Ctrl + O / E", "Open folder / copy or move picks"),
        new("Ctrl + Shift + E", "Convert to JPEG, PNG, TIFF, WebP or HEIC"),
        new("? or F1", "This help"),
        new("Esc", "Close help, zoom, compare or full screen"),
    ];

    readonly string? _startPath;
    readonly AppSettings _settings = AppSettings.Load();
    readonly ImageCache _cache = new(capacity: 8);
    readonly SidecarWriter _writer = new();
    readonly Stack<(PhotoItem Item, CullState Before)> _undo = new();

    ObservableCollection<PhotoItem> _items = [];
    ListCollectionView _view;
    BackgroundLoader? _loader;
    ViewFilter _filter = ViewFilter.All;
    string? _folder;
    PhotoViewer _active;
    bool _compare, _suppressSelection, _suppressOptions, _busy, _converting;
    int _wheelDelta;

    bool _fullscreen;
    WindowState _restoreState;
    WindowStyle _restoreStyle;
    ResizeMode _restoreResize;

    public MainWindow(string? startPath)
    {
        InitializeComponent();
        _startPath = startPath;
        _active = ViewerA;
        _view = CreateView(_items);
        Filmstrip.ItemsSource = _view;

        foreach (var viewer in new[] { ViewerA, ViewerB })
        {
            viewer.Cache = _cache;
            viewer.ClickedAt += Viewer_ClickedAt;
            viewer.Panned += Viewer_Panned;
            viewer.Activated += (s, _) => { if (_compare) SetActive((PhotoViewer)s!); };
            viewer.ConvertRequested += (s, _) =>
            {
                if (_compare) SetActive((PhotoViewer)s!);
                ShowConvert(startWithCurrent: true);
            };
        }

        _suppressOptions = true;
        foreach (var (_, label) in Filters) FilterCombo.Items.Add(label);
        FilterCombo.SelectedIndex = 0;
        SortCombo.SelectedIndex = _settings.SortByCaptureTime ? 1 : 0;
        SubfoldersCheck.IsChecked = _settings.IncludeSubfolders;
        AutoAdvanceCheck.IsChecked = _settings.AutoAdvance;
        HistogramButton.IsChecked = _settings.ShowHistogram;
        _suppressOptions = false;

        ShortcutList.ItemsSource = Shortcuts;
        _writer.Error += message => Dispatcher.InvokeAsync(() => SetStatus(message));
        ApplyPanelVisibility();
    }

    PhotoItem? Current => _active.Item;
    PhotoViewer Other => ReferenceEquals(_active, ViewerA) ? ViewerB : ViewerA;

    // ───────────────────────────── Opening folders ─────────────────────────────

    void Window_Loaded(object sender, RoutedEventArgs e)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        double longSide = Math.Max(SystemParameters.PrimaryScreenWidth * dpi.DpiScaleX,
                                   SystemParameters.PrimaryScreenHeight * dpi.DpiScaleY);
        _cache.LongSide = (int)Math.Clamp(longSide, 1280, 4096);

        if (_startPath is not null)
        {
            _ = OpenPathAsync(_startPath);
        }
        else if (_settings.LastFolder is { } last && Directory.Exists(last))
        {
            var name = Path.GetFileName(last.TrimEnd('\\'));
            ReopenButton.Content = $"Reopen {(name.Length > 0 ? name : last)}";
            ReopenButton.ToolTip = last;
            ReopenButton.Visibility = Visibility.Visible;
        }
    }

    void OpenButton_Click(object sender, RoutedEventArgs e) => ChooseFolder();

    void ReopenButton_Click(object sender, RoutedEventArgs e)
    {
        if (_settings.LastFolder is { } last) _ = OpenPathAsync(last);
    }

    void ChooseFolder()
    {
        if (_busy || _converting) return;
        var dialog = new OpenFolderDialog { Title = "Choose a folder of photos" };
        if ((_folder ?? _settings.LastFolder) is { } start && Directory.Exists(start)) dialog.InitialDirectory = start;
        if (dialog.ShowDialog(this) == true) _ = OpenPathAsync(dialog.FolderName);
    }

    void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } paths) _ = OpenPathAsync(paths[0]);
    }

    /// <summary>Opens a folder, or the folder containing a file (and selects that file).</summary>
    async Task OpenPathAsync(string path, string? selectPath = null)
    {
        if (_busy || _converting) return;
        string folder;
        if (File.Exists(path)) { folder = Path.GetDirectoryName(Path.GetFullPath(path))!; selectPath ??= Path.GetFullPath(path); }
        else if (Directory.Exists(path)) folder = Path.GetFullPath(path);
        else { SetStatus($"Not found: {path}"); return; }

        _busy = true;
        try
        {
            SetStatus($"Reading {folder}…");
            await _writer.FlushAsync();
            bool subfolders = _settings.IncludeSubfolders, byTime = _settings.SortByCaptureTime;
            List<PhotoItem> photos;
            try
            {
                photos = await Task.Run(() =>
                {
                    var list = FolderScanner.Scan(folder, subfolders).Select(f => new PhotoItem(f)
                    {
                        State = XmpSidecar.Read(f.SidecarPath),
                        DisplayName = subfolders ? Path.GetRelativePath(folder, f.PrimaryPath) : Path.GetFileName(f.PrimaryPath),
                        FileTime = File.GetLastWriteTime(f.PrimaryPath),
                    }).ToList();
                    if (byTime) ReadCaptureTimes(list);
                    return byTime ? SortByTime(list) : list;
                });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                SetStatus($"Couldn't open {folder}: {ex.Message}");
                return;
            }

            _folder = folder;
            _settings.LastFolder = folder;
            _settings.Save();
            _undo.Clear();
            ExitCompare();
            SetItems(photos);
            Title = $"{(Path.GetFileName(folder) is { Length: > 0 } name ? name : folder)} – SnapCull";

            int index = 0;
            if (selectPath is not null
                && photos.FirstOrDefault(p => p.Files.MediaFiles().Any(f => string.Equals(f, selectPath, StringComparison.OrdinalIgnoreCase))) is { } match
                && _view.IndexOf(match) is >= 0 and var i)
            {
                index = i;
            }
            GoTo(index);
            SetStatus(photos.Count == 0
                ? $"No photos in {folder}{(subfolders ? "" : " (tick Subfolders to look inside folders)")}"
                : $"{photos.Count} photos in {folder}   ·   Press ? for keyboard shortcuts");
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>Reads capture times for photos that don't have them yet (several files at once).</summary>
    static void ReadCaptureTimes(IEnumerable<PhotoItem> photos)
    {
        Parallel.ForEach(photos.Where(p => p.Exif is null), new ParallelOptions { MaxDegreeOfParallelism = 4 }, p =>
        {
            try
            {
                // The camera JPEG/HEIF is quicker to read than most RAW formats.
                var exif = (p.Files.ImagePath is { } image ? PreviewExtractor.ReadExif(image) : null)
                           ?? (p.Files.RawPath is { } raw ? PreviewExtractor.ReadExif(raw) : null);
                if (exif is not null) p.Exif = exif;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        });
    }

    static List<PhotoItem> SortByTime(List<PhotoItem> photos) =>
        photos.OrderBy(p => p.DateTaken ?? p.FileTime).ThenBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();

    static List<PhotoItem> SortByName(List<PhotoItem> photos) =>
        photos.OrderBy(p => p.Files.Folder, StringComparer.OrdinalIgnoreCase)
              .ThenBy(p => p.Files.BaseName, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Replaces the photo list (after opening a folder, re-sorting or moving photos away).</summary>
    void SetItems(List<PhotoItem> photos)
    {
        _loader?.Cancel();
        _cache.Clear();

        _items = new ObservableCollection<PhotoItem>(photos);
        _view = CreateView(_items);
        _suppressSelection = true;
        Filmstrip.ItemsSource = _view;
        _suppressSelection = false;
        ViewerA.Show(null);
        ViewerB.Show(null);

        _loader = new BackgroundLoader(photos, Dispatcher, _settings.CheckFocus);
        _loader.FocusProgress += Loader_FocusProgress;
        _loader.Start();
        ExportButton.IsEnabled = ConvertButton.IsEnabled = photos.Count > 0;
        UpdateCounts();
    }

    void Loader_FocusProgress(int done, int total)
    {
        if (done % 25 == 0 || done == total) UpdateCounts();
        if (done == total)
        {
            int soft = _items.Count(p => p.IsSoft);
            SetStatus(soft == 0
                ? "Focus check finished: everything looks sharp."
                : $"Focus check finished: {soft} photo{(soft == 1 ? "" : "s")} may be blurry or soft. Choose “Blurry or soft” next to Show to review them.");
        }
        else if (done % 10 == 0)
        {
            SetStatus($"Checking focus… {done} of {total}");
        }
    }

    ListCollectionView CreateView(ObservableCollection<PhotoItem> items) =>
        new(items) { Filter = o => Matches((PhotoItem)o) };

    async void SortCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressOptions) return;
        _settings.SortByCaptureTime = SortCombo.SelectedIndex == 1;
        _settings.Save();
        if (_items.Count == 0 || _busy || _converting) return;

        var keep = Current;
        var photos = _items.ToList();
        _busy = true;
        try
        {
            if (_settings.SortByCaptureTime)
            {
                SetStatus("Reading capture times…");
                await Task.Run(() => ReadCaptureTimes(photos));
            }
            ExitCompare();
            SetItems(_settings.SortByCaptureTime ? SortByTime(photos) : SortByName(photos));
            Refresh(select: keep, fallbackIndex: 0);
            SetStatus(_settings.SortByCaptureTime ? "Sorted by capture time" : "Sorted by file name");
        }
        finally
        {
            _busy = false;
        }
    }

    void SubfoldersCheck_Click(object sender, RoutedEventArgs e)
    {
        _settings.IncludeSubfolders = SubfoldersCheck.IsChecked == true;
        _settings.Save();
        if (_folder is not null) _ = OpenPathAsync(_folder, Current?.Files.PrimaryPath);
    }

    // ───────────────────────────── Navigation & display ─────────────────────────────

    int CurrentIndex => Current is null ? -1 : _view.IndexOf(Current);

    void GoTo(int index)
    {
        if (_view.Count == 0)
        {
            _suppressSelection = true;
            Filmstrip.SelectedIndex = -1;
            _suppressSelection = false;
            _active.Show(null);
            UpdatePositions();
            return;
        }

        index = Math.Clamp(index, 0, _view.Count - 1);
        var item = (PhotoItem)_view.GetItemAt(index);
        _suppressSelection = true;
        Filmstrip.SelectedIndex = index;
        _suppressSelection = false;
        Filmstrip.ScrollIntoView(item);
        ShowInActive(item);
    }

    void ShowInActive(PhotoItem item)
    {
        _active.Show(item);
        _loader?.Focus(item);
        UpdatePositions();
        Prefetch();
    }

    void Next() => GoTo(CurrentIndex + 1);
    void Previous() => GoTo(Math.Max(0, CurrentIndex - 1));

    void Filmstrip_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_suppressSelection && Filmstrip.SelectedItem is PhotoItem item && !ReferenceEquals(item, Current))
            ShowInActive(item);
    }

    void Prefetch()
    {
        int i = CurrentIndex;
        foreach (int d in (int[])[1, 2, -1])
        {
            int j = i + d;
            if (j >= 0 && j < _view.Count) _cache.Get((PhotoItem)_view.GetItemAt(j));
        }
    }

    void UpdatePositions()
    {
        foreach (var viewer in new[] { ViewerA, ViewerB })
        {
            if (viewer.Item is not { } item) continue;
            int i = _view.IndexOf(item);
            var where = i >= 0 ? $"{i + 1} of {_view.Count}" : "not in this filter";
            var position = $"{where}   ·   {item.FormatText}";
            viewer.PositionText = _filter == ViewFilter.All ? position : $"{position}   ·   {Filters[(int)_filter].Label}";
        }
        UpdateEmptyState();
    }

    void UpdateEmptyState()
    {
        bool empty = _view.Count == 0;
        EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        if (!empty || _folder is null) return;

        ReopenButton.Visibility = Visibility.Collapsed;
        if (_items.Count == 0)
        {
            EmptyTitle.Text = "No photos in this folder";
            EmptySubtitle.Text = _settings.IncludeSubfolders
                ? "SnapCull didn't find any RAW or image files here or in its subfolders."
                : "SnapCull looks for RAW and image files directly inside the folder. Tick “Subfolders” in the toolbar to look inside folders too.";
            EmptyButtons.Visibility = Visibility.Visible;
        }
        else
        {
            EmptyTitle.Text = "No photos match this filter";
            EmptySubtitle.Text = _filter is ViewFilter.Blurry or ViewFilter.Sharp && _items.Any(p => !p.FocusChecked)
                ? "The focus check is still running. Give it a moment, or pick a different option next to “Show”."
                : $"Choose a different option next to “Show” to see the other {_items.Count} photos.";
            EmptyButtons.Visibility = Visibility.Collapsed;
        }
    }

    void UpdateCounts()
    {
        int picks = 0, rejects = 0, rated = 0, soft = 0;
        foreach (var p in _items)
        {
            if (p.Flag == CullFlag.Pick) picks++;
            else if (p.Flag == CullFlag.Reject) rejects++;
            if (p.Stars > 0) rated++;
            if (p.IsSoft) soft++;
        }
        CountsText.Text = _items.Count == 0 ? "" :
            $"{_items.Count} photos   ·   {picks} picked   ·   {rejects} rejected   ·   {rated} rated" +
            (soft > 0 ? $"   ·   {soft} blurry or soft" : "");
    }

    void SetStatus(string text) => StatusText.Text = text;

    // ───────────────────────────── Rating ─────────────────────────────

    bool Matches(PhotoItem p) => _filter switch
    {
        ViewFilter.Picks => p.Flag == CullFlag.Pick,
        ViewFilter.Unflagged => p.Flag == CullFlag.None,
        ViewFilter.Rejects => p.Flag == CullFlag.Reject,
        ViewFilter.Stars1 => p.Stars >= 1,
        ViewFilter.Stars2 => p.Stars >= 2,
        ViewFilter.Stars3 => p.Stars >= 3,
        ViewFilter.Stars4 => p.Stars >= 4,
        ViewFilter.Stars5 => p.Stars >= 5,
        ViewFilter.Blurry => p.IsSoft,
        ViewFilter.Sharp => p.FocusVerdict == FocusVerdict.Sharp,
        _ => true,
    };

    // XMP stores "rejected" as rating -1, so a photo can't be rejected and starred at the same time.
    void SetStars(int stars) => ApplyChange(s => s with
    {
        Stars = stars,
        Flag = s.Flag == CullFlag.Reject && stars > 0 ? CullFlag.None : s.Flag,
    });

    void SetFlag(CullFlag flag) => ApplyChange(s => s with
    {
        Flag = flag,
        Stars = flag == CullFlag.Reject ? 0 : s.Stars,
    });

    void ToggleLabel(ColorLabel label) => ApplyChange(s => s with
    {
        Label = s.Label == label ? ColorLabel.None : label,
    });

    void ApplyChange(Func<CullState, CullState> change)
    {
        var item = Current;
        if (item is null || _busy) return;
        int index = CurrentIndex;

        var before = item.State;
        var after = change(before);
        if (after != before)
        {
            _undo.Push((item, before));
            item.State = after;
            _writer.Enqueue(item.Files.SidecarPath, after);
            UpdateCounts();
        }

        if (!Matches(item))
        {
            // It no longer fits the filter, so it drops out and the next photo slides into its place.
            Refresh(select: null, fallbackIndex: index);
            return;
        }

        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        if (_settings.AutoAdvance != shift) Next();
    }

    void Undo()
    {
        if (_undo.Count == 0) { SetStatus("Nothing to undo"); return; }
        var (item, before) = _undo.Pop();
        if (!_items.Contains(item)) return;

        item.State = before;
        _writer.Enqueue(item.Files.SidecarPath, before);
        UpdateCounts();
        Refresh(select: item, fallbackIndex: Math.Max(0, CurrentIndex));
        SetStatus($"Undid the last change to {item.FileName}");
    }

    /// <summary>Re-applies the filter, then shows <paramref name="select"/> if it's visible, otherwise the photo at <paramref name="fallbackIndex"/>.</summary>
    void Refresh(PhotoItem? select, int fallbackIndex)
    {
        _suppressSelection = true;
        _view.Refresh();
        _suppressSelection = false;
        int index = select is not null && Matches(select) ? _view.IndexOf(select) : fallbackIndex;
        GoTo(index);
    }

    void FilterCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FilterCombo.SelectedIndex < 0) return;
        _filter = Filters[FilterCombo.SelectedIndex].Filter;
        if (!_suppressOptions) Refresh(select: Current, fallbackIndex: 0);
    }

    void AutoAdvanceCheck_Click(object sender, RoutedEventArgs e)
    {
        _settings.AutoAdvance = AutoAdvanceCheck.IsChecked == true;
        _settings.Save();
    }

    void ToggleAutoAdvance()
    {
        _settings.AutoAdvance = !_settings.AutoAdvance;
        AutoAdvanceCheck.IsChecked = _settings.AutoAdvance;
        _settings.Save();
        SetStatus(_settings.AutoAdvance ? "Auto-advance on" : "Auto-advance off");
    }

    // ───────────────────────────── Zoom & compare ─────────────────────────────

    async void Viewer_ClickedAt(object? sender, Point? at)
    {
        var viewer = (PhotoViewer)sender!;
        if (viewer.IsZoomed)
        {
            ExitZoom();
            return;
        }
        if (at is null && !_compare) return; // clicked the empty area beside the photo
        await ZoomAsync(at ?? new Point(0.5, 0.5));
    }

    async Task ZoomAsync(Point at)
    {
        if (_compare) await Task.WhenAll(ViewerA.ZoomToAsync(at), ViewerB.ZoomToAsync(at));
        else await _active.ZoomToAsync(at);
    }

    void ExitZoom()
    {
        ViewerA.ExitZoom();
        ViewerB.ExitZoom();
    }

    void ToggleZoom()
    {
        if (_active.IsZoomed) ExitZoom();
        else _ = ZoomAsync(new Point(0.5, 0.5));
    }

    // In compare mode, panning one side moves the other to the same spot so details line up.
    void Viewer_Panned(object? sender, Point center)
    {
        if (!_compare) return;
        (ReferenceEquals(sender, ViewerA) ? ViewerB : ViewerA).SetCenter(center);
    }

    void CompareButton_Click(object sender, RoutedEventArgs e) => ToggleCompare();

    void ToggleCompare()
    {
        if (_compare) { ExitCompare(); return; }
        if (Current is not { } anchor || _view.Count < 2)
        {
            CompareButton.IsChecked = false;
            SetStatus("Compare needs at least two photos");
            return;
        }

        _compare = true;
        CompareButton.IsChecked = true;
        ExitZoom();
        CompareColumn.Width = new GridLength(1, GridUnitType.Star);
        ViewerB.Visibility = Visibility.Visible;
        ViewerB.ShowHistogram = _settings.ShowHistogram;

        // Keep the current photo on the left; the right side starts on the next one and moves with ← →.
        int i = _view.IndexOf(anchor);
        SetActive(ViewerB);
        GoTo(i + 1 < _view.Count ? i + 1 : i - 1);
        SetStatus("Compare: ← → change the active side (blue frame) · Tab or click switches sides · C or Esc to finish");
    }

    void ExitCompare()
    {
        if (!_compare) return;
        var keep = Current;
        _compare = false;
        CompareButton.IsChecked = false;
        ExitZoom();
        CompareColumn.Width = new GridLength(0);
        ViewerB.Visibility = Visibility.Collapsed;
        ViewerB.Show(null);
        SetActive(ViewerA);
        if (keep is not null && _view.IndexOf(keep) is >= 0 and var i) GoTo(i);
    }

    void SetActive(PhotoViewer viewer)
    {
        _active = viewer;
        ViewerA.ShowActiveFrame = _compare && ReferenceEquals(viewer, ViewerA);
        ViewerB.ShowActiveFrame = _compare && ReferenceEquals(viewer, ViewerB);
        if (Current is { } item && _view.IndexOf(item) is >= 0 and var i)
        {
            _suppressSelection = true;
            Filmstrip.SelectedIndex = i;
            _suppressSelection = false;
            Filmstrip.ScrollIntoView(item);
        }
    }

    void ViewerHost_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_active.IsZoomed || Current is null) return; // when zoomed, the wheel scrolls the photo
        e.Handled = true;
        _wheelDelta += e.Delta;
        if (_wheelDelta >= 120) { _wheelDelta = 0; Previous(); }
        else if (_wheelDelta <= -120) { _wheelDelta = 0; Next(); }
    }

    // ───────────────────────────── Keyboard ─────────────────────────────

    void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (FilterCombo.IsDropDownOpen || SortCombo.IsDropDownOpen || Keyboard.FocusedElement is TextBox) return;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        bool handled = true;

        if (HelpOverlay.Visibility == Visibility.Visible && key is Key.Escape or Key.F1 or Key.OemQuestion)
        {
            HelpOverlay.Visibility = Visibility.Collapsed;
            e.Handled = true;
            return;
        }

        if (ctrl)
        {
            switch (key)
            {
                case Key.O: ChooseFolder(); break;
                case Key.E when (Keyboard.Modifiers & ModifierKeys.Shift) != 0: ShowConvert(startWithCurrent: false); break;
                case Key.E: ShowExport(); break;
                case Key.Z: Undo(); break;
                default: handled = false; break;
            }
            e.Handled = handled;
            return;
        }

        switch (key)
        {
            case Key.Right or Key.Down: Next(); break;
            case Key.Left or Key.Up: Previous(); break;
            case Key.Home: GoTo(0); break;
            case Key.End: GoTo(_view.Count - 1); break;
            case Key.PageDown: GoTo(CurrentIndex + 10); break;
            case Key.PageUp: GoTo(Math.Max(0, CurrentIndex - 10)); break;

            case >= Key.D0 and <= Key.D5: SetStars(key - Key.D0); break;
            case >= Key.NumPad0 and <= Key.NumPad5: SetStars(key - Key.NumPad0); break;
            case Key.D6 or Key.NumPad6: ToggleLabel(ColorLabel.Red); break;
            case Key.D7 or Key.NumPad7: ToggleLabel(ColorLabel.Yellow); break;
            case Key.D8 or Key.NumPad8: ToggleLabel(ColorLabel.Green); break;
            case Key.D9 or Key.NumPad9: ToggleLabel(ColorLabel.Blue); break;
            case Key.P: SetFlag(CullFlag.Pick); break;
            case Key.X: SetFlag(CullFlag.Reject); break;
            case Key.U: SetFlag(CullFlag.None); break;
            case Key.A: ToggleAutoAdvance(); break;

            case Key.Z or Key.Space: ToggleZoom(); break;
            case Key.C: ToggleCompare(); break;
            case Key.H: ToggleHistogram(); break;
            case Key.I:
                _settings.ShowInfo = !_settings.ShowInfo;
                _settings.Save();
                ApplyPanelVisibility();
                break;
            case Key.Tab:
                if (_compare) SetActive(Other);
                else
                {
                    _settings.ShowFilmstrip = !_settings.ShowFilmstrip;
                    _settings.Save();
                    ApplyPanelVisibility();
                }
                break;
            case Key.F or Key.F11: ToggleFullscreen(); break;
            case Key.F1 or Key.OemQuestion: HelpOverlay.Visibility = Visibility.Visible; break;
            case Key.Escape:
                if (_active.IsZoomed) ExitZoom();
                else if (_compare) ExitCompare();
                else if (_fullscreen) ToggleFullscreen();
                break;
            default: handled = false; break;
        }
        e.Handled = handled;
    }

    void HistogramButton_Click(object sender, RoutedEventArgs e)
    {
        _settings.ShowHistogram = HistogramButton.IsChecked == true;
        _settings.Save();
        ApplyPanelVisibility();
    }

    void ToggleHistogram()
    {
        _settings.ShowHistogram = !_settings.ShowHistogram;
        HistogramButton.IsChecked = _settings.ShowHistogram;
        _settings.Save();
        ApplyPanelVisibility();
    }

    void ApplyPanelVisibility()
    {
        ViewerA.ShowInfo = ViewerB.ShowInfo = _settings.ShowInfo;
        ViewerA.ShowHistogram = ViewerB.ShowHistogram = _settings.ShowHistogram;
        Filmstrip.Visibility = _settings.ShowFilmstrip ? Visibility.Visible : Visibility.Collapsed;
    }

    void ToggleFullscreen()
    {
        if (!_fullscreen)
        {
            _restoreState = WindowState;
            _restoreStyle = WindowStyle;
            _restoreResize = ResizeMode;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Normal; // needed so Maximized re-applies without the title bar
            WindowState = WindowState.Maximized;
            Toolbar.Visibility = Visibility.Collapsed;
            StatusBar.Visibility = Visibility.Collapsed;
            _fullscreen = true;
        }
        else
        {
            WindowStyle = _restoreStyle;
            ResizeMode = _restoreResize;
            WindowState = _restoreState;
            Toolbar.Visibility = Visibility.Visible;
            StatusBar.Visibility = Visibility.Visible;
            _fullscreen = false;
        }
    }

    void HelpButton_Click(object sender, RoutedEventArgs e) => HelpOverlay.Visibility = Visibility.Visible;

    void HelpOverlay_MouseDown(object sender, MouseButtonEventArgs e)
    {
        HelpOverlay.Visibility = Visibility.Collapsed;
        e.Handled = true;
    }

    // ───────────────────────────── Copy / move ─────────────────────────────

    void ExportButton_Click(object sender, RoutedEventArgs e) => ShowExport();

    // ───────────────────────────── Convert ─────────────────────────────

    CancellationTokenSource? _convertCts;
    string? _lastConvertFolder;

    void ConvertButton_Click(object sender, RoutedEventArgs e) => ShowConvert(startWithCurrent: false);

    async void ShowConvert(bool startWithCurrent)
    {
        if (_folder is null || _items.Count == 0 || _busy || _converting) return;
        var shown = _view.Cast<PhotoItem>().ToList();
        var dialog = new ConvertWindow(_items.ToList(), shown, Filters[(int)_filter].Label, Current, startWithCurrent, _folder, _settings.LastConvert)
            { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Request is not var (photos, options)) return;

        _settings.LastConvert = options with { Destination = "" };
        _settings.Save();

        _busy = true;
        _convertCts = new CancellationTokenSource();
        ExportButton.IsEnabled = ConvertButton.IsEnabled = OpenButton.IsEnabled = false;
        StatusShowButton.Visibility = Visibility.Collapsed;
        StatusCancelButton.Visibility = Visibility.Visible;
        var format = options.Format.ToString().ToUpperInvariant();
        SetStatus($"Converting to {format}… 0 of {photos.Count}");
        try
        {
            var progress = new Progress<int>(n => SetStatus($"Converting to {format}… {n} of {photos.Count}"));
            var input = photos.Select(p => (p.Files, p.Exif)).ToList();
            var token = _convertCts.Token;
            var result = await Task.Run(() => PhotoConverter.Run(input, options, progress, token));

            var message = token.IsCancellationRequested
                ? $"Stopped: {result.Converted} of {photos.Count} photos converted to {format}"
                : $"Converted {result.Converted} photo{(result.Converted == 1 ? "" : "s")} to {format} in {options.Destination}";
            if (result.UsedPreviewInstead > 0)
                message += $"   ·   {result.UsedPreviewInstead} RAW file{(result.UsedPreviewInstead == 1 ? "" : "s")} used the camera preview (Windows couldn't render the RAW data)";
            if (result.Errors.Count > 0)
            {
                message += $"   ·   {result.Errors.Count} failed";
                MessageBox.Show(this, string.Join("\n", result.Errors.Take(15)), "Some photos couldn't be converted",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            SetStatus(message);
            if (result.Converted > 0)
            {
                _lastConvertFolder = options.Destination;
                StatusShowButton.Tag = result.FirstOutput;
                StatusShowButton.Visibility = Visibility.Visible;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            SetStatus($"Convert failed: {ex.Message}");
        }
        finally
        {
            _busy = false;
            _convertCts.Dispose();
            _convertCts = null;
            StatusCancelButton.Visibility = Visibility.Collapsed;
            OpenButton.IsEnabled = true;
            ExportButton.IsEnabled = ConvertButton.IsEnabled = _items.Count > 0;
        }
    }

    void StatusCancelButton_Click(object sender, RoutedEventArgs e)
    {
        _convertCts?.Cancel();
        SetStatus("Stopping after the photos already in progress…");
    }

    void StatusShowButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (StatusShowButton.Tag is string file && File.Exists(file))
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{file}\"");
            else if (_lastConvertFolder is { } folder && Directory.Exists(folder))
                System.Diagnostics.Process.Start("explorer.exe", $"\"{folder}\"");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            SetStatus($"Couldn't open File Explorer: {ex.Message}");
        }
    }

    async void ShowExport()
    {
        if (_folder is null || _items.Count == 0 || _busy || _converting) return;
        var dialog = new ExportWindow(_items.ToList(), _folder) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Request is not { } request) return;

        _converting = true;
        ExportButton.IsEnabled = OpenButton.IsEnabled = false;
        try
        {
            await _writer.FlushAsync(); // make sure sidecars hold the latest ratings before they're copied
            bool move = request.Mode == ExportMode.Move;
            var progress = new Progress<int>(n => SetStatus($"{(move ? "Moving" : "Copying")} {n} of {request.Photos.Count}…"));
            var result = await Task.Run(() => ExportService.Run(request, progress));

            if (move && result.Succeeded.Count > 0)
            {
                var moved = result.Succeeded.ToHashSet();
                int index = Math.Max(0, CurrentIndex);
                _undo.Clear();
                ExitCompare();
                SetItems(_items.Where(i => !moved.Contains(i.Files)).ToList());
                GoTo(index);
            }

            var message = $"{(move ? "Moved" : "Copied")} {result.Exported} photo{(result.Exported == 1 ? "" : "s")} to {request.Destination}";
            if (result.Skipped > 0) message += $"   ·   {result.Skipped} were already there";
            if (result.Errors.Count > 0)
            {
                message += $"   ·   {result.Errors.Count} failed";
                MessageBox.Show(this, string.Join("\n", result.Errors.Take(15)), "Some photos couldn't be exported",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            SetStatus(message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            SetStatus($"Export failed: {ex.Message}");
        }
        finally
        {
            _converting = false;
            OpenButton.IsEnabled = true;
            ExportButton.IsEnabled = ConvertButton.IsEnabled = _items.Count > 0;
        }
    }

    // ───────────────────────────── Closing ─────────────────────────────

    void Window_Closing(object? sender, CancelEventArgs e)
    {
        _loader?.Cancel();
        _convertCts?.Cancel();
        _settings.Save();
        // Give pending rating writes a moment to finish so nothing is lost.
        _writer.FlushAsync().Wait(TimeSpan.FromSeconds(5));
    }
}
