using System.Globalization;
using System.Text;

namespace SnapCull.Core;

public enum ConvertFormat { Jpeg, Png, Tiff, WebP, Heic }

public enum ConvertResize { Original, LongEdge, Percent }

/// <summary>Where RAW pixels come from when converting.</summary>
public enum RawSource
{
    /// <summary>The camera's preview when it is big enough, otherwise a full RAW render.</summary>
    Auto,
    /// <summary>The JPEG preview stored in the RAW file: instant, camera colours.</summary>
    Preview,
    /// <summary>Render the raw sensor data with Windows' Raw Image Extension: slower, full resolution.</summary>
    FullRender,
}

public sealed record ConvertOptions
{
    public ConvertFormat Format { get; init; } = ConvertFormat.Jpeg;
    /// <summary>1–100, for JPEG, WebP and HEIC.</summary>
    public int Quality { get; init; } = 90;
    /// <summary>16 bits per channel for TIFF, when the source has that much (full RAW renders do).</summary>
    public bool Tiff16Bit { get; init; }
    public ConvertResize Resize { get; init; } = ConvertResize.Original;
    /// <summary>Pixels for <see cref="ConvertResize.LongEdge"/>, or a percentage for <see cref="ConvertResize.Percent"/>.</summary>
    public int ResizeValue { get; init; } = 2048;
    public RawSource RawSource { get; init; } = RawSource.Auto;
    public bool KeepMetadata { get; init; } = true;
    public bool RemoveGps { get; init; }
    /// <summary>File name pattern; see <see cref="ConvertPlan.Tokens"/>.</summary>
    public string NamePattern { get; init; } = "{name}";
    public string Destination { get; init; } = "";
}

/// <summary>The parts of a conversion that don't need any pixels: names, sizes and safe file paths.</summary>
public static class ConvertPlan
{
    /// <summary>Placeholders that can be used in <see cref="ConvertOptions.NamePattern"/>.</summary>
    public static readonly (string Token, string Meaning)[] Tokens =
    [
        ("{name}", "original file name"),
        ("{date}", "capture date, 2026-11-14"),
        ("{time}", "capture time, 173012"),
        ("{seq}", "number in this batch, 0001"),
        ("{camera}", "camera model"),
    ];

    public static string Extension(ConvertFormat format) => format switch
    {
        ConvertFormat.Jpeg => ".jpg",
        ConvertFormat.Png => ".png",
        ConvertFormat.Tiff => ".tif",
        ConvertFormat.WebP => ".webp",
        ConvertFormat.Heic => ".heic",
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    /// <summary>Formats that use the quality setting.</summary>
    public static bool HasQuality(ConvertFormat format) => format is ConvertFormat.Jpeg or ConvertFormat.WebP or ConvertFormat.Heic;

    /// <summary>Formats SnapCull can write camera info into.</summary>
    public static bool CanKeepMetadata(ConvertFormat format) => format is ConvertFormat.Jpeg or ConvertFormat.Png or ConvertFormat.WebP or ConvertFormat.Tiff;

    /// <summary>Builds a file name (without folder) from the pattern. Characters Windows doesn't allow are replaced.</summary>
    public static string FileName(string pattern, PhotoFiles photo, ExifData? exif, int sequence, ConvertFormat format)
    {
        if (string.IsNullOrWhiteSpace(pattern)) pattern = "{name}";
        var date = exif?.DateTaken;
        var name = new StringBuilder(pattern)
            .Replace("{name}", photo.BaseName)
            .Replace("{date}", date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "nodate")
            .Replace("{time}", date?.ToString("HHmmss", CultureInfo.InvariantCulture) ?? "000000")
            .Replace("{seq}", sequence.ToString("0000", CultureInfo.InvariantCulture))
            .Replace("{camera}", exif?.CameraText ?? "camera")
            .ToString();

        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        if (clean.Length == 0) clean = photo.BaseName;
        if (clean.Length > 180) clean = clean[..180];
        return clean + Extension(format);
    }

    /// <summary>A path in <paramref name="folder"/> that doesn't exist yet and isn't already planned: "name.jpg", then "name (1).jpg"…</summary>
    public static string UniquePath(string folder, string fileName, ISet<string>? reserved = null)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        var path = Path.Combine(folder, fileName);
        for (int n = 1; File.Exists(path) || (reserved?.Contains(path) ?? false); n++)
            path = Path.Combine(folder, $"{stem} ({n}){ext}");
        reserved?.Add(path);
        return path;
    }

    /// <summary>The long side, in pixels, to produce from a source whose long side is <paramref name="sourceLongSide"/>. Never enlarges.</summary>
    public static int TargetLongSide(int sourceLongSide, ConvertOptions options)
    {
        if (sourceLongSide <= 0) return 0;
        int target = options.Resize switch
        {
            ConvertResize.LongEdge => options.ResizeValue,
            ConvertResize.Percent => (int)Math.Round(sourceLongSide * options.ResizeValue / 100.0),
            _ => sourceLongSide,
        };
        return Math.Clamp(target, 1, sourceLongSide);
    }
}
