using System.Globalization;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using PasswordManager.Core.Passwords;
using PasswordManager.Core.Vault;

namespace PasswordManager.App.E2E.Tests;

// The password generator in the entry editor (W-21), on the real app over the seeded vault: the defaults, a generated
// password saved to disk, the options and the length, the keyboard, and Discard leaving the vault untouched. The password
// is read only through the visible password box, after "Mostrar senha"; every character is checked against the Core
// constants, and a failure never prints a generated value. Every step waits on the UI, never on a fixed sleep.
public sealed class PasswordGeneratorE2ETests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(100);

    private static string SeededTitle(int index) => SeededVault.Entries[index].GetProperty("title").GetString()!;

    [Fact]
    public void NewEntry_GeneratedPassword_IsSavedToDisk()
    {
        using var app = AppSession.Launch(SeededVault.Seed);
        Unlock(app);
        app.Invoke("EntriesNew");
        app.WaitFor("DetailTitle");
        app.WaitGone("EntriesList");

        // The defaults: 20 characters, every option on.
        Assert.Equal(PasswordGenerator.DefaultLength, app.RangeOf("DetailGeneratorLength"));
        Assert.True(app.IsToggled("DetailGeneratorUppercase"));
        Assert.True(app.IsToggled("DetailGeneratorDigits"));
        Assert.True(app.IsToggled("DetailGeneratorSymbols"));

        // Generating makes the draft dirty but leaves the password hidden.
        app.Invoke("DetailGenerate");
        app.WaitEnabled("DetailDiscard");
        Assert.Null(app.Find("DetailPasswordText"));

        ShowPassword(app);
        var shown = WaitPasswordOfLength(app, PasswordGenerator.DefaultLength);
        AssertInAlphabet(shown, PasswordGenerator.LowercaseLetters + PasswordGenerator.UppercaseLetters
            + PasswordGenerator.DigitCharacters + PasswordGenerator.SymbolCharacters);
        AssertHasClass(shown, PasswordGenerator.LowercaseLetters, "lowercase letter");
        AssertHasClass(shown, PasswordGenerator.UppercaseLetters, "uppercase letter");
        AssertHasClass(shown, PasswordGenerator.DigitCharacters, "digit");
        AssertHasClass(shown, PasswordGenerator.SymbolCharacters, "symbol");

        app.SetValue("DetailTitle", "Entrada gerada fictícia");
        app.WaitEnabled("DetailSave");
        app.Invoke("DetailSave");
        app.WaitEnabled("DetailSave", expected: false);

        // On disk: one more entry, at the end, with exactly the password the page showed.
        var entries = SeededVault.ReadContent(app.VaultsDirectory, SeededVault.Password).Entries;
        Assert.Equal(SeededVault.Entries.Length + 1, entries.Count);
        var saved = entries[^1];
        Assert.Equal("Entrada gerada fictícia", saved.Title);
        Assert.True(string.Equals(shown, saved.Password, StringComparison.Ordinal), "The saved password is not the one shown.");
    }

    [Fact]
    public void Generate_OptionsOff_UsesOnlyLowercaseAtChosenLength()
    {
        using var app = AppSession.Launch(SeededVault.Seed);
        Unlock(app);
        OpenEntry(app, 0);

        // 32 characters, lowercase only.
        app.SetRange("DetailGeneratorLength", 32);
        Assert.Equal(32, app.RangeOf("DetailGeneratorLength"));
        app.Toggle("DetailGeneratorUppercase");
        app.Toggle("DetailGeneratorDigits");
        app.Toggle("DetailGeneratorSymbols");
        Assert.False(app.IsToggled("DetailGeneratorUppercase"));
        Assert.False(app.IsToggled("DetailGeneratorDigits"));
        Assert.False(app.IsToggled("DetailGeneratorSymbols"));
        app.Invoke("DetailGenerate");
        ShowPassword(app);
        var long32 = WaitPasswordOfLength(app, 32);
        AssertInAlphabet(long32, PasswordGenerator.LowercaseLetters);

        // The slider refuses a length outside the bounds and keeps the one it had, so a generation can never be asked for
        // fewer than 8 or more than 128 characters; the lowest length it takes gives 8.
        Assert.ThrowsAny<ArgumentException>(() => app.SetRange("DetailGeneratorLength", PasswordGenerator.MinLength - 1));
        Assert.ThrowsAny<ArgumentException>(() => app.SetRange("DetailGeneratorLength", PasswordGenerator.MaxLength + 1));
        Assert.Equal(32, app.RangeOf("DetailGeneratorLength"));
        app.SetRange("DetailGeneratorLength", PasswordGenerator.MinLength);
        Assert.Equal(PasswordGenerator.MinLength, app.RangeOf("DetailGeneratorLength"));
        app.WaitName("DetailGeneratorLengthValue", PasswordGenerator.MinLength.ToString(CultureInfo.InvariantCulture));
        app.Invoke("DetailGenerate");
        var short8 = WaitPasswordOfLength(app, PasswordGenerator.MinLength);
        AssertInAlphabet(short8, PasswordGenerator.LowercaseLetters);
    }

    [Fact]
    public void Generate_ByKeyboard_ThenDiscard_LeavesVaultUnchanged()
    {
        using var app = AppSession.Launch(SeededVault.Seed);
        Unlock(app);
        OpenEntry(app, 0);
        var vaultPath = SeededVault.VaultPath(app.VaultsDirectory);
        var seedBytes = File.ReadAllBytes(vaultPath);
        app.WaitEnabled("DetailSave", expected: false);
        app.WaitEnabled("DetailBack");

        // Enter on the focused button generates; the focus stays on it, the draft is dirty, and Back is blocked.
        app.WaitFor("DetailGenerate").Focus();
        app.WaitFocus("DetailGenerate");
        app.PressKey(VirtualKeyShort.RETURN);
        app.WaitEnabled("DetailSave");
        app.WaitEnabled("DetailBack", expected: false);
        app.WaitFocus("DetailGenerate");

        // Discard puts the seeded password back and frees Back and Save again.
        app.Invoke("DetailDiscard");
        app.WaitEnabled("DetailSave", expected: false);
        app.WaitEnabled("DetailBack");
        ShowPassword(app);
        var seeded = SeededVault.Entries[0].GetProperty("password").GetString()!;
        var restored = Retry.WhileFalse(
            () => string.Equals(app.ValueOf("DetailPasswordText"), seeded, StringComparison.Ordinal),
            Timeout,
            Poll,
            ignoreException: true).Success;
        Assert.True(restored, "Discard did not restore the seeded password.");

        // Nothing was written: the vault is the seed byte for byte, and no .bak was made.
        Assert.Equal(seedBytes, File.ReadAllBytes(vaultPath));
        Assert.False(File.Exists(vaultPath + VaultFile.BackupSuffix));
    }

    // Opens the only profile (from the profile list, or the Unlock page it leads to) and waits for the entry list.
    private static void Unlock(AppSession app)
    {
        if (app.Find("UnlockPassword") is null)
        {
            Assert.Single(app.Items("ProfilesList")).Patterns.Invoke.Pattern.Invoke();
        }

        app.TypePassword(SeededVault.Password);
        app.Invoke("UnlockButton");
        app.WaitFor("EntriesList");
        app.WaitGone("UnlockPassword");
    }

    // Opens seeded entry `index` (0-based) from the list by its title and waits until the editor shows it.
    private static void OpenEntry(AppSession app, int index)
    {
        var title = SeededTitle(index);
        var item = Retry.WhileNull(
            () => app.Items("EntriesList").FirstOrDefault(i => i.Name == title), Timeout, Poll, ignoreException: true).Result
            ?? throw new InvalidOperationException($"Entry #{index + 1} is not in the list.");
        item.Patterns.Invoke.Pattern.Invoke();
        app.WaitFor("DetailTitle");
        app.WaitGone("EntriesList");
        Assert.Equal(title, app.ValueOf("DetailTitle"));
    }

    // Switches the password to its visible box, the only place the tree exposes it.
    private static void ShowPassword(AppSession app)
    {
        app.Toggle("DetailShowPassword");
        app.WaitFor("DetailPasswordText");
    }

    // The visible password once it has this length (the box follows the view model a moment after a command ran).
    private static string WaitPasswordOfLength(AppSession app, int length)
    {
        Retry.WhileFalse(() => app.ValueOf("DetailPasswordText").Length == length, Timeout, Poll, ignoreException: true);
        var value = app.ValueOf("DetailPasswordText");
        Assert.Equal(length, value.Length);
        return value;
    }

    // The messages name the check, never the value: a generated password must not reach a test report.
    private static void AssertInAlphabet(string password, string alphabet) =>
        Assert.True(password.All(alphabet.Contains), "A character of the password is outside the expected alphabet.");

    private static void AssertHasClass(string password, string characters, string description) =>
        Assert.True(password.Any(characters.Contains), $"The password has no {description}.");
}
