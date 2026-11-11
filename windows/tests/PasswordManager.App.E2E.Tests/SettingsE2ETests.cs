using System.Diagnostics;
using System.Text;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using PasswordManager.App.E2E.Tests.Localization;
using PasswordManager.Core.Vault;

namespace PasswordManager.App.E2E.Tests;

// The settings page (W-26), on the real app over the seeded vaults: the auto-lock preferences are saved outside the vault and
// apply without a restart, the master password really changes (the new one opens the file and the .bak, the old one opens
// neither) with the unlock screen's delay, and a new Recovery Kit is shown once and written only when concluded. A canceled
// kit, a session lock and every write failure (the file held by the test) leave the current credentials working. Every
// password and kit is fictitious; the waits are on the UI, except the one minute of the idle test.
public sealed class SettingsE2ETests
{
    private const string WrongPassword = "senha-errada-ficticia";
    private const string NewPassword = "Tz9#kQ4!vL2$wP8@xN7";
    private const string ExportPassword = "Senha de exportação fictícia 2026";
    private const string SettingsFileName = "settings.json";
    private const string DefaultJson = """{"idleTimeoutMinutes":5,"lockOnMinimize":false}""";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(1);

    private static readonly int[] IdleMinutes = [1, 2, 5, 10, 15, 30, 60];

    private static string[] IdleLabels => [.. IdleMinutes.Select(IdleLabel)];

    [Fact]
    public void Settings_Open_ShowsDefaultsAndWritesNothing()
    {
        using var app = AppSession.Launch(SeededVault.Seed);
        Unlock(app, SeededVault.Password);
        var folder = Snapshot(app.VaultsDirectory);

        OpenSettings(app);
        app.WaitDisplayAffinity(NativeMethods.WDA_EXCLUDEFROMCAPTURE);
        Assert.Equal(IdleLabels, app.OptionNames("SettingsIdleTimeout"));
        Assert.Equal(IdleLabel(5), app.SelectedOption("SettingsIdleTimeout"));
        Assert.False(app.IsToggled("SettingsLockOnMinimize"));
        Assert.Null(app.Find("SettingsSaveFailed"));

        // Showing the values (and opening the drop-down) saved nothing: the same files, and no settings.json.
        AssertSnapshot(folder, app.VaultsDirectory);
        Assert.False(File.Exists(SettingsPath(app.VaultsDirectory)));

        app.Invoke("SettingsBack");
        app.WaitFor("EntriesList");
        app.WaitGone("SettingsTitle");
        AssertSnapshot(folder, app.VaultsDirectory);
    }

    [Fact]
    public void Settings_Preferences_SurviveRestart()
    {
        byte[] saved;
        using (var app = AppSession.Launch(SeededVault.Seed))
        {
            Unlock(app, SeededVault.Password);
            var folder = Snapshot(app.VaultsDirectory);
            OpenSettings(app);

            // Each choice is saved at once, as exactly the two keys; no .bak or temp file is left beside it.
            app.SelectOption("SettingsIdleTimeout", IdleLabel(15));
            app.Toggle("SettingsLockOnMinimize");
            var path = SettingsPath(app.VaultsDirectory);
            AssertFileBytes(path, Encoding.UTF8.GetBytes("""{"idleTimeoutMinutes":15,"lockOnMinimize":true}"""));
            Assert.Null(app.Find("SettingsSaveFailed"));
            Assert.Equal(folder.Keys.Append(SettingsFileName).Order(StringComparer.Ordinal), Names(app.VaultsDirectory));
            saved = File.ReadAllBytes(path);
        }

        // A new run reads the file that the first one left.
        using var second = AppSession.Launch(WithSettings(SeededVault.Seed, saved));
        Unlock(second, SeededVault.Password);
        OpenSettings(second);
        Assert.Equal(IdleLabel(15), second.SelectedOption("SettingsIdleTimeout"));
        Assert.True(second.IsToggled("SettingsLockOnMinimize"));
        Assert.Equal(saved, File.ReadAllBytes(SettingsPath(second.VaultsDirectory)));
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("""{"idleTimeoutMinutes":3,"lockOnMinimize":false}""")]
    public void Settings_BadFile_ShowsDefaultsUntilAChange(string content)
    {
        var bad = Encoding.UTF8.GetBytes(content);
        using var app = AppSession.Launch(WithSettings(SeededVault.Seed, bad));
        Unlock(app, SeededVault.Password);
        var path = SettingsPath(app.VaultsDirectory);

        // The app starts, shows the defaults, and does not touch the file just by showing them.
        OpenSettings(app);
        Assert.Equal(IdleLabel(5), app.SelectedOption("SettingsIdleTimeout"));
        Assert.False(app.IsToggled("SettingsLockOnMinimize"));
        Assert.Null(app.Find("SettingsSaveFailed"));
        Assert.Equal(bad, File.ReadAllBytes(path));

        // The first change replaces it with valid JSON.
        app.SelectOption("SettingsIdleTimeout", IdleLabel(10));
        AssertFileBytes(path, Encoding.UTF8.GetBytes("""{"idleTimeoutMinutes":10,"lockOnMinimize":false}"""));
        Assert.Null(app.Find("SettingsSaveFailed"));
    }

    [Fact]
    public void Settings_LockOnMinimize_AppliesWithoutRestart()
    {
        using var app = AppSession.Launch(SeededVault.Seed);
        Unlock(app, SeededVault.Password);
        OpenSettings(app);

        // By default minimizing keeps the vault open, with this page on top.
        app.Minimize();
        Thread.Sleep(Settle);
        app.Restore();
        app.WaitFor("SettingsTitle");
        Assert.Null(app.Find("UnlockPassword"));

        // Chosen here, it applies to the very next minimize.
        app.Toggle("SettingsLockOnMinimize");
        Assert.True(app.IsToggled("SettingsLockOnMinimize"));
        app.Minimize();
        app.Restore();
        app.WaitFor("UnlockPassword");
        app.WaitGone("SettingsTitle");
        Assert.Null(app.Find("EntriesList"));
        app.AssertNoEntryVisible();

        // The unlock page takes the master password, so the lock leaves the window excluded from capture.
        app.WaitDisplayAffinity(NativeMethods.WDA_EXCLUDEFROMCAPTURE);
    }

    [Fact]
    public void Settings_LockOnMinimizeFromFile_Locks()
    {
        // No Debug switch: only the saved file asks for the lock.
        using var app = AppSession.Launch(WithSettings(SeededVault.Seed, Encoding.UTF8.GetBytes("""{"idleTimeoutMinutes":5,"lockOnMinimize":true}""")));
        Unlock(app, SeededVault.Password);

        app.Minimize();
        app.Restore();

        app.WaitFor("UnlockPassword");
        app.WaitGone("EntriesList");
        app.AssertNoEntryVisible();

        // The unlock page takes the master password, so the lock leaves the window excluded from capture.
        app.WaitDisplayAffinity(NativeMethods.WDA_EXCLUDEFROMCAPTURE);
    }

    // Takes about a minute of real time, and needs the cursor off the window like the other idle tests.
    [Fact]
    public void Settings_IdleOneMinute_LocksWithoutRestart()
    {
        using var app = AppSession.Launch(SeededVault.Seed);
        app.ParkPointerOutside();
        Unlock(app, SeededVault.Password);
        OpenSettings(app);

        // The default is five minutes, so a lock inside the next minute and a quarter is the new timeout at work.
        app.SelectOption("SettingsIdleTimeout", IdleLabel(1));
        var chosen = Stopwatch.StartNew();
        Assert.True(
            app.WaitAppears("UnlockPassword", TimeSpan.FromSeconds(75), TimeSpan.FromMilliseconds(500)),
            "The vault did not lock after the one minute that was chosen.");
        chosen.Stop();
        Assert.True(
            chosen.Elapsed >= TimeSpan.FromSeconds(55),
            $"The vault locked after only {chosen.Elapsed}; the timeout that was chosen is one minute.");
        app.WaitGone("SettingsTitle");
        app.AssertNoEntryVisible();

        // The unlock page takes the master password, so the lock leaves the window excluded from capture.
        app.WaitDisplayAffinity(NativeMethods.WDA_EXCLUDEFROMCAPTURE);
    }

    [Fact]
    public void Settings_ChangePassword_NewOpensOldFails()
    {
        using var app = AppSession.Launch(SeededVault.Seed);
        Unlock(app, SeededVault.Password);
        var vaultPath = SeededVault.VaultPath(app.VaultsDirectory);
        var before = File.ReadAllBytes(vaultPath);
        var entries = SeededVault.ReadContent(app.VaultsDirectory, SeededVault.Password).Entries;
        OpenSettings(app);

        // The advisory meter follows the new password while it is typed.
        app.SetValue("SettingsNewPassword", "aaaaaaaaaaaa");
        app.WaitName("SettingsStrength", AppResources.Strings["PasswordStrengthVeryWeak"]);
        app.WaitFor("SettingsStrengthBar");
        app.SetValue("SettingsNewPassword", NewPassword);
        Retry.WhileFalse(
            () => app.WaitFor("SettingsStrength").Name != AppResources.Strings["PasswordStrengthVeryWeak"], Timeout, Poll);
        Assert.NotEqual(AppResources.Strings["PasswordStrengthVeryWeak"], app.WaitFor("SettingsStrength").Name);

        app.SetValue("SettingsCurrentPassword", SeededVault.Password);
        app.SetValue("SettingsConfirmPassword", NewPassword);
        app.Invoke("SettingsChangePassword");
        app.WaitFor("SettingsPasswordChanged");
        app.WaitEnabled("SettingsChangePassword");
        Assert.Null(app.Find("SettingsPasswordWrong"));
        Assert.Null(app.Find("SettingsPasswordFailed"));
        Assert.Null(app.Find("SettingsPasswordTooShort"));
        Assert.Null(app.Find("SettingsPasswordMismatch"));
        Assert.Null(app.Find("SettingsPasswordCountdown"));

        // On disk: the new password opens the same entries and the old one no longer opens the file.
        Assert.NotEqual(before, File.ReadAllBytes(vaultPath));
        Assert.Equal(entries, SeededVault.ReadContent(app.VaultsDirectory, NewPassword).Entries);
        Assert.Throws<VaultAuthenticationException>(() => SeededVault.ReadContent(app.VaultsDirectory, SeededVault.Password));

        // The page empties its boxes after the attempt, and with them the meter. A PasswordBox gives no text through UI
        // Automation, so this shows by pressing the button again with nothing typed: the new password is refused as too short
        // (it would be a wrong current password, or a second change, if the boxes still held what was typed).
        app.WaitGone("SettingsStrength");
        var changed = File.ReadAllBytes(vaultPath);
        app.Invoke("SettingsChangePassword");
        app.WaitFor("SettingsPasswordTooShort");
        app.WaitGone("SettingsPasswordChanged");
        Assert.Null(app.Find("SettingsPasswordWrong"));
        Assert.Equal(changed, File.ReadAllBytes(vaultPath));

        // The .bak holds the new file too, so the old password does not open the copy beside the vault either.
        var backup = vaultPath + VaultFile.BackupSuffix;
        Assert.Equal(File.ReadAllBytes(vaultPath), File.ReadAllBytes(backup));
        Assert.Equal(entries, SeededVault.ReadFile(backup, NewPassword).Entries);
        Assert.Throws<VaultAuthenticationException>(() => SeededVault.ReadFile(backup, SeededVault.Password));

        // The session stays open; after a lock the old password is refused and, once the delay is over, the new one opens.
        app.Invoke("SettingsBack");
        app.WaitFor("EntriesList");
        app.WaitGone("SettingsTitle");
        app.Invoke("EntriesLock");
        app.WaitFor("UnlockPassword");
        app.TypePassword(SeededVault.Password);
        app.Invoke("UnlockButton");
        app.WaitFor("UnlockWrongPassword");
        app.WaitEnabled("UnlockButton");
        app.TypePassword(NewPassword);
        app.Invoke("UnlockButton");
        app.WaitFor("EntriesList");
        app.WaitGone("UnlockPassword");
        Assert.Equal(
            SeededVault.Entries.Select(entry => entry.GetProperty("title").GetString()!).Order(StringComparer.Ordinal),
            app.WaitItems("EntriesList", SeededVault.Entries.Length).Select(item => item.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Settings_ChangePassword_WrongCurrent_ShowsErrorAndSharedDelay()
    {
        using var app = AppSession.Launch(SeededVault.Seed);
        Unlock(app, SeededVault.Password);
        var folder = Snapshot(app.VaultsDirectory);
        OpenSettings(app);

        // The current password is really checked: the error shows, and the delay is the one second of the first failure
        // that the unlock screen also uses; it blocks the kit form as well, which shows the same countdown.
        TypeChange(app, WrongPassword, NewPassword, NewPassword);
        app.Invoke("SettingsChangePassword");
        app.WaitFor("SettingsPasswordWrong");
        var countdown = AppResources.Format("UnlockRetryCountdown", 1);
        app.WaitName("SettingsPasswordCountdown", countdown);
        app.WaitName("SettingsKitCountdown", countdown);
        Assert.False(app.IsEnabled("SettingsChangePassword"));
        Assert.False(app.IsEnabled("SettingsKitGenerate"));
        AssertSnapshot(folder, app.VaultsDirectory);

        // The page emptied the three boxes after that attempt, which shows by what a partial one does. A PasswordBox gives no text
        // through UI Automation, so each box is left untouched in one attempt that would succeed if it still held what was typed.
        // Once the countdown is gone, the right current password and the new one, with the confirmation left alone, is a
        // mismatch: the confirmation does not hold the new password any more.
        app.WaitGone("SettingsPasswordCountdown");
        app.WaitEnabled("SettingsChangePassword");
        app.WaitEnabled("SettingsKitGenerate");
        app.SetValue("SettingsCurrentPassword", SeededVault.Password);
        app.SetValue("SettingsNewPassword", NewPassword);
        app.Invoke("SettingsChangePassword");
        app.WaitFor("SettingsPasswordMismatch");
        app.WaitGone("SettingsPasswordWrong");
        Assert.Null(app.Find("SettingsPasswordChanged"));
        AssertSnapshot(folder, app.VaultsDirectory);

        // That refusal emptied them again: the new password and its confirmation without the current one is a wrong
        // password (counted, so a second failure waits two seconds), not the change that the earlier right one would have made.
        app.SetValue("SettingsNewPassword", NewPassword);
        app.SetValue("SettingsConfirmPassword", NewPassword);
        app.Invoke("SettingsChangePassword");
        app.WaitFor("SettingsPasswordWrong");
        app.WaitGone("SettingsPasswordMismatch");
        app.WaitName("SettingsPasswordCountdown", AppResources.Format("UnlockRetryCountdown", 2));
        Assert.Null(app.Find("SettingsPasswordChanged"));
        AssertSnapshot(folder, app.VaultsDirectory);

        // After the longer delay the right password changes it.
        app.WaitGone("SettingsPasswordCountdown");
        app.WaitEnabled("SettingsChangePassword");
        TypeChange(app, SeededVault.Password, NewPassword, NewPassword);
        app.Invoke("SettingsChangePassword");
        app.WaitFor("SettingsPasswordChanged");
        app.WaitGone("SettingsPasswordWrong");
        Assert.Equal(
            SeededVault.Entries.Length,
            SeededVault.ReadContent(app.VaultsDirectory, NewPassword).Entries.Count);
    }

    [Fact]
    public void Settings_ChangePassword_Refusals_WriteNothing()
    {
        using var app = AppSession.Launch(SeededVault.Seed);
        Unlock(app, SeededVault.Password);
        var folder = Snapshot(app.VaultsDirectory);
        OpenSettings(app);

        // 11 characters: one short of the minimum. The master password is not even checked, so there is no delay.
        TypeChange(app, SeededVault.Password, "curta-ficti", "curta-ficti");
        app.Invoke("SettingsChangePassword");
        app.WaitFor("SettingsPasswordTooShort");
        app.WaitEnabled("SettingsChangePassword");
        Assert.Null(app.Find("SettingsPasswordMismatch"));
        Assert.Null(app.Find("SettingsPasswordWrong"));
        Assert.Null(app.Find("SettingsPasswordChanged"));
        Assert.Null(app.Find("SettingsPasswordCountdown"));
        AssertSnapshot(folder, app.VaultsDirectory);

        // Long enough, but the two boxes differ.
        TypeChange(app, SeededVault.Password, "senha-ficticia-um-123", "senha-ficticia-dois-456");
        app.Invoke("SettingsChangePassword");
        app.WaitFor("SettingsPasswordMismatch");
        app.WaitGone("SettingsPasswordTooShort");
        app.WaitEnabled("SettingsChangePassword");
        Assert.Null(app.Find("SettingsPasswordWrong"));
        Assert.Null(app.Find("SettingsPasswordChanged"));
        Assert.Null(app.Find("SettingsPasswordCountdown"));
        AssertSnapshot(folder, app.VaultsDirectory);
        Assert.Equal(
            SeededVault.Entries.Length, SeededVault.ReadContent(app.VaultsDirectory, SeededVault.Password).Entries.Count);
    }

    [Fact]
    public void Settings_NewKit_ShownOnceAndOldStopsWorking()
    {
        using var app = AppSession.Launch(SeededVault.SeedRecoveryKit);
        Unlock(app, SeededVault.RecoveryKitPassword);
        var vaultPath = SeededVault.VaultPath(app.VaultsDirectory);
        var backup = vaultPath + VaultFile.BackupSuffix;
        var oldKit = SeededVault.RecoveryKitText;
        var before = File.ReadAllBytes(vaultPath);
        var folder = Snapshot(app.VaultsDirectory);
        var entries = SeededVault.ReadFile(vaultPath, SeededVault.RecoveryKitPassword).Entries;
        Assert.True(SeededVault.OpensWithKit(vaultPath, oldKit));
        OpenSettings(app);

        // A wrong master password shows the error and the delay, and opens no kit page.
        app.SetValue("SettingsKitPassword", WrongPassword);
        app.Invoke("SettingsKitGenerate");
        app.WaitFor("SettingsKitWrong");
        app.WaitName("SettingsKitCountdown", AppResources.Format("UnlockRetryCountdown", 1));
        Assert.Null(app.Find("NewKitTitle"));
        Assert.Null(app.Find("NewKitText"));
        AssertSnapshot(folder, app.VaultsDirectory);
        app.WaitGone("SettingsKitCountdown");
        app.WaitEnabled("SettingsKitGenerate");

        // The right one shows the new kit above the settings, hidden from capture. Nothing is written yet.
        app.SetValue("SettingsKitPassword", SeededVault.RecoveryKitPassword);
        app.Invoke("SettingsKitGenerate");
        var kit = app.WaitFor("NewKitText").Name;
        app.WaitFor("NewKitTitle");
        app.WaitGone("SettingsTitle");
        app.WaitDisplayAffinity(NativeMethods.WDA_EXCLUDEFROMCAPTURE);
        Assert.Equal(RecoveryKit.TextLength, kit.Length);
        Assert.NotEqual(oldKit, kit);
        Assert.Equal(before, File.ReadAllBytes(vaultPath));
        Assert.True(SeededVault.OpensWithKit(vaultPath, oldKit));
        AssertSnapshot(folder, app.VaultsDirectory);

        // Concluding needs the check; then the page returns to the settings with the message and the focus on the button.
        app.WaitEnabled("NewKitDone", expected: false);
        app.Toggle("NewKitSavedCheck");
        app.WaitEnabled("NewKitDone");
        app.Invoke("NewKitDone");
        app.WaitFor("SettingsKitReplaced");
        app.WaitGone("NewKitTitle");
        app.WaitFocus("SettingsKitGenerate");

        // The shown kit opens the file and the .bak; the old kit opens neither; the master password still does.
        Assert.True(SeededVault.OpensWithKit(vaultPath, kit));
        Assert.False(SeededVault.OpensWithKit(vaultPath, oldKit));
        Assert.Equal(File.ReadAllBytes(vaultPath), File.ReadAllBytes(backup));
        Assert.True(SeededVault.OpensWithKit(backup, kit));
        Assert.False(SeededVault.OpensWithKit(backup, oldKit));
        Assert.Equal(entries, SeededVault.ReadFile(vaultPath, SeededVault.RecoveryKitPassword).Entries);

        // The kit was shown once: it is not in the window any more.
        Assert.Null(app.Find("NewKitText"));
        AssertNotOnScreen(app, kit);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Settings_NewKit_CancelAndEscape_KeepOldKit(bool useEscape)
    {
        using var app = AppSession.Launch(SeededVault.SeedRecoveryKit);
        Unlock(app, SeededVault.RecoveryKitPassword);
        var vaultPath = SeededVault.VaultPath(app.VaultsDirectory);
        var folder = Snapshot(app.VaultsDirectory);
        OpenSettings(app);
        var kit = OpenNewKit(app);
        app.Toggle("NewKitSavedCheck");

        if (useEscape)
        {
            app.WaitFocus("NewKitSavedCheck");
            app.PressKey(VirtualKeyShort.ESCAPE);
        }
        else
        {
            app.Invoke("NewKitCancel");
        }

        // Back on the settings with no message, the kit gone, and the file as it was: the old kit still opens it.
        app.WaitFor("SettingsTitle");
        app.WaitGone("NewKitTitle");
        Assert.Null(app.Find("SettingsKitReplaced"));
        Assert.Null(app.Find("NewKitText"));
        AssertNotOnScreen(app, kit);
        AssertSnapshot(folder, app.VaultsDirectory);
        Assert.True(SeededVault.OpensWithKit(vaultPath, SeededVault.RecoveryKitText));
        Assert.False(SeededVault.OpensWithKit(vaultPath, kit));
        Assert.False(File.Exists(vaultPath + VaultFile.BackupSuffix));
    }

    [Fact]
    public void Settings_NewKit_SessionLock_KeepsOldKit()
    {
        using var app = AppSession.Launch(SeededVault.SeedRecoveryKit);
        Unlock(app, SeededVault.RecoveryKitPassword);
        var vaultPath = SeededVault.VaultPath(app.VaultsDirectory);
        var folder = Snapshot(app.VaultsDirectory);
        OpenSettings(app);
        var kit = OpenNewKit(app);
        app.WaitDisplayAffinity(NativeMethods.WDA_EXCLUDEFROMCAPTURE);
        app.Toggle("NewKitSavedCheck");

        app.PostSessionChange(NativeMethods.WTS_SESSION_LOCK);
        app.WaitFor("UnlockTitle");
        app.WaitGone("NewKitTitle");

        // The unlock page takes the master password, so the lock leaves the window excluded from capture.
        app.WaitDisplayAffinity(NativeMethods.WDA_EXCLUDEFROMCAPTURE);
        Assert.Null(app.Find("NewKitText"));
        AssertNotOnScreen(app, kit);

        // Nothing was written: the old kit still opens the file, the shown one does not.
        AssertSnapshot(folder, app.VaultsDirectory);
        Assert.True(SeededVault.OpensWithKit(vaultPath, SeededVault.RecoveryKitText));
        Assert.False(SeededVault.OpensWithKit(vaultPath, kit));

        // The lock reset the stack to Profiles and Unlock: Back leads to the profile list, not to the kit page.
        app.Invoke("UnlockBack");
        app.WaitFor("ProfilesList");
        app.WaitGone("UnlockPassword");
        app.WaitDisplayAffinity(NativeMethods.WDA_NONE);
        Assert.Null(app.Find("NewKitTitle"));
        Assert.Null(app.Find("SettingsTitle"));
    }

    [Fact]
    public void Settings_SessionLock_LocksVault()
    {
        using var app = AppSession.Launch(SeededVault.Seed);
        Unlock(app, SeededVault.Password);
        OpenSettings(app);
        app.WaitDisplayAffinity(NativeMethods.WDA_EXCLUDEFROMCAPTURE);

        // A password typed but not submitted.
        app.SetValue("SettingsCurrentPassword", SeededVault.Password);

        app.PostSessionChange(NativeMethods.WTS_SESSION_LOCK);
        app.WaitFor("UnlockTitle");
        app.WaitGone("SettingsTitle");

        // The unlock page takes the master password, so the lock leaves the window excluded from capture.
        app.WaitDisplayAffinity(NativeMethods.WDA_EXCLUDEFROMCAPTURE);
        app.AssertNoEntryVisible();

        // The lock reset the stack to Profiles and Unlock: Back leads to the profile list, with nothing of the settings left.
        app.Invoke("UnlockBack");
        app.WaitFor("ProfilesList");
        app.WaitGone("UnlockPassword");
        app.WaitDisplayAffinity(NativeMethods.WDA_NONE);
        Assert.Null(app.Find("SettingsTitle"));
        Assert.Null(app.Find("SettingsCurrentPassword"));
        Assert.Null(app.Find("EntriesList"));
    }

    [Fact]
    public void Settings_ExportAndImportLinks_ReturnToSettings()
    {
        using var app = AppSession.Launch(SeededVault.Seed);
        Unlock(app, SeededVault.Password);
        var exports = Directory.CreateDirectory(Path.Combine(app.VaultsDirectory, "exports")).FullName;
        var exportPath = Path.Combine(exports, "backup.pmvault");
        var entries = SeededVault.ReadContent(app.VaultsDirectory, SeededVault.Password).Entries;
        OpenSettings(app);

        // The export page opened from here: the real save dialog writes a .pmvault, and Back returns to the settings.
        app.Invoke("SettingsExport");
        app.WaitFor("ExportTitle");
        app.WaitGone("SettingsTitle");
        app.SetValue("ExportPassword", ExportPassword);
        app.SetValue("ExportConfirm", ExportPassword);
        app.Invoke("ExportSave");
        app.SaveInFileDialog(exportPath);
        app.WaitName("ExportDone", AppResources.Format("ExportDone", "backup.pmvault"));
        Assert.Equal(entries, SeededVault.ReadFile(exportPath, ExportPassword).Entries);

        app.Invoke("ExportBack");
        app.WaitFor("SettingsTitle");
        app.WaitGone("ExportTitle");
        Assert.Null(app.Find("EntriesList"));

        // The import page opened from here previews that file with the real open dialog: the vault already holds every row.
        app.Invoke("SettingsImport");
        app.WaitFor("ImportTitle");
        app.WaitGone("SettingsTitle");
        app.Invoke("ImportChoose");
        app.OpenInFileDialog(exportPath);
        app.WaitFor("ImportPassword");
        app.SetValue("ImportPassword", ExportPassword);
        app.Invoke("ImportOpen");
        app.WaitName("ImportSummary", AppResources.Format("ImportSummary", 0, entries.Count));
        Assert.Equal(
            entries.Select(entry => AppResources.Format("ImportRowDuplicateName", entry.Title)).Order(StringComparer.Ordinal),
            app.WaitItems("ImportPreviewList", entries.Count).Select(item => item.Name).Order(StringComparer.Ordinal));

        app.Invoke("ImportBack");
        app.WaitFor("SettingsTitle");
        app.WaitGone("ImportTitle");
        Assert.Null(app.Find("EntriesList"));
    }

    // The files are held by the test with FileShare.None, so the app's atomic replacement of each one fails: the state must
    // stay as it was, with the message, and a retry once the file is free must work.
    [Theory]
    [InlineData("preferences")]
    [InlineData("password")]
    [InlineData("kit")]
    public void Settings_WriteFailures_KeepState(string target)
    {
        switch (target)
        {
            case "preferences":
                PreferencesWriteFails();
                break;
            case "password":
                PasswordWriteFails();
                break;
            default:
                KitWriteFails();
                break;
        }
    }

    private static void PreferencesWriteFails()
    {
        var original = Encoding.UTF8.GetBytes(DefaultJson);
        using var app = AppSession.Launch(WithSettings(SeededVault.Seed, original));
        Unlock(app, SeededVault.Password);
        var path = SettingsPath(app.VaultsDirectory);
        OpenSettings(app);

        // The file cannot be replaced: the message shows, the file is as it was, and the choice still stands on the page.
        using (Hold(path))
        {
            app.Toggle("SettingsLockOnMinimize");
            app.WaitName("SettingsSaveFailed", AppResources.ExpectedName("SettingsSaveFailed"));
            Assert.True(app.IsToggled("SettingsLockOnMinimize"));
        }

        Assert.Equal(original, File.ReadAllBytes(path));

        // The next change that is saved clears the message, and it carries the earlier choice too: it applied meanwhile.
        app.SelectOption("SettingsIdleTimeout", IdleLabel(10));
        app.WaitGone("SettingsSaveFailed");
        AssertFileBytes(path, Encoding.UTF8.GetBytes("""{"idleTimeoutMinutes":10,"lockOnMinimize":true}"""));

        // And the auto-lock uses it: minimizing locks.
        app.Minimize();
        app.Restore();
        app.WaitFor("UnlockPassword");
        app.WaitGone("SettingsTitle");
    }

    private static void PasswordWriteFails()
    {
        using var app = AppSession.Launch(SeededVault.Seed);
        Unlock(app, SeededVault.Password);
        var vaultPath = SeededVault.VaultPath(app.VaultsDirectory);
        var before = File.ReadAllBytes(vaultPath);
        var folder = Snapshot(app.VaultsDirectory);
        var entries = SeededVault.ReadContent(app.VaultsDirectory, SeededVault.Password).Entries;
        OpenSettings(app);

        // The vault cannot be replaced: the message shows, and it is not a wrong password, so there is no delay.
        using (Hold(vaultPath))
        {
            TypeChange(app, SeededVault.Password, NewPassword, NewPassword);
            app.Invoke("SettingsChangePassword");
            app.WaitFor("SettingsPasswordFailed");
            app.WaitEnabled("SettingsChangePassword");
            Assert.Null(app.Find("SettingsPasswordWrong"));
            Assert.Null(app.Find("SettingsPasswordChanged"));
            Assert.Null(app.Find("SettingsPasswordCountdown"));
        }

        // Neither the vault nor a .bak changed: the old password still opens it, the new one does not.
        Assert.Equal(before, File.ReadAllBytes(vaultPath));
        AssertSnapshot(folder, app.VaultsDirectory);
        Assert.Equal(entries, SeededVault.ReadContent(app.VaultsDirectory, SeededVault.Password).Entries);
        Assert.Throws<VaultAuthenticationException>(() => SeededVault.ReadContent(app.VaultsDirectory, NewPassword));

        // The same attempt with the file free works, with nothing delayed.
        TypeChange(app, SeededVault.Password, NewPassword, NewPassword);
        app.Invoke("SettingsChangePassword");
        app.WaitFor("SettingsPasswordChanged");
        app.WaitGone("SettingsPasswordFailed");
        Assert.Equal(entries, SeededVault.ReadContent(app.VaultsDirectory, NewPassword).Entries);
    }

    private static void KitWriteFails()
    {
        using var app = AppSession.Launch(SeededVault.SeedRecoveryKit);
        Unlock(app, SeededVault.RecoveryKitPassword);
        var vaultPath = SeededVault.VaultPath(app.VaultsDirectory);
        var before = File.ReadAllBytes(vaultPath);
        var folder = Snapshot(app.VaultsDirectory);
        OpenSettings(app);
        var kit = OpenNewKit(app);
        app.Toggle("NewKitSavedCheck");
        app.WaitEnabled("NewKitDone");

        // The vault cannot be replaced: the page stays with the kit still shown, and the message.
        using (Hold(vaultPath))
        {
            app.Invoke("NewKitDone");
            app.WaitFor("NewKitFailed");
            app.WaitEnabled("NewKitDone");
            app.WaitEnabled("NewKitCancel");
            Assert.Equal(kit, app.WaitFor("NewKitText").Name);
            Assert.Null(app.Find("SettingsTitle"));
        }

        // The old kit still opens the unchanged vault, and no .bak was made.
        Assert.Equal(before, File.ReadAllBytes(vaultPath));
        AssertSnapshot(folder, app.VaultsDirectory);
        Assert.True(SeededVault.OpensWithKit(vaultPath, SeededVault.RecoveryKitText));
        Assert.False(SeededVault.OpensWithKit(vaultPath, kit));

        // Concluding again, with the file free, writes the very kit that was shown.
        app.Invoke("NewKitDone");
        app.WaitFor("SettingsKitReplaced");
        app.WaitGone("NewKitTitle");
        Assert.True(SeededVault.OpensWithKit(vaultPath, kit));
        Assert.False(SeededVault.OpensWithKit(vaultPath, SeededVault.RecoveryKitText));
    }

    // Opens the only profile from the profile list (or the Unlock page it leads to) and waits for the entry list.
    private static void Unlock(AppSession app, string password)
    {
        if (app.Find("UnlockPassword") is null)
        {
            Assert.Single(app.Items("ProfilesList")).Patterns.Invoke.Pattern.Invoke();
        }

        app.TypePassword(password);
        app.Invoke("UnlockButton");
        app.WaitFor("EntriesList");
        app.WaitGone("UnlockPassword");
    }

    private static void OpenSettings(AppSession app)
    {
        app.Invoke("EntriesSettings");
        app.WaitFor("SettingsTitle");
        app.WaitGone("EntriesList");
    }

    // The master password goes in the kit box and the kit page opens; returns the kit text it shows. The settings page is open.
    private static string OpenNewKit(AppSession app)
    {
        app.SetValue("SettingsKitPassword", SeededVault.RecoveryKitPassword);
        app.Invoke("SettingsKitGenerate");
        var kit = app.WaitFor("NewKitText").Name;
        app.WaitFor("NewKitTitle");
        app.WaitGone("SettingsTitle");
        return kit;
    }

    private static void TypeChange(AppSession app, string current, string replacement, string confirmation)
    {
        app.SetValue("SettingsCurrentPassword", current);
        app.SetValue("SettingsNewPassword", replacement);
        app.SetValue("SettingsConfirmPassword", confirmation);
    }

    private static string IdleLabel(int minutes) => AppResources.Format("SettingsIdleOption", minutes);

    private static string SettingsPath(string vaultsDirectory) => Path.Combine(vaultsDirectory, SettingsFileName);

    // The seed of the vault plus a settings.json with exactly these bytes.
    private static Action<string> WithSettings(Action<string> seed, byte[] settings) => directory =>
    {
        seed(directory);
        File.WriteAllBytes(SettingsPath(directory), settings);
    };

    // Holds the file open with no sharing, so the app can neither read it, copy it nor replace it.
    private static FileStream Hold(string path) => new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

    private static string[] Names(string directory) =>
        [.. Directory.GetFileSystemEntries(directory).Select(path => Path.GetFileName(path)).Order(StringComparer.Ordinal)];

    // The preferences are saved on the UI thread as the choice is made, but the test reads from outside: it polls.
    private static void AssertFileBytes(string path, byte[] expected)
    {
        byte[]? actual = null;
        Retry.WhileFalse(
            () =>
            {
                actual = File.Exists(path) ? File.ReadAllBytes(path) : null;
                return actual is not null && actual.AsSpan().SequenceEqual(expected);
            },
            Timeout,
            Poll,
            ignoreException: true);
        Assert.Equal(expected, actual);
    }

    // Fails when some element of the window holds the value as its Name or its Value (a password box is never read),
    // without printing it.
    private static void AssertNotOnScreen(AppSession app, string value)
    {
        foreach (var element in app.Window.FindAllDescendants())
        {
            var name = element.Properties.Name.ValueOrDefault;
            var text = element.Properties.IsPassword.ValueOrDefault
                ? null
                : element.Patterns.Value.PatternOrDefault?.Value.ValueOrDefault;
            Assert.False(
                (name is not null && name.Contains(value, StringComparison.Ordinal))
                    || (text is not null && text.Contains(value, StringComparison.Ordinal)),
                "The Recovery Kit text is still in the window.");
        }
    }

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

    // The step has finished when this runs (its outcome is on screen), so one read is enough.
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
