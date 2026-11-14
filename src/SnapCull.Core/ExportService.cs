namespace SnapCull.Core;

public enum ExportMode { Copy, Move }

public sealed record ExportRequest(IReadOnlyList<PhotoFiles> Photos, string Destination, ExportMode Mode);

public sealed class ExportResult
{
    public int Exported { get; set; }
    /// <summary>Photos already at the destination with identical files.</summary>
    public int Skipped { get; set; }
    public List<string> Errors { get; } = [];
    public List<PhotoFiles> Succeeded { get; } = [];
}

/// <summary>Copies or moves photos (RAW, paired JPEG and .xmp sidecar together) into another folder.</summary>
public static class ExportService
{
    public static ExportResult Run(ExportRequest request, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        var result = new ExportResult();
        Directory.CreateDirectory(request.Destination);
        int done = 0;

        foreach (var photo in request.Photos)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var sources = photo.MediaFiles().ToList();
                bool hasSidecar = File.Exists(photo.SidecarPath);
                if (hasSidecar) sources.Add(photo.SidecarPath);

                var name = ChooseTargetName(photo, sources, request.Destination, out bool alreadyThere);
                if (alreadyThere)
                {
                    // Same photo exported before: just refresh its ratings.
                    if (hasSidecar && request.Mode == ExportMode.Copy)
                        File.Copy(photo.SidecarPath, Target(request.Destination, name, photo.SidecarPath), overwrite: true);
                    result.Skipped++;
                }
                else
                {
                    foreach (var source in sources)
                    {
                        var target = Target(request.Destination, name, source);
                        if (request.Mode == ExportMode.Copy) File.Copy(source, target, overwrite: false);
                        else File.Move(source, target, overwrite: false);
                    }
                    result.Exported++;
                    result.Succeeded.Add(photo);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                result.Errors.Add($"{photo.BaseName}: {ex.Message}");
            }
            progress?.Report(++done);
        }
        return result;
    }

    static string Target(string folder, string baseName, string source) =>
        Path.Combine(folder, baseName + Path.GetExtension(source));

    /// <summary>
    /// Picks a base name so every file of the photo lands without overwriting anything.
    /// If the same files are already there, reports that instead of making a duplicate.
    /// </summary>
    static string ChooseTargetName(PhotoFiles photo, List<string> sources, string folder, out bool alreadyThere)
    {
        alreadyThere = false;
        if (sources.All(s => !File.Exists(Target(folder, photo.BaseName, s)))) return photo.BaseName;

        alreadyThere = photo.MediaFiles().All(s =>
        {
            var t = new FileInfo(Target(folder, photo.BaseName, s));
            return t.Exists && t.Length == new FileInfo(s).Length;
        });
        if (alreadyThere) return photo.BaseName;

        for (int n = 1; ; n++)
        {
            var name = $"{photo.BaseName} ({n})";
            if (sources.All(s => !File.Exists(Target(folder, name, s)))) return name;
        }
    }
}
