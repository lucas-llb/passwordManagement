using System.Globalization;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using PasswordManager.App.E2E.Tests.Localization;
using PasswordManager.Core.Passwords;

namespace PasswordManager.App.E2E.Tests;

// Every visible text of every screen must come from Strings/pt-BR/Resources.resw (or be data the user or a test vector owns).
public sealed class LocalizationE2ETests
{
    private static readonly ControlType[] TextControlTypes =
        [ControlType.Text, ControlType.Button, ControlType.Edit, ControlType.List, ControlType.ListItem];

    // The number beside the generator's length slider is the slider's value, data rather than text: the default length.
    private static readonly string GeneratorLengthText =
        PasswordGenerator.DefaultLength.ToString(CultureInfo.InvariantCulture);

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
        AssertNameFromResources(app, "ProfilesList");
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

        // Wrong password: the countdown sentence is the resw format with the number of seconds; it may vanish while the
        // sweep runs, which is fine.
        app.TypePassword("senha-errada-ficticia");
        app.Invoke("UnlockButton");
        AssertNameFromResources(app, "UnlockWrongPassword");
        var countdown = AppResources.Format("UnlockRetryCountdown", 1);
        app.WaitName("UnlockRetryCountdown", countdown);
        AssertEveryTextFromResourcesOrData(app, SeededVault.ProfileName, countdown);

        // Entries, once the delay is over
        app.WaitEnabled("UnlockButton");
        app.TypePassword(SeededVault.Password);
        app.Invoke("UnlockButton");
        app.WaitFor("EntriesList");
        app.WaitGone("UnlockPassword");
        var items = app.Items("EntriesList");
        Assert.Equal(SeededVault.Entries.Length, items.Length);
        AssertNameFromResources(app, "EntriesTitle");
        AssertNameFromResources(app, "EntriesNew");
        AssertNameFromResources(app, "EntriesLock");
        AssertNameFromResources(app, "EntriesList");
        AssertNameFromResources(app, "EntriesSearch");
        AssertNameFromResources(app, "EntriesFavoritesOnly");
        AssertNameFromResources(app, "EntriesFolder");
        AssertEveryTextFromResourcesOrData(app, EntryTexts());

        // A search that matches nothing shows the "no matches" text instead of the list items.
        app.SetValue("EntriesSearch", "consulta-sem-resultado");
        AssertNameFromResources(app, "EntriesNoMatches");
        AssertEveryTextFromResourcesOrData(app, EntryTexts());

        // Detail of the first entry; the query is cleared first so the item is there to open.
        app.SetValue("EntriesSearch", string.Empty);
        app.WaitGone("EntriesNoMatches");
        items = app.WaitItems("EntriesList", SeededVault.Entries.Length);
        items[0].Patterns.Invoke.Pattern.Invoke();
        app.WaitFor("DetailTitle");
        app.WaitGone("EntriesList");
        AssertNameFromResources(app, "DetailBack", "NavBack");
        AssertNameFromResources(app, "DetailTitle");
        AssertNameFromResources(app, "DetailUsername");
        AssertNameFromResources(app, "DetailUrl");
        AssertNameFromResources(app, "DetailFolder");
        AssertNameFromResources(app, "DetailNotes");
        AssertNameFromResources(app, "DetailPassword");
        AssertNameFromResources(app, "DetailShowPassword");
        AssertNameFromResources(app, "DetailGenerate");
        AssertNameFromResources(app, "DetailGeneratorLength");
        AssertNameFromResources(app, "DetailGeneratorUppercase");
        AssertNameFromResources(app, "DetailGeneratorDigits");
        AssertNameFromResources(app, "DetailGeneratorSymbols");
        AssertNameFromResources(app, "DetailFavorite");
        AssertNameFromResources(app, "DetailSave");
        AssertNameFromResources(app, "DetailDiscard");
        AssertNameFromResources(app, "DetailDelete");

        // The two dates are the resw formats filled with the entry's timestamps in local time: data of the vault, not
        // fixed texts. A saved entry shows both, and no message.
        var created = AppResources.Format("DetailCreatedAt", DateOf(0, "createdAt").LocalDateTime);
        var updated = AppResources.Format("DetailUpdatedAt", DateOf(0, "updatedAt").LocalDateTime);
        Assert.Equal(created, app.WaitFor("DetailCreatedAt").Name);
        Assert.Equal(updated, app.WaitFor("DetailUpdatedAt").Name);
        Assert.Null(app.Find("DetailTitleRequired"));
        Assert.Null(app.Find("DetailSaveError"));
        Assert.Equal(GeneratorLengthText, app.WaitFor("DetailGeneratorLengthValue").Name);
        AssertEveryTextFromResourcesOrData(app, created, updated, GeneratorLengthText);

        // The password box is swapped for a text box while the password is shown; both are named "Senha".
        Assert.Null(app.Find("DetailPasswordText"));
        app.Toggle("DetailShowPassword");
        app.WaitGone("DetailPassword");
        AssertNameFromResources(app, "DetailPasswordText");
        AssertEveryTextFromResourcesOrData(app, created, updated, GeneratorLengthText);
        app.Toggle("DetailShowPassword");
        app.WaitGone("DetailPasswordText");
        app.WaitFor("DetailPassword");

        // The delete confirmation, then cancelled.
        app.Invoke("DetailDelete");
        app.WaitDialog("DetailDeleteDialog");
        AssertNameFromResources(app, "DetailDeleteDialog");
        AssertNameFromResources(app, "DetailDeleteWarning");
        AssertEveryTextFromResourcesOrData(app, created, updated, GeneratorLengthText);
        app.InvokeInDialog("DetailDeleteDialog", "CloseButton");
        app.WaitDialogGone("DetailDeleteDialog");

        // A new entry has no dates and no delete, and asks for the title.
        app.Invoke("DetailBack");
        app.WaitFor("EntriesList");
        app.WaitGone("DetailTitle");
        app.Invoke("EntriesNew");
        app.WaitFor("DetailTitle");
        app.WaitGone("EntriesList");
        AssertNameFromResources(app, "DetailTitleRequired");
        Assert.Null(app.Find("DetailCreatedAt"));
        Assert.Null(app.Find("DetailUpdatedAt"));
        Assert.Null(app.Find("DetailDelete"));
        AssertEveryTextFromResourcesOrData(app, GeneratorLengthText);
    }

    // A seeded entry's timestamp (the vector stores them as ISO 8601 UTC).
    private static DateTimeOffset DateOf(int entry, string field) =>
        DateTimeOffset.Parse(
            SeededVault.Entries[entry].GetProperty(field).GetString()!,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal);

    [Fact]
    public void CreatePage_TextsComeFromResources()
    {
        using var app = AppSession.Launch();
        app.Invoke("ProfilesCreate");

        // Both boxes empty, so the PasswordBox reveal buttons, named by WinUI, are not shown.
        app.WaitFor("CreateName");
        app.WaitGone("ProfilesList");
        AssertNameFromResources(app, "CreateTitle");
        AssertNameFromResources(app, "CreateName");
        AssertNameFromResources(app, "CreatePassword");
        AssertNameFromResources(app, "CreateConfirmPassword");
        AssertNameFromResources(app, "CreateNoRecoveryWarning");
        AssertNameFromResources(app, "CreateSuggest");
        AssertNameFromResources(app, "CreateButton");
        AssertNameFromResources(app, "CreateBack", "NavBack");
        AssertEveryTextFromResourcesOrData(app);

        // The meter text; the box is not empty, so the sweep waits until the attempt below empties it.
        app.SetValue("CreatePassword", "aaaaaaaaaaaa");
        app.WaitName("CreateStrength", AppResources.Strings["PasswordStrengthVeryWeak"]);

        // An empty name and a short fictitious password; the view empties the boxes after the attempt.
        app.SetValue("CreatePassword", "curta-ficti");
        app.SetValue("CreateConfirmPassword", "curta-ficti");
        app.Invoke("CreateButton");
        Assert.Equal(AppResources.Strings["ProfileNameEmpty"], app.WaitFor("CreateNameError").Name);
        AssertNameFromResources(app, "CreatePasswordTooShort");
        app.WaitGone("CreateStrength");
        AssertEveryTextFromResourcesOrData(app);

        // The suggestion fills both boxes, so only its hint is checked, not swept.
        app.Invoke("CreateSuggest");
        app.WaitFor("CreateSuggestion");
        AssertNameFromResources(app, "CreateSuggestionHint");
    }

    [Fact]
    public void RecoveryKitPage_TextsComeFromResources()
    {
        using var app = AppSession.Launch();
        app.Invoke("ProfilesCreate");
        app.WaitFor("CreateName");
        app.SetValue("CreateName", "Perfil Kit");
        app.SetValue("CreatePassword", "frase-ficticia-do-kit");
        app.SetValue("CreateConfirmPassword", "frase-ficticia-do-kit");
        app.Invoke("CreateButton");

        // Argon2id runs with the default cost here, about a second.
        var kitText = app.WaitFor("KitText").Name;
        app.WaitGone("CreateName");
        AssertNameFromResources(app, "KitTitle");
        AssertNameFromResources(app, "KitExplanation");
        AssertNameFromResources(app, "KitInstructions");
        AssertNameFromResources(app, "KitSavedCheck");
        AssertNameFromResources(app, "KitDone");
        AssertEveryTextFromResourcesOrData(app, kitText);
    }

    [Fact]
    public void ProfileDialogs_TextsComeFromResources()
    {
        using var app = AppSession.Launch(SeededVault.Seed);
        Assert.Single(app.WaitItems("ProfilesList", 1));

        app.Invoke("ProfileRename");
        app.WaitFor("ProfileRenameName");
        AssertNameFromResources(app, "ProfileRenameDialog");
        AssertNameFromResources(app, "ProfileRenameName");
        AssertEveryTextFromResourcesOrData(app, SeededVault.ProfileName);
        app.InvokeInDialog("ProfileRenameDialog", "CloseButton");
        app.WaitDialogGone("ProfileRenameDialog");

        app.Invoke("ProfileDelete");
        app.WaitFor("ProfileDeleteName");
        AssertNameFromResources(app, "ProfileDeleteDialog");
        AssertNameFromResources(app, "ProfileDeleteWarning");
        AssertEveryTextFromResourcesOrData(app, SeededVault.ProfileName);
        app.InvokeInDialog("ProfileDeleteDialog", "CloseButton");
        app.WaitDialogGone("ProfileDeleteDialog");
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

    // Sweeps the window content for the Text, Button, Edit, List and ListItem elements and fails on any name that is
    // neither a resw value nor data; list items are named from their data (the profile name, the entry title). Two
    // subtrees are skipped because the OS or WinUI names them in the system language: the title bar and the scroll bars
    // (their line and page buttons). Call it right after the screen's anchor element is shown, and on Unlock only while
    // the PasswordBox is empty, because its reveal button is named by WinUI, not by the resw. An element without a Name
    // (for example the TextBlock of an empty username) has no text, so it is skipped; reading Name directly would throw
    // there.
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

    // The data of the list page: the titles and usernames of the items, and the folder names of the folder filter.
    private static IEnumerable<string> EntryTexts() =>
        SeededVault.Entries.SelectMany(entry => new[]
        {
            entry.GetProperty("title").GetString()!,
            entry.GetProperty("username").GetString()!,
            entry.GetProperty("folder").GetString()!,
        });
}
