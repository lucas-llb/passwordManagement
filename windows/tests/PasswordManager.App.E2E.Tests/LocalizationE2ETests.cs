using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using PasswordManager.App.E2E.Tests.Localization;

namespace PasswordManager.App.E2E.Tests;

// Every visible text of every screen must come from Strings/pt-BR/Resources.resw (or be data the user or a test vector owns).
public sealed class LocalizationE2ETests
{
    private static readonly ControlType[] TextControlTypes = [ControlType.Text, ControlType.Button, ControlType.Edit];

    [Fact]
    public void EmptyFolder_ProfileTextsComeFromResources()
    {
        using var app = AppSession.Launch();

        Assert.Equal(AppResources.Strings["AppTitle"], app.Window.Title);
        AssertNameFromResources(app, "ProfilesTitle");
        AssertNameFromResources(app, "ProfilesEmptyTitle");
        AssertNameFromResources(app, "ProfilesEmptyHint");
        AssertEveryTextFromResourcesOrData(app);
    }

    [Fact]
    public void CorruptProfileList_LoadFailedTextComesFromResources()
    {
        using var app = AppSession.Launch(dir => File.WriteAllText(Path.Combine(dir, "profiles.json"), "{"));

        AssertNameFromResources(app, "ProfilesLoadFailed");
        AssertEveryTextFromResourcesOrData(app);
    }

    [Fact]
    public void MissingVaultFile_UnreadableTextComesFromResources()
    {
        using var app = AppSession.Launch(SeededVault.SeedProfileOnly);
        Assert.Single(app.Items("ProfilesList")).Patterns.Invoke.Pattern.Invoke();
        app.TypePassword("qualquer-senha-ficticia");
        app.Invoke("UnlockButton");

        AssertNameFromResources(app, "UnlockVaultUnreadable");
        AssertEveryTextFromResourcesOrData(app, SeededVault.ProfileName);
    }

    [Fact]
    public void SeededVault_EveryScreenTextComesFromResources()
    {
        using var app = AppSession.Launch(SeededVault.Seed);

        // Profiles
        var profile = Assert.Single(app.Items("ProfilesList"));
        AssertNameFromResources(app, "ProfilesTitle");
        AssertEveryTextFromResourcesOrData(app, SeededVault.ProfileName);
        profile.Patterns.Invoke.Pattern.Invoke();

        // Unlock
        app.WaitFor("UnlockPassword");
        app.WaitGone("ProfilesList");
        AssertNameFromResources(app, "UnlockTitle");
        AssertNameFromResources(app, "UnlockPassword");
        AssertNameFromResources(app, "UnlockButton");
        AssertNameFromResources(app, "UnlockBack", "NavBack");
        AssertEveryTextFromResourcesOrData(app, SeededVault.ProfileName);

        // Wrong password
        app.TypePassword("senha-errada-ficticia");
        app.Invoke("UnlockButton");
        AssertNameFromResources(app, "UnlockWrongPassword");
        AssertEveryTextFromResourcesOrData(app, SeededVault.ProfileName);

        // Entries
        app.TypePassword(SeededVault.Password);
        app.Invoke("UnlockButton");
        app.WaitFor("EntriesList");
        app.WaitGone("UnlockPassword");
        var items = app.Items("EntriesList");
        Assert.Equal(SeededVault.Entries.Length, items.Length);
        AssertNameFromResources(app, "EntriesTitle");
        AssertNameFromResources(app, "EntriesLock");
        AssertEveryTextFromResourcesOrData(app, EntryTexts());

        // Detail of the first entry
        items[0].Patterns.Invoke.Pattern.Invoke();
        app.WaitFor("DetailTitle");
        app.WaitGone("EntriesList");
        AssertNameFromResources(app, "DetailBack", "NavBack");
        AssertNameFromResources(app, "DetailTitle");
        AssertNameFromResources(app, "DetailUsername");
        AssertNameFromResources(app, "DetailUrl");
        AssertNameFromResources(app, "DetailFolder");
        AssertNameFromResources(app, "DetailNotes");
        AssertEveryTextFromResourcesOrData(app);
    }

    [Fact]
    public void EmptyVault_EntriesEmptyTextComesFromResources()
    {
        using var app = AppSession.Launch(SeededVault.SeedEmpty);
        Assert.Single(app.Items("ProfilesList")).Patterns.Invoke.Pattern.Invoke();
        app.TypePassword(SeededVault.EmptyPassword);
        app.Invoke("UnlockButton");

        AssertNameFromResources(app, "EntriesEmpty");
        Assert.Empty(app.Items("EntriesList"));
        AssertEveryTextFromResourcesOrData(app);
    }

    // The AutomationId of an element is its x:Uid, except the shared back button, whose uid is NavBack.
    private static void AssertNameFromResources(AppSession app, string automationId, string? uid = null) =>
        Assert.Equal(AppResources.ExpectedName(uid ?? automationId), app.WaitFor(automationId).Name);

    // Sweeps the window content for the Text, Button and Edit elements and fails on any name that is neither a resw value
    // nor data. Two subtrees are skipped because the OS or WinUI names them in the system language: the title bar and
    // the scroll bars (their line and page buttons). List items are left out too: their names are not texts of the app
    // yet (W-13). Call it right after the screen's anchor element is shown, and on Unlock only while the PasswordBox is
    // empty, because its reveal button is named by WinUI, not by the resw. An element without a Name (for example the
    // TextBlock of an empty username) has no text, so it is skipped; reading Name directly would throw there.
    private static void AssertEveryTextFromResourcesOrData(AppSession app, params IEnumerable<string> data)
    {
        var allowed = new HashSet<string>(AppResources.Strings.Values, StringComparer.Ordinal);
        allowed.UnionWith(data);

        var foreign = TextElements(app.Window)
            .Select(element => (Element: element, Name: element.Properties.Name.ValueOrDefault))
            .Where(named => !string.IsNullOrEmpty(named.Name) && !allowed.Contains(named.Name))
            .Select(named => $"{named.Element.ControlType} id '{named.Element.Properties.AutomationId.ValueOrDefault}' '{named.Name}'")
            .ToArray();

        Assert.Empty(foreign);
    }

    private static IEnumerable<AutomationElement> TextElements(AutomationElement parent)
    {
        foreach (var child in parent.FindAllChildren())
        {
            var type = child.ControlType;
            if (type is ControlType.TitleBar or ControlType.ScrollBar)
            {
                continue;
            }

            if (TextControlTypes.Contains(type))
            {
                yield return child;
            }

            foreach (var descendant in TextElements(child))
            {
                yield return descendant;
            }
        }
    }

    private static IEnumerable<string> EntryTexts() =>
        SeededVault.Entries.SelectMany(entry => new[]
        {
            entry.GetProperty("title").GetString()!,
            entry.GetProperty("username").GetString()!,
        });
}
