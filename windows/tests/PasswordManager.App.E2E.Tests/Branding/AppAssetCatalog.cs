namespace PasswordManager.App.E2E.Tests.Branding;

// One expected image: its file name in Assets and its exact size in pixels.
internal readonly record struct AssetSpec(string Name, int Width, int Height);

// The W-27 asset list, spelled out: every name and every pixel size is written down, never computed, so a rounding slip in
// the derivation script (63, not Math.Round(62.5) = 62) cannot make the expectation drift with it. The source of truth is
// the asset table in PLAN-w27-icons.md (Microsoft "Construct your Windows app's icon").
internal static class AppAssetCatalog
{
    public const string IconFileName = "AppIcon.ico";

    public const string Plated = "";
    public const string Unplated = "_altform-unplated";
    public const string LightUnplated = "_altform-lightunplated";

    // The Windows scale factors that have a file in every scale family.
    private static readonly int[] Scales = [100, 125, 150, 200, 400];

    // The sizes of the targetsize files and, in the same order, of the frames of AppIcon.ico.
    public static IReadOnlyList<int> TargetSizes { get; } = [16, 20, 24, 30, 32, 36, 40, 48, 60, 64, 72, 80, 96, 256];

    // The three altform suffixes Windows needs for every targetsize: all are required even when the images are identical,
    // or Windows draws the icon on a system plate.
    public static IReadOnlyList<string> Altforms { get; } = [Plated, Unplated, LightUnplated];

    // Family, then the size at each of Scales (100, 125, 150, 200, 400).
    private static readonly (string Family, (int Width, int Height)[] Sizes)[] ScaleFamilies =
    [
        ("Square44x44Logo", [(44, 44), (55, 55), (66, 66), (88, 88), (176, 176)]),
        ("SmallTile", [(71, 71), (89, 89), (107, 107), (142, 142), (284, 284)]),
        ("Square150x150Logo", [(150, 150), (188, 188), (225, 225), (300, 300), (600, 600)]),
        ("Wide310x150Logo", [(310, 150), (388, 188), (465, 225), (620, 300), (1240, 600)]),
        ("LargeTile", [(310, 310), (388, 388), (465, 465), (620, 620), (1240, 1240)]),
        ("StoreLogo", [(50, 50), (63, 63), (75, 75), (100, 100), (200, 200)]),
        ("SplashScreen", [(620, 300), (775, 375), (930, 450), (1240, 600), (2480, 1200)]),
    ];

    // The 77 PNGs: 35 scale files, then the 42 targetsize files.
    public static IReadOnlyList<AssetSpec> Pngs { get; } = BuildPngs();

    // Every file Assets must hold, and nothing else: the 77 PNGs and the ico.
    public static IReadOnlyList<string> FileNames { get; } = [.. Pngs.Select(png => png.Name), IconFileName];

    public static string TargetSizeName(int size, string altform) => $"Square44x44Logo.targetsize-{size}{altform}.png";

    public static AssetSpec TargetSize(int size, string altform) => new(TargetSizeName(size, altform), size, size);

    private static List<AssetSpec> BuildPngs()
    {
        var pngs = new List<AssetSpec>();
        foreach (var (family, sizes) in ScaleFamilies)
        {
            for (var i = 0; i < Scales.Length; i++)
            {
                pngs.Add(new AssetSpec($"{family}.scale-{Scales[i]}.png", sizes[i].Width, sizes[i].Height));
            }
        }

        foreach (var size in TargetSizes)
        {
            foreach (var altform in Altforms)
            {
                pngs.Add(TargetSize(size, altform));
            }
        }

        return pngs;
    }
}
