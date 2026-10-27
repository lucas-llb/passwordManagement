namespace PasswordManager.App.E2E.Tests;

public sealed class NavigationE2ETests
{
    [Fact]
    public void EmptyFolder_OpensOnProfilesWithEmptyState()
    {
        using var app = AppSession.Launch();

        app.WaitFor("ProfilesEmptyTitle");
        Assert.Empty(app.Items("ProfilesList"));
        Assert.Null(app.Find("ProfilesLoadFailed"));
    }

    [Fact]
    public void CorruptProfileList_ShowsLoadFailedWithoutEmptyState()
    {
        using var app = AppSession.Launch(dir => File.WriteAllText(Path.Combine(dir, "profiles.json"), "{"));

        app.WaitFor("ProfilesLoadFailed");
        Assert.Null(app.Find("ProfilesEmptyTitle"));
        Assert.Empty(app.Items("ProfilesList"));
    }

    [Fact]
    public void MissingVaultFile_ShowsVaultUnreadable()
    {
        using var app = AppSession.Launch(SeededVault.SeedProfileOnly);
        Assert.Single(app.Items("ProfilesList")).Patterns.Invoke.Pattern.Invoke();
        app.TypePassword("qualquer-senha-ficticia");
        app.Invoke("UnlockButton");

        app.WaitFor("UnlockVaultUnreadable");
        Assert.Null(app.Find("UnlockWrongPassword"));
        Assert.Null(app.Find("EntriesList"));
    }

    [Fact]
    public void WrongPassword_ShowsErrorAndStaysOnUnlock()
    {
        using var app = AppSession.Launch(SeededVault.Seed);
        var profile = Assert.Single(app.Items("ProfilesList"));
        Assert.Equal(SeededVault.ProfileName, AppSession.TextOf(profile, "ProfileName"));
        Assert.Null(app.Find("ProfilesEmptyTitle"));

        profile.Patterns.Invoke.Pattern.Invoke();
        Assert.Equal(SeededVault.ProfileName, app.WaitFor("UnlockProfileName").Name);
        Assert.Null(app.Find("UnlockWrongPassword"));

        app.TypePassword("senha-errada-ficticia");
        app.Invoke("UnlockButton");

        app.WaitFor("UnlockWrongPassword");
        Assert.NotNull(app.Find("UnlockPassword"));
        Assert.Null(app.Find("EntriesList"));

        app.TypePassword(SeededVault.Password);
        app.Invoke("UnlockButton");
        app.WaitFor("EntriesList");
        Assert.Null(app.Find("UnlockWrongPassword"));
    }

    [Fact]
    public void UnlockDetailBackLock_FollowsTheNavigationRules()
    {
        using var app = AppSession.Launch(SeededVault.Seed);
        Assert.Single(app.Items("ProfilesList")).Patterns.Invoke.Pattern.Invoke();
        app.TypePassword(SeededVault.Password);
        app.Invoke("UnlockButton");

        app.WaitFor("EntriesList");
        var entries = SeededVault.Entries;
        var items = app.Items("EntriesList");
        Assert.Equal(
            entries.Select(e => e.GetProperty("title").GetString()),
            items.Select(i => AppSession.TextOf(i, "EntryTitle")));
        Assert.Null(app.Find("EntriesEmpty"));

        items[0].Patterns.Invoke.Pattern.Invoke();
        Assert.Equal(entries[0].GetProperty("title").GetString(), app.ValueOf("DetailTitle"));
        Assert.Equal(entries[0].GetProperty("username").GetString(), app.ValueOf("DetailUsername"));
        Assert.Equal(entries[0].GetProperty("url").GetString(), app.ValueOf("DetailUrl"));
        Assert.Equal(entries[0].GetProperty("folder").GetString(), app.ValueOf("DetailFolder"));
        Assert.Equal(entries[0].GetProperty("notes").GetString(), app.ValueOf("DetailNotes"));

        app.Invoke("DetailBack");
        Assert.Equal(entries.Length, app.Items("EntriesList").Length);

        app.Invoke("EntriesLock");
        app.WaitFor("UnlockPassword");
        Assert.Null(app.Find("EntriesList"));
        Assert.Null(app.Find("UnlockWrongPassword"));

        app.Invoke("UnlockBack");
        app.WaitFor("ProfilesList");
        app.WaitGone("UnlockPassword");

        Assert.Single(app.Items("ProfilesList")).Patterns.Invoke.Pattern.Invoke();
        app.WaitFor("UnlockPassword");
        Assert.Null(app.Find("EntriesList"));
    }
}
