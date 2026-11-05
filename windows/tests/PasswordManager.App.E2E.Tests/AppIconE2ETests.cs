using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;
using PasswordManager.App.E2E.Tests.Accessibility;
using PasswordManager.App.E2E.Tests.Branding;

namespace PasswordManager.App.E2E.Tests;

// W-27: the exe and the running window carry the app icon. The exe is read as Explorer reads it and the window as the
// taskbar and title bar do, both from outside the app. They send no key and no click.
public sealed class AppIconE2ETests
{
    // An icon follows the display scale, so each one is compared at this size, over this backdrop. Mid gray stands far from
    // the teal and the pale keyhole of the symbol, and near neither end, so a missing symbol shows and a blended edge does not.
    private const int NormalizedSize = 32;

    private const double MaxSameIconFraction = 0.05;
    private const double MinOtherImageFraction = 0.20;

    private const uint MessageTimeoutMilliseconds = 5000;

    private static readonly Color Backdrop = Color.FromArgb(0x80, 0x80, 0x80);

    [Theory]
    [InlineData(16)]
    [InlineData(32)]
    [InlineData(48)]
    [InlineData(256)]
    public void Exe_EmbedsTheAppIcon(int size)
    {
        var exe = AppSession.RequireDebugAppExe();

        var count = NativeMethods.PrivateExtractIconsW(exe, 0, size, size, out var handle, out _, 1, 0);
        try
        {
            Assert.Equal(1u, count);
            Assert.NotEqual(nint.Zero, handle);

            using var bitmap = ToBitmap(handle);
            Assert.Equal((size, size), (bitmap.Width, bitmap.Height));

            var (differences, first) = Raster.FromBitmap(bitmap).CountDifferences(IcoFrameOf(size).Decode().Raster);
            Assert.True(differences == 0, $"N={size}: {differences} pixels of the exe's icon differ from the ico frame, the first {first}.");
        }
        finally
        {
            if (handle != nint.Zero)
            {
                NativeMethods.DestroyIcon(handle);
            }
        }
    }

    [Fact]
    public void Launch_WindowCarriesTheAppIcon()
    {
        using var app = AppSession.Launch();
        app.WaitFor("ProfilesList");

        var problems = new List<string>();
        foreach (var (name, kind) in new[] { ("small", NativeMethods.ICON_SMALL), ("big", NativeMethods.ICON_BIG), ("small2", NativeMethods.ICON_SMALL2) })
        {
            var sent = NativeMethods.SendMessageTimeoutNumeric(
                app.Handle, NativeMethods.WM_GETICON, kind, 0, NativeMethods.SMTO_ABORTIFHUNG, MessageTimeoutMilliseconds, out var result);
            Assert.True(sent != 0, $"WM_GETICON {name} failed with error {Marshal.GetLastPInvokeError()}.");
            if (result == 0)
            {
                problems.Add($"{name}: the window has no icon.");
                continue;
            }

            // The handle belongs to the window: Icon.FromHandle does not take it, and it is not destroyed here.
            using var bitmap = ToBitmap((nint)result);
            var frame = FrameFor(bitmap.Width);
            using var actual = Normalize(bitmap);
            using var expected = NormalizedFrame(frame);
            var changed = ScreenPixels.ChangedFraction(actual, expected);
            if (changed > MaxSameIconFraction)
            {
                problems.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{name}: {changed:P1} of the {bitmap.Width}x{bitmap.Height} icon differs from the ico's {frame} frame, over {MaxSameIconFraction:P0}."));
            }
        }

        Assert.Empty(problems);
    }

    // The control of the comparison above, at every frame it can pick: without it, the check could pass anything.
    [Theory]
    [MemberData(nameof(AppAssetsTests.TargetSizes), MemberType = typeof(AppAssetsTests))]
    public void Comparer_TellsAnotherImageApart(int size)
    {
        using var blank = new Bitmap(NormalizedSize, NormalizedSize);
        using var actual = Normalize(blank);
        using var expected = NormalizedFrame(size);

        var changed = ScreenPixels.ChangedFraction(actual, expected);

        Assert.True(
            changed > MinOtherImageFraction,
            string.Create(CultureInfo.InvariantCulture, $"A blank image differs from the ico's {size} frame in only {changed:P1}, expected over {MinOtherImageFraction:P0}."));
    }

    [Fact]
    public void Build_CopiesTheIconNextToTheExe()
    {
        var exe = AppSession.RequireDebugAppExe();
        var copy = AssetImages.RequireFile(Path.Combine(Path.GetDirectoryName(exe)!, "Assets", AppAssetCatalog.IconFileName));

        var source = AssetImages.ReadAsset(AppAssetCatalog.IconFileName);

        Assert.True(File.ReadAllBytes(copy).AsSpan().SequenceEqual(source), $"{copy} differs from the source ico.");
    }

    private static IcoFrame IcoFrameOf(int size)
    {
        var ico = IcoFile.Parse(AssetImages.ReadAsset(AppAssetCatalog.IconFileName));
        return ico.Frames[AppAssetCatalog.TargetSizes.ToList().IndexOf(size)];
    }

    // The pixels of an icon handle, which may belong to another process. The bitmap is a copy, so the handle stays open.
    private static Bitmap ToBitmap(nint handle)
    {
        using var icon = Icon.FromHandle(handle);
        return icon.ToBitmap();
    }

    // The frame an icon of this size comes from: the same one, or the next larger one at a display scale with no frame of
    // its own. A fixed reference would not do: the frames up to 32 come from the simplified glyph, whose bigger keyhole
    // differs from the 256 frame by design (7.9% at 32), so the check could only pass with a looser ceiling.
    private static int FrameFor(int iconSize) =>
        AppAssetCatalog.TargetSizes.FirstOrDefault(size => size >= iconSize, AppAssetCatalog.TargetSizes.Max());

    private static Bitmap NormalizedFrame(int size)
    {
        var raster = IcoFrameOf(size).Decode().Raster;
        using var frame = new Bitmap(raster.Width, raster.Height, PixelFormat.Format32bppArgb);
        for (var y = 0; y < raster.Height; y++)
        {
            for (var x = 0; x < raster.Width; x++)
            {
                frame.SetPixel(x, y, raster.Pixel(x, y));
            }
        }

        return Normalize(frame);
    }

    private static Bitmap Normalize(Bitmap source)
    {
        var normalized = new Bitmap(NormalizedSize, NormalizedSize);
        using var graphics = Graphics.FromImage(normalized);
        graphics.Clear(Backdrop);
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.DrawImage(source, new Rectangle(0, 0, NormalizedSize, NormalizedSize));
        return normalized;
    }
}
