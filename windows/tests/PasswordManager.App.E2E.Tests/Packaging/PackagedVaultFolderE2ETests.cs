using System.Diagnostics;

namespace PasswordManager.App.E2E.Tests.Packaging;

// W-28: a loose exe has no package identity, so --packaged-local-folder stands in for ApplicationData.Current.LocalFolder.
// The app appends "vaults" to that folder, and the seed writes there, so the profile on screen proves the app read the
// packaged vaults folder the seam names, not another one. The invalid case keeps the seam honest: a relative value must
// stop the launch instead of falling back to the real folder.
public sealed class PackagedVaultFolderE2ETests
{
    [Fact]
    public void Launch_PackagedLocalFolder_ReadsVaultsFromIt()
    {
        using var app = AppSession.LaunchPackaged(SeededVault.SeedProfileOnly);

        app.WaitFor("ProfilesList");
        var profile = Assert.Single(app.WaitItems("ProfilesList", 1));
        Assert.Equal(SeededVault.ProfileName, AppSession.TextOf(profile, "ProfileName"));

        // The seeded list is the one the app read: nothing was written beside the vaults folder as the app's own list.
        Assert.True(File.Exists(Path.Combine(app.VaultsDirectory, "profiles.json")), "The seed did not write the vaults folder.");
        Assert.False(
            File.Exists(Path.Combine(Path.GetDirectoryName(app.VaultsDirectory)!, "profiles.json")),
            "The app read or wrote profiles.json beside the vaults folder.");
    }

    [Fact]
    public void Launch_PackagedLocalFolder_InvalidValue_DoesNotLaunch()
    {
        // The --vaults-dir contract: a bad value ends the launch instead of falling back to the real folder. A fallback
        // would keep the window up, so exiting is the proof; it also means no window can have appeared.
        var start = new ProcessStartInfo(AppSession.RequireDebugAppExe());
        start.ArgumentList.Add("--packaged-local-folder");
        start.ArgumentList.Add("pasta-ficticia-relativa");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("The app did not start.");
        Assert.True(
            process.WaitForExit(30_000),
            "The app stayed up with a relative --packaged-local-folder: the packaged folder seam fell back.");
    }
}
