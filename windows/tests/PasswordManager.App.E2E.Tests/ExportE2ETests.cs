using PasswordManager.App.E2E.Tests.Localization;
using PasswordManager.Core.Vault;

namespace PasswordManager.App.E2E.Tests;

// The .pmvault export (W-23), on the real app over the seeded vault and the real Windows save dialog driven through UI
// Automation: the file lands only where the user chose, opens with the export password alone (one slot, a fresh salt and
// a fresh Vault Key) and leaves the vaults folder as it was; a canceled dialog, a refused password and the vaults folder
// write nothing. Every password is fictitious.
public sealed class ExportE2ETests
{
    private const string ExportPassword = "Senha de exportação fictícia 2026";
    private const string ExportFileName = "backup.pmvault";

    [Fact]
    public void Export_SavesToChosenFile_OpensOnlyWithExportPassword()
    {
        using var app = AppSession.Launch(SeededVault.Seed);
        Unlock(app);
        var exports = CreateExportsFolder(app);
        var vaults = Snapshot(app.VaultsDirectory);

        OpenExport(app);
        app.WaitDisplayAffinity(NativeMethods.WDA_EXCLUDEFROMCAPTURE);
        TypePasswords(app, ExportPassword, ExportPassword);
        app.Invoke("ExportSave");
        app.SaveInFileDialog(Path.Combine(exports, ExportFileName));
        app.WaitName("ExportDone", AppResources.Format("ExportDone", ExportFileName));
        Assert.Null(app.Find("ExportFailed"));
        Assert.Null(app.Find("ExportVaultsFolder"));

        // Only the chosen file: no .tmp left beside it and no .bak.
        Assert.Equal([ExportFileName], Names(exports));
        var export = File.ReadAllBytes(Path.Combine(exports, ExportFileName));

        // One master password slot, at the default Argon2id cost, with its own salt and its own wrapped Vault Key.
        var header = VaultHeader.Parse(export);
        var slot = Assert.Single(header.Slots);
        Assert.Equal(VaultFormat.SlotTypePassword, slot.Type);
        Assert.Null(header.RecoveryKitSlotOffset);
        var slotBytes = export.AsSpan(slot.Offset, slot.Length);
        Assert.Equal(KdfParameters.Default, KdfParameters.ReadFromSlot(slotBytes));
        Assert.Equal(VaultFormat.DefaultKdfParallelism, slotBytes[VaultFormat.SlotKdfParallelismOffset]);
        var seeded = File.ReadAllBytes(SeededVault.VaultPath(app.VaultsDirectory));
        var seededSlot = SlotBytes(seeded, VaultFormat.SlotTypePassword);
        Assert.False(
            Salt(slotBytes).SequenceEqual(Salt(seededSlot)), "The export reuses the salt of the vault.");
        Assert.False(
            WrappedKey(slotBytes).SequenceEqual(WrappedKey(seededSlot)), "The export reuses the wrapped key of the vault.");

        // Another program (or Android) reads the same entries with the export password; the master password fails.
        var exportPath = Path.Combine(exports, ExportFileName);
        Assert.Equal(
            SeededVault.ReadContent(app.VaultsDirectory, SeededVault.Password).Entries,
            SeededVault.ReadFile(exportPath, ExportPassword).Entries);
        Assert.Throws<VaultAuthenticationException>(() => SeededVault.ReadFile(exportPath, SeededVault.Password));

        // The vaults folder keeps the same files and bytes, with no .bak.
        AssertSnapshot(vaults, app.VaultsDirectory);
    }

    [Fact]
    public void Export_DialogCanceled_WritesNothingAndStaysUsable()
    {
        using var app = AppSession.Launch(SeededVault.Seed);
        Unlock(app);
        var exports = CreateExportsFolder(app);
        var vaults = Snapshot(app.VaultsDirectory);

        OpenExport(app);
        TypePasswords(app, ExportPassword, ExportPassword);
        app.Invoke("ExportSave");

        // While the dialog is open the export runs: Save and Back are disabled and the ring is shown.
        app.WaitFileDialog();
        app.WaitEnabled("ExportSave", expected: false);
        app.WaitEnabled("ExportBack", expected: false);
        app.WaitFor("ExportProgress");
        app.CancelFileDialog();

        app.WaitEnabled("ExportSave");
        app.WaitEnabled("ExportBack");
        app.WaitGone("ExportProgress");
        Assert.Null(app.Find("ExportDone"));
        Assert.Null(app.Find("ExportFailed"));
        Assert.Null(app.Find("ExportVaultsFolder"));
        Assert.Empty(Names(exports));
        AssertSnapshot(vaults, app.VaultsDirectory);

        app.Invoke("ExportBack");
        app.WaitFor("EntriesList");
        app.WaitGone("ExportTitle");
    }

    [Fact]
    public void Export_InvalidPassword_ShowsErrorAndNoDialog()
    {
        using var app = AppSession.Launch(SeededVault.Seed);
        Unlock(app);
        var exports = CreateExportsFolder(app);
        var vaults = Snapshot(app.VaultsDirectory);
        OpenExport(app);

        // 11 characters: one short of the minimum. The error shows the refusal ran, so a dialog would already be open.
        TypePasswords(app, "curta-ficti", "curta-ficti");
        app.Invoke("ExportSave");
        app.WaitFor("ExportPasswordTooShort");
        app.WaitEnabled("ExportSave");
        Assert.Null(app.FindFileDialog());
        Assert.Null(app.Find("ExportPasswordMismatch"));

        // The page empties both boxes on every attempt, so they are typed again.
        TypePasswords(app, "senha-ficticia-um-123", "senha-ficticia-dois-456");
        app.Invoke("ExportSave");
        app.WaitFor("ExportPasswordMismatch");
        app.WaitGone("ExportPasswordTooShort");
        app.WaitEnabled("ExportSave");
        Assert.Null(app.FindFileDialog());

        Assert.Null(app.Find("ExportDone"));
        Assert.Empty(Names(exports));
        AssertSnapshot(vaults, app.VaultsDirectory);
    }

    [Fact]
    public void Export_IntoVaultsFolder_IsRefused()
    {
        using var app = AppSession.Launch(SeededVault.Seed);
        Unlock(app);
        var vaults = Snapshot(app.VaultsDirectory);
        OpenExport(app);

        // A new name, so the dialog asks nothing more: the picker does not create the file, and the app refuses the folder.
        TypePasswords(app, ExportPassword, ExportPassword);
        app.Invoke("ExportSave");
        app.SaveInFileDialog(Path.Combine(app.VaultsDirectory, ExportFileName));
        app.WaitFor("ExportVaultsFolder");
        app.WaitEnabled("ExportSave");
        Assert.Null(app.Find("ExportDone"));
        Assert.Null(app.Find("ExportFailed"));

        AssertSnapshot(vaults, app.VaultsDirectory);
    }

    [Fact]
    public void Export_SessionLock_OnExportPage_LocksVault()
    {
        using var app = AppSession.Launch(SeededVault.Seed);
        Unlock(app);
        OpenExport(app);
        app.WaitDisplayAffinity(NativeMethods.WDA_EXCLUDEFROMCAPTURE);
        TypePasswords(app, ExportPassword, ExportPassword);

        app.PostSessionChange(NativeMethods.WTS_SESSION_LOCK);
        app.WaitFor("UnlockTitle");
        app.WaitGone("ExportTitle");

        // The unlock page takes the master password, so the lock leaves the window excluded from capture.
        app.WaitDisplayAffinity(NativeMethods.WDA_EXCLUDEFROMCAPTURE);

        // The lock reset the stack to Profiles and Unlock: Back leads to the profile list, not to the export page.
        app.Invoke("UnlockBack");
        app.WaitFor("ProfilesList");
        app.WaitGone("UnlockPassword");
        app.WaitDisplayAffinity(NativeMethods.WDA_NONE);
        Assert.Null(app.Find("ExportTitle"));
    }

    private static void Unlock(AppSession app)
    {
        Assert.Single(app.Items("ProfilesList")).Patterns.Invoke.Pattern.Invoke();
        app.TypePassword(SeededVault.Password);
        app.Invoke("UnlockButton");
        app.WaitFor("EntriesList");
        app.WaitGone("UnlockPassword");
    }

    private static void OpenExport(AppSession app)
    {
        app.Invoke("EntriesExport");
        app.WaitFor("ExportTitle");
        app.WaitGone("EntriesList");
    }

    private static void TypePasswords(AppSession app, string password, string confirmation)
    {
        app.SetValue("ExportPassword", password);
        app.SetValue("ExportConfirm", confirmation);
    }

    // The export target: a subfolder of the test's own folder, removed with it.
    private static string CreateExportsFolder(AppSession app) =>
        Directory.CreateDirectory(Path.Combine(app.VaultsDirectory, "exports")).FullName;

    private static string[] Names(string directory) =>
        [.. Directory.GetFileSystemEntries(directory).Select(path => Path.GetFileName(path)).Order(StringComparer.Ordinal)];

    // Every file directly in the folder with its bytes (and each subfolder by name), to prove nothing was added or changed.
    private static SortedDictionary<string, byte[]?> Snapshot(string directory)
    {
        var snapshot = new SortedDictionary<string, byte[]?>(StringComparer.Ordinal);
        foreach (var entry in Directory.GetFileSystemEntries(directory))
        {
            snapshot.Add(Path.GetFileName(entry), File.Exists(entry) ? File.ReadAllBytes(entry) : null);
        }

        return snapshot;
    }

    // The export has finished when this runs (its outcome is on screen), so one read is enough.
    private static void AssertSnapshot(SortedDictionary<string, byte[]?> expected, string directory)
    {
        var actual = Snapshot(directory);
        Assert.Equal(expected.Keys, actual.Keys);
        foreach (var (name, bytes) in expected)
        {
            Assert.True(
                bytes is null ? actual[name] is null : actual[name] is { } now && now.AsSpan().SequenceEqual(bytes),
                $"'{name}' in the vaults folder changed.");
        }
    }

    private static byte[] SlotBytes(byte[] file, byte type)
    {
        var range = Assert.Single(VaultHeader.Parse(file).Slots, slot => slot.Type == type);
        return file[range.Offset..(range.Offset + range.Length)];
    }

    private static ReadOnlySpan<byte> Salt(ReadOnlySpan<byte> slot) =>
        slot.Slice(VaultFormat.SlotSaltOffset, VaultFormat.SaltSize);

    private static ReadOnlySpan<byte> WrappedKey(ReadOnlySpan<byte> slot) =>
        slot.Slice(VaultFormat.SlotWrappedKeyOffset, VaultFormat.WrappedKeySize);
}
