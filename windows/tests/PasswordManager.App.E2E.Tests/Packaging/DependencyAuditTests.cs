using System.Text.Json;
using System.Xml.Linq;
using PasswordManager.App.E2E.Tests.Localization;

namespace PasswordManager.App.E2E.Tests.Packaging;

// W-31 static guards over the NuGet dependency audit: Central Package Management stays the single version source, the
// direct list is the audited one, no package that ships in the MSIX is a network or telemetry package, the test
// runner's telemetry stays out of the shipped projects, every project commits a lockfile with hashes, the audit
// document records the final list, the app sources use neither WebView2 nor the Windows AI APIs, and the architecture
// and the epic record the story. They read the repository straight from AppResources, as the other static guards do,
// and open no window, so they run in Debug and Release.
public sealed class DependencyAuditTests
{
    // The six projects and the direct (PackageReference) list the audit approves for each. A new project or a new
    // PackageReference fails here until docs/dependency-audit.md and this map change together.
    private static readonly Dictionary<string, (string RelativePath, string[] DirectPackages)> AuditedProjects =
        new(StringComparer.Ordinal)
        {
            ["PasswordManager.App"] = (
                Path.Combine("src", "PasswordManager.App", "PasswordManager.App.csproj"),
                ["CommunityToolkit.Mvvm", "Microsoft.Windows.SDK.BuildTools", "Microsoft.WindowsAppSDK"]),
            ["PasswordManager.App.E2E.Tests"] = (
                Path.Combine("tests", "PasswordManager.App.E2E.Tests", "PasswordManager.App.E2E.Tests.csproj"),
                ["FlaUI.UIA3", "xunit.v3"]),
            ["PasswordManager.Core"] = (
                Path.Combine("src", "PasswordManager.Core", "PasswordManager.Core.csproj"),
                ["NSec.Cryptography"]),
            ["PasswordManager.Core.Tests"] = (
                Path.Combine("tests", "PasswordManager.Core.Tests", "PasswordManager.Core.Tests.csproj"),
                ["xunit.v3"]),
            ["PasswordManager.ViewModels"] = (
                Path.Combine("src", "PasswordManager.ViewModels", "PasswordManager.ViewModels.csproj"),
                ["CommunityToolkit.Mvvm"]),
            ["PasswordManager.ViewModels.Tests"] = (
                Path.Combine("tests", "PasswordManager.ViewModels.Tests", "PasswordManager.ViewModels.Tests.csproj"),
                ["xunit.v3"]),
        };

    // Assembled into the MSIX: their lockfiles are the audited shipped graph.
    private static readonly string[] ShippedProjects =
        ["PasswordManager.App", "PasswordManager.Core", "PasswordManager.ViewModels"];

    // Never shipped: the E2E project references the App with ReferenceOutputAssembly="false".
    private static readonly string[] TestProjects =
        ["PasswordManager.Core.Tests", "PasswordManager.ViewModels.Tests", "PasswordManager.App.E2E.Tests"];

    // Package ids that would open a network path or report telemetry from the app (AGENTS.md §1 and §7).
    private static readonly string[] ForbiddenPackageTokens =
    [
        "ApplicationInsights", "Telemetry", "OpenTelemetry", "Sentry", "AppCenter", "Crashlytics", "NewRelic",
        "Datadog", "OpenCensus", "Analytics", "Mixpanel", "RestSharp", "Refit", "Grpc", "Microsoft.Extensions.Http",
        "System.Net.Http",
    ];

    // xunit.v3 pulls both of these for Microsoft.Testing.Platform; they are acceptable only in the test projects.
    private static readonly string[] TestRunnerTelemetryPackages =
        ["Microsoft.ApplicationInsights", "Microsoft.Testing.Extensions.Telemetry"];

    // The headings and tokens docs/dependency-audit.md must carry: the executable contract of the audit document.
    private static readonly string[] AuditDocumentHeadings =
    [
        "# Auditoria de dependências (W-31)",
        "## Lista final de pacotes diretos",
        "## Grafo transitivo do app",
        "## Só de teste",
        "## Só de build",
        "## Veredito",
        "## Como reproduzir",
    ];

    private static readonly string[] AuditDocumentTokens =
    [
        "AGENTS", "§7", "packages.lock.json", "offline", "NSec.Cryptography", "CommunityToolkit.Mvvm",
        "Microsoft.WindowsAppSDK", "Microsoft.Windows.SDK.BuildTools", "xunit.v3", "FlaUI.UIA3",
        "Microsoft.Web.WebView2", "Microsoft.Windows.AI.MachineLearning", "System.Numerics.Tensors",
        "Microsoft.ApplicationInsights", "Microsoft.NET.ILLink.Tasks",
    ];

    // Shipped assemblies the app must never host: WebView2 would make it a network client, and the Windows AI/ML
    // components are accepted as unused in the audit.
    private static readonly string[] ForbiddenSourceTokens =
        ["WebView2", "Microsoft.Web.WebView2", "Microsoft.Windows.AI"];

    private static readonly string[] SourcePatterns = ["*.cs", "*.xaml"];

    private static string WindowsDir => Path.GetFullPath(Path.Combine(AppResources.SourceDir, ".."));

    private static string RepoRoot => Path.GetFullPath(Path.Combine(WindowsDir, ".."));

    private static string PackagesPropsPath => Path.Combine(WindowsDir, "Directory.Packages.props");

    private static string BuildPropsPath => Path.Combine(WindowsDir, "Directory.Build.props");

    private static string AuditDocumentPath => Path.Combine(RepoRoot, "docs", "dependency-audit.md");

    private static string ArchitecturePath => Path.Combine(RepoRoot, "docs", "architecture.md");

    private static string EpicPath => Path.Combine(RepoRoot, "docs", "epic-windows-release.md");

    private static XDocument PackagesProps => ReadXml(PackagesPropsPath);

    // ---- Central Package Management ----

    [Fact]
    public void Props_PinsEveryReferencedPackage()
    {
        // Central Package Management makes Directory.Packages.props the single version source: a PackageReference
        // without a matching PackageVersion fails the restore, and a PackageVersion nobody references hides the audit
        // drifting away from the graph.
        var pinned = Named(PackagesProps, "PackageVersion")
            .Select(element => RequiredAttribute(element, "Include"))
            .ToArray();

        var duplicated = pinned
            .GroupBy(id => id, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.True(
            duplicated.Length == 0,
            $"Directory.Packages.props pins a package more than once: {string.Join(", ", duplicated)}.");

        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in AuditedProjects.Keys)
        {
            foreach (var id in PackageReferenceIds(name))
            {
                Assert.True(
                    pinned.Contains(id, StringComparer.OrdinalIgnoreCase),
                    $"{name} references {id}, which Directory.Packages.props does not pin.");
                referenced.Add(id);
            }
        }

        var orphans = pinned
            .Where(id => !referenced.Contains(id))
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.True(
            orphans.Length == 0,
            $"Directory.Packages.props pins packages no project references: {string.Join(", ", orphans)}.");
    }

    [Fact]
    public void Projects_PinNoInlineVersion()
    {
        // CPM is the single version source (AGENTS.md §7: versions pinned in Directory.Packages.props and lockfiles);
        // an inline Version or VersionOverride would silently win over the audited one.
        var central = Assert.Single(Named(PackagesProps, "ManagePackageVersionsCentrally"));
        Assert.True(
            string.Equals(central.Value.Trim(), "true", StringComparison.OrdinalIgnoreCase),
            $"Directory.Packages.props must set ManagePackageVersionsCentrally to true, it is '{central.Value.Trim()}'.");

        foreach (var name in AuditedProjects.Keys)
        {
            var inline = Named(Project(name), "PackageReference")
                .SelectMany(reference => reference.Attributes()
                    .Where(attribute => attribute.Name.LocalName is "Version" or "VersionOverride")
                    .Select(attribute => $"{attribute.Name.LocalName} on {RequiredAttribute(reference, "Include")}"))
                .ToArray();
            Assert.True(
                inline.Length == 0,
                $"{name} pins a version outside Central Package Management: {string.Join(", ", inline)}.");
        }
    }

    [Fact]
    public void DirectPackages_AreTheAuditedList()
    {
        // A new dependency must change the audit and this map together; an unknown csproj fails too, so the guard
        // cannot be outrun by adding a project.
        var discovered = DiscoveredProjectNames();
        Assert.Equal(AuditedProjects.Keys.Order(StringComparer.Ordinal), discovered);

        foreach (var (name, audit) in AuditedProjects)
        {
            Assert.Equal(audit.DirectPackages.Order(StringComparer.Ordinal), PackageReferenceIds(name));
        }
    }

    // ---- The shipped graph ----

    [Fact]
    public void ShippedGraph_HasNoNetworkOrTelemetryPackage()
    {
        // The audit's central claim: no package that ships in the MSIX opens a network path or reports telemetry. The
        // libsodium positive control proves the lockfile was really read.
        foreach (var name in ShippedProjects)
        {
            var packages = LockFilePackageNames(name);

            var forbidden = packages
                .Where(id => ForbiddenPackageTokens.Any(token => id.Contains(token, StringComparison.OrdinalIgnoreCase)))
                .Order(StringComparer.Ordinal)
                .ToArray();
            Assert.True(
                forbidden.Length == 0,
                $"{name} ships a network or telemetry package: {string.Join(", ", forbidden)}.");

            Assert.True(
                packages.Contains("libsodium", StringComparer.OrdinalIgnoreCase),
                $"The {name} lockfile does not list libsodium: the shipped graph was not read.");
        }
    }

    [Fact]
    public void TestTooling_StaysOutOfTheShippedProjects()
    {
        // xunit.v3 brings Microsoft.Testing.Platform, which pulls Microsoft.ApplicationInsights and
        // Microsoft.Testing.Extensions.Telemetry. They are acceptable only in the test projects; the shipped graph
        // never carries them.
        foreach (var name in TestProjects)
        {
            var packages = LockFilePackageNames(name);
            foreach (var id in TestRunnerTelemetryPackages)
            {
                Assert.True(
                    packages.Contains(id, StringComparer.OrdinalIgnoreCase),
                    $"The {name} lockfile does not list {id}: the test-runner graph this audit documents changed.");
            }
        }

        foreach (var name in ShippedProjects)
        {
            var packages = LockFilePackageNames(name);

            var leaked = TestRunnerTelemetryPackages
                .Where(id => packages.Contains(id, StringComparer.OrdinalIgnoreCase))
                .ToArray();
            Assert.True(leaked.Length == 0, $"{name} ships test-runner telemetry: {string.Join(", ", leaked)}.");

            var testing = packages
                .Where(id => id.Contains("Microsoft.Testing", StringComparison.OrdinalIgnoreCase))
                .Order(StringComparer.Ordinal)
                .ToArray();
            Assert.True(
                testing.Length == 0,
                $"{name} ships Microsoft.Testing packages: {string.Join(", ", testing)}.");
        }
    }

    [Fact]
    public void EveryProject_CommitsALockFile()
    {
        // RestorePackagesWithLockFile=true makes the lockfile part of the build contract; version 2 is the format of
        // the six committed files, and a resolved package without a contentHash is a lockfile that was hand-edited.
        var buildProps = ReadXml(BuildPropsPath);
        var restore = Assert.Single(Named(buildProps, "RestorePackagesWithLockFile"));
        Assert.True(
            string.Equals(restore.Value.Trim(), "true", StringComparison.OrdinalIgnoreCase),
            $"Directory.Build.props must keep RestorePackagesWithLockFile=true, it is '{restore.Value.Trim()}'.");

        foreach (var name in AuditedProjects.Keys)
        {
            var path = LockFilePath(name);
            Assert.True(File.Exists(path), $"{name} does not commit packages.lock.json.");
            Assert.True(LockFileVersion(path) == 2, $"{name}/packages.lock.json is not version 2.");

            var unpinned = LockFileEntries(path)
                .Where(entry => !string.Equals(entry.Type, "Project", StringComparison.Ordinal))
                .Where(entry => string.IsNullOrEmpty(entry.Resolved) || string.IsNullOrEmpty(entry.ContentHash))
                .Select(entry => entry.Id)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.Ordinal)
                .ToArray();
            Assert.True(
                unpinned.Length == 0,
                $"{name}/packages.lock.json has packages without a resolved version or a contentHash: {string.Join(", ", unpinned)}.");
        }
    }

    // ---- The audit document and its documentation ----

    [Fact]
    public void AuditDocument_RecordsTheFinalList()
    {
        // The audit is the record: the final direct list, the transitive graph with a verdict per package, the
        // test-only and build-only sections, the AGENTS.md §7 verdict and how to reproduce it. A package that leaves
        // the graph has to leave the document too, which is what the tokens pin.
        var document = Read(AuditDocumentPath);

        var missingHeadings = AuditDocumentHeadings
            .Where(heading => !document.Contains(heading, StringComparison.Ordinal))
            .ToArray();
        Assert.True(
            missingHeadings.Length == 0,
            $"docs/dependency-audit.md no longer has the headings: {string.Join(", ", missingHeadings)}.");

        Assert.Matches(@"\b\d{4}-\d{2}-\d{2}\b", document);

        var missingTokens = AuditDocumentTokens
            .Where(token => !document.Contains(token, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Assert.True(
            missingTokens.Length == 0,
            $"docs/dependency-audit.md no longer names: {string.Join(", ", missingTokens)}.");
    }

    [Fact]
    public void Sources_UseNeitherWebView2NorTheWindowsAiApis()
    {
        // WebView2 and the Windows App SDK AI/ML components ship with the metapackage but the app never hosts them.
        // The guard keeps a future screen from quietly turning the app into a network client.
        var offenders = new List<string>();
        foreach (var pattern in SourcePatterns)
        {
            foreach (var path in AppResources.SourceFiles(AppResources.SourceDir, pattern))
            {
                var text = File.ReadAllText(path);
                if (ForbiddenSourceTokens.Any(token => text.Contains(token, StringComparison.OrdinalIgnoreCase)))
                {
                    offenders.Add(Path.GetRelativePath(WindowsDir, path));
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            $"Windows sources now mention WebView2 or the Windows AI APIs: {string.Join(", ", offenders)}.");
    }

    [Fact]
    public void Docs_RecordTheAudit()
    {
        // §9.19 tells the story, §12 keeps the existing "no network or telemetry" claim qualified by the link to the
        // audit, and the epic marks W-31 delivered like the previous stories.
        var architecture = Read(ArchitecturePath);
        Assert.Contains("### 9.19", architecture, StringComparison.Ordinal);
        Assert.Contains("Auditoria de dependências", architecture, StringComparison.Ordinal);
        Assert.Contains("dependency-audit.md", Section(architecture, "## 12."), StringComparison.Ordinal);

        var delivery = Assert.Single(
            File.ReadAllLines(EpicPath),
            line => line.StartsWith("| W-31 |", StringComparison.Ordinal));
        Assert.Contains("Entregue:", delivery, StringComparison.Ordinal);
    }

    // ---- Helpers ----

    private static XDocument Project(string name) => ReadXml(ProjectPath(name));

    private static string ProjectPath(string name) =>
        Path.Combine(WindowsDir, AuditedProjects[name].RelativePath);

    private static string LockFilePath(string name) =>
        Path.Combine(Path.GetDirectoryName(ProjectPath(name))!, "packages.lock.json");

    private static string[] PackageReferenceIds(string name) =>
        [.. Named(Project(name), "PackageReference")
            .Select(reference => RequiredAttribute(reference, "Include"))
            .Order(StringComparer.Ordinal)];

    private static string[] DiscoveredProjectNames()
    {
        string[] roots = [Path.Combine(WindowsDir, "src"), Path.Combine(WindowsDir, "tests")];
        return
        [
            .. roots
                .SelectMany(root => Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories))
                .Where(path => !IsBuildOutput(path))
                .Select(path => Path.GetFileNameWithoutExtension(path)!)
                .Order(StringComparer.Ordinal),
        ];
    }

    private static string[] LockFilePackageNames(string name) =>
        [.. LockFileEntries(LockFilePath(name))
            .Select(entry => entry.Id)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.Ordinal)];

    private static int LockFileVersion(string path)
    {
        using var document = JsonDocument.Parse(Read(path));
        return document.RootElement.GetProperty("version").GetInt32();
    }

    // Every package entry of every TFM and RID subgraph, kept apart so a hash missing in one subgraph is not hidden.
    private static List<(string Id, string Type, string? Resolved, string? ContentHash)> LockFileEntries(string path)
    {
        using var document = JsonDocument.Parse(Read(path));
        List<(string Id, string Type, string? Resolved, string? ContentHash)> entries = [];
        foreach (var framework in document.RootElement.GetProperty("dependencies").EnumerateObject())
        {
            foreach (var package in framework.Value.EnumerateObject())
            {
                var value = package.Value;
                entries.Add((
                    package.Name,
                    value.GetProperty("type").GetString() ?? string.Empty,
                    value.TryGetProperty("resolved", out var resolved) ? resolved.GetString() : null,
                    value.TryGetProperty("contentHash", out var hash) ? hash.GetString() : null));
            }
        }

        return entries;
    }

    // The text of a top-level "## ..." section, from its heading to the next one.
    private static string Section(string markdown, string heading)
    {
        var start = markdown.IndexOf(heading, StringComparison.Ordinal);
        Assert.True(start >= 0, $"docs/architecture.md has no '{heading}' section.");
        var end = markdown.IndexOf("\n## ", start + heading.Length, StringComparison.Ordinal);
        return end < 0 ? markdown[start..] : markdown[start..end];
    }

    private static XDocument ReadXml(string path)
    {
        Assert.True(File.Exists(path), $"The file does not exist: {path}");
        return XDocument.Load(path);
    }

    private static string Read(string path)
    {
        Assert.True(File.Exists(path), $"The file does not exist: {path}");
        return File.ReadAllText(path);
    }

    private static string RequiredAttribute(XElement element, string name) =>
        (string?)element.Attribute(name)
            ?? throw new InvalidDataException($"A <{element.Name.LocalName}> element has no {name} attribute.");

    // Elements by local name, whatever the section namespace: the SDK projects have none, but the helper stays the same
    // shape as ReleaseBuildTests so both read the repository the same way.
    private static IEnumerable<XElement> Named(XContainer document, string localName) =>
        document.Descendants().Where(element => element.Name.LocalName == localName);

    // Build output holds generated copies of the sources and of the project files, so it never counts.
    private static bool IsBuildOutput(string path)
    {
        var segments = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return segments.Any(segment =>
            segment.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
            segment.Equals("obj", StringComparison.OrdinalIgnoreCase));
    }
}
