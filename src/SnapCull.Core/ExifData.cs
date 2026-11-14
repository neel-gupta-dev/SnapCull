using System.Globalization;

namespace SnapCull.Core;

/// <summary>The handful of EXIF fields a photographer wants to see while culling.</summary>
public sealed class ExifData
{
    public string? Make { get; set; }
    public string? Model { get; set; }
    public string? LensModel { get; set; }
    public DateTime? DateTaken { get; set; }
    /// <summary>Exposure time in seconds.</summary>
    public double? ExposureTime { get; set; }
    public double? FNumber { get; set; }
    public int? Iso { get; set; }
    /// <summary>Focal length in millimetres.</summary>
    public double? FocalLength { get; set; }
    /// <summary>EXIF orientation, 1–8. 1 means "no rotation needed".</summary>
    public int Orientation { get; set; } = 1;
    /// <summary>Where the photo was taken, in decimal degrees (south and west are negative), if the camera recorded it.</summary>
    public double? GpsLatitude { get; set; }
    public double? GpsLongitude { get; set; }
    /// <summary>Metres above sea level (negative below).</summary>
    public double? GpsAltitude { get; set; }
    public bool HasGps => GpsLatitude is not null && GpsLongitude is not null;

    /// <summary>A copy, so callers can change fields (e.g. drop GPS) without touching the original.</summary>
    public ExifData Clone() => (ExifData)MemberwiseClone();

    /// <summary>
    /// "Canon EOS R6", "SONY ILCE-7M4", "OLYMPUS E-M1MarkII" — drops company suffixes such as "CORPORATION"
    /// and avoids repeating the brand when the model already includes it.
    /// </summary>
    public string? CameraText
    {
        get
        {
            var brand = Brand(Make);
            if (string.IsNullOrWhiteSpace(Model)) return brand;
            if (string.IsNullOrWhiteSpace(brand)) return Model;
            var firstWord = brand.Split(' ')[0];
            return Model.StartsWith(firstWord, StringComparison.OrdinalIgnoreCase) ? Model : $"{brand} {Model}";
        }
    }

    static readonly string[] CompanySuffixes =
        ["CORPORATION", "CORP.", "CORP", "IMAGING", "CAMERA", "AG", "TECHWIN", "ELECTRONICS", "CO.,LTD.", "CO.,", "CO.", "LTD.", "LTD", "INC.", "INC", "GMBH"];

    /// <summary>"NIKON CORPORATION" → "NIKON", "OLYMPUS IMAGING CORP." → "OLYMPUS", "LEICA CAMERA AG" → "LEICA".</summary>
    static string? Brand(string? make)
    {
        if (string.IsNullOrWhiteSpace(make)) return null;
        if (make.StartsWith("OM Digital Solutions", StringComparison.OrdinalIgnoreCase)) return "OM System";
        var words = make.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        while (words.Count > 1 && CompanySuffixes.Contains(words[^1].TrimEnd(','), StringComparer.OrdinalIgnoreCase))
            words.RemoveAt(words.Count - 1);
        return string.Join(' ', words).TrimEnd(',');
    }

    public string? ShutterText => ExposureTime is double t && t > 0 ? FormatShutter(t) : null;

    /// <summary>"50 mm  ·  f/2.8  ·  1/250 s  ·  ISO 400", or null when nothing is known.</summary>
    public string? SettingsText
    {
        get
        {
            var parts = new List<string>(4);
            if (FocalLength is double fl && fl > 0) parts.Add(Invariant($"{fl:0.#} mm"));
            if (FNumber is double f && f > 0) parts.Add(Invariant($"f/{f:0.#}"));
            if (ShutterText is string sh) parts.Add(sh);
            if (Iso is int iso && iso > 0) parts.Add(Invariant($"ISO {iso}"));
            return parts.Count == 0 ? null : string.Join("  ·  ", parts);
        }
    }

    public static string FormatShutter(double seconds)
    {
        if (seconds >= 0.3) return Invariant($"{seconds:0.#} s");
        return Invariant($"1/{Math.Round(1 / seconds):0} s");
    }

    /// <summary>Fills in any field this instance doesn't have yet from <paramref name="other"/>.</summary>
    public void MergeFrom(ExifData other)
    {
        Make ??= other.Make;
        Model ??= other.Model;
        LensModel ??= other.LensModel;
        DateTaken ??= other.DateTaken;
        ExposureTime ??= other.ExposureTime;
        FNumber ??= other.FNumber;
        Iso ??= other.Iso;
        FocalLength ??= other.FocalLength;
        if (Orientation == 1) Orientation = other.Orientation;
        if (!HasGps && other.HasGps)
        {
            GpsLatitude = other.GpsLatitude;
            GpsLongitude = other.GpsLongitude;
            GpsAltitude = other.GpsAltitude;
        }
    }

    static string Invariant(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
}
