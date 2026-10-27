using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.WindowsAPI;

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

        // The list is the only tab stop of the screen, so Tab wraps back to it.
        app.PressKey(VirtualKeyShort.TAB);
        app.WaitFocus(ProfileItem);

        app.PressKey(VirtualKeyShort.RETURN);
        app.WaitFor("UnlockPassword");
        app.WaitFocus("UnlockPassword");
        AssertEveryFocusableElementIsNamed(app);
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
        app.WaitFocus("UnlockPassword");
        Assert.Null(app.Find("EntriesList"));

        UnlockByKeyboard(app, SeededVault.Password);
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

        app.PressKey(VirtualKeyShort.TAB);
        app.WaitFocus("EntriesLock");
        app.PressKey(VirtualKeyShort.TAB);
        app.WaitFocus(EntryItem(0));

        OpenEntryByKeyboard(app);
        AssertEveryFocusableElementIsNamed(app);
        foreach (var expected in new[] { "DetailUsername", "DetailUrl", "DetailFolder", "DetailNotes", "DetailBack" })
        {
            app.PressKey(VirtualKeyShort.TAB);
            app.WaitFocus(expected);
        }

        // Enter on the focused back button.
        app.PressKey(VirtualKeyShort.RETURN);
        app.WaitFor("EntriesList");
        app.WaitGone("DetailTitle");
        app.WaitFocus(EntryItem(0));

        OpenEntryByKeyboard(app);
        app.PressKey(VirtualKeyShort.ESCAPE);
        app.WaitFor("EntriesList");
        app.WaitGone("DetailTitle");
        app.WaitFocus(EntryItem(0));

        app.PressKey(VirtualKeyShort.TAB);
        app.WaitFocus("EntriesLock");
        app.PressKey(VirtualKeyShort.RETURN);
        app.WaitFor("UnlockPassword");
        app.WaitGone("EntriesList");
        app.WaitFocus("UnlockPassword");
    }

    [Fact]
    public void EmptyVault_FocusStartsOnLock()
    {
        using var app = AppSession.Launch(SeededVault.SeedEmpty);
        OpenProfileByKeyboard(app);
        UnlockByKeyboard(app, SeededVault.EmptyPassword);

        app.WaitFocus("EntriesLock");
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
