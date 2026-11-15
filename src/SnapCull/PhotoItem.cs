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
    static readonly Brush BestBrush = Frozen(0xC9, 0x9A, 0x16);
    static readonly Brush BurstBrush = Frozen(0x3A, 0x5F, 0x9E);
    static readonly Brush BurstBarBlue = Frozen(0x5B, 0x8F, 0xE0);
    static readonly Brush BurstBarTeal = Frozen(0x3F, 0xC1, 0xA8);

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
    /// <summary>Size of each file of the photo, e.g. [("CR3", 28 MB), ("JPG", 9 MB)].</summary>
    public IReadOnlyList<(string Kind, long Bytes)> FileSizes { get; set; } = [];

    /// <summary>"6000 × 4000 px · 24.0 MP · 28.4 MB CR3 + 9.1 MB JPG".</summary>
    public string? ResolutionLine
    {
        get
        {
            var size = FileSizes.Count switch
            {
                0 => null,
                1 => FormatBytes(FileSizes[0].Bytes),
                _ => string.Join(" + ", FileSizes.Select(f => $"{FormatBytes(f.Bytes)} {f.Kind}")),
            };
            var resolution = _exif?.ResolutionText;
            return (resolution, size) switch
            {
                (null, null) => null,
                (null, _) => size,
                (_, null) => resolution,
                _ => $"{resolution} · {size}",
            };
        }
    }

    static string FormatBytes(long bytes) => bytes >= 1024 * 1024
        ? $"{bytes / (1024.0 * 1024):0.0} MB"
        : $"{Math.Max(1, bytes / 1024)} KB";

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
            Changed(nameof(HasInfo)); Changed(nameof(CameraLine)); Changed(nameof(LensLine)); Changed(nameof(ResolutionLine));
            Changed(nameof(SettingsLine)); Changed(nameof(DateLine));
        }
    }

    public bool HasInfo => (_exif is not null && (CameraLine ?? SettingsLine ?? DateLine) is not null) || FocusLine is not null || _burst is not null;
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

    // ── Bursts ──

    BurstGroup? _burst;
    FrameRank? _burstRank;

    /// <summary>The burst this photo belongs to, if any.</summary>
    public BurstGroup? Burst
    {
        get => _burst;
        set
        {
            if (!Set(ref _burst, value)) return;
            _burstRank = null;
            BurstChanged();
        }
    }

    /// <summary>How this frame ranks within its burst, once analysed.</summary>
    public FrameRank? BurstRank
    {
        get => _burstRank;
        set { _burstRank = value; BurstChanged(); }
    }

    public bool InBurst => _burst is not null;
    public bool IsBurstBest => _burstRank?.Rank == 1;
    /// <summary>Filmstrip chip: "★ BEST", "#3" or "BURST" (not yet ranked).</summary>
    public string BurstBadgeText => _burst is null ? "" : _burstRank is null ? "BURST" : _burstRank.Rank == 1 ? "★ BEST" : $"#{_burstRank.Rank}";
    public Brush? BurstBadgeBrush => _burst is null ? null : IsBurstBest ? BestBrush : BurstBrush;
    /// <summary>The filmstrip bar under burst frames; neighbouring bursts alternate colours so their edges show.</summary>
    public Brush? BurstBarBrush => _burst is null ? null : _burst.Number % 2 == 1 ? BurstBarBlue : BurstBarTeal;
    /// <summary>Info line, for example "Burst of 14 · #3 · a little softer than the best".</summary>
    public string? BurstLine => _burst is null ? null : _burstRank is null
        ? $"Burst of {_burst.Frames.Count} · finding the best frame…"
        : $"Burst of {_burst.Frames.Count} · #{_burstRank.Rank} · {_burstRank.Note}";

    void BurstChanged()
    {
        Changed(nameof(BurstRank)); Changed(nameof(InBurst)); Changed(nameof(IsBurstBest));
        Changed(nameof(BurstBadgeText)); Changed(nameof(BurstBadgeBrush)); Changed(nameof(BurstBarBrush));
        Changed(nameof(BurstLine)); Changed(nameof(HasInfo));
    }
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
