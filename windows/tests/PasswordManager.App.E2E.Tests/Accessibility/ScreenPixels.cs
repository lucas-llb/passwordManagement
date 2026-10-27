using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Capturing;

namespace PasswordManager.App.E2E.Tests.Accessibility;

// WCAG 2.x contrast measured on rendered pixels. A capture of a text element holds its background and its glyphs: the
// most frequent color is the background, and the pixel farthest from it in contrast is the core of a glyph.
internal static class ScreenPixels
{
    // Channel distance under which two colors are one background shade; text is always far more than this apart.
    private const int SameShade = 8;

    // WCAG relative luminance of an sRGB color: 0 for black, 1 for white.
    public static double RelativeLuminance(Color color) =>
        (0.2126 * Linear(color.R)) + (0.7152 * Linear(color.G)) + (0.0722 * Linear(color.B));

    public static double ContrastRatio(Color a, Color b)
    {
        var la = RelativeLuminance(a);
        var lb = RelativeLuminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    // The most frequent color, counting near-identical shades as one: Mica spreads the background over a gradient of
    // shades a step or two apart, and in a short line of large text the solid core of the glyphs can outnumber any
    // single one of them. The winner is the exact color with the most pixels within SameShade of it.
    public static Color Background(Bitmap bitmap)
    {
        var colors = Pixels(bitmap).GroupBy(p => p).Select(g => (Color: Color.FromArgb(g.Key), Count: g.Count())).ToArray();
        return colors.MaxBy(c => colors.Where(o => IsSameShade(o.Color, c.Color)).Sum(o => o.Count)).Color;
    }

    public static double MaxContrast(Bitmap bitmap, Color background) =>
        Pixels(bitmap).Distinct().Max(p => ContrastRatio(Color.FromArgb(p), background));

    public static (Color Background, double Luminance, double Contrast) Measure(AutomationElement element)
    {
        using var image = Capture.Element(element);
        var background = Background(image.Bitmap);
        return (background, RelativeLuminance(background), MaxContrast(image.Bitmap, background));
    }

    public static string Hex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    private static bool IsSameShade(Color a, Color b) =>
        Math.Abs(a.R - b.R) <= SameShade && Math.Abs(a.G - b.G) <= SameShade && Math.Abs(a.B - b.B) <= SameShade;

    private static double Linear(byte channel)
    {
        var c = channel / 255.0;
        return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }

    // Every pixel as opaque ARGB; captures of the screen carry no transparency.
    private static int[] Pixels(Bitmap bitmap)
    {
        var area = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(area, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var pixels = new int[bitmap.Width * bitmap.Height];
            for (var row = 0; row < bitmap.Height; row++)
            {
                Marshal.Copy(data.Scan0 + (row * data.Stride), pixels, row * bitmap.Width, bitmap.Width);
            }

            for (var i = 0; i < pixels.Length; i++)
            {
                pixels[i] |= unchecked((int)0xFF000000);
            }

            return pixels;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }
}
