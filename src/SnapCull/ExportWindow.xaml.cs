using System.IO;
using System.Windows;
using Microsoft.Win32;
using SnapCull.Core;

namespace SnapCull;

public partial class ExportWindow : Window
{
    static readonly (string Label, Func<PhotoItem, bool> Match)[] Choices =
    [
        ("Picked photos", p => p.Flag == CullFlag.Pick),
        ("Rated ★ or higher", p => p.Stars >= 1),
        ("Rated ★★ or higher", p => p.Stars >= 2),
        ("Rated ★★★ or higher", p => p.Stars >= 3),
        ("Rated ★★★★ or higher", p => p.Stars >= 4),
        ("Rated ★★★★★", p => p.Stars >= 5),
        ("Everything except rejects", p => p.Flag != CullFlag.Reject),
        ("Rejected photos", p => p.Flag == CullFlag.Reject),
    ];

    readonly IReadOnlyList<PhotoItem> _items;
    readonly string _sourceFolder;
    readonly bool _ready;

    public ExportRequest? Request { get; private set; }

    public ExportWindow(IReadOnlyList<PhotoItem> items, string sourceFolder)
    {
        InitializeComponent();
        _items = items;
        _sourceFolder = sourceFolder;

        foreach (var (label, _) in Choices) WhichCombo.Items.Add(label);
        // Start on the first choice that actually has photos (normally "Picked").
        int first = Array.FindIndex(Choices, c => items.Any(c.Match));
        WhichCombo.SelectedIndex = first >= 0 ? first : 0;
        DestinationBox.Text = Path.Combine(sourceFolder, "Picks");

        _ready = true;
        UpdateSummary();
    }

    bool IsMove => MoveRadio.IsChecked == true;

    List<PhotoItem> Selected() => _items.Where(Choices[Math.Max(0, WhichCombo.SelectedIndex)].Match).ToList();

    void Inputs_Changed(object sender, RoutedEventArgs e)
    {
        if (_ready) UpdateSummary();
    }

    void UpdateSummary()
    {
        int count = Selected().Count;
        OkButton.Content = IsMove ? "Move" : "Copy";
        OkButton.IsEnabled = count > 0 && DestinationBox.Text.Trim().Length > 0;
        SummaryText.Text = count == 0
            ? "No photos match this choice."
            : $"{count} photo{(count == 1 ? "" : "s")} will be {(IsMove ? "moved" : "copied")}, together with any paired JPEGs " +
              "and .xmp sidecars, so your ratings come along. Nothing at the destination is overwritten.";
    }

    void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose where to put the photos" };
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
            MessageBox.Show(this, "That destination folder isn't a valid path.", "Copy or move photos", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (string.Equals(destination.TrimEnd('\\'), Path.GetFullPath(_sourceFolder).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, "Choose a different folder from the one you're culling.", "Copy or move photos", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Request = new ExportRequest(Selected().Select(p => p.Files).ToList(), destination, IsMove ? ExportMode.Move : ExportMode.Copy);
        DialogResult = true;
    }
}
