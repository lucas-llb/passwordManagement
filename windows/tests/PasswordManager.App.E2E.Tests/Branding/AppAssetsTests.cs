using System.Buffers.Binary;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;
using PasswordManager.App.E2E.Tests.Accessibility;
using PasswordManager.App.E2E.Tests.Localization;

namespace PasswordManager.App.E2E.Tests.Branding;

// W-27 static guards over the icon assets in the App project: names, exact sizes, real alpha, the ico frames and the
// legibility of the small sizes on both taskbars. They read files straight from the repository and open no window, so
// they run in Debug and Release. They stay red while Assets (or art) is missing, and the failure says which file.
public sealed class AppAssetsTests
{
    // A pixel counts as part of the symbol above this alpha: the same threshold art/Build-AppAssets.ps1 trims the masters at.
    private const int SymbolAlpha = 8;

    // The legibility rule: of the pixels that are mostly opaque, this share must reach 3:1 against the taskbar.
    private const int OpaqueAlpha = 128;
    private const double LegibleContrast = 3.0;
    private const double LegibleShare = 0.60;

    private const int MasterMinimumSize = 1024;

    private static readonly Color LightTaskbar = Color.FromArgb(0xF3, 0xF3, 0xF3);
    private static readonly Color DarkTaskbar = Color.FromArgb(0x20, 0x20, 0x20);

    private static readonly Dictionary<string, AssetSpec> PngsByName =
        AppAssetCatalog.Pngs.ToDictionary(png => png.Name, StringComparer.Ordinal);

    public static TheoryData<string> PngNames { get; } = new(AppAssetCatalog.Pngs.Select(png => png.Name));

    public static TheoryData<int> TargetSizes { get; } = new(AppAssetCatalog.TargetSizes);

    public static TheoryData<string> MasterNames { get; } = new() { "icon-master.png", "glyph-master.png" };

    // ---- The folder ----

    [Fact]
    public void AssetsFolder_HoldsExactlyTheCatalog()
    {
        var expected = AppAssetCatalog.FileNames;
        Assert.Equal(78, expected.Count);
        Assert.Equal(expected.Count, expected.Distinct(StringComparer.OrdinalIgnoreCase).Count());

        var directory = AssetImages.AssetsDir;
        Assert.True(Directory.Exists(directory), $"The Assets folder does not exist: {directory}");

        var actual = Directory
            .EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(directory, path).Replace('\\', '/'))
            .ToArray();

        var missing = expected.Except(actual, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var extra = actual.Except(expected, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

        Assert.True(
            missing.Length == 0 && extra.Length == 0,
            $"Assets is not the catalog. Missing ({missing.Length}): {string.Join(", ", missing)}. Extra ({extra.Length}): {string.Join(", ", extra)}.");
    }

    // ---- The PNGs ----

    [Theory]
    [MemberData(nameof(PngNames))]
    public void Png_HasExactSizeAndRealAlpha(string name)
    {
        var spec = PngsByName[name];
        var png = AssetImages.DecodePng(AssetImages.ReadAsset(name));

        Assert.True(png.IsPng, $"{name} is not a PNG.");
        Assert.True(png.HasAlphaChannel, $"{name} has no alpha channel.");
        Assert.Equal((spec.Width, spec.Height), (png.Raster.Width, png.Raster.Height));
        Assert.True(png.Raster.CornersAreTransparent(), $"{name} has an opaque or translucent corner: the background is not transparent.");
        Assert.True(png.Raster.HasOpaquePixel(), $"{name} has no fully opaque pixel: there is no symbol.");
    }

    [Theory]
    [MemberData(nameof(PngNames))]
    public void Png_SymbolIsCentered(string name)
    {
        var raster = AssetImages.DecodePng(AssetImages.ReadAsset(name)).Raster;

        var bounds = raster.AlphaBounds(SymbolAlpha);
        Assert.True(bounds is not null, $"{name} has no visible pixel.");

        var (offsetX, offsetY) = CenterOffset(raster, bounds.Value);
        var tolerance = CenterTolerance(raster);
        Assert.True(
            Math.Abs(offsetX) <= tolerance && Math.Abs(offsetY) <= tolerance,
            string.Create(
                CultureInfo.InvariantCulture,
                $"{name}: the symbol {bounds} is off center by ({offsetX:0.0}, {offsetY:0.0}) px on a {raster.Width}x{raster.Height} canvas, more than {tolerance:0.0} px."));
    }

    [Theory]
    [MemberData(nameof(TargetSizes))]
    public void TargetSizeVariants_AreIdentical(int size)
    {
        var plated = AssetImages.ReadAsset(AppAssetCatalog.TargetSizeName(size, AppAssetCatalog.Plated));
        var unplated = AssetImages.ReadAsset(AppAssetCatalog.TargetSizeName(size, AppAssetCatalog.Unplated));
        var lightUnplated = AssetImages.ReadAsset(AppAssetCatalog.TargetSizeName(size, AppAssetCatalog.LightUnplated));

        Assert.True(plated.AsSpan().SequenceEqual(unplated), $"targetsize-{size}: the plated and unplated files differ.");
        Assert.True(plated.AsSpan().SequenceEqual(lightUnplated), $"targetsize-{size}: the plated and lightunplated files differ.");
    }

    // ---- The ico ----

    [Fact]
    public void Ico_HasTheFourteenFramesInOrder()
    {
        var ico = IcoFile.Parse(AssetImages.ReadAsset(AppAssetCatalog.IconFileName));

        Assert.Equal(AppAssetCatalog.TargetSizes.Count, ico.Frames.Count);

        var problems = new List<string>();
        for (var i = 0; i < ico.Frames.Count; i++)
        {
            var size = AppAssetCatalog.TargetSizes[i];
            var frame = ico.Frames[i];
            var label = $"frame {i} (N={size})";

            if (frame.Width != size || frame.Height != size)
            {
                problems.Add($"{label}: entry size is {frame.Width}x{frame.Height}.");
            }

            if (frame.Planes != 1 || frame.BitCount != 32)
            {
                problems.Add($"{label}: entry has wPlanes={frame.Planes} and wBitCount={frame.BitCount}, expected 1 and 32.");
            }

            if (size == 256)
            {
                if (!frame.IsPng)
                {
                    problems.Add($"{label}: the 256 frame does not start with the PNG signature.");
                }

                continue;
            }

            if (frame.IsPng)
            {
                problems.Add($"{label}: a frame below 256 is a PNG, expected a DIB.");
                continue;
            }

            problems.AddRange(DibProblems(label, frame, size));
        }

        Assert.Empty(problems);
    }

    [Theory]
    [MemberData(nameof(TargetSizes))]
    public void Ico_FramesMatchTheTargetSizePngs(int size)
    {
        var ico = IcoFile.Parse(AssetImages.ReadAsset(AppAssetCatalog.IconFileName));
        var index = AppAssetCatalog.TargetSizes.ToList().IndexOf(size);
        Assert.True(index < ico.Frames.Count, $"The ico has no frame for N={size}.");

        var frame = ico.Frames[index].Decode().Raster;
        var png = AssetImages.DecodePng(AssetImages.ReadAsset(AppAssetCatalog.TargetSizeName(size, AppAssetCatalog.Unplated))).Raster;

        Assert.Equal((png.Width, png.Height), (frame.Width, frame.Height));
        var (count, first) = frame.CountDifferences(png);
        Assert.True(count == 0, $"N={size}: {count} pixels of the ico frame differ from the unplated PNG, the first {first}.");
    }

    // ---- Legibility ----

    [Theory]
    [InlineData(16)]
    [InlineData(24)]
    [InlineData(32)]
    public void SmallTargetSizes_AreLegibleOnBothTaskbars(int size)
    {
        var raster = AssetImages.DecodePng(AssetImages.ReadAsset(AppAssetCatalog.TargetSizeName(size, AppAssetCatalog.Unplated))).Raster;

        var problems = new List<string>();
        foreach (var (taskbar, backdrop) in new[] { ("light #F3F3F3", LightTaskbar), ("dark #202020", DarkTaskbar) })
        {
            var share = LegibleShareOn(raster, backdrop);
            if (share is null)
            {
                problems.Add($"N={size} on {taskbar}: no pixel is mostly opaque.");
            }
            else if (share < LegibleShare)
            {
                problems.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"N={size} on {taskbar}: only {share:P0} of the opaque pixels reach {LegibleContrast}:1, expected {LegibleShare:P0}."));
            }
        }

        Assert.Empty(problems);
    }

    // The control of the legibility check: the teal of the plan reaches 3:1 on both taskbars, near-black only on the
    // light one and near-white only on the dark one. Without this the check could pass anything.
    [Fact]
    public void Legibility_TellsLowAndHighContrastApart()
    {
        var teal = Raster.Solid(16, 16, Color.FromArgb(0x0E, 0x93, 0x84));
        var black = Raster.Solid(16, 16, Color.FromArgb(0x08, 0x08, 0x08));
        var white = Raster.Solid(16, 16, Color.FromArgb(0xFA, 0xFA, 0xFA));

        Assert.Equal(1.0, LegibleShareOn(teal, LightTaskbar));
        Assert.Equal(1.0, LegibleShareOn(teal, DarkTaskbar));
        Assert.Equal(1.0, LegibleShareOn(black, LightTaskbar));
        Assert.Equal(0.0, LegibleShareOn(black, DarkTaskbar));
        Assert.Equal(0.0, LegibleShareOn(white, LightTaskbar));
        Assert.Equal(1.0, LegibleShareOn(white, DarkTaskbar));

        // Pixels below the opaque threshold do not count either way.
        Assert.Null(LegibleShareOn(Raster.Solid(16, 16, Color.FromArgb(0x40, 0, 0, 0)), LightTaskbar));
    }

    // The control of the geometry checks: a square off to one side is measured as off center, a transparent canvas has
    // transparent corners, and a symbol that reaches a corner does not.
    [Fact]
    public void Raster_MeasuresBoundsCornersAndCenter()
    {
        var centered = Raster.Square(40, 10, 10, 20, Color.Black);
        var bounds = centered.AlphaBounds(SymbolAlpha);
        Assert.Equal(new Rectangle(10, 10, 20, 20), bounds);
        Assert.Equal((0.0, 0.0), CenterOffset(centered, bounds!.Value));
        Assert.True(centered.CornersAreTransparent());
        Assert.True(centered.HasOpaquePixel());

        var shifted = Raster.Square(40, 14, 10, 20, Color.Black);
        Assert.Equal((4.0, 0.0), CenterOffset(shifted, shifted.AlphaBounds(SymbolAlpha)!.Value));

        var corner = Raster.Square(40, 0, 0, 20, Color.Black);
        Assert.False(corner.CornersAreTransparent());

        var faint = Raster.Square(40, 10, 10, 20, Color.FromArgb(SymbolAlpha, 0, 0, 0));
        Assert.Null(faint.AlphaBounds(SymbolAlpha));
        Assert.False(faint.HasOpaquePixel());
    }

    // ---- The masters ----

    [Theory]
    [MemberData(nameof(MasterNames))]
    public void Masters_AreSquareWithRealAlpha(string name)
    {
        var png = AssetImages.DecodePng(File.ReadAllBytes(AssetImages.RequireFile(Path.Combine(AssetImages.ArtDir, name))));

        Assert.True(png.IsPng, $"{name} is not a PNG.");
        Assert.True(png.HasAlphaChannel, $"{name} has no alpha channel.");
        Assert.True(png.Raster.Width == png.Raster.Height, $"{name} is {png.Raster.Width}x{png.Raster.Height}, not square.");
        Assert.True(png.Raster.Width >= MasterMinimumSize, $"{name} is {png.Raster.Width} px wide, under {MasterMinimumSize}.");
        Assert.True(png.Raster.CornersAreTransparent(), $"{name} has an opaque or translucent corner: the background is not transparent.");
        Assert.True(png.Raster.HasOpaquePixel(), $"{name} has no fully opaque pixel: there is no symbol.");
    }

    // ---- Helpers ----

    // How far the center of the symbol is from the center of the canvas, in pixels.
    private static (double X, double Y) CenterOffset(Raster raster, Rectangle bounds) =>
        (((bounds.Left + bounds.Right) / 2.0) - (raster.Width / 2.0), ((bounds.Top + bounds.Bottom) / 2.0) - (raster.Height / 2.0));

    // 2 px, scaled with the canvas so the tile and splash sizes are held to the same relative precision (1% of the short side).
    private static double CenterTolerance(Raster raster) => Math.Max(2.0, Math.Min(raster.Width, raster.Height) * 0.01);

    // The share of the mostly opaque pixels that reach the contrast against the backdrop once composited on it; null if no pixel is mostly opaque.
    private static double? LegibleShareOn(Raster raster, Color backdrop)
    {
        var opaque = 0;
        var legible = 0;
        for (var y = 0; y < raster.Height; y++)
        {
            for (var x = 0; x < raster.Width; x++)
            {
                var pixel = raster.Pixel(x, y);
                if (pixel.A < OpaqueAlpha)
                {
                    continue;
                }

                opaque++;
                if (ScreenPixels.ContrastRatio(Composite(pixel, backdrop), backdrop) >= LegibleContrast)
                {
                    legible++;
                }
            }
        }

        return opaque == 0 ? null : (double)legible / opaque;
    }

    // The pixel over an opaque backdrop: what the taskbar shows.
    private static Color Composite(Color pixel, Color backdrop)
    {
        static int Mix(int foreground, int background, int alpha) => ((foreground * alpha) + (background * (255 - alpha)) + 127) / 255;

        return Color.FromArgb(
            Mix(pixel.R, backdrop.R, pixel.A),
            Mix(pixel.G, backdrop.G, pixel.A),
            Mix(pixel.B, backdrop.B, pixel.A));
    }

    // A 32-bit DIB frame is a 40-byte BITMAPINFOHEADER, N rows of N BGRA pixels, then a 1-bit AND mask of N rows padded to 4 bytes.
    private static IEnumerable<string> DibProblems(string label, IcoFrame frame, int size)
    {
        var header = frame.ReadDibHeader();
        if (header.HeaderSize != 40 || header.Width != size || header.Height != 2 * size || header.Planes != 1 || header.BitCount != 32 || header.Compression != 0)
        {
            yield return $"{label}: the DIB header is size={header.HeaderSize}, {header.Width}x{header.Height}, planes={header.Planes}, bitcount={header.BitCount}, compression={header.Compression}; expected 40, {size}x{2 * size}, 1, 32, 0.";
            yield break;
        }

        var expectedLength = IcoFrame.DibLength(size);
        if (frame.Data.Length != expectedLength)
        {
            yield return $"{label}: the DIB frame is {frame.Data.Length} bytes, expected {expectedLength} (header, BGRA pixels, AND mask).";
            yield break;
        }

        var decoded = frame.Decode();
        if (decoded.MaskViolations != 0)
        {
            yield return $"{label}: the AND mask disagrees with the alpha in {decoded.MaskViolations} pixels (set where A = 0, clear where A = 255).";
        }
    }
}

// The 32-bit pixels of an image: BGRA bytes, top row first, straight (not premultiplied) alpha.
internal sealed class Raster
{
    private readonly byte[] _bgra;

    public Raster(int width, int height, byte[] bgra)
    {
        ArgumentNullException.ThrowIfNull(bgra);
        if (bgra.Length != width * height * 4)
        {
            throw new ArgumentException($"{bgra.Length} bytes do not hold {width}x{height} BGRA pixels.", nameof(bgra));
        }

        Width = width;
        Height = height;
        _bgra = bgra;
    }

    public int Width { get; }

    public int Height { get; }

    public static Raster FromBitmap(Bitmap bitmap)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        var area = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(area, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var rowBytes = bitmap.Width * 4;
            var bgra = new byte[rowBytes * bitmap.Height];
            for (var row = 0; row < bitmap.Height; row++)
            {
                Marshal.Copy(data.Scan0 + (row * data.Stride), bgra, row * rowBytes, rowBytes);
            }

            return new Raster(bitmap.Width, bitmap.Height, bgra);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    // A canvas of one color, for the controls.
    public static Raster Solid(int width, int height, Color color)
    {
        var bgra = new byte[width * height * 4];
        for (var i = 0; i < width * height; i++)
        {
            bgra[i * 4] = color.B;
            bgra[(i * 4) + 1] = color.G;
            bgra[(i * 4) + 2] = color.R;
            bgra[(i * 4) + 3] = color.A;
        }

        return new Raster(width, height, bgra);
    }

    // A transparent square canvas holding one square of a color, for the controls.
    public static Raster Square(int canvas, int left, int top, int side, Color color)
    {
        var raster = Solid(canvas, canvas, Color.Transparent);
        for (var y = top; y < top + side; y++)
        {
            for (var x = left; x < left + side; x++)
            {
                var offset = ((y * canvas) + x) * 4;
                raster._bgra[offset] = color.B;
                raster._bgra[offset + 1] = color.G;
                raster._bgra[offset + 2] = color.R;
                raster._bgra[offset + 3] = color.A;
            }
        }

        return raster;
    }

    public Color Pixel(int x, int y)
    {
        var offset = ((y * Width) + x) * 4;
        return Color.FromArgb(_bgra[offset + 3], _bgra[offset + 2], _bgra[offset + 1], _bgra[offset]);
    }

    // True when the four corner pixels are fully transparent: a real alpha background, not a plate or a painted checkerboard.
    public bool CornersAreTransparent() =>
        Pixel(0, 0).A == 0 && Pixel(Width - 1, 0).A == 0 && Pixel(0, Height - 1).A == 0 && Pixel(Width - 1, Height - 1).A == 0;

    public bool HasOpaquePixel()
    {
        for (var i = 3; i < _bgra.Length; i += 4)
        {
            if (_bgra[i] == 255)
            {
                return true;
            }
        }

        return false;
    }

    // The box of the pixels with an alpha above the threshold (right and bottom exclusive); null if there is none.
    public Rectangle? AlphaBounds(int threshold)
    {
        int left = Width, top = Height, right = -1, bottom = -1;
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                if (_bgra[((((y * Width) + x) * 4) + 3)] <= threshold)
                {
                    continue;
                }

                left = Math.Min(left, x);
                top = Math.Min(top, y);
                right = Math.Max(right, x);
                bottom = Math.Max(bottom, y);
            }
        }

        return right < 0 ? null : Rectangle.FromLTRB(left, top, right + 1, bottom + 1);
    }

    // How many pixels differ from another raster of the same size, and where the first one is. The color of a fully
    // transparent pixel is invisible, so it is not compared: only the alpha is.
    public (int Count, string First) CountDifferences(Raster other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (other.Width != Width || other.Height != Height)
        {
            throw new ArgumentException($"The sizes differ: {Width}x{Height} and {other.Width}x{other.Height}.", nameof(other));
        }

        var count = 0;
        var first = string.Empty;
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                var a = Pixel(x, y);
                var b = other.Pixel(x, y);
                if (a.A == b.A && (a.A == 0 || a.ToArgb() == b.ToArgb()))
                {
                    continue;
                }

                if (count++ == 0)
                {
                    first = $"at ({x}, {y}): {a} against {b}";
                }
            }
        }

        return (count, first);
    }
}

internal sealed record PngImage(Raster Raster, bool IsPng, bool HasAlphaChannel);

// Where the assets are, and how to read them.
internal static class AssetImages
{
    public static string AssetsDir => Path.Combine(AppResources.AppDir, "Assets");

    // windows/art, next to windows/src: the Codex masters, outside the App project so they never reach the package.
    public static string ArtDir => Path.GetFullPath(Path.Combine(AppResources.SourceDir, "..", "art"));

    // The path, once the file is known to exist: a missing asset fails with its name instead of a FileNotFoundException.
    public static string RequireFile(string path)
    {
        Assert.True(File.Exists(path), $"The file does not exist: {path}");
        return path;
    }

    public static byte[] ReadAsset(string name) => File.ReadAllBytes(RequireFile(Path.Combine(AssetsDir, name)));

    public static PngImage DecodePng(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        using var bitmap = new Bitmap(stream);
        return new PngImage(
            Raster.FromBitmap(bitmap),
            bitmap.RawFormat.Guid == ImageFormat.Png.Guid,
            Image.IsAlphaPixelFormat(bitmap.PixelFormat));
    }
}

internal readonly record struct DibHeader(int HeaderSize, int Width, int Height, int Planes, int BitCount, int Compression);

internal sealed record DecodedFrame(Raster Raster, int MaskViolations);

// One image of an .ico: the directory entry and the bytes it points to.
internal sealed class IcoFrame
{
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public IcoFrame(int width, int height, int planes, int bitCount, byte[] data)
    {
        Width = width;
        Height = height;
        Planes = planes;
        BitCount = bitCount;
        Data = data;
    }

    // The entry stores 0 for 256.
    public int Width { get; }

    public int Height { get; }

    public int Planes { get; }

    public int BitCount { get; }

    public byte[] Data { get; }

    public bool IsPng => Data.AsSpan().StartsWith(PngSignature);

    // The bytes of a 32-bit DIB frame without a color table: header, N*N BGRA pixels, N rows of AND mask padded to 4 bytes.
    public static int DibLength(int size) => 40 + (size * size * 4) + (MaskStride(size) * size);

    public DibHeader ReadDibHeader()
    {
        if (Data.Length < 40)
        {
            throw new InvalidDataException($"A DIB frame needs a 40-byte header, this one has {Data.Length} bytes.");
        }

        var data = Data.AsSpan();
        return new DibHeader(
            BinaryPrimitives.ReadInt32LittleEndian(data),
            BinaryPrimitives.ReadInt32LittleEndian(data[4..]),
            BinaryPrimitives.ReadInt32LittleEndian(data[8..]),
            BinaryPrimitives.ReadUInt16LittleEndian(data[12..]),
            BinaryPrimitives.ReadUInt16LittleEndian(data[14..]),
            BinaryPrimitives.ReadInt32LittleEndian(data[16..]));
    }

    // The pixels of the frame. For a DIB, the rows are stored bottom-up, and MaskViolations counts the pixels whose
    // AND mask bit contradicts the alpha: clear where A = 0 (should be set) or set where A = 255 (should be clear).
    public DecodedFrame Decode()
    {
        if (IsPng)
        {
            return new DecodedFrame(AssetImages.DecodePng(Data).Raster, 0);
        }

        var header = ReadDibHeader();
        var size = header.Width;
        if (header.BitCount != 32 || header.Compression != 0 || header.Height != 2 * size || Data.Length != DibLength(size))
        {
            throw new InvalidDataException($"Not a 32-bit uncompressed DIB frame with an AND mask: {header}, {Data.Length} bytes.");
        }

        var bgra = new byte[size * size * 4];
        var maskOffset = 40 + (size * size * 4);
        var maskStride = MaskStride(size);
        var violations = 0;
        for (var row = 0; row < size; row++)
        {
            var y = size - 1 - row;
            Buffer.BlockCopy(Data, 40 + (row * size * 4), bgra, y * size * 4, size * 4);
            for (var x = 0; x < size; x++)
            {
                var alpha = bgra[(((y * size) + x) * 4) + 3];
                var masked = (Data[maskOffset + (row * maskStride) + (x / 8)] & (0x80 >> (x % 8))) != 0;
                if ((alpha == 0 && !masked) || (alpha == 255 && masked))
                {
                    violations++;
                }
            }
        }

        return new DecodedFrame(new Raster(size, size, bgra), violations);
    }

    private static int MaskStride(int size) => (size + 31) / 32 * 4;
}

// An .ico: ICONDIR (reserved 0, type 1, count) then one 16-byte ICONDIRENTRY per image, then the images.
internal sealed class IcoFile
{
    private const int HeaderLength = 6;
    private const int EntryLength = 16;

    private IcoFile(IReadOnlyList<IcoFrame> frames)
    {
        Frames = frames;
    }

    public IReadOnlyList<IcoFrame> Frames { get; }

    public static IcoFile Parse(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length < HeaderLength)
        {
            throw new InvalidDataException($"An ico needs a 6-byte header, this file has {bytes.Length} bytes.");
        }

        var file = bytes.AsSpan();
        var reserved = BinaryPrimitives.ReadUInt16LittleEndian(file);
        var type = BinaryPrimitives.ReadUInt16LittleEndian(file[2..]);
        var count = BinaryPrimitives.ReadUInt16LittleEndian(file[4..]);
        if (reserved != 0 || type != 1)
        {
            throw new InvalidDataException($"Not an ico: reserved={reserved}, type={type}, expected 0 and 1.");
        }

        if (bytes.Length < HeaderLength + (count * EntryLength))
        {
            throw new InvalidDataException($"The ico declares {count} images but its directory is cut short.");
        }

        var frames = new List<IcoFrame>(count);
        for (var i = 0; i < count; i++)
        {
            var entry = file[(HeaderLength + (i * EntryLength))..];
            var length = BinaryPrimitives.ReadInt32LittleEndian(entry[8..]);
            var offset = BinaryPrimitives.ReadInt32LittleEndian(entry[12..]);
            if (offset < 0 || length < 0 || (long)offset + length > bytes.Length)
            {
                throw new InvalidDataException($"Image {i} of the ico points outside the file: offset {offset}, {length} bytes.");
            }

            frames.Add(new IcoFrame(
                entry[0] == 0 ? 256 : entry[0],
                entry[1] == 0 ? 256 : entry[1],
                BinaryPrimitives.ReadUInt16LittleEndian(entry[4..]),
                BinaryPrimitives.ReadUInt16LittleEndian(entry[6..]),
                bytes[offset..(offset + length)]));
        }

        return new IcoFile(frames);
    }
}
