using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SnapCull.Core;

namespace SnapCull;

/// <summary>One photo in the filmstrip, with its rating and everything the UI binds to.</summary>
public sealed class PhotoItem(PhotoFiles files) : INotifyPropertyChanged
{
    static readonly Brush PickBrush = Frozen(0x2E, 0xB8, 0x55);
    static readonly Brush RejectBrush = Frozen(0xE5, 0x48, 0x48);
    static readonly Brush RedBrush = Frozen(0xE5, 0x48, 0x48);
    static readonly Brush YellowBrush = Frozen(0xF2, 0xC9, 0x4C);
    static readonly Brush GreenBrush = Frozen(0x4C, 0xC2, 0x6A);
    static readonly Brush BlueBrush = Frozen(0x4F, 0xA3, 0xFF);
    static readonly Brush PurpleBrush = Frozen(0xA8, 0x6B, 0xF2);
    static readonly Brush SoftBrush = Frozen(0xE0, 0x8A, 0x2E);

    int _stars;
    CullFlag _flag;
    ColorLabel _label;
    BitmapSource? _thumbnail;
    ExifData? _exif;
    string? _error;

    public event PropertyChangedEventHandler? PropertyChanged;

    public PhotoFiles Files { get; } = files;
    public string FileName => Path.GetFileName(Files.PrimaryPath);
    /// <summary>File name plus its subfolder, when the folder was opened with "include subfolders".</summary>
    public string DisplayName { get; set; } = Path.GetFileName(files.PrimaryPath);
    /// <summary>When the file was last changed — used to sort photos that have no capture time.</summary>
    public DateTime FileTime { get; set; }

    /// <summary>"CR3 + JPG", "NEF", "JPG".</summary>
    public string FormatText => string.Join(" + ", Files.MediaFiles().Select(f => Path.GetExtension(f).TrimStart('.').ToUpperInvariant()));

    public CullState State
    {
        get => new(_stars, _flag, _label);
        set
        {
            if (Set(ref _stars, value.Stars)) Changed(nameof(StarsText));
            if (Set(ref _flag, value.Flag))
            {
                Changed(nameof(IsPicked)); Changed(nameof(IsRejected));
                Changed(nameof(FlagText)); Changed(nameof(FlagGlyph)); Changed(nameof(FlagBrush));
            }
            if (Set(ref _label, value.Label)) { Changed(nameof(LabelBrush)); Changed(nameof(HasLabel)); }
        }
    }

    public int Stars => _stars;
    public CullFlag Flag => _flag;
    public ColorLabel Label => _label;

    public string StarsText => _stars > 0 ? new string('★', _stars) : "";
    public bool IsPicked => _flag == CullFlag.Pick;
    public bool IsRejected => _flag == CullFlag.Reject;
    public string FlagText => _flag switch { CullFlag.Pick => "PICK", CullFlag.Reject => "REJECT", _ => "" };
    public string FlagGlyph => _flag switch { CullFlag.Pick => "✓", CullFlag.Reject => "✕", _ => "" };
    public Brush? FlagBrush => _flag switch { CullFlag.Pick => PickBrush, CullFlag.Reject => RejectBrush, _ => null };
    public bool HasLabel => _label != ColorLabel.None;
    public Brush? LabelBrush => _label switch
    {
        ColorLabel.Red => RedBrush,
        ColorLabel.Yellow => YellowBrush,
        ColorLabel.Green => GreenBrush,
        ColorLabel.Blue => BlueBrush,
        ColorLabel.Purple => PurpleBrush,
        _ => null,
    };

    public BitmapSource? Thumbnail
    {
        get => _thumbnail;
        set => Set(ref _thumbnail, value);
    }

    public ExifData? Exif
    {
        get => _exif;
        set
        {
            if (!Set(ref _exif, value)) return;
            Changed(nameof(HasInfo)); Changed(nameof(CameraLine)); Changed(nameof(LensLine));
            Changed(nameof(SettingsLine)); Changed(nameof(DateLine));
        }
    }

    public bool HasInfo => (_exif is not null && (CameraLine ?? SettingsLine ?? DateLine) is not null) || FocusLine is not null;
    public string? CameraLine => _exif?.CameraText;
    public string? LensLine => _exif?.LensModel;
    public string? SettingsLine => _exif?.SettingsText;
    public string? DateLine => _exif?.DateTaken?.ToString("d MMM yyyy   HH:mm:ss");
    public DateTime? DateTaken => _exif?.DateTaken;

    /// <summary>Sharpness from 0 to 100 (see <see cref="FocusAnalyzer"/>), or null until it has been measured.</summary>
    public double? FocusScore
    {
        get => _focusScore;
        set
        {
            if (!Set(ref _focusScore, value)) return;
            Changed(nameof(FocusVerdict)); Changed(nameof(IsBlurry)); Changed(nameof(IsSoft));
            Changed(nameof(FocusLine)); Changed(nameof(FocusBadgeText)); Changed(nameof(FocusBadgeBrush)); Changed(nameof(HasInfo));
        }
    }
    double? _focusScore;

    /// <summary>True once focus has been checked (even if the photo had no detail to judge).</summary>
    public bool FocusChecked { get; set; }

    public FocusVerdict? FocusVerdict => _focusScore is double s ? FocusAnalyzer.Verdict(s) : null;
    public bool IsBlurry => FocusVerdict == Core.FocusVerdict.Blurry;
    public bool IsSoft => FocusVerdict is Core.FocusVerdict.Blurry or Core.FocusVerdict.Soft;
    public string? FocusLine => _focusScore is double s
        ? $"Focus {s:0}/100 · {FocusVerdict switch { Core.FocusVerdict.Sharp => "looks sharp", Core.FocusVerdict.Soft => "a little soft", _ => "probably blurry" }}"
        : null;
    public string FocusBadgeText => FocusVerdict switch { Core.FocusVerdict.Blurry => "BLUR", Core.FocusVerdict.Soft => "SOFT", _ => "" };
    public Brush? FocusBadgeBrush => FocusVerdict switch { Core.FocusVerdict.Blurry => RejectBrush, Core.FocusVerdict.Soft => SoftBrush, _ => null };

    public string? Error
    {
        get => _error;
        set => Set(ref _error, value);
    }

    bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Changed(name!);
        return true;
    }

    void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
