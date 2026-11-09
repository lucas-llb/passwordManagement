using System.Xml.Linq;
using PasswordManager.App.E2E.Tests.Localization;

namespace PasswordManager.App.E2E.Tests.Packaging;

// W-30 static guards over the package identity and its version mirror: Identity/@Name is frozen (a change moves the
// PackageFamilyName and with it the LocalState where vaults live), Publisher is compared verbatim to the signing
// certificate subject, and the csproj <Version> is the human-facing source the manifest mirrors as a 4-part MSIX
// version. They read the repository straight from AppDir, as the other static guards do, and open no window, so they
// run in Debug and Release.
public sealed class PackageIdentityTests
{
    // Read this string as final before changing it: the PackageFamilyName, and the LocalState folder holding vaults,
    // is derived from it.
    private const string FrozenIdentityName = "com.passwordmanagement.windows";

    // A lowercase reverse-DNS label: letters, digits and inner hyphens, never an empty label or a leading/trailing dash.
    private const string DnsLabelPattern = "^[a-z0-9]([a-z0-9-]*[a-z0-9])?$";

    private static string ManifestPath => Path.Combine(AppResources.AppDir, "Package.appxmanifest");

    private static string ProjectPath => Path.Combine(AppResources.AppDir, "PasswordManager.App.csproj");

    // ---- The manifest ----

    [Fact]
    public void Manifest_IdentityNameIsFrozen()
    {
        var name = IdentityValue("Name");

        Assert.Equal(FrozenIdentityName, name);

        var labels = name.Split('.');
        Assert.True(labels.Length >= 2, $"The identity name must be reverse-DNS, it is '{name}'.");
        Assert.DoesNotContain(string.Empty, labels);
        Assert.All(labels, label => Assert.Matches(DnsLabelPattern, label));
    }

    [Fact]
    public void Manifest_PublisherIsAFullDn()
    {
        var publisher = IdentityValue("Publisher");

        // The signing script compares this verbatim to X509Certificate2.Subject, so surrounding whitespace or a bare
        // value would never match.
        Assert.Equal(publisher.Trim(), publisher);
        Assert.StartsWith("CN=", publisher, StringComparison.Ordinal);
        Assert.False(
            string.IsNullOrWhiteSpace(publisher["CN=".Length..]),
            "The publisher DN has an empty common name.");
    }

    [Fact]
    public void Manifest_VersionIsFourNumericParts()
    {
        // The MSIX Identity version is 4-part; its first three parts are the csproj <Version>.
        Assert.Matches(@"^\d+\.\d+\.\d+\.\d+$", IdentityValue("Version"));
    }

    // ---- The project ----

    [Fact]
    public void Csproj_VersionMirrorsTheManifest()
    {
        var version = Value(Project, "Version");
        var manifestVersion = IdentityValue("Version");

        // The release preflight in tools/New-Release.ps1 applies the same rule: the manifest reads <Version>.0.
        Assert.True(
            string.Equals($"{version}.0", manifestVersion, StringComparison.Ordinal),
            $"The App csproj <Version> is '{version}', so the manifest must read '{version}.0', it is '{manifestVersion}'.");
    }

    [Fact]
    public void Csproj_DeclinesTheSymbolPackage()
    {
        var project = Project;

        // No crash reporting or telemetry (AGENTS.md §1 and §7) and the Store does not require symbols, so 1.0 ships
        // without a .appxsym. AppxBundle stays off because the release recipe produces one .msix per RID.
        var enabled = Value(project, "AppxSymbolPackageEnabled");
        Assert.True(
            enabled.Equals("false", StringComparison.OrdinalIgnoreCase),
            $"AppxSymbolPackageEnabled must stay false for 1.0, it is '{enabled}'.");
        Assert.Empty(Named(project, "AppxBundle"));
    }

    [Fact]
    public void Csproj_StillHasNoSigningKey()
    {
        // Signing stays out of band: the key is a certificate outside the repository, never a csproj property.
        var project = Project;

        Assert.Empty(Named(project, "PackageCertificateKeyFile"));
        Assert.Empty(Named(project, "PackageCertificateThumbprint"));

        var generateOnBuild = Named(project, "GenerateAppxPackageOnBuild")
            .Select(element => element.Value.Trim())
            .ToArray();
        Assert.DoesNotContain(generateOnBuild, value => value.Equals("true", StringComparison.OrdinalIgnoreCase));
    }

    // ---- Helpers ----

    private static XDocument Manifest => ReadXml(ManifestPath);

    private static XDocument Project => ReadXml(ProjectPath);

    private static XDocument ReadXml(string path)
    {
        Assert.True(File.Exists(path), $"The file does not exist: {path}");
        return XDocument.Load(path);
    }

    private static string IdentityValue(string attributeName) =>
        (string?)Assert.Single(Named(Manifest, "Identity")).Attribute(attributeName)
        ?? throw new InvalidDataException($"The manifest Identity has no '{attributeName}' attribute.");

    private static string Value(XDocument project, string localName) =>
        Assert.Single(Named(project, localName)).Value.Trim();

    // Elements by local name, whatever the section namespace: the manifest uses the appx namespace and the SDK project
    // none, but the helper stays the same shape as PackageManifestTests so both read the repository the same way.
    private static IEnumerable<XElement> Named(XContainer document, string localName) =>
        document.Descendants().Where(element => element.Name.LocalName == localName);
}
