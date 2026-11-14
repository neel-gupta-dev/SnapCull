namespace SnapCull.Core;

public static class PhotoFormats
{
    public static readonly IReadOnlySet<string> RawExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".cr2", ".cr3", ".crw",          // Canon
        ".nef", ".nrw",                  // Nikon
        ".arw", ".srf", ".sr2",          // Sony
        ".raf",                          // Fujifilm
        ".orf",                          // OM System / Olympus
        ".rw2", ".rwl", ".raw",          // Panasonic, Leica
        ".dng",                          // Adobe DNG (Leica, Pentax, Ricoh, Sigma, phones, drones…)
        ".gpr",                          // GoPro
        ".pef", ".ptx",                  // Pentax
        ".srw",                          // Samsung
        ".3fr", ".fff",                  // Hasselblad
        ".iiq", ".cap",                  // Phase One
        ".mrw",                          // Minolta
        ".x3f",                          // Sigma
        ".erf",                          // Epson
        ".kdc", ".dcr", ".dcs",          // Kodak
        ".mos",                          // Leaf
        ".mef",                          // Mamiya
        ".bay",                          // Casio
    };

    public static readonly IReadOnlySet<string> JpegExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".jpe",
    };

    /// <summary>
    /// Ready-to-view images other than JPEG. HEIC/HEIF/AVIF need Microsoft's free "HEIF Image Extensions"
    /// (and "AV1 Video Extension" for AVIF) from the Microsoft Store; Windows 11 usually has them.
    /// </summary>
    public static readonly IReadOnlySet<string> OtherImageExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".heic", ".heif", ".hif", ".avif", ".png", ".tif", ".tiff", ".webp",
    };

    public static bool IsRaw(string path) => RawExtensions.Contains(Path.GetExtension(path));
    public static bool IsJpeg(string path) => JpegExtensions.Contains(Path.GetExtension(path));
    public static bool IsOtherImage(string path) => OtherImageExtensions.Contains(Path.GetExtension(path));
    /// <summary>A finished picture (JPEG, HEIF, PNG…) rather than RAW sensor data.</summary>
    public static bool IsImage(string path) => IsJpeg(path) || IsOtherImage(path);
    public static bool IsSupported(string path) => IsRaw(path) || IsImage(path);
}

/// <summary>
/// One photo as the photographer thinks of it: a RAW file, an image (JPEG, HEIF…), or a RAW + image pair
/// with the same name, as cameras write when shooting RAW+JPEG. Both files share one .xmp sidecar.
/// </summary>
public sealed class PhotoFiles
{
    public PhotoFiles(string folder, string baseName)
    {
        Folder = folder;
        BaseName = baseName;
    }

    public string Folder { get; }
    public string BaseName { get; }
    public string? RawPath { get; internal set; }
    /// <summary>The camera-rendered image: JPEG, HEIF, PNG…</summary>
    public string? ImagePath { get; internal set; }

    public string PrimaryPath => RawPath ?? ImagePath ?? throw new InvalidOperationException("Photo has no files.");
    public string SidecarPath => Path.Combine(Folder, BaseName + ".xmp");
    public bool IsPair => RawPath is not null && ImagePath is not null;

    public IEnumerable<string> MediaFiles()
    {
        if (RawPath is not null) yield return RawPath;
        if (ImagePath is not null) yield return ImagePath;
    }

    public static PhotoFiles FromPath(string path)
    {
        var files = new PhotoFiles(Path.GetDirectoryName(path) ?? "", Path.GetFileNameWithoutExtension(path));
        if (PhotoFormats.IsRaw(path)) files.RawPath = path; else files.ImagePath = path;
        return files;
    }

    public override string ToString() => PrimaryPath;
}

public static class FolderScanner
{
    /// <summary>
    /// Finds RAW and image files in a folder, pairing files in the same folder that share a name.
    /// Hidden and system folders are skipped when <paramref name="includeSubfolders"/> is on.
    /// </summary>
    public static List<PhotoFiles> Scan(string folder, bool includeSubfolders = false)
    {
        var groups = new Dictionary<string, PhotoFiles>(StringComparer.OrdinalIgnoreCase);
        var extras = new List<PhotoFiles>();
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = includeSubfolders,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
        };

        foreach (var path in Directory.EnumerateFiles(folder, "*", options))
        {
            var name = Path.GetFileName(path);
            if (name.StartsWith("._", StringComparison.Ordinal)) continue; // macOS metadata files on shared cards
            bool raw = PhotoFormats.IsRaw(path), image = PhotoFormats.IsImage(path);
            if (!raw && !image) continue;

            var dir = Path.GetDirectoryName(path)!;
            var baseName = Path.GetFileNameWithoutExtension(path);
            var key = Path.Combine(dir, baseName);
            if (!groups.TryGetValue(key, out var group))
                groups[key] = group = new PhotoFiles(dir, baseName);

            if (raw && group.RawPath is null) group.RawPath = path;
            else if (image && group.ImagePath is null) group.ImagePath = path;
            else extras.Add(PhotoFiles.FromPath(path)); // e.g. IMG_1.CR3 and IMG_1.DNG side by side
        }

        return groups.Values.Concat(extras)
            .OrderBy(g => g.Folder, StringComparer.OrdinalIgnoreCase)
            .ThenBy(g => g.BaseName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(g => g.PrimaryPath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
