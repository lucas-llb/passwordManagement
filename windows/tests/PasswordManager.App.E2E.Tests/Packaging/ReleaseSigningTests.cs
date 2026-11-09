using PasswordManager.App.E2E.Tests.Localization;

namespace PasswordManager.App.E2E.Tests.Packaging;

// W-30 static guards over the release driver: tools/New-Release.ps1 wraps the W-29 publish recipe, signs with a
// certificate that must stay outside the repository, verifies the signature before hashing, and refuses to run
// without a certificate unless -Unsigned is explicit. They read the repository straight from AppResources, as the
// other static guards do, and open no window, so they run in Debug and Release.
public sealed class ReleaseSigningTests
{
    // Key material that must never be committed or produced inside the repository.
    private static readonly string[] SigningMaterialPatterns = ["*.pfx", "*.p12", "*.cer", "*.snk"];

    // The .gitignore patterns that keep every kind of key material out of a commit.
    private static readonly string[] KeyMaterialIgnorePatterns = ["*.pfx", "*.p12", "*.cer", "*.snk", "*.key"];

    private static string ToolsDir => Path.GetFullPath(Path.Combine(AppResources.SourceDir, "..", "tools"));

    private static string RepoRoot => Path.GetFullPath(Path.Combine(AppResources.SourceDir, "..", ".."));

    private static string ScriptPath => Path.Combine(ToolsDir, "New-Release.ps1");

    private static string GitIgnorePath => Path.Combine(RepoRoot, ".gitignore");

    // ---- The driver ----

    [Fact]
    public void Script_ExistsAndDrivesPublishRelease()
    {
        var script = ReadScript();

        // The W-29 recipe is called as a child process and keeps owning the publish; the driver only locates the .msix
        // by the documented AppPackages glob afterwards.
        string[] required = ["Publish-Release.ps1", "AppPackages", "*_$($target.MsixArch).msix", "& $hostExe @publishArguments"];
        AssertNames(script, required);
        Assert.True(File.Exists(Path.Combine(ToolsDir, "Publish-Release.ps1")), "tools/Publish-Release.ps1 is missing.");
    }

    [Fact]
    public void Script_SignsAndVerifies()
    {
        var script = ReadScript();

        string[] required = ["signtool", "Get-AuthenticodeSignature", "Get-FileHash", "SHA256", "/fd"];
        AssertNames(script, required);
    }

    [Fact]
    public void Script_RefusesACertificateInsideTheRepository()
    {
        var script = ReadScript();

        // Full-path containment: the certificate must not sit under the repository root, whatever its casing.
        string[] required = ["RepoRoot", "StartsWith", "OrdinalIgnoreCase", "DirectorySeparatorChar", "inside the repository"];
        AssertNames(script, required);
    }

    [Fact]
    public void Script_RequiresACertificateUnlessUnsigned()
    {
        var script = ReadScript();

        string[] required = ["-Unsigned", "PM_CERT_PASSWORD", "-CertificatePath"];
        AssertNames(script, required);

        // Both certificate parameters are optional in PowerShell only because there is no default: a default path or
        // password literal would ship key material with the script.
        Assert.Matches(@"\[string\]\$CertificatePath\s*(?:,|\r?\n)", script);
        Assert.Matches(@"\[string\]\$CertificatePassword\s*(?:,|\r?\n)", script);
        Assert.DoesNotContain(@"C:\Users\", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Script_ChecksThePublisherAgainstTheCertificate()
    {
        var script = ReadScript();

        // On mismatch the script prints the exact line to paste into the manifest, then stops before signing.
        string[] required = ["Subject", "Publisher", "$($certificate.Subject)", "Package.appxmanifest"];
        AssertNames(script, required);
    }

    [Fact]
    public void Script_OrdersSignBeforeHash()
    {
        var script = ReadScript();

        // Hashing must never run before the signature was verified: the sign call comes first in the text and the
        // Get-FileHash call after it.
        var sign = script.IndexOf("& $signToolPath @signArguments", StringComparison.Ordinal);
        var hash = script.IndexOf("Get-FileHash", StringComparison.Ordinal);
        Assert.True(sign >= 0, "New-Release.ps1 no longer invokes signtool.");
        Assert.True(hash >= 0, "New-Release.ps1 no longer computes a hash.");
        Assert.True(sign < hash, "New-Release.ps1 computes the hash before the package is signed.");
    }

    // ---- The repository ----

    [Fact]
    public void GitIgnore_CoversKeyMaterial()
    {
        var lines = File.ReadAllLines(GitIgnorePath)
            .Select(line => line.Trim())
            .ToArray();

        var missing = KeyMaterialIgnorePatterns
            .Where(pattern => !lines.Contains(pattern, StringComparer.Ordinal))
            .ToArray();
        Assert.True(missing.Length == 0, $".gitignore no longer ignores: {string.Join(", ", missing)}.");
    }

    [Fact]
    public void Repo_HasNoSigningMaterial()
    {
        // The certificate lives outside the repository (AGENTS.md §9); a copied .pfx under src/ or tools/ fails the release.
        var material = new[] { AppResources.SourceDir, ToolsDir }
            .SelectMany(directory => SigningMaterialPatterns
                .SelectMany(pattern => Directory.Exists(directory)
                    ? Directory.EnumerateFiles(directory, pattern, SearchOption.AllDirectories)
                    : Array.Empty<string>()))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            material.Length == 0,
            $"Signing material must never be committed (AGENTS.md §9): {string.Join(", ", material)}");
    }

    // ---- Helpers ----

    private static string ReadScript()
    {
        Assert.True(File.Exists(ScriptPath), $"The script does not exist: {ScriptPath}");
        return File.ReadAllText(ScriptPath);
    }

    private static void AssertNames(string text, string[] required)
    {
        var missing = required.Where(name => !text.Contains(name, StringComparison.OrdinalIgnoreCase)).ToArray();
        Assert.True(
            missing.Length == 0,
            $"{Path.GetFileName(ScriptPath)} no longer names: {string.Join(", ", missing)}.");
    }
}
