using System.Xml.Linq;
using PasswordManager.App.E2E.Tests.Localization;

namespace PasswordManager.App.E2E.Tests.Packaging;

// W-29 static guards over the release recipe: the App stays self-contained for both RIDs, trimming and ReadyToRun live
// only in tools/Publish-Release.ps1 (so a developer Release build never runs ILLink or rewrites packages.lock.json),
// publish items are materialized before the MSIX target lets an optimization run, and no signing key is committed. They
// read the repository straight from AppResources, as the other static guards do, and open no window, so they run in
// Debug and Release.
public sealed class ReleaseBuildTests
{
    // The two artifacts the recipe produces and the smoke tests exercise; the script must keep naming both.
    private static readonly string[] RuntimeIdentifiers = ["win-x64", "win-arm64"];

    private static string ToolsDir => Path.GetFullPath(Path.Combine(AppResources.SourceDir, "..", "tools"));

    private static string ProjectPath => Path.Combine(AppResources.AppDir, "PasswordManager.App.csproj");

    private static string PublishScriptPath => Path.Combine(ToolsDir, "Publish-Release.ps1");

    private static string MeasureScriptPath => Path.Combine(ToolsDir, "Measure-Release.ps1");

    // ---- The project ----

    [Fact]
    public void Csproj_IsSelfContainedForBothRids()
    {
        var project = Project;

        AssertProperty(project, "SelfContained", "true");
        AssertProperty(project, "WindowsAppSDKSelfContained", "true");

        var declared = Value(project, "RuntimeIdentifiers")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // Containing both RIDs, not exactly two: a later target may be added without breaking the guard.
        Assert.Contains("win-x64", declared);
        Assert.Contains("win-arm64", declared);
    }

    [Fact]
    public void Csproj_HasNoTrimmingOrReadyToRun()
    {
        // D5 resolved as trimming OFF: the Windows App SDK/WinRT assemblies are not trim-compatible (IL2104 in
        // Microsoft.Windows.SDK.NET and WinRT.Runtime, then NETSDK1144). ReadyToRun belongs to the release publish in
        // tools/Publish-Release.ps1, never to a developer Release build: ILLink would run on every build and its task
        // package would enter the restore graph, which is the packages.lock.json churn W-29 closes. Core keeps
        // IsTrimmable; the App carries neither switch, whatever the condition.
        var project = Project;

        Assert.Empty(Named(project, "PublishTrimmed"));
        Assert.Empty(Named(project, "PublishReadyToRun"));
    }

    [Fact]
    public void Csproj_MaterializesPublishItemsBeforeOptimization()
    {
        // The MSIX trap: EnableMsixTooling wires PrepareMsixPackage into Build, and its
        // __SnapshotResolvedFileToPublishBeforeOptimization runs BeforeTargets CreateReadyToRunImages and ILLink before
        // ComputeResolvedFilesToPublishList has filled ResolvedFileToPublish. The optimizations then run on an empty list,
        // are cached as built and silently no-op in the real publish. This target must stay: it is what makes the
        // release ReadyToRun (and the -Trim opt-in) actually run.
        var target = Named(Project, "Target")
            .SingleOrDefault(element => (string?)element.Attribute("Name") == "_EnsurePublishItemsForOptimization");
        Assert.True(
            target is not null,
            "The App csproj no longer defines _EnsurePublishItemsForOptimization: ReadyToRun and -Trim would silently no-op.");

        var dependsOn = ((string?)target!.Attribute("DependsOnTargets") ?? string.Empty)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Contains("ComputeResolvedFilesToPublishList", dependsOn);
    }

    [Fact]
    public void Csproj_HasNoSigningCertificate()
    {
        // Signing is W-30 and needs a key outside the repository; a certificate path or thumbprint here would break the
        // unsigned-package contract (PackageManifestTests.Csproj_IsPackagedByDefault checks the same pair).
        var project = Project;

        Assert.Empty(Named(project, "PackageCertificateKeyFile"));
        Assert.Empty(Named(project, "PackageCertificateThumbprint"));
    }

    // ---- The scripts ----

    [Fact]
    public void PublishScript_CoversBothRidsAndTheReleaseSwitches()
    {
        var script = ReadScript(PublishScriptPath);

        // ReadyToRun is the release default. Trimming is OFF (D5: IL2104 / NETSDK1144), so PublishTrimmed must appear
        // only as the documented -Trim opt-in, never as the default switch; RestorePackagesWithLockFile=false keeps its
        // task package out of the committed lock files even when that flag is exercised. The test checks the tokens, not
        // the default publish line: a -Trim opt-in is allowed to name PublishTrimmed.
        string[] required =
        [
            .. RuntimeIdentifiers,
            "-Trim",
            "PublishTrimmed",
            "PublishReadyToRun",
            "RestorePackagesWithLockFile=false",
        ];
        AssertNames(script, PublishScriptPath, required);
    }

    [Fact]
    public void MeasureScript_MeasuresSizeAndWarmStartup()
    {
        var script = ReadScript(MeasureScriptPath);

        // The numbers §9.17 records: the .msix and publish-folder sizes, and the warm startup to the first *visible*
        // main window (a handle alone can belong to a hidden host window, so both are checked).
        string[] required = ["ExePath", "MsixPath", "Runs", "Warmup", "CsvPath", "MainWindowHandle", "IsWindowVisible"];
        AssertNames(script, MeasureScriptPath, required);
    }

    // ---- Helpers ----

    private static XDocument Project => ReadXml(ProjectPath);

    private static XDocument ReadXml(string path)
    {
        Assert.True(File.Exists(path), $"The file does not exist: {path}");
        return XDocument.Load(path);
    }

    private static string ReadScript(string path)
    {
        Assert.True(File.Exists(path), $"The script does not exist: {path}");
        return File.ReadAllText(path);
    }

    private static void AssertNames(string text, string path, string[] required)
    {
        var missing = required.Where(name => !text.Contains(name, StringComparison.Ordinal)).ToArray();
        Assert.True(
            missing.Length == 0,
            $"{Path.GetFileName(path)} no longer names: {string.Join(", ", missing)}.");
    }

    private static void AssertProperty(XDocument project, string name, string expected)
    {
        // The App may state a property once; a second copy would silently shadow the first.
        var actual = Value(project, name);
        Assert.True(
            actual.Equals(expected, StringComparison.OrdinalIgnoreCase),
            $"{name} must be '{expected}', it is '{actual}'.");
    }

    private static string Value(XDocument project, string localName) =>
        Assert.Single(Named(project, localName)).Value.Trim();

    // Elements by local name, whatever the section namespace: the SDK project has none, but the helpers stay the same
    // shape as PackageManifestTests so both read the repository the same way.
    private static IEnumerable<XElement> Named(XContainer document, string localName) =>
        document.Descendants().Where(element => element.Name.LocalName == localName);
}
