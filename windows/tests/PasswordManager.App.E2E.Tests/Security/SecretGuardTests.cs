using PasswordManager.App.E2E.Tests.Localization;

namespace PasswordManager.App.E2E.Tests.Security;

// W-32 static guards over the security review (docs/security-review.md): no logging API and no SecureString in
// windows/src, no ToString() override outside the audited pair, and the review, the architecture and the epic keep
// recording the story. They read the repository straight from AppResources, as the other static guards do, and open no
// window, so they run in Debug and Release.
public sealed class SecretGuardTests
{
    // Every API that would let a secret reach a log, a debugger or the event log. The scan is textual on purpose, exactly
    // like the sweep in the review: a comment or a string that mentions one of them fails too.
    private static readonly string[] LoggingTokens =
    [
        "Console.", "Debug.Write", "Debug.Print", "Trace.", "TraceSource", "ILogger", "EventLog", "Debugger.",
    ];

    // AGENTS.md §3.2 forbids SecureString: it gives a false sense of protection over a managed string.
    private static readonly string[] SecureStringTokens = ["SecureString"];

    // The only two ToString() overrides the review audited: VaultEntry prints Id and Title, EntryDetailViewModel prints
    // the type name. A new one fails until the review audits it and this list grows with it.
    private static readonly string[] AuditedToStringFiles =
    [
        Path.Combine("src", "PasswordManager.Core", "Vault", "VaultEntry.cs"),
        Path.Combine("src", "PasswordManager.ViewModels", "EntryDetailViewModel.cs"),
    ];

    private const string ToStringDeclaration = "override string ToString()";

    // The document contract of the review: its title and the eight sections it has to keep.
    private static readonly string[] ReviewHeadings =
    [
        "# Revisão de segurança (W-32)",
        "## Método e escopo",
        "## Modelo de ameaças: linha a linha",
        "## Segredos em log e em ToString()",
        "## Zeragem de chaves no Lock",
        "## Strings gerenciadas e limites aceitos",
        "## Achados e correções",
        "## Veredito",
        "## Como reproduzir",
    ];

    private static readonly string[] ReviewTokens =
        ["threat-model.md", "AGENTS", "§3.5", "ZeroMemory", "ToString", "PasswordBox"];

    private static string WindowsDir => Path.GetFullPath(Path.Combine(AppResources.SourceDir, ".."));

    private static string RepoRoot => Path.GetFullPath(Path.Combine(WindowsDir, ".."));

    private static string ReviewPath => Path.Combine(RepoRoot, "docs", "security-review.md");

    private static string ArchitecturePath => Path.Combine(RepoRoot, "docs", "architecture.md");

    private static string EpicPath => Path.Combine(RepoRoot, "docs", "epic-windows-release.md");

    [Fact]
    public void Sources_UseNoLoggingApi()
    {
        // Positive control first: every token is found in a synthetic snippet, so a clean result from the real sources
        // cannot be an empty scan that passed silently.
        foreach (var token in LoggingTokens)
        {
            (string RelativePath, string Text)[] control = [("Synthetic.cs", $"// {token} in a synthetic snippet")];
            Assert.Equal("Synthetic.cs", Assert.Single(FilesContaining(control, LoggingTokens)));
        }

        var sources = SourceTexts();
        AssertSourcesWereRead(sources);

        var offenders = FilesContaining(sources, LoggingTokens);
        Assert.True(
            offenders.Length == 0,
            $"Windows sources now use a logging API ({string.Join(", ", LoggingTokens)}): {string.Join(", ", offenders)}.");
    }

    [Fact]
    public void Sources_UseNoSecureString()
    {
        var sources = SourceTexts();
        AssertSourcesWereRead(sources);

        var offenders = FilesContaining(sources, SecureStringTokens);
        Assert.True(
            offenders.Length == 0,
            $"Windows sources use SecureString, which AGENTS.md §3.2 forbids: {string.Join(", ", offenders)}.");
    }

    [Fact]
    public void Sources_OverrideToStringOnlyWhereAudited()
    {
        var sources = SourceTexts();
        AssertSourcesWereRead(sources);

        var overriding = sources
            .Where(source => source.Text.Contains(ToStringDeclaration, StringComparison.Ordinal))
            .Select(source => source.RelativePath)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var audited = AuditedToStringFiles.Order(StringComparer.Ordinal).ToArray();

        Assert.True(
            overriding.SequenceEqual(audited, StringComparer.Ordinal),
            $"The only files overriding ToString() under windows/src must be {string.Join(" and ", audited)}; found "
                + $"{string.Join(", ", overriding)}. A new override needs a review entry in docs/security-review.md before it can ship.");
    }

    [Fact]
    public void ReviewDocument_RecordsTheReview()
    {
        // The review is the record of the story: the threat-model matrix, the log and ToString() sweep, the Lock
        // zeroing, the accepted string limits, the findings, the verdict and how to reproduce. The architecture and the
        // epic point at it, so the story cannot be delivered without the document.
        var review = Read(ReviewPath);

        var missingHeadings = ReviewHeadings
            .Where(heading => !review.Contains(heading, StringComparison.Ordinal))
            .ToArray();
        Assert.True(
            missingHeadings.Length == 0,
            $"docs/security-review.md no longer has the headings: {string.Join(", ", missingHeadings)}.");

        Assert.Matches(@"\b\d{4}-\d{2}-\d{2}\b", review);

        var missingTokens = ReviewTokens
            .Where(token => !review.Contains(token, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Assert.True(
            missingTokens.Length == 0,
            $"docs/security-review.md no longer names: {string.Join(", ", missingTokens)}.");

        var architecture = Read(ArchitecturePath);
        Assert.Contains("### 9.20", architecture, StringComparison.Ordinal);
        Assert.Contains("Revisão de segurança", architecture, StringComparison.Ordinal);

        var delivery = Assert.Single(
            File.ReadAllLines(EpicPath),
            line => line.StartsWith("| W-32 |", StringComparison.Ordinal));
        Assert.Contains("Entregue:", delivery, StringComparison.Ordinal);
    }

    // Every C# source under windows/src with its path relative to windows/, so a failure names the file.
    private static (string RelativePath, string Text)[] SourceTexts() =>
        [.. AppResources.SourceFiles(AppResources.SourceDir, "*.cs")
            .Select(path => (Path.GetRelativePath(WindowsDir, path), File.ReadAllText(path)))];

    private static string[] FilesContaining(IEnumerable<(string RelativePath, string Text)> sources, string[] tokens) =>
        [.. sources
            .Where(source => tokens.Any(token => source.Text.Contains(token, StringComparison.Ordinal)))
            .Select(source => source.RelativePath)
            .Order(StringComparer.Ordinal)];

    // An empty scan, or a SourceDir that resolves elsewhere, must not pass silently: the sources read have to include
    // a known file.
    private static void AssertSourcesWereRead((string RelativePath, string Text)[] sources)
    {
        Assert.True(sources.Length > 0, $"No *.cs file was found under {AppResources.SourceDir}.");
        Assert.Contains(sources, source => source.RelativePath.EndsWith("VaultSession.cs", StringComparison.Ordinal));
    }

    private static string Read(string path)
    {
        Assert.True(File.Exists(path), $"The file does not exist: {path}");
        return File.ReadAllText(path);
    }
}
