using System.Text.Json;
using FlaUI.Core.Tools;
using PasswordManager.App.E2E.Tests.Localization;

namespace PasswordManager.App.E2E.Tests;

// Create, rename and delete on the profile screen, checked on screen and on disk. Every password is fictitious and every
// seeded vault is a copy of a test vector.
public sealed class ProfilesE2ETests
{
    private const string NewProfileName = "Perfil Criado";
    private const string NewPassword = "frase-ficticia-de-teste";

    [Fact]
    public void EmptyFolder_CreateProfile_ListsItAndItsVaultUnlocks()
    {
        using var app = AppSession.Launch();
        app.WaitFor("ProfilesEmptyTitle");

        app.Invoke("ProfilesCreate");
        app.WaitFor("CreateName");
        app.WaitDisplayAffinity(NativeMethods.WDA_EXCLUDEFROMCAPTURE);

        app.SetValue("CreateName", NewProfileName);
        app.SetValue("CreatePassword", NewPassword);
        app.SetValue("CreateConfirmPassword", NewPassword);
        app.Invoke("CreateButton");

        // Argon2id runs with the default cost here, about a second.
        app.WaitFor("ProfilesList");
        app.WaitGone("CreateName");
        var profile = Assert.Single(app.WaitItems("ProfilesList", 1));
        Assert.Equal(NewProfileName, AppSession.TextOf(profile, "ProfileName"));
        app.WaitDisplayAffinity(NativeMethods.WDA_NONE);

        var id = SeededVault.ProfileId(app.VaultsDirectory);
        Assert.Equal(NewProfileName, DisplayNames(app.VaultsDirectory).Single());
        Assert.True(File.Exists(VaultPath(app.VaultsDirectory, id)));
        Assert.False(File.Exists(VaultPath(app.VaultsDirectory, id) + ".bak"));

        profile.Patterns.Invoke.Pattern.Invoke();
        app.TypePassword(NewPassword);
        app.Invoke("UnlockButton");
        app.WaitFor("EntriesEmpty");
    }

    [Fact]
    public void CreateProfile_InvalidInput_ShowsEachErrorAndWritesNothing()
    {
        using var app = AppSession.Launch(SeededVault.Seed);
        Assert.Single(app.WaitItems("ProfilesList", 1));
        var before = Snapshot(app.VaultsDirectory);

        app.Invoke("ProfilesCreate");
        app.WaitFor("CreateName");

        // The view empties both boxes after every attempt, so each attempt fills all three fields.
        Attempt(app, SeededVault.ProfileName.ToUpperInvariant(), NewPassword, NewPassword);
        Assert.Equal(AppResources.Strings["ProfileNameDuplicate"], app.WaitFor("CreateNameError").Name);

        Attempt(app, NewProfileName, "curta-ficti", "curta-ficti");
        app.WaitFor("CreatePasswordTooShort");
        app.WaitGone("CreateNameError");

        Attempt(app, NewProfileName, NewPassword, NewPassword + "-outra");
        app.WaitFor("CreatePasswordMismatch");
        app.WaitGone("CreatePasswordTooShort");

        app.Invoke("CreateBack");
        app.WaitFor("ProfilesList");
        app.WaitGone("CreateName");
        Assert.Single(app.WaitItems("ProfilesList", 1));
        AssertSameFiles(before, Snapshot(app.VaultsDirectory));
    }

    [Fact]
    public void RenameProfile_CancelThenInvalidThenValid_SavesNameAndKeepsVault()
    {
        const string renamed = "Perfil Renomeado";
        using var app = AppSession.Launch(SeededVault.Seed);
        Assert.Single(app.WaitItems("ProfilesList", 1));
        var id = SeededVault.ProfileId(app.VaultsDirectory);
        var vault = File.ReadAllBytes(VaultPath(app.VaultsDirectory, id));

        // Close discards the typed text.
        app.Invoke("ProfileRename");
        Assert.Equal(SeededVault.ProfileName, app.ValueOf("ProfileRenameName"));
        app.SetValue("ProfileRenameName", "Nome Descartado");
        app.InvokeInDialog("ProfileRenameDialog", "CloseButton");
        app.WaitDialogGone("ProfileRenameDialog");
        Assert.Equal(SeededVault.ProfileName, AppSession.TextOf(Assert.Single(app.WaitItems("ProfilesList", 1)), "ProfileName"));
        Assert.Equal(SeededVault.ProfileName, DisplayNames(app.VaultsDirectory).Single());

        // An invalid name keeps the dialog open with the rule's text.
        app.Invoke("ProfileRename");
        Assert.Equal(SeededVault.ProfileName, app.ValueOf("ProfileRenameName"));
        app.SetValue("ProfileRenameName", string.Empty);
        app.InvokeInDialog("ProfileRenameDialog", "PrimaryButton");
        Assert.Equal(AppResources.Strings["ProfileNameEmpty"], app.WaitFor("ProfileRenameError").Name);
        Assert.NotNull(app.FindDialog("ProfileRenameDialog"));

        app.SetValue("ProfileRenameName", renamed);
        app.InvokeInDialog("ProfileRenameDialog", "PrimaryButton");
        app.WaitDialogGone("ProfileRenameDialog");
        var shown = Retry.WhileFalse(
            () => AppSession.TextOf(Assert.Single(app.WaitItems("ProfilesList", 1)), "ProfileName") == renamed,
            TimeSpan.FromSeconds(15),
            TimeSpan.FromMilliseconds(100)).Success;
        Assert.True(shown, "The list item does not show the new name.");

        Assert.Equal(id, SeededVault.ProfileId(app.VaultsDirectory));
        Assert.Equal(renamed, DisplayNames(app.VaultsDirectory).Single());
        Assert.Equal(vault, File.ReadAllBytes(VaultPath(app.VaultsDirectory, id)));

        Assert.Single(app.Items("ProfilesList")).Patterns.Invoke.Pattern.Invoke();
        Assert.Equal(renamed, app.WaitFor("UnlockProfileName").Name);
        app.TypePassword(SeededVault.Password);
        app.Invoke("UnlockButton");
        app.WaitFor("EntriesList");
    }

    [Fact]
    public void DeleteProfile_Cancel_KeepsProfileAndFiles()
    {
        using var app = AppSession.Launch(SeedWithBackupAndTemp);
        Assert.Single(app.WaitItems("ProfilesList", 1));
        var before = Snapshot(app.VaultsDirectory);
        Assert.Equal(4, before.Count);

        app.Invoke("ProfileDelete");
        Assert.Equal(SeededVault.ProfileName, app.WaitFor("ProfileDeleteName").Name);
        app.InvokeInDialog("ProfileDeleteDialog", "CloseButton");
        app.WaitDialogGone("ProfileDeleteDialog");

        var profile = Assert.Single(app.WaitItems("ProfilesList", 1));
        Assert.Equal(SeededVault.ProfileName, AppSession.TextOf(profile, "ProfileName"));
        AssertSameFiles(before, Snapshot(app.VaultsDirectory));
    }

    [Fact]
    public void DeleteProfile_Confirm_RemovesEntryAndItsThreeFilesOnly()
    {
        var orphan = $"{Guid.NewGuid()}.pmvault";
        using var app = AppSession.Launch(directory =>
        {
            SeedWithBackupAndTemp(directory);
            File.Copy(
                Path.Combine(AppSession.Metadata("TestVectorsDir"), "empty.pmvault"),
                Path.Combine(directory, orphan));
        });
        Assert.Single(app.WaitItems("ProfilesList", 1));
        var id = SeededVault.ProfileId(app.VaultsDirectory);
        var orphanBytes = File.ReadAllBytes(Path.Combine(app.VaultsDirectory, orphan));

        app.Invoke("ProfileDelete");
        app.InvokeInDialog("ProfileDeleteDialog", "PrimaryButton");
        app.WaitDialogGone("ProfileDeleteDialog");
        app.WaitFor("ProfilesEmptyTitle");
        Assert.Empty(app.Items("ProfilesList"));

        Assert.Equal("""{"profiles":[]}""", File.ReadAllText(Path.Combine(app.VaultsDirectory, "profiles.json")));
        var vault = VaultPath(app.VaultsDirectory, id);
        Assert.False(File.Exists(vault));
        Assert.False(File.Exists(vault + ".bak"));
        Assert.False(File.Exists(vault + ".tmp"));
        Assert.Equal(
            new[] { "profiles.json", orphan }.Order(StringComparer.Ordinal),
            Snapshot(app.VaultsDirectory).Keys.Order(StringComparer.Ordinal));
        Assert.Equal(orphanBytes, File.ReadAllBytes(Path.Combine(app.VaultsDirectory, orphan)));
    }

    [Fact]
    public void CorruptProfileList_CreateIsDisabled()
    {
        using var app = AppSession.Launch(dir => File.WriteAllText(Path.Combine(dir, "profiles.json"), "{"));

        app.WaitFor("ProfilesLoadFailed");
        Assert.False(app.WaitFor("ProfilesCreate").IsEnabled);
    }

    private static void Attempt(AppSession app, string name, string password, string confirmation)
    {
        app.SetValue("CreateName", name);
        app.SetValue("CreatePassword", password);
        app.SetValue("CreateConfirmPassword", confirmation);
        app.Invoke("CreateButton");
    }

    // The seeded profile plus a .bak and a .tmp of its vault: the three files a delete owns.
    private static void SeedWithBackupAndTemp(string directory)
    {
        SeededVault.Seed(directory);
        var vault = VaultPath(directory, SeededVault.ProfileId(directory));
        File.Copy(vault, vault + ".bak");
        File.Copy(vault, vault + ".tmp");
    }

    private static string VaultPath(string directory, string id) => Path.Combine(directory, $"{id}.pmvault");

    private static string[] DisplayNames(string directory)
    {
        using var list = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory, "profiles.json")));
        return [.. list.RootElement.GetProperty("profiles").EnumerateArray().Select(p => p.GetProperty("displayName").GetString()!)];
    }

    // Every file of the folder, by name, with its bytes.
    private static Dictionary<string, byte[]> Snapshot(string directory) =>
        Directory.EnumerateFiles(directory).ToDictionary(path => Path.GetFileName(path), File.ReadAllBytes, StringComparer.Ordinal);

    private static void AssertSameFiles(Dictionary<string, byte[]> expected, Dictionary<string, byte[]> actual)
    {
        Assert.Equal(expected.Keys.Order(StringComparer.Ordinal), actual.Keys.Order(StringComparer.Ordinal));
        foreach (var (name, bytes) in expected)
        {
            Assert.True(bytes.AsSpan().SequenceEqual(actual[name]), $"'{name}' changed.");
        }
    }
}
