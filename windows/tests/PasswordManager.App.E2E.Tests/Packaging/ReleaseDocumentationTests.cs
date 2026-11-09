using System.Text.RegularExpressions;
using System.Xml.Linq;
using PasswordManager.App.E2E.Tests.Localization;

namespace PasswordManager.App.E2E.Tests.Packaging;

// W-30 static guards over the release documentation: the changelog is the single source of the release notes and must
// carry a section for the csproj version (New-Release.ps1 refuses to publish without it), the process document must
// name the shipped commands and keep the certificate outside the repository, and the architecture and the epic must
// record the signing story. They read the repository straight from AppResources, as the other static guards do, and
// open no window, so they run in Debug and Release.
public sealed class ReleaseDocumentationTests
{
    private static string RepoRoot => Path.GetFullPath(Path.Combine(AppResources.SourceDir, "..", ".."));

    private static string ProjectPath => Path.Combine(AppResources.AppDir, "PasswordManager.App.csproj");

    private static string ChangelogPath => Path.Combine(RepoRoot, "CHANGELOG.md");

    private static string ReleaseProcessPath => Path.Combine(RepoRoot, "docs", "release-process.md");

    private static string ArchitecturePath => Path.Combine(RepoRoot, "docs", "architecture.md");

    private static string EpicPath => Path.Combine(RepoRoot, "docs", "epic-windows-release.md");

    // ---- The changelog ----

    [Fact]
    public void Changelog_HasASectionForTheCurrentVersion()
    {
        // New-Release.ps1 looks for the same heading before it touches a package, and the heading must be the newest
        // version section: copying the notes from an older release is the mistake this guard exists to catch.
        var version = ProjectVersion();
        var headings = Regex.Matches(Read(ChangelogPath), @"(?m)^##\s+\[([^\]]+)\]")
            .Select(match => match.Groups[1].Value.Trim())
            .ToArray();

        var versions = headings.Where(heading => Regex.IsMatch(heading, @"^\d+\.\d+\.\d+$")).ToArray();
        Assert.True(versions.Length > 0, "CHANGELOG.md has no version heading (expected '## [<version>]').");
        Assert.True(
            string.Equals(version, versions[0], StringComparison.Ordinal),
            $"The newest CHANGELOG.md version is '{versions[0]}', but the App csproj <Version> is '{version}'.");
    }

    [Fact]
    public void Changelog_WarnsThatThereIsNoRecoveryWithoutTheKit()
    {
        // The epic's release notes are copied from here, so the 1.0 warning has to live in the file, in pt-BR.
        var changelog = Read(ChangelogPath);

        Assert.Contains("Kit de Recuperação", changelog, StringComparison.Ordinal);
        Assert.Contains("não há recuperação", changelog, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("senha mestra", changelog, StringComparison.OrdinalIgnoreCase);
    }

    // ---- The process document ----

    [Fact]
    public void ReleaseProcess_NamesEveryCommand()
    {
        var process = Read(ReleaseProcessPath);

        // The process is only usable while it names the driver, the W-29 recipe it wraps, the password variable, the
        // hash file, the changelog and both publication channels.
        string[] required =
        [
            "New-Release.ps1",
            "Publish-Release.ps1",
            "PM_CERT_PASSWORD",
            "sha256",
            "CHANGELOG.md",
            "GitHub Releases",
            "Store",
        ];
        AssertNames(process, ReleaseProcessPath, required);
    }

    [Fact]
    public void ReleaseProcess_SaysTheKeyStaysOutsideTheRepository()
    {
        // AGENTS.md section 9: key material never enters the repository, so neither may the instructions.
        var process = Read(ReleaseProcessPath);

        Assert.Contains("fora do repositório", process, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("certificado", process, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("-CertificatePath", process, StringComparison.Ordinal);
    }

    [Fact]
    public void ReleaseProcess_HasNoSecretOrDeveloperPath()
    {
        // A copied command line with a real certificate path or password is a leak in waiting.
        var process = Read(ReleaseProcessPath);

        Assert.DoesNotContain(@"C:\Users\", process, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/p ", process, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"[A-Za-z]:\\", process);
    }

    // ---- The architecture and the epic ----

    [Fact]
    public void Architecture_DocumentsSigning()
    {
        var architecture = Read(ArchitecturePath);

        Assert.Contains("### 9.18", architecture, StringComparison.Ordinal);
        Assert.Contains("Assinatura e publicação", architecture, StringComparison.Ordinal);

        // The epic marks delivered stories with an "Entregue:" paragraph in the story's row; W-30 must be one of them.
        var delivery = Assert.Single(File.ReadAllLines(EpicPath),
            line => line.StartsWith("| W-30 |", StringComparison.Ordinal));
        Assert.Contains("Entregue:", delivery, StringComparison.Ordinal);
        Assert.Contains("Nenhum certificado real foi usado", delivery, StringComparison.Ordinal);
    }

    // ---- Helpers ----

    private static string ProjectVersion()
    {
        Assert.True(File.Exists(ProjectPath), $"The file does not exist: {ProjectPath}");
        var project = XDocument.Load(ProjectPath);
        return Assert.Single(project.Descendants(), element => element.Name.LocalName == "Version").Value.Trim();
    }

    private static string Read(string path)
    {
        Assert.True(File.Exists(path), $"The file does not exist: {path}");
        return File.ReadAllText(path);
    }

    private static void AssertNames(string text, string path, string[] required)
    {
        var missing = required.Where(name => !text.Contains(name, StringComparison.OrdinalIgnoreCase)).ToArray();
        Assert.True(
            missing.Length == 0,
            $"{Path.GetFileName(path)} no longer names: {string.Join(", ", missing)}.");
    }
}
