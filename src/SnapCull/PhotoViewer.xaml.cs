using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SnapCull.Core;

namespace SnapCull;

/// <summary>
/// Shows one photo with its overlays (rating, file name, camera info, histogram) and handles 100% zoom and panning.
/// The main window has two of these; the second is only visible in compare mode.
/// </summary>
public partial class PhotoViewer : UserControl
{
    PhotoItem? _item;
    BitmapSource? _display;
    (PhotoItem Item, Task<ImageDecoder.Loaded?> Task)? _full;
    Histogram? _histogram;
    int _token;
    bool _zoomed, _showHistogram, _mouseDown, _dragging;
    Point _dragStart, _dragStartOffset;

    public PhotoViewer() => InitializeComponent();

    public ImageCache? Cache { get; set; }
    public PhotoItem? Item => _item;
    public bool IsZoomed => _zoomed;

    /// <summary>A click without dragging: where on the photo (0–1 in each direction), or null if outside it.</summary>
    public event EventHandler<Point?>? ClickedAt;
    /// <summary>The view was dragged while zoomed; the argument is the new centre (0–1 in each direction).</summary>
    public event EventHandler<Point>? Panned;
    /// <summary>The mouse was pressed on this viewer (used to pick the active side in compare mode).</summary>
    public event EventHandler? Activated;
    /// <summary>"Convert this photo…" was chosen from the right-click menu.</summary>
    public event EventHandler? ConvertRequested;

    void Convert_Click(object sender, RoutedEventArgs e) => ConvertRequested?.Invoke(this, EventArgs.Empty);

    public string PositionText { set => PositionLabel.Text = value; }

    public bool ShowInfo
    {
        set => InfoHost.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    public bool ShowHistogram
    {
        set
        {
            _showHistogram = value;
            HistogramHost.Visibility = value && _histogram is not null ? Visibility.Visible : Visibility.Collapsed;
            if (value && _histogram is null) ComputeHistogram(_token);
        }
    }

    public bool ShowActiveFrame
    {
        set => ActiveFrame.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    // ───────────────────────────── Showing a photo ─────────────────────────────

    public async void Show(PhotoItem? item)
    {
        bool sameItem = ReferenceEquals(item, _item) && _display is not null;
        _item = item;
        DataContext = item;
        NamePanel.Visibility = item is null ? Visibility.Collapsed : Visibility.Visible;
        if (sameItem) return;

        int token = ++_token;
        ExitZoom();
        _full = null;
        _display = null;
        _histogram = null;
        HistogramHost.Visibility = Visibility.Collapsed;
        ErrorText.Visibility = Visibility.Collapsed;

        if (item is null || Cache is null)
        {
            MainImage.Source = null;
            LoadingText.Visibility = Visibility.Collapsed;
            return;
        }

        var task = Cache.Get(item);
        if (!task.IsCompleted)
        {
            MainImage.Source = item.Thumbnail; // blurry stand-in until the real preview arrives
            LoadingText.Visibility = Visibility.Visible;
        }

        var loaded = await task;
        if (token != _token) return; // already showing something else

        LoadingText.Visibility = Visibility.Collapsed;
        if (loaded is not null)
        {
            _display = loaded.Bitmap;
            MainImage.Source = loaded.Bitmap;
            item.Exif ??= loaded.Exif;
            if (_showHistogram) ComputeHistogram(token);
            if (!item.FocusChecked) MeasureFocus(item, loaded.Bitmap);
        }
        else
        {
            MainImage.Source = item.Thumbnail;
            ErrorText.Text = ImageDecoder.MissingCodecHint(item.Files)
                ?? $"Couldn't find a preview inside {item.FileName}.\nThe file may be damaged, or this camera isn't supported yet.";
            ErrorText.Visibility = Visibility.Visible;
        }
    }

    static async void MeasureFocus(PhotoItem item, BitmapSource bitmap)
    {
        var score = await Task.Run(() => ImageDecoder.FocusScore(bitmap));
        if (item.FocusChecked) return;
        item.FocusScore = score;
        item.FocusChecked = true;
    }

    async void ComputeHistogram(int token)
    {
        var bitmap = _display;
        if (bitmap is null) return;
        var histogram = await Task.Run(() => ImageDecoder.Histogram(bitmap));
        if (token != _token) return;
        _histogram = histogram;
        DrawHistogram(histogram);
        HistogramHost.Visibility = _showHistogram ? Visibility.Visible : Visibility.Collapsed;
    }

    void DrawHistogram(Histogram h)
    {
        // Scale to the tallest bin, ignoring pure black/white so a clipped spike doesn't flatten everything else.
        double max = 1;
        foreach (var bins in new[] { h.Red, h.Green, h.Blue })
            for (int i = 1; i < 255; i++) max = Math.Max(max, bins[i]);

        RedPath.Data = Area(h.Red, max);
        GreenPath.Data = Area(h.Green, max);
        BluePath.Data = Area(h.Blue, max);
        LumaPath.Data = Area(h.Luminance, max);
        ClippingText.Text = $"Highlights clipped {h.HighlightClipping:0.0%}   ·   Shadows {h.ShadowClipping:0.0%}";
    }

    static Geometry Area(int[] bins, double max)
    {
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(0, 100), isFilled: true, isClosed: true);
            for (int i = 0; i < 256; i++)
                ctx.LineTo(new Point(i, 100 - Math.Min(100, bins[i] / max * 100)), isStroked: true, isSmoothJoin: false);
            ctx.LineTo(new Point(255, 100), isStroked: true, isSmoothJoin: false);
        }
        geometry.Freeze();
        return geometry;
    }

    // ───────────────────────────── Zoom & pan ─────────────────────────────

    /// <summary>Zoom steps for the + and − keys, as a fraction of 100% (one photo pixel per screen pixel).</summary>
    static readonly double[] ZoomSteps = [0.125, 0.25, 0.33, 0.5, 0.67, 1, 1.5, 2, 3, 4, 6, 8];
    const double MaxZoom = 8;

    double _zoom; // current level when zoomed; 1 = 100%

    /// <summary>The zoom level (1 = 100%), or null when the whole photo is fitted to the window.</summary>
    public double? ZoomLevel => _zoomed ? _zoom : null;

    /// <summary>The user changed the zoom on this viewer: level (null = fit) and the photo point at the centre (0–1).</summary>
    public event EventHandler<(double? Level, Point Center)>? ZoomChanged;

    /// <summary>Zooms to 100% centred on <paramref name="at"/> (0–1 in each direction).</summary>
    public Task ZoomToAsync(Point at) => SetZoomAsync(1, at, viewportAnchor: null, raise: false);

    /// <summary>
    /// Shows the photo at <paramref name="level"/> (1 = 100%), keeping photo point <paramref name="imagePoint"/> (0–1)
    /// under <paramref name="viewportAnchor"/> (or the centre). Levels at or below "fit" go back to fitting the window.
    /// </summary>
    public async Task SetZoomAsync(double level, Point imagePoint, Point? viewportAnchor, bool raise)
    {
        var item = _item;
        if (item is null) return;
        int token = _token;

        var full = await LoadFullAsync(item);
        if (token != _token || full is null) return;

        double fit = FitLevel(full);
        if (level <= fit * 1.001)
        {
            ExitZoom();
            if (raise) ZoomChanged?.Invoke(this, (null, imagePoint));
            return;
        }
        level = Math.Min(level, MaxZoom);

        var dpi = VisualTreeHelper.GetDpi(this);
        MainImage.Source = full;
        MainImage.Stretch = Stretch.Fill;
        MainImage.Width = full.PixelWidth * level / dpi.DpiScaleX;
        MainImage.Height = full.PixelHeight * level / dpi.DpiScaleY;
        // Above 100%, show crisp square pixels for checking focus; below, smooth scaling.
        RenderOptions.SetBitmapScalingMode(MainImage, level >= 2 ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);
        Scroller.HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden;
        Scroller.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
        Scroller.Cursor = Cursors.Hand;
        ZoomText.Text = $"{level * 100:0}%";
        ZoomBadge.Visibility = Visibility.Visible;
        _zoomed = true;
        _zoom = level;

        Scroller.UpdateLayout();
        var anchor = viewportAnchor ?? new Point(Scroller.ViewportWidth / 2, Scroller.ViewportHeight / 2);
        Scroller.ScrollToHorizontalOffset(imagePoint.X * MainImage.ActualWidth - anchor.X);
        Scroller.ScrollToVerticalOffset(imagePoint.Y * MainImage.ActualHeight - anchor.Y);
        if (raise) ZoomChanged?.Invoke(this, (level, imagePoint));
    }

    /// <summary>Zooms by a factor around a point of the viewer (for Ctrl + mouse wheel and pinch).</summary>
    public async Task ZoomByAsync(double factor, Point? viewportPoint)
    {
        if (_item is null) return;
        var full = await LoadFullAsync(_item);
        if (full is null) return;
        double current = _zoomed ? _zoom : FitLevel(full);
        var (imagePoint, anchor) = PointUnder(viewportPoint);
        await SetZoomAsync(current * factor, imagePoint, anchor, raise: true);
    }

    /// <summary>Goes to the next zoom step in (+1) or out (−1), for the + and − keys.</summary>
    public async Task ZoomStepAsync(int direction)
    {
        if (_item is null) return;
        var full = await LoadFullAsync(_item);
        if (full is null) return;
        double fit = FitLevel(full);
        double current = _zoomed ? _zoom : fit;
        double next = direction > 0
            ? ZoomSteps.Where(s => s > current * 1.01).DefaultIfEmpty(MaxZoom).First()
            : ZoomSteps.Where(s => s < current * 0.99).DefaultIfEmpty(fit).Last();
        if (direction < 0 && next < fit) next = fit;
        var (imagePoint, _) = PointUnder(null);
        await SetZoomAsync(next, imagePoint, null, raise: true);
    }

    /// <summary>Follows another viewer's zoom (compare mode).</summary>
    public async Task FollowZoomAsync(double? level, Point center)
    {
        if (level is null) { ExitZoom(); return; }
        await SetZoomAsync(level.Value, center, null, raise: false);
    }

    Task<BitmapSource?> LoadFullAsync(PhotoItem item)
    {
        if (_full is not { } full || !ReferenceEquals(full.Item, item))
        {
            var files = item.Files;
            _full = full = (item, Task.Run(() => ImageDecoder.TryLoad(files, PreviewSize.Largest, maxLongSide: null)));
        }
        return AwaitFull(full.Task);
    }

    async Task<BitmapSource?> AwaitFull(Task<ImageDecoder.Loaded?> task)
    {
        if (!task.IsCompleted) LoadingText.Visibility = Visibility.Visible;
        var loaded = await task;
        LoadingText.Visibility = Visibility.Collapsed;
        return loaded?.Bitmap;
    }

    /// <summary>The zoom level at which the whole photo just fits the viewer.</summary>
    double FitLevel(BitmapSource full)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        double w = Math.Max(1, Scroller.ActualWidth) * dpi.DpiScaleX, h = Math.Max(1, Scroller.ActualHeight) * dpi.DpiScaleY;
        return Math.Min(1, Math.Min(w / full.PixelWidth, h / full.PixelHeight));
    }

    /// <summary>Which photo point (0–1) is under a viewer point (default: the centre), and that viewer point.</summary>
    (Point ImagePoint, Point Anchor) PointUnder(Point? viewportPoint)
    {
        var anchor = viewportPoint ?? new Point(Scroller.ActualWidth / 2, Scroller.ActualHeight / 2);
        var onImage = Scroller.TranslatePoint(anchor, MainImage);
        double w = Math.Max(1, MainImage.ActualWidth), h = Math.Max(1, MainImage.ActualHeight);
        return (new Point(Math.Clamp(onImage.X / w, 0, 1), Math.Clamp(onImage.Y / h, 0, 1)), anchor);
    }

    public void ExitZoom()
    {
        if (!_zoomed) return;
        _zoomed = false;
        MainImage.Source = _display ?? _item?.Thumbnail;
        MainImage.Stretch = Stretch.Uniform;
        MainImage.Width = double.NaN;
        MainImage.Height = double.NaN;
        RenderOptions.SetBitmapScalingMode(MainImage, BitmapScalingMode.HighQuality);
        Scroller.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        Scroller.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        Scroller.Cursor = null;
        ZoomBadge.Visibility = Visibility.Collapsed;
    }

    public void SetCenter(Point at)
    {
        if (!_zoomed) return;
        Scroller.ScrollToHorizontalOffset(at.X * Scroller.ExtentWidth - Scroller.ViewportWidth / 2);
        Scroller.ScrollToVerticalOffset(at.Y * Scroller.ExtentHeight - Scroller.ViewportHeight / 2);
    }

    Point Center => new(
        Scroller.ExtentWidth > 0 ? (Scroller.HorizontalOffset + Scroller.ViewportWidth / 2) / Scroller.ExtentWidth : 0.5,
        Scroller.ExtentHeight > 0 ? (Scroller.VerticalOffset + Scroller.ViewportHeight / 2) / Scroller.ExtentHeight : 0.5);

    // Ctrl + mouse wheel (and two-finger pinch on a touchpad, which Windows sends the same way) zooms at the pointer.
    async void Scroller_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0 || _item is null) return;
        e.Handled = true;
        Activated?.Invoke(this, EventArgs.Empty);
        await ZoomByAsync(Math.Pow(1.25, e.Delta / 120.0), e.GetPosition(Scroller));
    }

    void Scroller_MouseDown(object sender, MouseButtonEventArgs e)
    {
        Activated?.Invoke(this, EventArgs.Empty);
        if (_item is null) return;
        _mouseDown = true;
        _dragging = false;
        _dragStart = e.GetPosition(Scroller);
        _dragStartOffset = new Point(Scroller.HorizontalOffset, Scroller.VerticalOffset);
        Scroller.CaptureMouse();
        e.Handled = true;
    }

    void Scroller_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_mouseDown) return;
        var delta = e.GetPosition(Scroller) - _dragStart;
        if (!_dragging && (Math.Abs(delta.X) > 4 || Math.Abs(delta.Y) > 4)) _dragging = true;
        if (_dragging && _zoomed)
        {
            Scroller.Cursor = Cursors.SizeAll;
            Scroller.ScrollToHorizontalOffset(_dragStartOffset.X - delta.X);
            Scroller.ScrollToVerticalOffset(_dragStartOffset.Y - delta.Y);
            Panned?.Invoke(this, Center);
        }
    }

    void Scroller_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_mouseDown) return;
        _mouseDown = false;
        Scroller.ReleaseMouseCapture();
        if (_zoomed) Scroller.Cursor = Cursors.Hand;
        if (_dragging) { _dragging = false; return; }

        var p = e.GetPosition(MainImage);
        bool onImage = p.X >= 0 && p.Y >= 0 && p.X <= MainImage.ActualWidth && p.Y <= MainImage.ActualHeight && MainImage.ActualWidth > 0;
        ClickedAt?.Invoke(this, onImage ? new Point(p.X / MainImage.ActualWidth, p.Y / MainImage.ActualHeight) : null);
    }

    void Scroller_RightDown(object sender, MouseButtonEventArgs e) => Activated?.Invoke(this, EventArgs.Empty);

    // ───────────────────────────── Right-click menu ─────────────────────────────

    void PhotoMenu_Opened(object sender, RoutedEventArgs e)
    {
        bool has = _item is not null;
        foreach (var entry in PhotoMenu.Items.OfType<MenuItem>()) entry.IsEnabled = has;
        OpenRawItem.Visibility = _item?.Files.IsPair == true ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>The file people usually want to open: the camera JPEG/HEIF if there is one, otherwise the RAW.</summary>
    string? MainFile => _item?.Files.ImagePath ?? _item?.Files.RawPath;

    void ShowInExplorer_Click(object sender, RoutedEventArgs e)
    {
        if (_item?.Files.PrimaryPath is { } path) Launch("explorer.exe", $"/select,\"{path}\"");
    }

    void OpenDefault_Click(object sender, RoutedEventArgs e)
    {
        if (MainFile is { } path) Launch(path, null);
    }

    void OpenRaw_Click(object sender, RoutedEventArgs e)
    {
        if (_item?.Files.RawPath is { } path) Launch(path, null);
    }

    void OpenWith_Click(object sender, RoutedEventArgs e)
    {
        if (_item?.Files.PrimaryPath is { } path) Launch("rundll32.exe", $"shell32.dll,OpenAs_RunDLL {path}");
    }

    void CopyPath_Click(object sender, RoutedEventArgs e)
    {
        if (_item?.Files.PrimaryPath is { } path)
        {
            try { Clipboard.SetText(path); } catch (System.Runtime.InteropServices.COMException) { }
        }
    }

    void Launch(string file, string? arguments)
    {
        try
        {
            Process.Start(new ProcessStartInfo(file) { Arguments = arguments ?? "", UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException or InvalidOperationException)
        {
            MessageBox.Show(Window.GetWindow(this), ex.Message, "SnapCull", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
