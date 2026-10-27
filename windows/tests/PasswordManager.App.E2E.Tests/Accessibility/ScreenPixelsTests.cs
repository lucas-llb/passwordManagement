using System.Drawing;

namespace PasswordManager.App.E2E.Tests.Accessibility;

// The contrast math of the theme E2E test. No window is opened, so these also run in Release.
public sealed class ScreenPixelsTests
{
    [Fact]
    public void ContrastRatio_BlackOnWhite_Is21()
    {
        Assert.Equal(21.0, ScreenPixels.ContrastRatio(Color.Black, Color.White), 6);
    }

    [Fact]
    public void ContrastRatio_767676OnWhite_IsAboutFourPointFiveFour()
    {
        // #767676 is the lightest gray that still passes AA (4.5:1) on white.
        Assert.Equal(4.54, ScreenPixels.ContrastRatio(Color.FromArgb(0x76, 0x76, 0x76), Color.White), 2);
    }

    [Fact]
    public void ContrastRatio_IsSymmetric()
    {
        var a = Color.FromArgb(0x20, 0x20, 0x20);
        var b = Color.FromArgb(0xC8, 0x64, 0x32);

        Assert.Equal(ScreenPixels.ContrastRatio(a, b), ScreenPixels.ContrastRatio(b, a), 12);
    }

    [Fact]
    public void RelativeLuminance_WhiteAndBlack_AreOneAndZero()
    {
        Assert.Equal(1.0, ScreenPixels.RelativeLuminance(Color.White), 12);
        Assert.Equal(0.0, ScreenPixels.RelativeLuminance(Color.Black), 12);
    }

    [Fact]
    public void Background_MostlyWhiteBitmap_IsWhite()
    {
        using var bitmap = WhiteWithBlackRow();

        Assert.Equal(Color.White.ToArgb(), ScreenPixels.Background(bitmap).ToArgb());
    }

    // The theme test's flake: a Mica gradient splits the background into shades a step apart, each rarer than the
    // solid glyph color. 80 pixels in 8 near-white shades (10 each) and 20 black ones: the background is near-white.
    [Fact]
    public void Background_GradientOutnumberedShadeByShadeByText_IsTheGradient()
    {
        using var bitmap = new Bitmap(10, 10);
        for (var x = 0; x < 10; x++)
        {
            for (var y = 0; y < 10; y++)
            {
                var shade = 0xF0 + (((y * 10) + x) % 8);
                bitmap.SetPixel(x, y, y < 2 ? Color.Black : Color.FromArgb(shade, shade, shade));
            }
        }

        Assert.True(ScreenPixels.RelativeLuminance(ScreenPixels.Background(bitmap)) > 0.8);
    }

    [Fact]
    public void MaxContrast_BlackPixelsOnWhite_Is21()
    {
        using var bitmap = WhiteWithBlackRow();

        Assert.Equal(21.0, ScreenPixels.MaxContrast(bitmap, Color.White), 6);
    }

    // 10×10: 90 white pixels and one black row of 10.
    private static Bitmap WhiteWithBlackRow()
    {
        var bitmap = new Bitmap(10, 10);
        for (var x = 0; x < 10; x++)
        {
            for (var y = 0; y < 10; y++)
            {
                bitmap.SetPixel(x, y, y == 4 ? Color.Black : Color.White);
            }
        }

        return bitmap;
    }
}
