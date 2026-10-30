using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.WindowsAPI;
using PasswordManager.Core.Passwords;

namespace PasswordManager.App.E2E.Tests;

// The whole flow works from the keyboard alone: each screen sets its initial focus, Tab and Shift+Tab follow the
// document order, the arrows move inside the list, Enter activates and Escape goes back. The keys are real input
// (SendInput); AppSession sends them only while the app window is the foreground window.
public sealed class AccessibilityE2ETests
{
    private static readonly string ProfileItem = $"ProfilesList[{SeededVault.ProfileName}]";

    [Fact]
    public void Profiles_FocusStartsOnProfile_EnterOpensUnlock()
    {
        using var app = AppSession.Launch(SeededVault.Seed);

        app.WaitFocus(ProfileItem);
        AssertEveryFocusableElementIsNamed(app);

        // From the item, Tab goes into its buttons, then wraps to the create button above the list and back to the item.
        foreach (var expected in new[] { "ProfileRename", "ProfileDelete", "ProfilesCreate", ProfileItem })
        {
            app.PressKey(VirtualKeyShort.TAB);
            app.WaitFocus(expected);
        }

        foreach (var expected in new[] { "ProfilesCreate", "ProfileDelete", "ProfileRename", ProfileItem })
        {
            app.PressChord(VirtualKeyShort.SHIFT, VirtualKeyShort.TAB);
            app.WaitFocus(expected);
        }

        app.PressKey(VirtualKeyShort.RETURN);
        app.WaitFor("UnlockPassword");
        app.WaitFocus("UnlockPassword");
        AssertEveryFocusableElementIsNamed(app);
    }

    [Fact]
    public void EmptyFolder_CreateWorksWithoutMouse()
    {
        using var app = AppSession.Launch();

        app.WaitFocus("ProfilesCreate");
        AssertEveryFocusableElementIsNamed(app);

        app.PressKey(VirtualKeyShort.RETURN);
        app.WaitFor("CreateName");
        app.WaitFocus("CreateName");
        AssertEveryFocusableElementIsNamed(app);
        foreach (var expected in new[] { "CreatePassword", "CreateConfirmPassword", "CreateSuggest", "CreateButton", "CreateBack", "CreateName" })
        {
            app.PressKey(VirtualKeyShort.TAB);
            app.WaitFocus(expected);
        }

        app.PressKey(VirtualKeyShort.ESCAPE);
        app.WaitFor("ProfilesList");
        app.WaitGone("CreateName");
        app.WaitFocus("ProfilesCreate");

        // The values go in through UI Automation (fictitious password); Enter in the confirmation creates.
        app.PressKey(VirtualKeyShort.RETURN);
        app.WaitFor("CreateName");
        app.WaitFocus("CreateName");
        app.SetValue("CreateName", "Perfil Teclado");
        app.SetValue("CreatePassword", "frase-ficticia-teclado");
        app.SetValue("CreateConfirmPassword", "frase-ficticia-teclado");
        app.WaitFor("CreateConfirmPassword").Focus();
        app.WaitFocus("CreateConfirmPassword");
        app.PressKey(VirtualKeyShort.RETURN);

        // The kit page starts on its checkbox; it has no way back, so Escape does nothing there.
        app.WaitFor("KitText");
        app.WaitGone("CreateName");
        app.WaitFocus("KitSavedCheck");
        AssertEveryFocusableElementIsNamed(app);
        app.PressKey(VirtualKeyShort.ESCAPE);
        Assert.NotNull(app.Find("KitText"));
        Assert.Null(app.Find("ProfilesList"));
        app.WaitFocus("KitSavedCheck");

        // Space checks the box, which enables Concluir; Enter on it returns to the profile list.
        app.PressKey(VirtualKeyShort.SPACE);
        app.WaitEnabled("KitDone");
        app.PressKey(VirtualKeyShort.TAB);
        app.WaitFocus("KitDone");
        app.PressKey(VirtualKeyShort.RETURN);

        app.WaitFor("ProfilesList");
        app.WaitGone("KitText");
        app.WaitItems("ProfilesList", 1);
        app.WaitFocus("ProfilesList[Perfil Teclado]");
    }

    [Fact]
    public void ProfileDialogs_WorkWithoutMouse()
    {
        using var app = AppSession.Launch(SeededVault.Seed);
        app.WaitFocus(ProfileItem);
        var id = SeededVault.ProfileId(app.VaultsDirectory);
        var files = Directory.GetFiles(app.VaultsDirectory).Order(StringComparer.Ordinal).ToArray();
        var list = File.ReadAllBytes(Path.Combine(app.VaultsDirectory, "profiles.json"));

        app.PressKey(VirtualKeyShort.TAB);
        app.WaitFocus("ProfileRename");
        app.PressKey(VirtualKeyShort.TAB);
        app.WaitFocus("ProfileDelete");
        app.PressKey(VirtualKeyShort.RETURN);
        app.WaitDialog("ProfileDeleteDialog");
        AssertEveryFocusableElementIsNamed(app);

        // Close is the default button, so Enter cancels.
        app.PressKey(VirtualKeyShort.RETURN);
        app.WaitDialogGone("ProfileDeleteDialog");
        app.WaitFocus(ProfileItem);
        Assert.Single(app.WaitItems("ProfilesList", 1));
        Assert.Equal(files, Directory.GetFiles(app.VaultsDirectory).Order(StringComparer.Ordinal));
        Assert.Equal(list, File.ReadAllBytes(Path.Combine(app.VaultsDirectory, "profiles.json")));
        Assert.True(File.Exists(Path.Combine(app.VaultsDirectory, $"{id}.pmvault")));

        app.PressKey(VirtualKeyShort.TAB);
        app.WaitFocus("ProfileRename");
        app.PressKey(VirtualKeyShort.RETURN);
        app.WaitDialog("ProfileRenameDialog");
        app.WaitFocus("ProfileRenameName");

        app.PressKey(VirtualKeyShort.ESCAPE);
        app.WaitDialogGone("ProfileRenameDialog");
        app.WaitFocus(ProfileItem);
        Assert.Equal(list, File.ReadAllBytes(Path.Combine(app.VaultsDirectory, "profiles.json")));
    }

    [Fact]
    public void Unlock_TabEscapeAndEnter_WorkWithoutMouse()
    {
        using var app = AppSession.Launch(SeededVault.Seed);
        OpenProfileByKeyboard(app);

        foreach (var expected in new[] { "UnlockButton", "UnlockBack", "UnlockPassword" })
        {
            app.PressKey(VirtualKeyShort.TAB);
            app.WaitFocus(expected);
        }

        foreach (var expected in new[] { "UnlockBack", "UnlockButton", "UnlockPassword" })
        {
            app.PressChord(VirtualKeyShort.SHIFT, VirtualKeyShort.TAB);
            app.WaitFocus(expected);
        }

        app.PressKey(VirtualKeyShort.ESCAPE);
        app.WaitFor("ProfilesList");
        app.WaitGone("UnlockPassword");
        app.WaitFocus(ProfileItem);

        app.PressKey(VirtualKeyShort.RETURN);
        app.WaitFor("UnlockPassword");
        app.WaitFocus("UnlockPassword");

        app.TypePassword("senha-errada-ficticia");
        app.PressKey(VirtualKeyShort.RETURN);
        app.WaitFor("UnlockWrongPassword");
        Assert.Null(app.Find("EntriesList"));

        // During the one-second delay Enter does nothing: the right password stays in the box and the page stays on
        // Unlock. The countdown still shown after the key was processed proves the key came inside the delay.
        app.TypePassword(SeededVault.Password);
        app.PressKey(VirtualKeyShort.RETURN);
        Assert.NotNull(app.Find("UnlockRetryCountdown"));
        Assert.Null(app.Find("EntriesList"));
        app.WaitFocus("UnlockPassword");

        // Once the delay ends, Enter on the same, untouched box unlocks.
        app.WaitEnabled("UnlockButton");
        app.WaitGone("UnlockRetryCountdown");
        app.WaitFocus("UnlockPassword");
        app.PressKey(VirtualKeyShort.RETURN);
        app.WaitFor("EntriesList");
        app.WaitGone("UnlockPassword");
        AssertEveryFocusableElementIsNamed(app);
        app.WaitFocus(EntryItem(0));
    }

    [Fact]
    public void Entries_ArrowsTabEnterEscape_ReachEveryControl()
    {
        using var app = AppSession.Launch(SeededVault.Seed);
        OpenProfileByKeyboard(app);
        UnlockByKeyboard(app, SeededVault.Password);
        app.WaitFocus(EntryItem(0));

        app.PressKey(VirtualKeyShort.DOWN);
        app.WaitFocus(EntryItem(1));
        app.PressKey(VirtualKeyShort.UP);
        app.WaitFocus(EntryItem(0));

        // The list is the last stop, so Tab wraps to the first control: New entry and Lock, then the search and the two
        // filters in document order, and back to the list.
        foreach (var expected in new[] { "EntriesNew", "EntriesLock", "EntriesSearch", "EntriesFavoritesOnly", "EntriesFolder", EntryItem(0) })
        {
            app.PressKey(VirtualKeyShort.TAB);
            app.WaitFocus(expected);
        }

        OpenEntryByKeyboard(app);
        AssertEveryFocusableElementIsNamed(app);

        // A clean, saved entry: Save and Discard are disabled and so skipped. Back sits first in the document, so the
        // cycle runs through the form (the generator sits below the password row), Delete, then wraps to Back and on to
        // the title again. The length slider takes the focus itself and announces its bounds and value through its range
        // value, so a screen reader hears the default and the limits without any text of WinUI's in its name.
        foreach (var expected in new[]
        {
            "DetailUsername", "DetailPassword", "DetailShowPassword", "DetailGenerate", "DetailGeneratorLength",
            "DetailGeneratorUppercase", "DetailGeneratorDigits", "DetailGeneratorSymbols", "DetailUrl", "DetailFolder",
            "DetailNotes", "DetailFavorite", "DetailDelete", "DetailBack", "DetailTitle",
        })
        {
            app.PressKey(VirtualKeyShort.TAB);
            app.WaitFocus(expected);
            if (expected == "DetailGeneratorLength")
            {
                var range = app.Focused()!.Patterns.RangeValue.Pattern;
                Assert.Equal(PasswordGenerator.MinLength, range.Minimum.Value);
                Assert.Equal(PasswordGenerator.MaxLength, range.Maximum.Value);
                Assert.Equal(PasswordGenerator.DefaultLength, range.Value.Value);
            }
        }

        // Enter on the focused back button.
        app.PressChord(VirtualKeyShort.SHIFT, VirtualKeyShort.TAB);
        app.WaitFocus("DetailBack");
        app.PressKey(VirtualKeyShort.RETURN);
        app.WaitFor("EntriesList");
        app.WaitGone("DetailTitle");
        app.WaitFocus(EntryItem(0));

        // Back from the second entry puts the focus on that entry, not on the first one.
        app.PressKey(VirtualKeyShort.DOWN);
        app.WaitFocus(EntryItem(1));
        OpenEntryByKeyboard(app);
        app.PressKey(VirtualKeyShort.ESCAPE);
        app.WaitFor("EntriesList");
        app.WaitGone("DetailTitle");
        app.WaitFocus(EntryItem(1));

        // Typing in the title makes the draft dirty: Escape (the same command as Back) stays on the page.
        var title = SeededVault.Entries[1].GetProperty("title").GetString()!;
        OpenEntryByKeyboard(app);
        app.PressKey(VirtualKeyShort.KEY_X);
        app.WaitEnabled("DetailSave");
        app.WaitEnabled("DetailBack", expected: false);
        Assert.NotEqual(title, app.ValueOf("DetailTitle"));
        app.PressKey(VirtualKeyShort.ESCAPE);
        Assert.NotNull(app.Find("DetailTitle"));
        Assert.Null(app.Find("EntriesList"));

        // With the draft dirty, Save and Discard join the cycle between the favorite box and Delete, and Back drops out of
        // it (it is disabled); backwards from the title: Delete, Discard, Save.
        foreach (var expected in new[] { "DetailDelete", "DetailDiscard", "DetailSave" })
        {
            app.PressChord(VirtualKeyShort.SHIFT, VirtualKeyShort.TAB);
            app.WaitFocus(expected);
        }

        app.PressKey(VirtualKeyShort.TAB);
        app.WaitFocus("DetailDiscard");

        // Enter on Discard puts the saved title back and frees Back. The button disables itself, so the focus is put
        // back on the title before Escape goes back to the list, on the same entry.
        app.PressKey(VirtualKeyShort.RETURN);
        app.WaitEnabled("DetailDiscard", expected: false);
        app.WaitEnabled("DetailBack");
        Assert.Equal(title, app.ValueOf("DetailTitle"));
        app.WaitFor("DetailTitle").Focus();
        app.WaitFocus("DetailTitle");
        app.PressKey(VirtualKeyShort.ESCAPE);
        app.WaitFor("EntriesList");
        app.WaitGone("DetailTitle");
        app.WaitFocus(EntryItem(1));

        app.PressKey(VirtualKeyShort.TAB);
        app.WaitFocus("EntriesNew");
        app.PressKey(VirtualKeyShort.TAB);
        app.WaitFocus("EntriesLock");
        app.PressKey(VirtualKeyShort.RETURN);
        app.WaitFor("UnlockPassword");
        app.WaitGone("EntriesList");
        app.WaitFocus("UnlockPassword");
    }

    [Fact]
    public void DetailDeleteDialog_CloseIsDefault_EnterAndEscapeCancelWithoutLeavingThePage()
    {
        using var app = AppSession.Launch(SeededVault.Seed);
        OpenProfileByKeyboard(app);
        UnlockByKeyboard(app, SeededVault.Password);
        app.WaitFocus(EntryItem(0));
        OpenEntryByKeyboard(app);

        // Delete is the last control of the form; Tab from the favorite box reaches it.
        app.WaitFor("DetailFavorite").Focus();
        app.WaitFocus("DetailFavorite");
        app.PressKey(VirtualKeyShort.TAB);
        app.WaitFocus("DetailDelete");
        app.PressKey(VirtualKeyShort.RETURN);
        app.WaitDialog("DetailDeleteDialog");
        AssertEveryFocusableElementIsNamed(app);

        // Close is the default button, so Enter cancels; the page stays and the focus returns to Delete.
        app.PressKey(VirtualKeyShort.RETURN);
        app.WaitDialogGone("DetailDeleteDialog");
        app.WaitFocus("DetailDelete");
        Assert.NotNull(app.Find("DetailTitle"));
        Assert.Null(app.Find("EntriesList"));

        // Escape closes the dialog too, and it is the dialog that takes the key: the Escape accelerator of the Back button
        // must not also leave the page.
        app.PressKey(VirtualKeyShort.RETURN);
        app.WaitDialog("DetailDeleteDialog");
        app.PressKey(VirtualKeyShort.ESCAPE);
        app.WaitDialogGone("DetailDeleteDialog");
        app.WaitFocus("DetailDelete");
        Assert.NotNull(app.Find("DetailTitle"));
        Assert.Null(app.Find("EntriesList"));
    }

    [Fact]
    public void EmptyVault_FocusStartsOnNew()
    {
        using var app = AppSession.Launch(SeededVault.SeedEmpty);
        OpenProfileByKeyboard(app);
        UnlockByKeyboard(app, SeededVault.EmptyPassword);

        app.WaitFocus("EntriesNew");
        app.WaitFor("EntriesEmpty");
        AssertEveryFocusableElementIsNamed(app);
    }

    private static string EntryItem(int index) =>
        $"EntriesList[{SeededVault.Entries[index].GetProperty("title").GetString()}]";

    // Profiles to Unlock with Enter on the focused profile; both screens are swept, Unlock while the box is empty.
    private static void OpenProfileByKeyboard(AppSession app)
    {
        app.WaitFocus(ProfileItem);
        AssertEveryFocusableElementIsNamed(app);
        app.PressKey(VirtualKeyShort.RETURN);
        app.WaitFor("UnlockPassword");
        app.WaitGone("ProfilesList");
        app.WaitFocus("UnlockPassword");
        AssertEveryFocusableElementIsNamed(app);
    }

    // The password is set through UI Automation (only fictitious ones from the seed); Enter in the box unlocks.
    private static void UnlockByKeyboard(AppSession app, string password)
    {
        app.TypePassword(password);
        app.PressKey(VirtualKeyShort.RETURN);
        app.WaitFor("EntriesList");
        app.WaitGone("UnlockPassword");
        AssertEveryFocusableElementIsNamed(app);
    }

    private static void OpenEntryByKeyboard(AppSession app)
    {
        app.PressKey(VirtualKeyShort.RETURN);
        app.WaitFor("DetailTitle");
        app.WaitGone("EntriesList");
        app.WaitFocus("DetailTitle");
    }

    // Every XAML element a keyboard user can reach must be announced: a non-empty Name, and an AutomationId too, except
    // for list items, whose containers are named from their data. The scroll bars are WinUI's and are skipped.
    private static void AssertEveryFocusableElementIsNamed(AppSession app)
    {
        var focusable = FocusableElements(app.Window).ToArray();
        Assert.NotEmpty(focusable);
        var unnamed = focusable
            .Where(element => string.IsNullOrEmpty(element.Properties.Name.ValueOrDefault)
                || (element.ControlType != ControlType.ListItem
                    && string.IsNullOrEmpty(element.Properties.AutomationId.ValueOrDefault)))
            .Select(element => $"{element.ControlType} id '{element.Properties.AutomationId.ValueOrDefault}' "
                + $"'{element.Properties.Name.ValueOrDefault}'")
            .ToArray();

        Assert.Empty(unnamed);
    }

    private static IEnumerable<AutomationElement> FocusableElements(AutomationElement parent)
    {
        foreach (var child in parent.FindAllChildren())
        {
            if (child.ControlType == ControlType.ScrollBar)
            {
                continue;
            }

            if (string.Equals(child.Properties.FrameworkId.ValueOrDefault, "XAML", StringComparison.Ordinal)
                && child.Properties.IsKeyboardFocusable.ValueOrDefault)
            {
                yield return child;
            }

            foreach (var descendant in FocusableElements(child))
            {
                yield return descendant;
            }
        }
    }
}
