using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace P15Printer;

/// <summary>
/// Converts images and text into the 1-bit MSB-first raster the P15 expects.
/// Cross-platform port of the Windows encoder: uses SixLabors.ImageSharp instead
/// of System.Drawing, so it runs on Linux. The packing / dithering math is
/// identical, so it produces the same raster as the Windows build.
/// </summary>
public static class ImageEncoder
{
    /// <summary>Default printable width of the P15 head in dots (203 dpi, ~48 mm).</summary>
    public const int DefaultWidthDots = 384;

    public readonly record struct Raster(byte[] Data, int WidthBytes, int Height);

    /// <summary>
    /// Loads an image file, scales it to <paramref name="widthDots"/> (preserving
    /// aspect ratio), and packs it into a 1bpp raster.
    /// </summary>
    public static Raster FromFile(string path, int widthDots = DefaultWidthDots, bool dither = true)
    {
        using var src = Image.Load<Rgba32>(path);
        int height = Math.Max(1, (int)Math.Round(src.Height * (widthDots / (double)src.Width)));

        src.Mutate(x => x
            .Resize(new ResizeOptions
            {
                Size = new Size(widthDots, height),
                Mode = ResizeMode.Stretch,
                Sampler = KnownResamplers.Bicubic,
            })
            // Flatten any transparency onto white so alpha doesn't read as black.
            .BackgroundColor(Color.White));

        return Pack(ToGray(src, widthDots, height), widthDots, height, dither);
    }

    private static double[,] ToGray(Image<Rgba32> img, int w, int h)
    {
        var gray = new double[h, w];
        img.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < h; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (int x = 0; x < w; x++)
                {
                    Rgba32 c = row[x];
                    gray[y, x] = c.R * 0.299 + c.G * 0.587 + c.B * 0.114;
                }
            }
        });
        return gray;
    }

    private static Raster Pack(double[,] gray, int w, int h, bool dither)
    {
        int widthBytes = (w + 7) / 8;
        var data = new byte[widthBytes * h];

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                double v = gray[y, x];
                bool black = v < 128;
                if (black) data[y * widthBytes + (x >> 3)] |= (byte)(0x80 >> (x & 7));

                if (dither)
                {
                    // Floyd–Steinberg error diffusion.
                    double err = v - (black ? 0 : 255);
                    Spread(gray, x + 1, y,     w, h, err * 7 / 16);
                    Spread(gray, x - 1, y + 1, w, h, err * 3 / 16);
                    Spread(gray, x,     y + 1, w, h, err * 5 / 16);
                    Spread(gray, x + 1, y + 1, w, h, err * 1 / 16);
                }
            }
        }
        return new Raster(data, widthBytes, h);
    }

    private static void Spread(double[,] gray, int x, int y, int w, int h, double err)
    {
        if (x >= 0 && x < w && y >= 0 && y < h) gray[y, x] += err;
    }

    /// <summary>Renders a text block to a raster using the given font.</summary>
    public static Raster FromText(string text, int widthDots = DefaultWidthDots,
                                  string? fontName = null, float fontSize = 28f)
    {
        Font font = ResolveFont(fontName, fontSize);

        var textOptions = new RichTextOptions(font)
        {
            WrappingLength = widthDots,
            Origin = new PointF(0, 4),
        };

        // Measure required height, then render onto a white canvas.
        FontRectangle bounds = TextMeasurer.MeasureBounds(text, textOptions);
        int height = Math.Max(1, (int)Math.Ceiling(bounds.Bottom) + 8);

        using var img = new Image<Rgba32>(widthDots, height);
        img.Mutate(ctx =>
        {
            ctx.BackgroundColor(Color.White);
            ctx.DrawText(textOptions, text, Color.Black);
        });

        return Pack(ToGray(img, widthDots, height), widthDots, height, dither: false);
    }

    /// <summary>
    /// Picks a usable sans-serif system font. Windows ships "Segoe UI"; Linux
    /// distros vary, so try a list of common families before giving up.
    /// </summary>
    private static Font ResolveFont(string? name, float size)
    {
        if (name is not null && SystemFonts.TryGet(name, out var requested))
            return requested.CreateFont(size, FontStyle.Regular);

        foreach (var candidate in new[]
                 {
                     "DejaVu Sans", "Liberation Sans", "Noto Sans",
                     "FreeSans", "Ubuntu", "Segoe UI", "Arial",
                 })
        {
            if (SystemFonts.TryGet(candidate, out var found))
                return found.CreateFont(size, FontStyle.Regular);
        }

        if (SystemFonts.Families.Any())
            return SystemFonts.Families.First().CreateFont(size, FontStyle.Regular);

        throw new InvalidOperationException(
            "No system fonts found for text rendering. Install a TrueType font, e.g.:\n" +
            "  Debian/Ubuntu: sudo apt install fonts-dejavu-core\n" +
            "  Fedora:        sudo dnf install dejavu-sans-fonts\n" +
            "  Arch:          sudo pacman -S ttf-dejavu");
    }
}
