using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SkiaSharp;
using SnapCull.Core;

namespace SnapCull;

public sealed class ConvertResult
{
    public int Converted { get; set; }
    public List<string> Errors { get; } = [];
    /// <summary>RAW files where a full render was wanted but the Windows RAW codec couldn't do it, so the camera preview was used.</summary>
    public int UsedPreviewInstead { get; set; }
    public string? FirstOutput { get; set; }
}

/// <summary>
/// Converts photos to JPEG, PNG, TIFF, WebP or HEIC. The originals are never changed and existing files are never overwritten.
/// Thread-safe: photos are converted in parallel.
/// </summary>
public static class PhotoConverter
{
    /// <summary>Embedded previews at least this big are used as-is in Auto mode; smaller ones get a full RAW render.</summary>
    const int GoodPreviewLongSide = 2000;
    static readonly Guid HeifContainer = new("E1E62521-6787-405B-A339-500715B5763F");
    static readonly Lazy<bool> HeicEncoder = new(ProbeHeicEncoder);

    /// <summary>True when Windows can write HEIC (needs the HEIF and HEVC video extensions from the Microsoft Store).</summary>
    public static bool CanWriteHeic => HeicEncoder.Value;

    public static ConvertResult Run(IReadOnlyList<(PhotoFiles Files, ExifData? Exif)> photos, ConvertOptions options,
        IProgress<int>? progress = null, CancellationToken ct = default)
    {
        var result = new ConvertResult();
        Directory.CreateDirectory(options.Destination);

        // Decide every output name up front, in order, so {seq} numbers and "(1)" suffixes are predictable.
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var jobs = photos.Select((p, i) =>
        {
            var exif = p.Exif ?? SafeReadExif(p.Files);
            var name = ConvertPlan.FileName(options.NamePattern, p.Files, exif, i + 1, options.Format);
            return (p.Files, Exif: exif, Target: ConvertPlan.UniquePath(options.Destination, name, reserved));
        }).ToList();

        int done = 0;
        var gate = new Lock();
        // Full-resolution RAW renders use a lot of memory, so convert fewer of them at once.
        int parallel = options.RawSource == RawSource.FullRender ? 2 : Math.Clamp(Environment.ProcessorCount / 2, 1, 4);
        try
        {
            Parallel.ForEach(jobs, new ParallelOptions { MaxDegreeOfParallelism = parallel, CancellationToken = ct }, job =>
            {
                try
                {
                    bool fellBack = ConvertOne(job.Files, job.Exif, options, job.Target);
                    lock (gate)
                    {
                        result.Converted++;
                        if (fellBack) result.UsedPreviewInstead++;
                        if (result.FirstOutput is null || string.CompareOrdinal(job.Target, result.FirstOutput) < 0) result.FirstOutput = job.Target;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Debug.WriteLine($"SnapCull: convert failed for {job.Files.PrimaryPath}: {ex}");
                    lock (gate) result.Errors.Add($"{Path.GetFileName(job.Files.PrimaryPath)}: {ex.Message}");
                }
                progress?.Report(Interlocked.Increment(ref done));
            });
        }
        catch (OperationCanceledException) { }
        return result;
    }

    static ExifData? SafeReadExif(PhotoFiles files)
    {
        try { return PreviewExtractor.ReadExif(files.ImagePath ?? files.PrimaryPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Converts one photo to <paramref name="target"/>. Returns true if a wanted RAW render fell back to the preview.</summary>
    public static bool ConvertOne(PhotoFiles files, ExifData? exif, ConvertOptions options, string target)
    {
        var (bitmap, sourceExif, fellBack) = LoadSource(files, options);
        var metadata = (exif ?? sourceExif).Clone();
        var bytes = Encode(bitmap, metadata, options);

        // Write to a temporary name first, so a half-written file never appears under the real name.
        var temp = target + ".snapcull-tmp";
        File.WriteAllBytes(temp, bytes);
        try
        {
            File.Move(temp, target, overwrite: false);
        }
        catch (IOException) when (File.Exists(target))
        {
            // Something else created that name meanwhile: pick the next free one rather than overwrite it.
            File.Move(temp, ConvertPlan.UniquePath(Path.GetDirectoryName(target)!, Path.GetFileName(target)), overwrite: false);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
        return fellBack;
    }

    /// <summary>Gets the pixels to convert, already the right way up and at the output size.</summary>
    static (BitmapSource Bitmap, ExifData Exif, bool FellBack) LoadSource(PhotoFiles files, ConvertOptions options)
    {
        bool wantHighBitDepth = options.Format == ConvertFormat.Tiff && options.Tiff16Bit;

        // A camera JPEG/HEIF next to the RAW is the camera's own full-size rendering: use it unless a RAW render was asked for.
        if (files.ImagePath is { } image && (files.RawPath is null || options.RawSource != RawSource.FullRender))
            return (FromPreview(PreviewExtractor.Extract(image, PreviewSize.Largest), options, image), Exif(image), false);

        var raw = files.RawPath!;
        var preview = PreviewExtractor.Extract(raw, PreviewSize.Largest);
        int previewLongSide = preview is null ? 0 : Math.Max(ImageDecoder.SizeOf(preview).Width, ImageDecoder.SizeOf(preview).Height);
        bool render = options.RawSource == RawSource.FullRender
                      || (options.RawSource == RawSource.Auto && previewLongSide < GoodPreviewLongSide);

        if (render)
        {
            var rendered = ImageDecoder.RenderRaw(raw, full => ConvertPlan.TargetLongSide(full, options), preview?.Exif, wantHighBitDepth);
            // Some formats (e.g. Leaf MOS) come back black from the codec; then the camera preview is the better choice.
            if (rendered is not null && (preview is null || !ImageDecoder.IsNearlyBlack(rendered.Bitmap)))
                return (rendered.Bitmap, rendered.Exif, false);
        }
        if (preview is null) throw new InvalidDataException("No preview in this file, and Windows couldn't render it.");
        return (FromPreview(preview, options, raw), preview.Exif, render);

        static ExifData Exif(string path) => PreviewExtractor.ReadExif(path) ?? new ExifData();
    }

    static BitmapSource FromPreview(PreviewResult? preview, ConvertOptions options, string path)
    {
        if (preview is null) throw new InvalidDataException($"Couldn't read {Path.GetFileName(path)}.");
        var (w, h) = ImageDecoder.SizeOf(preview);
        int longSide = Math.Max(w, h);
        int target = ConvertPlan.TargetLongSide(longSide, options);
        return ImageDecoder.Decode(preview, target < longSide ? target : null);
    }

    static byte[] Encode(BitmapSource bitmap, ExifData exif, ConvertOptions options)
    {
        bool keep = options.KeepMetadata;
        bool gps = !options.RemoveGps;
        var tiffExif = keep ? ExifWriter.Build(exif, includeGps: gps) : null;

        switch (options.Format)
        {
            case ConvertFormat.Jpeg:
            {
                var bytes = Save(new JpegBitmapEncoder { QualityLevel = Math.Clamp(options.Quality, 1, 100) }, To(bitmap, PixelFormats.Bgr24));
                return tiffExif is null ? bytes : MetadataEmbedder.EmbedInJpeg(bytes, tiffExif);
            }
            case ConvertFormat.Png:
            {
                var bytes = Save(new PngBitmapEncoder(), To(bitmap, PixelFormats.Bgr24));
                return tiffExif is null ? bytes : MetadataEmbedder.EmbedInPng(bytes, tiffExif);
            }
            case ConvertFormat.Tiff:
            {
                var pixels = options.Tiff16Bit && bitmap.Format.BitsPerPixel >= 48 ? To(bitmap, PixelFormats.Rgb48) : To(bitmap, PixelFormats.Bgr24);
                var encoder = new TiffBitmapEncoder { Compression = TiffCompressOption.Zip };
                encoder.Frames.Add(BitmapFrame.Create(pixels, null, keep ? TiffMetadata(exif, gps) : null, null));
                return Save(encoder);
            }
            case ConvertFormat.WebP:
            {
                var bytes = EncodeWebp(bitmap, options.Quality);
                return tiffExif is null ? bytes : MetadataEmbedder.EmbedInWebp(bytes, tiffExif, bitmap.PixelWidth, bitmap.PixelHeight);
            }
            case ConvertFormat.Heic:
            {
                if (!CanWriteHeic) throw new NotSupportedException("Windows can't write HEIC on this PC. Install “HEIF Image Extensions” and “HEVC Video Extensions” from the Microsoft Store.");
                return Save(BitmapEncoder.Create(HeifContainer), To(bitmap, PixelFormats.Bgr32));
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(options));
        }
    }

    static BitmapSource To(BitmapSource source, PixelFormat format)
    {
        if (source.Format == format) return source;
        var converted = new FormatConvertedBitmap(source, format, null, 0);
        converted.Freeze();
        return converted;
    }

    static byte[] Save(BitmapEncoder encoder, BitmapSource? frame = null)
    {
        if (frame is not null) encoder.Frames.Add(BitmapFrame.Create(frame));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    static byte[] EncodeWebp(BitmapSource bitmap, int quality)
    {
        var bgra = To(bitmap, PixelFormats.Bgra32);
        int w = bgra.PixelWidth, h = bgra.PixelHeight, stride = w * 4;
        var pixels = new byte[stride * h];
        bgra.CopyPixels(pixels, stride, 0);
        var info = new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using var image = SKImage.FromPixelCopy(info, pixels, stride);
        using var data = image.Encode(SKEncodedImageFormat.Webp, Math.Clamp(quality, 1, 100));
        return data.ToArray();
    }

    /// <summary>Camera info for TIFF output, through Windows' own TIFF metadata writer.</summary>
    static BitmapMetadata TiffMetadata(ExifData exif, bool includeGps)
    {
        var m = new BitmapMetadata("tiff");
        void Set(string query, object? value)
        {
            if (value is null) return;
            try { m.SetQuery(query, value); } catch (Exception ex) when (ex is ArgumentException or NotSupportedException or InvalidOperationException) { }
        }
        // EXIF rationals are passed as a 64-bit number: numerator in the low half, denominator in the high half.
        static ulong Rational(double value, uint den) => (ulong)den << 32 | (uint)Math.Round(value * den);
        static ulong Exposure(double s) => s < 1 ? (ulong)(uint)Math.Round(1 / s) << 32 | 1u : (10ul << 32) | (uint)Math.Round(s * 10);

        Set("/ifd/{ushort=271}", exif.Make);
        Set("/ifd/{ushort=272}", exif.Model);
        Set("/ifd/{ushort=274}", (ushort)1);
        Set("/ifd/{ushort=305}", "SnapCull");
        Set("/ifd/{ushort=306}", exif.DateTaken?.ToString("yyyy:MM:dd HH:mm:ss"));
        Set("/ifd/exif/{ushort=36867}", exif.DateTaken?.ToString("yyyy:MM:dd HH:mm:ss"));
        if (exif.ExposureTime is double t && t > 0) Set("/ifd/exif/{ushort=33434}", Exposure(t));
        if (exif.FNumber is double f && f > 0) Set("/ifd/exif/{ushort=33437}", Rational(f, 10));
        if (exif.Iso is int iso && iso > 0) Set("/ifd/exif/{ushort=34855}", (ushort)Math.Min(iso, ushort.MaxValue));
        if (exif.FocalLength is double fl && fl > 0) Set("/ifd/exif/{ushort=37386}", Rational(fl, 10));
        Set("/ifd/exif/{ushort=42036}", exif.LensModel);
        if (includeGps && exif.HasGps)
        {
            static ulong[] Dms(double deg)
            {
                deg = Math.Abs(deg);
                int d = (int)deg; double minTotal = (deg - d) * 60; int min = (int)minTotal;
                return [Rational(d, 1), Rational(min, 1), Rational((minTotal - min) * 60, 10000)];
            }
            Set("/ifd/gps/{ushort=1}", exif.GpsLatitude < 0 ? "S" : "N");
            Set("/ifd/gps/{ushort=2}", Dms(exif.GpsLatitude!.Value));
            Set("/ifd/gps/{ushort=3}", exif.GpsLongitude < 0 ? "W" : "E");
            Set("/ifd/gps/{ushort=4}", Dms(exif.GpsLongitude!.Value));
        }
        return m;
    }

    static bool ProbeHeicEncoder()
    {
        try
        {
            var tiny = BitmapSource.Create(16, 16, 96, 96, PixelFormats.Bgr32, null, new byte[16 * 16 * 4], 16 * 4);
            return Save(BitmapEncoder.Create(HeifContainer), tiny).Length > 0;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"SnapCull: no HEIC encoder: {ex.Message}");
            return false;
        }
    }
}
