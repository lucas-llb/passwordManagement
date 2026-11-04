using System.Collections.Concurrent;
using System.Reflection;
using PasswordManager.Core.Passwords;
using PasswordManager.Core.Preferences;
using PasswordManager.Core.Profiles;
using PasswordManager.Core.Vault;
using PasswordManager.ViewModels.Navigation;

namespace PasswordManager.ViewModels.Tests;

public sealed class SettingsViewModelTests : IDisposable
{
    // All fictitious.
    private const string WrongPassword = "a fictitious wrong passphrase";
    private const string NewPassword = "a fictitious new passphrase";
    private const string ShortPassword = "too short";

    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(30);

    private readonly TestProfiles _profiles = new();
    private readonly NavigationService _navigation = new();
    private readonly List<VaultSession> _sessions = [];

    public enum ChangePath
    {
        Right,
        Wrong,
        TooShort,
        Mismatch,
        DuringDelay,
        WriteFailure,
    }

    public enum KitPath
    {
        Right,
        Wrong,
        LoneSurrogate,
        DuringDelay,
        VaultLocked,
    }

    public void Dispose()
    {
        foreach (var session in _sessions)
        {
            session.Dispose();
        }

        _profiles.Dispose();
    }

    // List -> Settings, with an open vault of two entries. The vault costs little (LowCost).
    private (Profile Profile, VaultSession Session, EntryListViewModel List, SettingsViewModel Settings) OpenSettings()
    {
        var profile = _profiles.CreateProfile("Alice", TestProfiles.Password, TestProfiles.Entry("Mail"), TestProfiles.Entry("Bank"));
        var session = _profiles.OpenSession(profile);
        _sessions.Add(session);
        var list = new EntryListViewModel(profile, session, _profiles.Repository, _navigation, _profiles.Throttle, _profiles.Clipboard, _profiles.Settings);
        _navigation.ResetTo(list);
        list.OpenSettingsCommand.Execute(null);

        return (profile, session, list, Assert.IsType<SettingsViewModel>(_navigation.Current));
    }

    private string VaultPath(Profile profile) => _profiles.Repository.GetVaultPath(profile.Id);

    private string SettingsFilePath => Path.Combine(_profiles.DirectoryPath, PreferencesStore.FileName);

    private static ChangePasswordRequest Request(string current, string newPassword = NewPassword, string? confirmation = null) =>
        new(current.ToCharArray(), newPassword.ToCharArray(), (confirmation ?? newPassword).ToCharArray());

    private static void AssertZeroed(char[] chars)
    {
        Assert.NotEmpty(chars);
        Assert.All(chars, c => Assert.Equal('\0', c));
    }

    private static void AssertZeroed(ChangePasswordRequest request)
    {
        AssertZeroed(request.CurrentPassword);
        AssertZeroed(request.NewPassword);
        AssertZeroed(request.Confirmation);
    }

    private static bool PasswordOpens(string path, string password)
    {
        using var session = new VaultSession(path, TestProfiles.LowCost);
        try
        {
            session.Unlock(password);
            return true;
        }
        catch (VaultAuthenticationException)
        {
            return false;
        }
    }

    private static bool KitOpens(string path, string kitText)
    {
        Span<byte> key = stackalloc byte[VaultFormat.KeySize];
        try
        {
            RecoveryKit.Unlock(File.ReadAllBytes(path), kitText, key);
            return true;
        }
        catch (VaultAuthenticationException)
        {
            return false;
        }
    }

    // Replaces the kit that the profile was created with by one whose text the test knows.
    private static string SeedKnownKit(VaultSession session)
    {
        var kit = new char[RecoveryKit.TextLength];
        session.CommitRecoveryKit(session.PrepareRecoveryKit(kit));
        return new string(kit);
    }

    private static byte[] KitSlotBytes(string path)
    {
        var file = File.ReadAllBytes(path);
        var header = VaultHeader.Parse(file);
        var slot = header.Slots.Single(range => range.Offset == header.RecoveryKitSlotOffset);
        return file.AsSpan(slot.Offset, slot.Length).ToArray();
    }

    // The name and bytes of every file directly in the vaults folder.
    private Dictionary<string, byte[]> FolderSnapshot() =>
        Directory.GetFiles(_profiles.DirectoryPath)
            .ToDictionary(path => Path.GetFileName(path), path => File.ReadAllBytes(path), StringComparer.Ordinal);

    private void AssertFolderUnchanged(Dictionary<string, byte[]> before)
    {
        var after = FolderSnapshot();

        Assert.Equal(before.Keys.Order(StringComparer.Ordinal), after.Keys.Order(StringComparer.Ordinal));
        foreach (var (name, bytes) in before)
        {
            Assert.Equal(bytes, after[name]);
        }
    }

    // Runs a command with its continuations held, so the test can act while the work ended and the page has not seen it.
    private static async Task RunHeld(Func<Task> start, Action<QueuedSynchronizationContext, Task> whileHeld)
    {
        var context = new QueuedSynchronizationContext();
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(context);
        Task running;
        try
        {
            running = start();
            whileHeld(context, running);

            var ended = running;
            Assert.True(SpinWait.SpinUntil(
                () =>
                {
                    context.Pump();
                    return ended.IsCompleted;
                },
                WaitLimit));
            context.Pump();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        await running;
    }

    private static void AwaitPending(QueuedSynchronizationContext context) =>
        Assert.True(SpinWait.SpinUntil(() => context.HasPending, WaitLimit));

    // --- Preferences ---

    [Fact]
    public void Constructor_NullArguments_Throw()
    {
        var (_, _, list, _) = OpenSettings();

        Assert.Throws<ArgumentNullException>(() => new SettingsViewModel(null!, _navigation));
        Assert.Throws<ArgumentNullException>(() => new SettingsViewModel(list, null!));
    }

    [Fact]
    public void Request_NullArgument_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new ChangePasswordRequest(null!, [], []));
        Assert.Throws<ArgumentNullException>(() => new ChangePasswordRequest([], null!, []));
        Assert.Throws<ArgumentNullException>(() => new ChangePasswordRequest([], [], null!));
    }

    [Fact]
    public void Open_ShowsCurrentValuesAndWritesNothing()
    {
        Assert.True(_profiles.Settings.Update(new AppPreferences(15, lockOnMinimize: true)));
        var raised = 0;
        _profiles.Settings.Changed += (_, _) => raised++;
        var profile = _profiles.CreateProfile("Alice", TestProfiles.Password, TestProfiles.Entry("Mail"));
        var session = _profiles.OpenSession(profile);
        _sessions.Add(session);
        var list = new EntryListViewModel(profile, session, _profiles.Repository, _navigation, _profiles.Throttle, _profiles.Clipboard, _profiles.Settings);
        _navigation.ResetTo(list);
        var before = FolderSnapshot();
        var stamp = File.GetLastWriteTimeUtc(SettingsFilePath);

        list.OpenSettingsCommand.Execute(null);

        var settings = Assert.IsType<SettingsViewModel>(_navigation.Current);
        Assert.Equal(15, settings.IdleTimeoutMinutes);
        Assert.True(settings.LockOnMinimize);
        Assert.Equal([1, 2, 5, 10, 15, 30, 60], settings.IdleOptions);
        Assert.False(settings.SaveFailed);
        Assert.False(settings.IsBusy);
        Assert.Equal(SettingsOutcome.None, settings.Outcome);
        Assert.Equal(NewPasswordError.None, settings.PasswordError);
        Assert.Equal(PasswordStrengthLevel.None, settings.Strength);
        Assert.Equal(0, settings.RetrySeconds);
        Assert.Equal(0, raised);
        AssertFolderUnchanged(before);
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(SettingsFilePath));
    }

    [Fact]
    public void Open_DefaultValues_CreatesNoFile()
    {
        var (_, _, _, settings) = OpenSettings();

        Assert.Equal(5, settings.IdleTimeoutMinutes);
        Assert.False(settings.LockOnMinimize);
        Assert.False(File.Exists(SettingsFilePath));
    }

    [Fact]
    public void IdleTimeout_Changed_SavesAndRaisesChanged()
    {
        var (_, _, _, settings) = OpenSettings();
        var raised = 0;
        _profiles.Settings.Changed += (_, _) => raised++;

        settings.IdleTimeoutMinutes = 15;

        Assert.Equal(1, raised);
        Assert.Equal(new AppPreferences(15, lockOnMinimize: false), _profiles.Settings.Current);
        Assert.Equal("""{"idleTimeoutMinutes":15,"lockOnMinimize":false}""", File.ReadAllText(SettingsFilePath));
        Assert.False(settings.SaveFailed);
    }

    [Fact]
    public void LockOnMinimize_Changed_SavesAndRaisesChanged()
    {
        var (_, _, _, settings) = OpenSettings();
        settings.IdleTimeoutMinutes = 30;
        var raised = 0;
        _profiles.Settings.Changed += (_, _) => raised++;

        settings.LockOnMinimize = true;

        Assert.Equal(1, raised);
        Assert.True(_profiles.Settings.AutoLockOptions.LockOnMinimize);
        Assert.Equal(TimeSpan.FromMinutes(30), _profiles.Settings.AutoLockOptions.IdleTimeout);
        Assert.Equal("""{"idleTimeoutMinutes":30,"lockOnMinimize":true}""", File.ReadAllText(SettingsFilePath));
    }

    [Fact]
    public void SameValue_WritesNothing()
    {
        var (_, _, _, settings) = OpenSettings();
        var raised = 0;
        _profiles.Settings.Changed += (_, _) => raised++;

        settings.IdleTimeoutMinutes = 5;
        settings.LockOnMinimize = false;

        Assert.Equal(0, raised);
        Assert.False(File.Exists(SettingsFilePath));
    }

    [Fact]
    public void Change_SaveFails_ShowsSaveFailedAndNextSaveClearsIt()
    {
        var (_, _, _, settings) = OpenSettings();
        settings.IdleTimeoutMinutes = 10;
        var saved = File.ReadAllBytes(SettingsFilePath);

        using (new FileStream(SettingsFilePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            settings.IdleTimeoutMinutes = 15;

            // The value still applies; only the file is behind.
            Assert.True(settings.SaveFailed);
            Assert.Equal(15, settings.IdleTimeoutMinutes);
            Assert.Equal(15, _profiles.Settings.Current.IdleTimeoutMinutes);
        }

        Assert.Equal(saved, File.ReadAllBytes(SettingsFilePath));

        settings.LockOnMinimize = true;

        Assert.False(settings.SaveFailed);
        Assert.Equal("""{"idleTimeoutMinutes":15,"lockOnMinimize":true}""", File.ReadAllText(SettingsFilePath));
    }

    // --- Change password ---

    [Fact]
    public async Task ChangePassword_Null_Throws()
    {
        var (_, _, _, settings) = OpenSettings();

        await Assert.ThrowsAsync<ArgumentNullException>(() => settings.ChangePasswordCommand.ExecuteAsync(null));
    }

    [Fact]
    public async Task ChangePassword_Right_NewOpensOldFailsVaultStaysOpen()
    {
        var (profile, session, list, settings) = OpenSettings();
        var path = VaultPath(profile);
        var kit = SeedKnownKit(session);
        var slot = KitSlotBytes(path);
        var entries = list.Entries;
        var request = Request(TestProfiles.Password);

        await settings.ChangePasswordCommand.ExecuteAsync(request);

        Assert.Equal(SettingsOutcome.PasswordChanged, settings.Outcome);
        Assert.Equal(NewPasswordError.None, settings.PasswordError);
        Assert.False(settings.IsBusy);
        Assert.Same(settings, _navigation.Current);
        AssertZeroed(request);

        // The vault stays open, with the same entries.
        Assert.True(list.IsOpen);
        Assert.Same(entries, list.Entries);

        Assert.True(PasswordOpens(path, NewPassword));
        Assert.False(PasswordOpens(path, TestProfiles.Password));

        // The kit slot did not change, and the kit keeps opening the vault.
        Assert.Equal(slot, KitSlotBytes(path));
        Assert.True(KitOpens(path, kit));

        // The copy beside the vault has the new file too: the old password does not open it.
        var backup = path + VaultFile.BackupSuffix;
        Assert.False(PasswordOpens(backup, TestProfiles.Password));
        Assert.True(PasswordOpens(backup, NewPassword));
    }

    [Fact]
    public async Task ChangePassword_TooShort_NoArgon2idAndNothingCounted()
    {
        var (profile, _, _, settings) = OpenSettings();
        var before = FolderSnapshot();

        // With a wrong current password: if Argon2id ran, the outcome would be PasswordWrong and it would count.
        await settings.ChangePasswordCommand.ExecuteAsync(Request(WrongPassword, ShortPassword));

        Assert.Equal(NewPasswordError.TooShort, settings.PasswordError);
        Assert.Equal(SettingsOutcome.None, settings.Outcome);
        Assert.Equal(TimeSpan.Zero, _profiles.Throttle.RemainingDelay(profile.Id));
        Assert.Equal(0, settings.RetrySeconds);
        AssertFolderUnchanged(before);
    }

    [Fact]
    public async Task ChangePassword_Mismatch_NoArgon2idAndNothingCounted()
    {
        var (profile, _, _, settings) = OpenSettings();
        var before = FolderSnapshot();

        await settings.ChangePasswordCommand.ExecuteAsync(Request(WrongPassword, NewPassword, NewPassword + "x"));

        Assert.Equal(NewPasswordError.Mismatch, settings.PasswordError);
        Assert.Equal(SettingsOutcome.None, settings.Outcome);
        Assert.Equal(TimeSpan.Zero, _profiles.Throttle.RemainingDelay(profile.Id));
        Assert.Equal(0, settings.RetrySeconds);
        AssertFolderUnchanged(before);
    }

    [Fact]
    public async Task ChangePassword_TooShortWinsOverMismatch()
    {
        var (_, _, _, settings) = OpenSettings();

        await settings.ChangePasswordCommand.ExecuteAsync(Request(TestProfiles.Password, ShortPassword, ShortPassword + "x"));

        Assert.Equal(NewPasswordError.TooShort, settings.PasswordError);
    }

    [Fact]
    public async Task ChangePassword_WeakButTwelveRunes_IsAccepted()
    {
        var (profile, _, _, settings) = OpenSettings();
        const string weak = "aaaaaaaaaaaa";
        settings.UpdateStrength(weak);
        Assert.Equal(PasswordStrength.Evaluate(weak), settings.Strength);
        Assert.True(settings.Strength < PasswordStrengthLevel.Fair);

        await settings.ChangePasswordCommand.ExecuteAsync(Request(TestProfiles.Password, weak));

        // The meter is advisory: only the length is a rule.
        Assert.Equal(NewPasswordError.None, settings.PasswordError);
        Assert.Equal(SettingsOutcome.PasswordChanged, settings.Outcome);
        Assert.True(PasswordOpens(VaultPath(profile), weak));
    }

    [Fact]
    public void UpdateStrength_FollowsTheText()
    {
        var (_, _, _, settings) = OpenSettings();
        Assert.Equal(PasswordStrengthLevel.None, settings.Strength);

        settings.UpdateStrength("aaaa");
        var weak = settings.Strength;
        Assert.Equal(PasswordStrength.Evaluate("aaaa"), weak);

        settings.UpdateStrength("correct horse battery staple is not this phrase");
        Assert.True(settings.Strength > weak);

        settings.UpdateStrength(ReadOnlySpan<char>.Empty);
        Assert.Equal(PasswordStrengthLevel.None, settings.Strength);
    }

    [Fact]
    public async Task ChangePassword_WrongCurrent_CountsAndDelays()
    {
        var (profile, _, list, settings) = OpenSettings();
        var before = FolderSnapshot();

        await settings.ChangePasswordCommand.ExecuteAsync(Request(WrongPassword));

        Assert.Equal(SettingsOutcome.PasswordWrong, settings.Outcome);
        Assert.Equal(1, settings.RetrySeconds);
        Assert.False(settings.ChangePasswordCommand.CanExecute(null));
        Assert.False(settings.GenerateKitCommand.CanExecute(null));
        AssertFolderUnchanged(before);
        Assert.True(list.IsOpen);
        Assert.Same(settings, _navigation.Current);

        // The unlock screen of the profile shares the delay.
        var unlock = new UnlockViewModel(profile, _profiles.Repository, _navigation, _profiles.Throttle, _profiles.Clipboard, _profiles.Settings);
        unlock.OnNavigatedTo();
        Assert.Equal(1, unlock.RetrySeconds);

        // Once it runs out, the forms work again.
        _profiles.Time.Advance(TimeSpan.FromSeconds(1));
        settings.RefreshRetryDelay();
        Assert.Equal(0, settings.RetrySeconds);
        Assert.True(settings.ChangePasswordCommand.CanExecute(null));
        Assert.True(settings.GenerateKitCommand.CanExecute(null));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ChangePassword_DuringDelay_IsIgnored(bool rightPassword)
    {
        var (profile, _, _, settings) = OpenSettings();
        var before = FolderSnapshot();

        // A wrong password on the unlock screen of the same profile: the delay is shared.
        _profiles.Throttle.RecordFailure(profile.Id);
        var request = Request(rightPassword ? TestProfiles.Password : WrongPassword);

        await settings.ChangePasswordCommand.ExecuteAsync(request);

        // No Argon2id and nothing counted: a right password did not reset the delay, a wrong one did not double it.
        Assert.Equal(TimeSpan.FromSeconds(1), _profiles.Throttle.RemainingDelay(profile.Id));
        Assert.Equal(1, settings.RetrySeconds);
        Assert.Equal(SettingsOutcome.None, settings.Outcome);
        AssertFolderUnchanged(before);
        AssertZeroed(request);
    }

    [Fact]
    public async Task ChangePassword_DuringDelay_KeepsTheMessageOfTheWrongPassword()
    {
        var (_, _, _, settings) = OpenSettings();
        await settings.ChangePasswordCommand.ExecuteAsync(Request(WrongPassword));

        await settings.ChangePasswordCommand.ExecuteAsync(Request(TestProfiles.Password));

        Assert.Equal(SettingsOutcome.PasswordWrong, settings.Outcome);
    }

    [Fact]
    public async Task ChangePassword_RightAfterDelay_ClearsIt()
    {
        var (profile, _, _, settings) = OpenSettings();

        // Two wrong passwords: the delay grows to 2 s, then it runs out.
        await settings.ChangePasswordCommand.ExecuteAsync(Request(WrongPassword));
        _profiles.Time.Advance(TimeSpan.FromSeconds(1));
        await settings.ChangePasswordCommand.ExecuteAsync(Request(WrongPassword));
        Assert.Equal(TimeSpan.FromSeconds(2), _profiles.Throttle.RemainingDelay(profile.Id));
        _profiles.Time.Advance(TimeSpan.FromSeconds(2));

        await settings.ChangePasswordCommand.ExecuteAsync(Request(TestProfiles.Password));

        Assert.Equal(SettingsOutcome.PasswordChanged, settings.Outcome);
        Assert.Equal(0, settings.RetrySeconds);
        Assert.Equal(TimeSpan.Zero, _profiles.Throttle.RemainingDelay(profile.Id));

        // The failures were forgotten: the next wrong password is the first again (1 s, not 4 s).
        await settings.ChangePasswordCommand.ExecuteAsync(Request(WrongPassword));

        Assert.Equal(TimeSpan.FromSeconds(1), _profiles.Throttle.RemainingDelay(profile.Id));
    }

    [Fact]
    public async Task ChangePassword_WriteFails_IsFailedAndOldPasswordStays()
    {
        var (profile, _, list, settings) = OpenSettings();
        var path = VaultPath(profile);
        var bytes = File.ReadAllBytes(path);
        var request = Request(TestProfiles.Password);

        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await settings.ChangePasswordCommand.ExecuteAsync(request);
        }

        Assert.Equal(SettingsOutcome.PasswordFailed, settings.Outcome);
        Assert.Equal(TimeSpan.Zero, _profiles.Throttle.RemainingDelay(profile.Id));
        Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.True(PasswordOpens(path, TestProfiles.Password));
        Assert.True(list.IsOpen);
        AssertZeroed(request);

        // The session did not change either: the old password is the one a retry needs.
        await settings.ChangePasswordCommand.ExecuteAsync(Request(TestProfiles.Password));

        Assert.Equal(SettingsOutcome.PasswordChanged, settings.Outcome);
        Assert.True(PasswordOpens(path, NewPassword));
        Assert.False(PasswordOpens(path, TestProfiles.Password));
    }

    [Fact]
    public async Task ChangePassword_LoneSurrogate_IsFailedAndNotCounted()
    {
        var (profile, _, list, settings) = OpenSettings();
        var before = FolderSnapshot();
        var request = Request(TestProfiles.Password, "\uD800" + "abcdefghijklmn");

        await settings.ChangePasswordCommand.ExecuteAsync(request);

        // The rule counts it as long enough, and the key derivation refuses it. It says nothing about the current password.
        Assert.Equal(SettingsOutcome.PasswordFailed, settings.Outcome);
        Assert.Equal(NewPasswordError.None, settings.PasswordError);
        Assert.Equal(TimeSpan.Zero, _profiles.Throttle.RemainingDelay(profile.Id));
        Assert.Equal(0, settings.RetrySeconds);
        AssertFolderUnchanged(before);
        AssertZeroed(request);
        Assert.True(list.IsOpen);
    }

    [Theory]
    [InlineData(ChangePath.Right)]
    [InlineData(ChangePath.Wrong)]
    [InlineData(ChangePath.TooShort)]
    [InlineData(ChangePath.Mismatch)]
    [InlineData(ChangePath.DuringDelay)]
    [InlineData(ChangePath.WriteFailure)]
    public async Task ChangePassword_EveryPath_ZeroesTheThreeArrays(ChangePath path)
    {
        var (profile, _, _, settings) = OpenSettings();
        var request = path switch
        {
            ChangePath.Wrong => Request(WrongPassword),
            ChangePath.TooShort => Request(TestProfiles.Password, ShortPassword),
            ChangePath.Mismatch => Request(TestProfiles.Password, NewPassword, NewPassword + "x"),
            _ => Request(TestProfiles.Password),
        };

        if (path == ChangePath.DuringDelay)
        {
            _profiles.Throttle.RecordFailure(profile.Id);
        }

        if (path == ChangePath.WriteFailure)
        {
            using var held = new FileStream(VaultPath(profile), FileMode.Open, FileAccess.Read, FileShare.None);
            await settings.ChangePasswordCommand.ExecuteAsync(request);
        }
        else
        {
            await settings.ChangePasswordCommand.ExecuteAsync(request);
        }

        AssertZeroed(request);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ChangePassword_LockedWhileRunning_SetsNothing(bool rightPassword)
    {
        var (profile, _, list, settings) = OpenSettings();
        var path = VaultPath(profile);
        var request = Request(rightPassword ? TestProfiles.Password : WrongPassword);

        await RunHeld(
            () => settings.ChangePasswordCommand.ExecuteAsync(request),
            (context, _) =>
            {
                // The change ended, and the page does not know yet: the UI thread is busy with the lock.
                AwaitPending(context);
                list.LockCommand.Execute(null);
            });

        Assert.Equal(SettingsOutcome.None, settings.Outcome);
        Assert.Equal(NewPasswordError.None, settings.PasswordError);
        Assert.Equal(0, settings.RetrySeconds);
        Assert.False(settings.IsBusy);
        Assert.IsType<UnlockViewModel>(_navigation.Current);
        AssertZeroed(request);

        // A change that started completes, so the new password is the valid one. A password that was tried still counts.
        Assert.Equal(rightPassword, PasswordOpens(path, NewPassword));
        Assert.Equal(!rightPassword, PasswordOpens(path, TestProfiles.Password));
        Assert.Equal(rightPassword ? TimeSpan.Zero : TimeSpan.FromSeconds(1), _profiles.Throttle.RemainingDelay(profile.Id));
    }

    [Fact]
    public async Task ChangePassword_VaultAlreadyLocked_DoesNothingAndCountsNothing()
    {
        var (profile, _, list, settings) = OpenSettings();
        list.LockCommand.Execute(null);
        var before = FolderSnapshot();
        var request = Request(TestProfiles.Password);

        await settings.ChangePasswordCommand.ExecuteAsync(request);

        Assert.Equal(SettingsOutcome.None, settings.Outcome);
        Assert.Equal(TimeSpan.Zero, _profiles.Throttle.RemainingDelay(profile.Id));
        AssertFolderUnchanged(before);
        AssertZeroed(request);
    }

    // --- Kit ---

    [Fact]
    public async Task GenerateKit_Null_Throws()
    {
        var (_, _, _, settings) = OpenSettings();

        await Assert.ThrowsAsync<ArgumentNullException>(() => settings.GenerateKitCommand.ExecuteAsync(null));
    }

    [Fact]
    public async Task GenerateKit_Right_OpensKitPageAndWritesNothing()
    {
        var (profile, session, list, settings) = OpenSettings();
        var path = VaultPath(profile);
        var oldKit = SeedKnownKit(session);
        _profiles.Throttle.RecordFailure(profile.Id);
        _profiles.Time.Advance(TimeSpan.FromSeconds(1));
        settings.RefreshRetryDelay();
        var before = FolderSnapshot();

        await settings.GenerateKitCommand.ExecuteAsync(TestProfiles.Password.ToCharArray());

        var kitPage = Assert.IsType<NewRecoveryKitViewModel>(_navigation.Current);
        var kit = new string(kitPage.KitText);
        Assert.Equal(RecoveryKit.TextLength, kit.Length);
        Assert.DoesNotContain('\0', kit);
        Assert.False(settings.IsBusy);
        Assert.True(list.IsOpen);

        // The right password forgets the failures.
        Assert.Equal(TimeSpan.Zero, _profiles.Throttle.RemainingDelay(profile.Id));

        // Nothing was written: the current kit still opens the file, and the new one does not.
        AssertFolderUnchanged(before);
        Assert.True(KitOpens(path, oldKit));
        Assert.False(KitOpens(path, kit));
    }

    [Fact]
    public async Task GenerateKit_Wrong_CountsAndStays()
    {
        var (profile, _, list, settings) = OpenSettings();
        var before = FolderSnapshot();

        await settings.GenerateKitCommand.ExecuteAsync(WrongPassword.ToCharArray());

        Assert.Equal(SettingsOutcome.KitWrong, settings.Outcome);
        Assert.Equal(1, settings.RetrySeconds);
        Assert.Equal(TimeSpan.FromSeconds(1), _profiles.Throttle.RemainingDelay(profile.Id));
        Assert.False(settings.GenerateKitCommand.CanExecute(null));
        Assert.False(settings.ChangePasswordCommand.CanExecute(null));
        Assert.Same(settings, _navigation.Current);
        Assert.True(list.IsOpen);
        AssertFolderUnchanged(before);
    }

    [Fact]
    public async Task GenerateKit_LoneSurrogate_IsAWrongPassword()
    {
        var (profile, _, _, settings) = OpenSettings();

        await settings.GenerateKitCommand.ExecuteAsync(("\uD800" + "abcdefghijk").ToCharArray());

        // Here the only password is the one being checked, so it counts like any other wrong one.
        Assert.Equal(SettingsOutcome.KitWrong, settings.Outcome);
        Assert.Equal(TimeSpan.FromSeconds(1), _profiles.Throttle.RemainingDelay(profile.Id));
        Assert.Same(settings, _navigation.Current);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GenerateKit_DuringDelay_IsIgnored(bool rightPassword)
    {
        var (profile, _, _, settings) = OpenSettings();
        _profiles.Throttle.RecordFailure(profile.Id);
        var before = FolderSnapshot();
        var password = (rightPassword ? TestProfiles.Password : WrongPassword).ToCharArray();

        await settings.GenerateKitCommand.ExecuteAsync(password);

        Assert.Equal(TimeSpan.FromSeconds(1), _profiles.Throttle.RemainingDelay(profile.Id));
        Assert.Equal(1, settings.RetrySeconds);
        Assert.Equal(SettingsOutcome.None, settings.Outcome);
        Assert.Same(settings, _navigation.Current);
        AssertFolderUnchanged(before);
        AssertZeroed(password);
    }

    [Theory]
    [InlineData(KitPath.Right)]
    [InlineData(KitPath.Wrong)]
    [InlineData(KitPath.LoneSurrogate)]
    [InlineData(KitPath.DuringDelay)]
    [InlineData(KitPath.VaultLocked)]
    public async Task GenerateKit_EveryPath_ZeroesThePassword(KitPath path)
    {
        var (profile, _, list, settings) = OpenSettings();
        var password = path switch
        {
            KitPath.Wrong => WrongPassword.ToCharArray(),
            KitPath.LoneSurrogate => ("\uD800" + "abcdefghijk").ToCharArray(),
            _ => TestProfiles.Password.ToCharArray(),
        };

        if (path == KitPath.DuringDelay)
        {
            _profiles.Throttle.RecordFailure(profile.Id);
        }

        if (path == KitPath.VaultLocked)
        {
            list.LockCommand.Execute(null);
        }

        await settings.GenerateKitCommand.ExecuteAsync(password);

        AssertZeroed(password);
    }

    [Fact]
    public async Task GenerateKit_LockedWhileRunning_DoesNotNavigateAndZeroesKit()
    {
        var (_, _, list, settings) = OpenSettings();
        var inProgress = typeof(SettingsViewModel).GetField("_kitInProgress", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var password = TestProfiles.Password.ToCharArray();
        char[]? kit = null;

        await RunHeld(
            () => settings.GenerateKitCommand.ExecuteAsync(password),
            (context, _) =>
            {
                // The check ends first; let that one through, then the kit is generated and the page has not seen it.
                Assert.True(SpinWait.SpinUntil(
                    () =>
                    {
                        context.Pump();
                        kit = (char[]?)inProgress.GetValue(settings);
                        return kit is not null;
                    },
                    WaitLimit));
                AwaitPending(context);
                Assert.Contains(kit!, c => c != '\0');

                list.LockCommand.Execute(null);
            });

        Assert.IsType<UnlockViewModel>(_navigation.Current);
        Assert.Equal(SettingsOutcome.None, settings.Outcome);
        Assert.False(settings.IsBusy);
        AssertZeroed(kit!);
        AssertZeroed(password);
        Assert.Null(inProgress.GetValue(settings));
    }

    [Fact]
    public async Task GenerateKit_LockedDuringTheCheck_NeverGeneratesAKit()
    {
        var (_, _, list, settings) = OpenSettings();
        var inProgress = typeof(SettingsViewModel).GetField("_kitInProgress", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var generated = false;

        await RunHeld(
            () => settings.GenerateKitCommand.ExecuteAsync(TestProfiles.Password.ToCharArray()),
            (context, _) =>
            {
                AwaitPending(context);
                generated = inProgress.GetValue(settings) is not null;
                list.LockCommand.Execute(null);
            });

        Assert.False(generated);
        Assert.Null(inProgress.GetValue(settings));
        Assert.IsType<UnlockViewModel>(_navigation.Current);
    }

    // --- Page ---

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task IsBusy_BlocksBackAndLinks(bool changePassword)
    {
        var (profile, _, list, settings) = OpenSettings();
        var before = FolderSnapshot();
        Func<Task> start = changePassword
            ? () => settings.ChangePasswordCommand.ExecuteAsync(Request(WrongPassword))
            : () => settings.GenerateKitCommand.ExecuteAsync(WrongPassword.ToCharArray());
        var ignored = Request(TestProfiles.Password);
        var ignoredKit = TestProfiles.Password.ToCharArray();

        await RunHeld(
            start,
            (_, _) =>
            {
                Assert.True(settings.IsBusy);
                Assert.False(settings.GoBackCommand.CanExecute(null));
                Assert.False(settings.OpenExportCommand.CanExecute(null));
                Assert.False(settings.OpenImportCommand.CanExecute(null));
                Assert.False(settings.ChangePasswordCommand.CanExecute(null));
                Assert.False(settings.GenerateKitCommand.CanExecute(null));

                // Direct calls are ignored too, including the other form.
                settings.GoBackCommand.Execute(null);
                settings.OpenExportCommand.Execute(null);
                settings.OpenImportCommand.Execute(null);
                Assert.Same(settings, _navigation.Current);
                Assert.True(settings.ChangePasswordCommand.ExecuteAsync(ignored).IsCompletedSuccessfully);
                Assert.True(settings.GenerateKitCommand.ExecuteAsync(ignoredKit).IsCompletedSuccessfully);
                AssertZeroed(ignored);
                AssertZeroed(ignoredKit);

                // The preference controls stay enabled.
                settings.IdleTimeoutMinutes = 30;
                Assert.Equal(30, _profiles.Settings.Current.IdleTimeoutMinutes);
            });

        Assert.False(settings.IsBusy);
        Assert.True(settings.GoBackCommand.CanExecute(null));
        Assert.True(settings.OpenExportCommand.CanExecute(null));
        Assert.True(settings.OpenImportCommand.CanExecute(null));

        // The attempt that was ignored did not reach Argon2id: only the first wrong password counted.
        Assert.Equal(TimeSpan.FromSeconds(1), _profiles.Throttle.RemainingDelay(profile.Id));
        Assert.Equal(before[Path.GetFileName(VaultPath(profile))], File.ReadAllBytes(VaultPath(profile)));

        settings.GoBackCommand.Execute(null);
        Assert.Same(list, _navigation.Current);
    }

    [Fact]
    public async Task OnNavigatedFrom_ClearsOutcomeErrorAndStrength()
    {
        var (_, _, _, settings) = OpenSettings();
        settings.UpdateStrength("aaaa");
        await settings.ChangePasswordCommand.ExecuteAsync(Request(TestProfiles.Password, ShortPassword));
        Assert.Equal(NewPasswordError.TooShort, settings.PasswordError);
        Assert.NotEqual(PasswordStrengthLevel.None, settings.Strength);
        await settings.GenerateKitCommand.ExecuteAsync(WrongPassword.ToCharArray());
        Assert.Equal(SettingsOutcome.KitWrong, settings.Outcome);

        settings.OnNavigatedFrom();

        Assert.Equal(SettingsOutcome.None, settings.Outcome);
        Assert.Equal(NewPasswordError.None, settings.PasswordError);
        Assert.Equal(PasswordStrengthLevel.None, settings.Strength);
    }

    [Fact]
    public void OnNavigatedTo_PicksUpTheDelayOfTheUnlockScreen()
    {
        var (profile, _, _, settings) = OpenSettings();
        Assert.Equal(0, settings.RetrySeconds);
        _profiles.Throttle.RecordFailure(profile.Id);

        settings.OpenExportCommand.Execute(null);
        Assert.IsType<ExportViewModel>(_navigation.Current).GoBackCommand.Execute(null);

        Assert.Same(settings, _navigation.Current);
        Assert.Equal(1, settings.RetrySeconds);
    }

    // Holds every continuation until the test asks for them, on the test's own thread.
    private sealed class QueuedSynchronizationContext : SynchronizationContext
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _queue = new();

        public bool HasPending => !_queue.IsEmpty;

        public override void Post(SendOrPostCallback d, object? state) => _queue.Enqueue((d, state));

        public void Pump()
        {
            while (_queue.TryDequeue(out var item))
            {
                item.Callback(item.State);
            }
        }
    }
}
