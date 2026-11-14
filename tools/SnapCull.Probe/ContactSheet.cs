using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SnapCull;
using SnapCull.Core;

/// <summary>Draws a grid of photos with their file names, using the same loading path as the app.</summary>
static class ContactSheet
{
    const int Cell = 220, Label = 34, Columns = 8;

    public static void Render(IReadOnlyList<string> paths, string output)
    {
        int rows = (paths.Count + Columns - 1) / Columns;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(24, 24, 24)), null, new Rect(0, 0, Columns * Cell, rows * (Cell + Label)));
            var typeface = new Typeface("Segoe UI");
            for (int i = 0; i < paths.Count; i++)
            {
                double x = i % Columns * Cell, y = i / Columns * (Cell + Label);
                var loaded = ImageDecoder.TryLoad(PhotoFiles.FromPath(paths[i]), PreviewSize.Largest, 400);
                if (loaded is not null)
                {
                    var b = loaded.Bitmap;
                    double scale = (Cell - 10.0) / Math.Max(b.PixelWidth, b.PixelHeight);
                    double w = b.PixelWidth * scale, h = b.PixelHeight * scale;
                    dc.DrawImage(b, new Rect(x + (Cell - w) / 2, y + (Cell - h) / 2, w, h));
                }
                var name = Path.GetFileNameWithoutExtension(paths[i]);
                if (name.Length > 30) name = name[..30];
                var text = new FormattedText(name + (loaded?.FromWindowsCodec == true ? " (W)" : loaded is null ? " FAILED" : ""),
                    CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, 11, Brushes.Gainsboro, 1.0)
                { MaxTextWidth = Cell - 6, MaxLineCount = 2 };
                dc.DrawText(text, new Point(x + 3, y + Cell));
            }
        }

        var target = new RenderTargetBitmap(Columns * Cell, rows * (Cell + Label), 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(target));
        using var file = File.Create(output);
        encoder.Save(file);
    }
}
