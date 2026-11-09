using System.Xml.Linq;
using PasswordManager.App.E2E.Tests.Localization;

namespace PasswordManager.App.E2E.Tests.Packaging;

// W-28 static guards over the packaging pair: Package.appxmanifest, which is what turns the MSIX packaging targets on,
// and the project that enables the tooling. They read the repository straight from AppDir, as the other static guards
// do, and open no window, so they run in Debug and Release. A revert of the manifest, of EnableMsixTooling, or a leaked
// signing certificate fails one of them.
public sealed class PackageManifestTests
{
    // The seven logo families the manifest names, per the W-27 asset table; each needs a scale-100 file in Assets.
    private static readonly string[] AssetBaseNames =
        ["Square44x44Logo", "Square150x150Logo", "Wide310x150Logo", "LargeTile", "SmallTile", "StoreLogo", "SplashScreen"];

    // Capabilities that would give the offline app network or enterprise identity access.
    private static readonly string[] ForbiddenCapabilities =
        ["internetClient", "internetClientServer", "privateNetworkClientServer", "enterpriseAuthentication", "proximity"];

    private static readonly string[] CertificatePatterns = ["*.pfx", "*.p12"];

    private static string ManifestPath => Path.Combine(AppResources.AppDir, "Package.appxmanifest");

    private static string ProjectPath => Path.Combine(AppResources.AppDir, "PasswordManager.App.csproj");

    // ---- The manifest ----

    [Fact]
    public void Manifest_HasNoNetworkCapability()
    {
        var names = Named(Manifest, "Capability")
            .Select(capability => (string?)capability.Attribute("Name") ?? string.Empty)
            .ToArray();

        Assert.True(
            names.Length == 1 && names[0] == "runFullTrust",
            $"The manifest must carry only the runFullTrust capability, it declares: {string.Join(", ", names)}.");

        var raw = File.ReadAllText(ManifestPath);
        var declared = ForbiddenCapabilities
            .Where(capability => raw.Contains(capability, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Assert.True(
            declared.Length == 0,
            $"The manifest names a forbidden capability: {string.Join(", ", declared)}.");
    }

    [Fact]
    public void Manifest_MatchesTheAppPackage()
    {
        var application = Assert.Single(Named(Manifest, "Application"));

        // The packaging task expands these two tokens: $targetnametoken$ -> PasswordManager.App and
        // $targetentrypoint$ -> PasswordManager.App.App. Keeping the tokens is what binds the manifest to this project.
        Assert.Equal("$targetnametoken$.exe", (string?)application.Attribute("Executable"));
        Assert.Equal("$targetentrypoint$", (string?)application.Attribute("EntryPoint"));
    }

    [Fact]
    public void Manifest_TargetsWindows10AndPtBr()
    {
        var deviceFamily = Assert.Single(Named(Manifest, "TargetDeviceFamily"));

        Assert.Equal("Windows.Desktop", (string?)deviceFamily.Attribute("Name"));
        Assert.Equal("10.0.19041.0", (string?)deviceFamily.Attribute("MinVersion"));

        var resource = Assert.Single(Named(Manifest, "Resource"));
        Assert.Equal("pt-BR", (string?)resource.Attribute("Language"));
    }

    [Fact]
    public void Manifest_ReferencesOnlyExistingAssets()
    {
        // An asset path is an attribute on the visual elements (or a leaf element value, as Properties/Logo is).
        var referenced = Manifest
            .Descendants()
            .SelectMany(element => element.Attributes()
                .Select(attribute => attribute.Value)
                .Append(element.HasElements ? string.Empty : element.Value))
            .Where(value => value.StartsWith(@"Assets\", StringComparison.Ordinal))
            .Select(value => Path.GetFileNameWithoutExtension(value.Replace('\\', '/')))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(AssetBaseNames.Order(StringComparer.Ordinal).ToArray(), referenced);

        var assetsDir = Path.Combine(AppResources.AppDir, "Assets");
        var missing = referenced
            .Select(baseName => Path.Combine(assetsDir, $"{baseName}.scale-100.png"))
            .Where(path => !File.Exists(path))
            .ToArray();
        Assert.True(missing.Length == 0, $"The manifest names an asset with no scale-100 file: {string.Join(", ", missing)}.");

        // The title is UI text: the manifest reuses the AppTitle key MainWindow already uses, never a manifest-only key.
        var visualElements = Assert.Single(Named(Manifest, "VisualElements"));
        Assert.Equal("ms-resource:AppTitle", (string?)visualElements.Attribute("DisplayName"));
        Assert.Equal("ms-resource:AppTitle", (string?)visualElements.Attribute("Description"));
        Assert.All(Named(Manifest, "DisplayName"), element => Assert.Equal("ms-resource:AppTitle", element.Value));

        Assert.True(
            AppResources.Strings.TryGetValue("AppTitle", out var appTitle),
            "The resw has no AppTitle key, so ms-resource:AppTitle resolves to nothing.");
        Assert.False(string.IsNullOrWhiteSpace(appTitle), "The AppTitle resw value is empty.");
    }

    // ---- The project ----

    [Fact]
    public void Csproj_IsPackagedByDefault()
    {
        var project = Project;

        // Packaged is the default only while Package.appxmanifest exists; None would silently undo W-28.
        var packageTypes = Named(project, "WindowsPackageType").Select(element => element.Value.Trim()).ToArray();
        Assert.True(
            !packageTypes.Any(value => value.Equals("None", StringComparison.OrdinalIgnoreCase)),
            "WindowsPackageType=None makes the build unpackaged; the manifest is what turns the MSIX targets on.");

        var msixTooling = Assert.Single(Named(project, "EnableMsixTooling"));
        Assert.True(
            msixTooling.Value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase),
            $"EnableMsixTooling must be true, it is '{msixTooling.Value.Trim()}'.");

        Assert.Empty(Named(project, "PackageCertificateKeyFile"));
        Assert.Empty(Named(project, "PackageCertificateThumbprint"));
    }

    // ---- The repository ----

    [Fact]
    public void Repo_HasNoCertificate()
    {
        var certificates = CertificatePatterns
            .SelectMany(pattern => Directory.EnumerateFiles(AppResources.SourceDir, pattern, SearchOption.AllDirectories))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            certificates.Length == 0,
            $"A certificate must never be committed (AGENTS.md §9): {string.Join(", ", certificates)}");
    }

    // ---- Helpers ----

    private static XDocument Manifest => ReadXml(ManifestPath);

    private static XDocument Project => ReadXml(ProjectPath);

    private static XDocument ReadXml(string path)
    {
        Assert.True(File.Exists(path), $"The file does not exist: {path}");
        return XDocument.Load(path);
    }

    // Elements by local name, whatever the section namespace: capabilities live in rescap, everything else in the
    // default appx namespace, and the project in none.
    private static IEnumerable<XElement> Named(XContainer document, string localName) =>
        document.Descendants().Where(element => element.Name.LocalName == localName);
}
