using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SnapCull.Core;

namespace SnapCull;

/// <summary>Turns a photo's embedded preview into a WPF bitmap, rotated the right way up. Thread-safe.</summary>
public static class ImageDecoder
{
    /// <param name="FromWindowsCodec">True when the picture was rendered from the raw data by Windows' RAW codec instead of an embedded preview.</param>
    public sealed record Loaded(BitmapSource Bitmap, ExifData Exif, bool FromWindowsCodec = false);

    /// <summary>Embedded previews smaller than this are replaced by a Windows RAW codec rendering for the main view, when possible.</summary>
    const int MinGoodPreviewSide = 1000;

    /// <summary>Set when the last failure looked like a missing Windows codec (HEIF/AVIF extensions).</summary>
    public static string? MissingCodecHint(PhotoFiles files)
    {
        var ext = Path.GetExtension(files.ImagePath ?? "").ToLowerInvariant();
        return ext switch
        {
            ".heic" or ".heif" or ".hif" => "Install Microsoft's free “HEIF Image Extensions” from the Microsoft Store to view HEIF/HEIC photos.",
            ".avif" => "Install Microsoft's free “AV1 Video Extension” from the Microsoft Store to view AVIF photos.",
            _ => null,
        };
    }

    /// <param name="maxLongSide">Decode at most this many pixels on the long side (much faster for big JPEGs). Null = full size.</param>
    public static Loaded? TryLoad(PhotoFiles files, PreviewSize size, int? maxLongSide)
    {
        Loaded? found = null;
        foreach (var path in SourceOrder(files, size))
        {
            try
            {
                var preview = PreviewExtractor.Extract(path, size);
                if (preview is null) continue;
                found = new Loaded(Decode(preview, maxLongSide), preview.Exif);
                break;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"SnapCull: could not load {path}: {ex}");
            }
        }

        // Some RAW files (many phone DNGs, cinema DNGs, Kodak, Leica M8…) carry no usable preview, or only a tiny one.
        // Windows' free Raw Image Extension can render the raw data itself: slower, but much better than nothing.
        if (files.RawPath is { } raw && files.ImagePath is null)
        {
            int have = found is null ? 0 : Math.Max(found.Bitmap.PixelWidth, found.Bitmap.PixelHeight);
            int want = Math.Min(maxLongSide ?? int.MaxValue, MinGoodPreviewSide);
            if (found is null || (size == PreviewSize.Largest && have < want))
            {
                var rendered = DecodeWithWindowsCodec(raw, maxLongSide, found?.Exif);
                // Some formats (e.g. Leaf MOS) come back black from the codec; a small real preview beats that.
                if (rendered is not null && (found is null || !IsNearlyBlack(rendered.Bitmap))) return rendered;
            }
        }
        return found;
    }

    static bool IsNearlyBlack(BitmapSource bitmap)
    {
        var (pixels, w, h, stride) = Pixels(bitmap, 64, PixelFormats.Gray8);
        long sum = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++) sum += pixels[y * stride + x];
        return sum < (long)w * h * 6; // average below 6 out of 255
    }

    /// <summary>Renders a RAW file with whatever Windows codec is installed (the Raw Image Extension, for RAW formats).</summary>
    static Loaded? DecodeWithWindowsCodec(string path, int? maxLongSide, ExifData? exif)
    {
        try
        {
            exif ??= PreviewExtractor.ReadExif(path) ?? new ExifData();
            int width, height;
            using (var probe = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                var frame = BitmapDecoder.Create(probe, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile,
                    BitmapCacheOption.None).Frames[0];
                (width, height) = (frame.PixelWidth, frame.PixelHeight);
            }

            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(path);
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile | BitmapCreateOptions.IgnoreImageCache;
            if (maxLongSide is int max && Math.Max(width, height) > max)
            {
                if (width >= height) image.DecodePixelWidth = max; else image.DecodePixelHeight = max;
            }
            image.EndInit();
            image.Freeze();

            // The codec may hand back 16-bit-per-channel pixels; 8-bit is plenty for viewing and uses far less memory.
            BitmapSource result = image;
            if (image.Format.BitsPerPixel > 32)
            {
                var converted = new WriteableBitmap(new FormatConvertedBitmap(image, PixelFormats.Bgr32, null, 0));
                converted.Freeze();
                result = converted;
            }
            return new Loaded(result, exif, FromWindowsCodec: true);
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or IOException or UnauthorizedAccessException
                                   or ArgumentException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            Debug.WriteLine($"SnapCull: Windows codec could not decode {path}: {ex.Message}");
            return null;
        }
    }

    // For a RAW + JPEG pair, the camera JPEG is the best full-size view; the RAW's small preview is the cheapest thumbnail.
    static IEnumerable<string> SourceOrder(PhotoFiles f, PreviewSize size)
    {
        var (first, second) = size == PreviewSize.Thumbnail ? (f.RawPath, f.ImagePath) : (f.ImagePath, f.RawPath);
        if (first is not null) yield return first;
        if (second is not null) yield return second;
    }

    public static BitmapSource Decode(PreviewResult preview, int? maxLongSide)
    {
        int width = preview.Width, height = preview.Height;
        if (width == 0 || height == 0)
        {
            // Non-JPEG formats: ask the Windows codec for the size without decoding the pixels.
            using var probe = new MemoryStream(preview.Image, writable: false);
            var frame = BitmapDecoder.Create(probe, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile,
                BitmapCacheOption.None).Frames[0];
            width = frame.PixelWidth;
            height = frame.PixelHeight;
        }

        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        image.StreamSource = new MemoryStream(preview.Image, writable: false);
        if (maxLongSide is int max)
        {
            if (width >= height) { if (width > max) image.DecodePixelWidth = max; }
            else if (height > max) image.DecodePixelHeight = max;
        }
        int orientation = preview.Exif.Orientation;
        image.Rotation = orientation switch
        {
            3 => Rotation.Rotate180,
            6 => Rotation.Rotate90,
            8 => Rotation.Rotate270,
            _ => Rotation.Rotate0,
        };
        image.EndInit();
        image.Freeze();

        // Mirrored orientations (2, 4, 5, 7) are rare in camera files but handled for completeness.
        Transform? mirror = orientation switch
        {
            2 => new ScaleTransform(-1, 1),
            4 => new ScaleTransform(1, -1),
            5 => new TransformGroup { Children = { new RotateTransform(90), new ScaleTransform(-1, 1) } },
            7 => new TransformGroup { Children = { new RotateTransform(270), new ScaleTransform(-1, 1) } },
            _ => null,
        };
        if (mirror is null) return image;

        var copy = new WriteableBitmap(new TransformedBitmap(image, mirror));
        copy.Freeze();
        return copy;
    }

    /// <summary>A copy no bigger than <paramref name="longSide"/> pixels, in the given pixel format, with its raw pixels.</summary>
    public static (byte[] Pixels, int Width, int Height, int Stride) Pixels(BitmapSource source, int longSide, PixelFormat format)
    {
        BitmapSource scaled = source;
        double scale = (double)longSide / Math.Max(source.PixelWidth, source.PixelHeight);
        if (scale < 1) scaled = new TransformedBitmap(source, new ScaleTransform(scale, scale));
        var converted = new FormatConvertedBitmap(scaled, format, null, 0);
        int stride = (converted.PixelWidth * format.BitsPerPixel + 7) / 8;
        stride = (stride + 3) & ~3;
        var pixels = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(pixels, stride, 0);
        return (pixels, converted.PixelWidth, converted.PixelHeight, stride);
    }

    /// <summary>Sharpness score (0–100) of a decoded photo; see <see cref="FocusAnalyzer"/>.</summary>
    public static double? FocusScore(BitmapSource source)
    {
        var (pixels, w, h, stride) = Pixels(source, 1024, PixelFormats.Gray8);
        return FocusAnalyzer.Score(pixels, w, h, stride);
    }

    public static Histogram Histogram(BitmapSource source)
    {
        var (pixels, w, h, stride) = Pixels(source, 640, PixelFormats.Bgra32);
        return Core.Histogram.FromBgra(pixels, w, h, stride);
    }
}
