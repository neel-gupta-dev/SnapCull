// SnapCull probe: checks that previews and camera info can be read from every photo in a folder.
//
//   dotnet run --project tools/SnapCull.Probe -- <folder> [--focus]
//
// --focus also prints sharpness scores for each photo and for artificially blurred copies,
// which is how the focus-check thresholds were calibrated.

using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SnapCull;
using SnapCull.Core;

if (args.Length == 0 || !Directory.Exists(args[0]))
{
    Console.WriteLine("Usage: SnapCull.Probe <folder> [--focus]");
    return 2;
}
bool focus = args.Contains("--focus");

// --convert <jpeg|png|tiff|webp|heic> <out-folder> [--long-edge N] [--raw auto|preview|render]: convert every photo with the app's converter.
if (args.Contains("--convert"))
{
    int at = Array.IndexOf(args, "--convert");
    var format = Enum.Parse<ConvertFormat>(args[at + 1], ignoreCase: true);
    var outDir = args[at + 2];
    int longEdge = args.Contains("--long-edge") ? int.Parse(args[Array.IndexOf(args, "--long-edge") + 1]) : 0;
    var rawSource = args.Contains("--raw") ? args[Array.IndexOf(args, "--raw") + 1] switch { "preview" => RawSource.Preview, "render" => RawSource.FullRender, _ => RawSource.Auto } : RawSource.Auto;
    var options = new ConvertOptions
    {
        Format = format, Destination = outDir, RawSource = rawSource, Tiff16Bit = args.Contains("--16bit"),
        Resize = longEdge > 0 ? ConvertResize.LongEdge : ConvertResize.Original, ResizeValue = longEdge,
    };
    var photos = Directory.EnumerateFiles(args[0]).Where(PhotoFormats.IsSupported).OrderBy(f => f)
        .Select(f => (PhotoFiles.FromPath(f), (ExifData?)null)).ToList();
    var timer = Stopwatch.StartNew();
    var converted = PhotoConverter.Run(photos, options);
    Console.WriteLine($"Converted {converted.Converted} of {photos.Count} to {format} in {timer.Elapsed.TotalSeconds:0.0} s; " +
                      $"{converted.UsedPreviewInstead} used the preview instead of a RAW render.");
    foreach (var error in converted.Errors) Console.WriteLine("  FAILED " + error);
    Console.WriteLine($"HEIC encoder available: {PhotoConverter.CanWriteHeic}");
    return converted.Errors.Count == 0 ? 0 : 1;
}

// --sheet <out.png>: render every photo the way the app shows it into one contact sheet, to eyeball rotation and colours.
if (args.Contains("--sheet"))
{
    var output = args[Array.IndexOf(args, "--sheet") + 1];
    var photos = Directory.EnumerateFiles(args[0]).Where(PhotoFormats.IsSupported).OrderBy(f => f).ToList();
    Exception? failure = null;
    var thread = new Thread(() =>
    {
        try { ContactSheet.Render(photos, output); }
        catch (Exception ex) { failure = ex; }
    });
    thread.SetApartmentState(ApartmentState.STA); // WPF drawing needs an STA thread
    thread.Start();
    thread.Join();
    if (failure is not null) { Console.WriteLine(failure); return 1; }
    Console.WriteLine($"Wrote {output}");
    return 0;
}

// --ifd <file>: list the image directories inside a TIFF-based RAW file (for adding support for new cameras).
if (args.Contains("--ifd"))
{
    foreach (var file in Directory.EnumerateFiles(args[0], args[Array.IndexOf(args, "--ifd") + 1]))
    {
        Console.WriteLine(Path.GetFileName(file));
        using var fs = File.OpenRead(file);
        var tiff = TiffParser.Parse(fs, 0);
        if (tiff is null) { Console.WriteLine("  not a TIFF-based file"); continue; }
        foreach (var ifd in tiff.Ifds) Console.WriteLine("  " + ifd);
        foreach (var r in tiff.JpegRanges) Console.WriteLine($"  JPEG candidate @{r.Offset} ({r.Length:N0} bytes)");
    }
    return 0;
}

var files = Directory.EnumerateFiles(args[0], "*", SearchOption.AllDirectories)
    .Where(PhotoFormats.IsSupported)
    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
    .ToList();

int failures = 0;
Console.WriteLine($"{"File",-58} {"Preview",-11} {"Thumb",-9} {"ms",5}  {"Camera / lens / settings"}");
foreach (var path in files)
{
    var name = Path.GetFileName(path);
    if (name.Length > 57) name = name[..54] + "...";
    var sw = Stopwatch.StartNew();
    PreviewResult? large = null, thumb = null;
    string? error = null;
    BitmapSource? bitmap = null;
    bool viaCodec = false;
    try
    {
        large = PreviewExtractor.Extract(path, PreviewSize.Largest);
        thumb = PreviewExtractor.Extract(path, PreviewSize.Thumbnail);
        if (large is not null) bitmap = ImageDecoder.Decode(large, 1024); // proves Windows can decode it
    }
    catch (Exception ex) { error = ex.GetType().Name + ": " + ex.Message; }

    // What the app actually shows: falls back to Windows' RAW codec when the preview is missing or tiny.
    var shown = ImageDecoder.TryLoad(PhotoFiles.FromPath(path), PreviewSize.Largest, null);
    sw.Stop();
    if (shown is { FromWindowsCodec: true })
    {
        viaCodec = true;
        bitmap = shown.Bitmap;
        large ??= new PreviewResult([], 0, 0, shown.Exif);
    }

    if (large is null || bitmap is null)
    {
        failures++;
        Console.WriteLine($"{name,-58} {"FAILED",-11} {"",-9} {sw.ElapsedMilliseconds,5}  {error ?? "no preview found, and Windows can't decode it"}");
        continue;
    }

    var e = large.Exif;
    var size = viaCodec ? $"{bitmap.PixelWidth}x{bitmap.PixelHeight}W"
             : large.Width > 0 ? $"{large.Width}x{large.Height}" : $"{bitmap.PixelWidth}x{bitmap.PixelHeight}*";
    var thumbSize = thumb is null ? "-" : thumb.Width > 0 ? $"{thumb.Width}x{thumb.Height}" : "full";
    var info = string.Join(" | ", new[] { e.CameraText, e.LensModel, e.SettingsText, e.DateTaken?.ToString("yyyy-MM-dd HH:mm"), e.Orientation != 1 ? $"orient {e.Orientation}" : null }
        .Where(s => !string.IsNullOrEmpty(s)));
    var warn = Math.Max(bitmap.PixelWidth, bitmap.PixelHeight) < 1000 ? "  [SMALL PREVIEW]" : "";
    Console.WriteLine($"{name,-58} {size,-11} {thumbSize,-9} {sw.ElapsedMilliseconds,5}  {info}{warn}");

    if (focus)
    {
        var (gray, gw, gh, gstride) = ImageDecoder.Pixels(bitmap, 1024, PixelFormats.Gray8);
        var sharp = FocusAnalyzer.Score(gray, gw, gh, gstride);
        // Blur the analysis image itself (3 box-blur passes ≈ Gaussian), mimicking missed focus or shake at viewing size.
        string Blurred(int radius) => FocusAnalyzer.Score(BoxBlur(gray, gw, gh, gstride, radius), gw, gh, gstride)?.ToString("0") ?? "-";
        Console.WriteLine($"{"",58}   focus: original {sharp?.ToString("0") ?? "-"}, slight blur {Blurred(1)}, blurry {Blurred(2)}, very blurry {Blurred(4)}");
    }
}

static byte[] BoxBlur(byte[] src, int w, int h, int stride, int radius)
{
    var a = (byte[])src.Clone();
    var b = new byte[a.Length];
    for (int pass = 0; pass < 3; pass++)
    {
        for (int y = 0; y < h; y++)          // horizontal
            for (int x = 0; x < w; x++)
            {
                int sum = 0, n = 0;
                for (int d = -radius; d <= radius; d++) { int xx = Math.Clamp(x + d, 0, w - 1); sum += a[y * stride + xx]; n++; }
                b[y * stride + x] = (byte)(sum / n);
            }
        for (int y = 0; y < h; y++)          // vertical
            for (int x = 0; x < w; x++)
            {
                int sum = 0, n = 0;
                for (int d = -radius; d <= radius; d++) { int yy = Math.Clamp(y + d, 0, h - 1); sum += b[yy * stride + x]; n++; }
                a[y * stride + x] = (byte)(sum / n);
            }
    }
    return a;
}

Console.WriteLine();
Console.WriteLine("* = size reported by the Windows codec   W = rendered from raw data by Windows' RAW codec (no usable embedded preview)");
Console.WriteLine($"{files.Count - failures} of {files.Count} files OK.");
return failures == 0 ? 0 : 1;
