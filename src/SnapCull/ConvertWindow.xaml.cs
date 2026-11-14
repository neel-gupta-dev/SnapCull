using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using SnapCull.Core;

namespace SnapCull;

public partial class ConvertWindow : Window
{
    static readonly (ConvertFormat Format, string Label)[] Formats =
    [
        (ConvertFormat.Jpeg, "JPEG: best for sharing and printing"),
        (ConvertFormat.Png, "PNG: lossless, larger files"),
        (ConvertFormat.Tiff, "TIFF: lossless, for editing"),
        (ConvertFormat.WebP, "WebP: small files for websites"),
        (ConvertFormat.Heic, "HEIC: small files, high quality"),
    ];

    readonly List<(string Label, Func<List<PhotoItem>> Pick)> _choices = [];
    readonly string _sourceFolder;
    readonly bool _ready;

    public (List<PhotoItem> Photos, ConvertOptions Options)? Request { get; private set; }

    /// <param name="all">Every photo in the folder.</param>
    /// <param name="shown">The photos the current filter shows.</param>
    /// <param name="current">The photo on screen, if any.</param>
    /// <param name="startWithCurrent">Pre-select "this photo" (when opened from the right-click menu).</param>
    public ConvertWindow(IReadOnlyList<PhotoItem> all, IReadOnlyList<PhotoItem> shown, string filterLabel,
        PhotoItem? current, bool startWithCurrent, string sourceFolder, ConvertOptions? last)
    {
        InitializeComponent();
        _sourceFolder = sourceFolder;

        if (current is not null) _choices.Add(($"This photo ({current.FileName})", () => [current]));
        _choices.Add(("Picked photos", () => all.Where(p => p.Flag == CullFlag.Pick).ToList()));
        _choices.Add(("Rated ★ or higher", () => all.Where(p => p.Stars >= 1).ToList()));
        _choices.Add(("Rated ★★★ or higher", () => all.Where(p => p.Stars >= 3).ToList()));
        _choices.Add(("Rated ★★★★★", () => all.Where(p => p.Stars >= 5).ToList()));
        if (shown.Count != all.Count) _choices.Add(($"Photos shown now ({filterLabel})", () => shown.ToList()));
        _choices.Add(("Everything except rejects", () => all.Where(p => p.Flag != CullFlag.Reject).ToList()));
        _choices.Add(("All photos", () => all.ToList()));
        foreach (var (label, _) in _choices) WhichCombo.Items.Add(label);
        int firstWithPhotos = _choices.FindIndex(c => c.Pick().Count > 0);
        WhichCombo.SelectedIndex = startWithCurrent && current is not null ? 0 : Math.Max(0, firstWithPhotos);

        bool heic = PhotoConverter.CanWriteHeic;
        foreach (var (format, label) in Formats)
            FormatCombo.Items.Add(format == ConvertFormat.Heic && !heic ? label + " (not available on this PC)" : label);

        var o = last ?? new ConvertOptions();
        FormatCombo.SelectedIndex = Array.FindIndex(Formats, f => f.Format == o.Format);
        QualitySlider.Value = Math.Clamp(o.Quality, 40, 100);
        Tiff16Check.IsChecked = o.Tiff16Bit;
        ResizeCombo.SelectedIndex = (int)o.Resize;
        ResizeBox.Text = o.Resize == ConvertResize.Original ? "2048" : o.ResizeValue.ToString();
        RawCombo.SelectedIndex = (int)o.RawSource;
        KeepMetadataCheck.IsChecked = o.KeepMetadata;
        RemoveGpsCheck.IsChecked = o.RemoveGps;
        PatternBox.Text = string.IsNullOrWhiteSpace(o.NamePattern) ? "{name}" : o.NamePattern;
        DestinationBox.Text = Path.Combine(sourceFolder, "Converted");

        _ready = true;
        UpdateForm();
    }

    ConvertFormat SelectedFormat => Formats[Math.Max(0, FormatCombo.SelectedIndex)].Format;
    ConvertResize SelectedResize => (ConvertResize)Math.Max(0, ResizeCombo.SelectedIndex);
    List<PhotoItem> Selected() => _choices[Math.Max(0, WhichCombo.SelectedIndex)].Pick();

    void Inputs_Changed(object sender, RoutedEventArgs e)
    {
        if (_ready) UpdateForm();
    }

    void QualitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (QualityValue is not null) QualityValue.Text = ((int)QualitySlider.Value).ToString();
    }

    /// <summary>Reads the resize box; null when the value doesn't make sense for the chosen mode.</summary>
    int? ResizeValue()
    {
        if (SelectedResize == ConvertResize.Original) return 0;
        if (!int.TryParse(ResizeBox.Text.Trim(), out int v)) return null;
        return SelectedResize == ConvertResize.Percent ? (v is >= 1 and <= 100 ? v : null) : (v is >= 16 and <= 30000 ? v : null);
    }

    void UpdateForm()
    {
        var format = SelectedFormat;
        bool quality = ConvertPlan.HasQuality(format) && format != ConvertFormat.Heic; // Windows' HEIC writer has no quality setting
        QualityLabel.Visibility = QualityPanel.Visibility = quality ? Visibility.Visible : Visibility.Collapsed;
        QualityValue.Text = ((int)QualitySlider.Value).ToString();
        Tiff16Check.Visibility = format == ConvertFormat.Tiff ? Visibility.Visible : Visibility.Collapsed;

        ResizeBox.Visibility = ResizeUnit.Visibility = SelectedResize == ConvertResize.Original ? Visibility.Collapsed : Visibility.Visible;
        ResizeUnit.Text = SelectedResize == ConvertResize.Percent ? "%  of the original" : "px on the longest side";

        bool canKeep = ConvertPlan.CanKeepMetadata(format);
        KeepMetadataCheck.IsEnabled = canKeep;
        RemoveGpsCheck.IsEnabled = canKeep && KeepMetadataCheck.IsChecked == true;

        var photos = Selected();
        var example = photos.FirstOrDefault();
        var exampleName = example is null ? "" :
            ConvertPlan.FileName(PatternBox.Text, example.Files, example.Exif, 1, format);
        PatternHelp.Text = "Use {name}, {date}, {time}, {seq} or {camera}." + (exampleName.Length > 0 ? $"   Example: {exampleName}" : "");

        string? problem = null;
        if (photos.Count == 0) problem = "No photos match this choice.";
        else if (format == ConvertFormat.Heic && !PhotoConverter.CanWriteHeic)
            problem = "Windows can't write HEIC on this PC. Install “HEIF Image Extensions” and “HEVC Video Extensions” from the Microsoft Store, or choose another format.";
        else if (ResizeValue() is null)
            problem = SelectedResize == ConvertResize.Percent ? "Enter a percentage from 1 to 100." : "Enter a size from 16 to 30000 pixels.";
        else if (DestinationBox.Text.Trim().Length == 0) problem = "Choose where to save the converted photos.";

        OkButton.IsEnabled = problem is null;
        if (problem is not null) { SummaryText.Text = problem; return; }

        var notes = new List<string>
        {
            $"{photos.Count} photo{(photos.Count == 1 ? "" : "s")} will be saved as {format.ToString().ToUpperInvariant()} files. " +
            "Your originals are not changed, and existing files are never overwritten.",
        };
        if (RawCombo.SelectedIndex == (int)RawSource.FullRender || (format == ConvertFormat.Tiff && Tiff16Check.IsChecked == true))
            notes.Add("Full RAW renders take a few seconds per photo.");
        if (!canKeep) notes.Add("HEIC files won't include camera info.");
        SummaryText.Text = string.Join(" ", notes);
    }

    void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose where to save the converted photos" };
        var current = DestinationBox.Text.Trim();
        dialog.InitialDirectory = Directory.Exists(current) ? current : _sourceFolder;
        if (dialog.ShowDialog(this) == true) DestinationBox.Text = dialog.FolderName;
    }

    void Ok_Click(object sender, RoutedEventArgs e)
    {
        string destination;
        try
        {
            var text = DestinationBox.Text.Trim();
            destination = Path.GetFullPath(Path.IsPathRooted(text) ? text : Path.Combine(_sourceFolder, text));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            MessageBox.Show(this, "That folder isn't a valid path.", "Convert photos", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var format = SelectedFormat;
        bool tiff16 = format == ConvertFormat.Tiff && Tiff16Check.IsChecked == true;
        var options = new ConvertOptions
        {
            Format = format,
            Quality = (int)QualitySlider.Value,
            Tiff16Bit = tiff16,
            Resize = SelectedResize,
            ResizeValue = ResizeValue() ?? 0,
            // 16-bit only makes sense with the full sensor data.
            RawSource = tiff16 ? RawSource.FullRender : (RawSource)Math.Max(0, RawCombo.SelectedIndex),
            KeepMetadata = KeepMetadataCheck.IsChecked == true && ConvertPlan.CanKeepMetadata(format),
            RemoveGps = RemoveGpsCheck.IsChecked == true,
            NamePattern = PatternBox.Text.Trim(),
            Destination = destination,
        };
        Request = (Selected(), options);
        DialogResult = true;
    }
}
