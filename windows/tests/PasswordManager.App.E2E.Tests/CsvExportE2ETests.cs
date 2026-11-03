using System.Text;
using PasswordManager.App.E2E.Tests.Localization;
using PasswordManager.Core.Vault;

namespace PasswordManager.App.E2E.Tests;

// The plaintext CSV export (W-24), on the real app over the seeded vault and the real Windows save dialog driven through
// UI Automation: after the warning, the acknowledgement and the master password, the file lands only where the user
// chose, with exactly the bytes of VaultCsv; a wrong master password, a canceled dialog and the vaults folder write
// nothing. Every password is fictitious.
public sealed class CsvExportE2ETests
{
    private const string WrongPassword = "senha-errada-ficticia";
    private const string CsvFileName = "senhas.csv";

    [Fact]
    public void CsvExport_SavesReadableFileAtChosenPath()
    {
        using var app = AppSession.Launch(SeededVault.Seed);
        Unlock(app);
        var exports = CreateExportsFolder(app);
        var vaults = Snapshot(app.VaultsDirectory);

        OpenCsvExport(app);
        app.WaitDisplayAffinity(NativeMethods.WDA_EXCLUDEFROMCAPTURE);

        // Save stays disabled until the user says they understand the warning.
        app.WaitEnabled("CsvExportSave", expected: false);
        app.Toggle("CsvExportAcknowledge");
        app.WaitEnabled("CsvExportSave");

        app.SetValue("CsvExportPassword", SeededVault.Password);
        app.Invoke("CsvExportSave");
        app.SaveInFileDialog(Path.Combine(exports, CsvFileName));
        app.WaitName("CsvExportDone", AppResources.Format("CsvExportDone", CsvFileName));
        Assert.Null(app.Find("CsvExportWrongPassword"));
        Assert.Null(app.Find("CsvExportFailed"));
        Assert.Null(app.Find("CsvExportVaultsFolder"));

        // Only the chosen file: no .tmp left beside it and no .bak.
        Assert.Equal([CsvFileName], Names(exports));
        var csv = File.ReadAllBytes(Path.Combine(exports, CsvFileName));

        // UTF-8 without BOM, the header and CRLF first, and exactly what Core writes for the entries of the vault.
        Assert.False(csv.AsSpan().StartsWith(Encoding.UTF8.Preamble), "The CSV starts with a BOM.");
        var text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(csv);
        Assert.StartsWith(VaultCsv.HeaderLine + "\r\n", text, StringComparison.Ordinal);
        var entries = SeededVault.ReadContent(app.VaultsDirectory, SeededVault.Password).Entries;
        Assert.Equal(VaultCsv.Serialize(entries), csv);

        // The notes of the 4th entry hold a line break, tab, quotes and a backslash: the field is quoted, the quotes are
        // doubled and the line breaks stay. Built from the manifest value, not retyped (the notes hold NFD).
        var notes = SeededVault.Entries[3].GetProperty("notes").GetString()!;
        Assert.Contains('\n', notes);
        Assert.Contains('"', notes);
        Assert.Contains("\"" + notes.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"", text, StringComparison.Ordinal);

        // The vaults folder keeps the same files and bytes, with no .bak.
        AssertSnapshot(vaults, app.VaultsDirectory);
    }

    [Fact]
    public void CsvExport_WrongPassword_ShowsErrorAndDelayAndNoDialog()
    {
        using var app = AppSession.Launch(SeededVault.Seed);
        Unlock(app);
        var exports = CreateExportsFolder(app);
        var vaults = Snapshot(app.VaultsDirectory);
        OpenCsvExport(app);
        app.Toggle("CsvExportAcknowledge");
        app.WaitEnabled("CsvExportSave");
        Assert.Null(app.Find("CsvExportRetryCountdown"));

        // The refusal runs before any dialog: the error shows the master password was really checked and rejected, and
        // the delay is the one second of the first failure, shared with the unlock screen.
        app.SetValue("CsvExportPassword", WrongPassword);
        app.Invoke("CsvExportSave");
        app.WaitFor("CsvExportWrongPassword");
        app.WaitName("CsvExportRetryCountdown", AppResources.Format("UnlockRetryCountdown", 1));
        Assert.False(app.IsEnabled("CsvExportSave"));
        Assert.Null(app.FindFileDialog());
        Assert.Empty(Names(exports));

        // Once the countdown is gone the button is back, and the right password opens the dialog.
        app.WaitGone("CsvExportRetryCountdown");
        app.WaitEnabled("CsvExportSave");
        app.SetValue("CsvExportPassword", SeededVault.Password);
        app.Invoke("CsvExportSave");
        app.WaitFileDialog();
        app.CancelFileDialog();

        app.WaitEnabled("CsvExportSave");
        Assert.Null(app.Find("CsvExportDone"));
        Assert.Null(app.Find("CsvExportFailed"));
        Assert.Empty(Names(exports));
        AssertSnapshot(vaults, app.VaultsDirectory);
    }

    [Fact]
    public void CsvExport_DialogCanceled_WritesNothingAndStaysUsable()
    {
        using var app = AppSession.Launch(SeededVault.Seed);
        Unlock(app);
        var exports = CreateExportsFolder(app);
        var vaults = Snapshot(app.VaultsDirectory);
        OpenCsvExport(app);

        app.Toggle("CsvExportAcknowledge");
        app.SetValue("CsvExportPassword", SeededVault.Password);
        app.Invoke("CsvExportSave");

        // While the dialog is open the export runs: Save and Back are disabled and the ring is shown.
        app.WaitFileDialog();
        app.WaitEnabled("CsvExportSave", expected: false);
        app.WaitEnabled("CsvExportBack", expected: false);
        app.WaitFor("CsvExportProgress");
        app.CancelFileDialog();

        app.WaitEnabled("CsvExportSave");
        app.WaitEnabled("CsvExportBack");
        app.WaitGone("CsvExportProgress");
        Assert.Null(app.Find("CsvExportDone"));
        Assert.Null(app.Find("CsvExportWrongPassword"));
        Assert.Null(app.Find("CsvExportFailed"));
        Assert.Null(app.Find("CsvExportVaultsFolder"));
        Assert.Empty(Names(exports));
        AssertSnapshot(vaults, app.VaultsDirectory);

        // Back returns to the export page, not to the list.
        app.Invoke("CsvExportBack");
        app.WaitFor("ExportTitle");
        app.WaitGone("CsvExportTitle");
    }

    [Fact]
    public void CsvExport_IntoVaultsFolder_IsRefused()
    {
        using var app = AppSession.Launch(SeededVault.Seed);
        Unlock(app);
        var vaults = Snapshot(app.VaultsDirectory);
        OpenCsvExport(app);

        // A new name, so the dialog asks nothing more: the picker does not create the file, and the app refuses the folder.
        app.Toggle("CsvExportAcknowledge");
        app.SetValue("CsvExportPassword", SeededVault.Password);
        app.Invoke("CsvExportSave");
        app.SaveInFileDialog(Path.Combine(app.VaultsDirectory, CsvFileName));
        app.WaitFor("CsvExportVaultsFolder");
        app.WaitEnabled("CsvExportSave");
        Assert.Null(app.Find("CsvExportDone"));
        Assert.Null(app.Find("CsvExportFailed"));

        AssertSnapshot(vaults, app.VaultsDirectory);
    }

    [Fact]
    public void CsvExport_SessionLock_OnCsvPage_LocksVault()
    {
        using var app = AppSession.Launch(SeededVault.Seed);
        Unlock(app);
        OpenCsvExport(app);
        app.WaitDisplayAffinity(NativeMethods.WDA_EXCLUDEFROMCAPTURE);
        app.Toggle("CsvExportAcknowledge");

        app.PostSessionChange(NativeMethods.WTS_SESSION_LOCK);
        app.WaitFor("UnlockTitle");
        app.WaitGone("CsvExportTitle");
        app.WaitDisplayAffinity(NativeMethods.WDA_NONE);

        // The lock reset the stack to Profiles and Unlock: Back leads to the profile list, not to the CSV page.
        app.Invoke("UnlockBack");
        app.WaitFor("ProfilesList");
        app.WaitGone("UnlockPassword");
        Assert.Null(app.Find("CsvExportTitle"));
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

    // List, then the export page, then the CSV page above it.
    private static void OpenCsvExport(AppSession app)
    {
        app.Invoke("EntriesExport");
        app.WaitFor("ExportTitle");
        app.WaitGone("EntriesList");
        app.Invoke("ExportOpenCsv");
        app.WaitFor("CsvExportTitle");
        app.WaitGone("ExportTitle");
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
}
