using System.Collections.Concurrent;
using PasswordManager.Core.Profiles;
using PasswordManager.Core.Vault;
using PasswordManager.ViewModels.Navigation;

namespace PasswordManager.ViewModels.Tests;

public sealed class NewRecoveryKitViewModelTests : IDisposable
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(30);

    private readonly TestProfiles _profiles = new();
    private readonly NavigationService _navigation = new();
    private readonly List<VaultSession> _sessions = [];

    public enum LeaveBy
    {
        Done,
        Cancel,
        Lock,
    }

    public void Dispose()
    {
        foreach (var session in _sessions)
        {
            session.Dispose();
        }

        _profiles.Dispose();
    }

    private sealed record Opened(
        Profile Profile,
        EntryListViewModel List,
        SettingsViewModel Settings,
        NewRecoveryKitViewModel Page,
        char[] KitBuffer,
        string OldKit,
        string NewKit,
        string VaultPath);

    // List -> Settings -> new kit, on a vault whose current kit the test knows. The test keeps the array it passed.
    private Opened OpenKitPage()
    {
        var profile = _profiles.CreateProfile("Alice", TestProfiles.Password, TestProfiles.Entry("Mail"));
        var session = _profiles.OpenSession(profile);
        _sessions.Add(session);
        var list = new EntryListViewModel(profile, session, _profiles.Repository, _navigation, _profiles.Throttle, _profiles.Clipboard, _profiles.Settings);
        _navigation.ResetTo(list);
        list.OpenSettingsCommand.Execute(null);
        var settings = Assert.IsType<SettingsViewModel>(_navigation.Current);

        var old = new char[RecoveryKit.TextLength];
        session.CommitRecoveryKit(session.PrepareRecoveryKit(old));

        var kit = new char[RecoveryKit.TextLength];
        var replacement = session.PrepareRecoveryKit(kit);
        var page = new NewRecoveryKitViewModel(list, settings, _navigation, kit, replacement);
        _navigation.NavigateTo(page);

        return new Opened(profile, list, settings, page, kit, new string(old), new string(kit), _profiles.Repository.GetVaultPath(profile.Id));
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

    private static void AssertZeroed(char[] chars)
    {
        Assert.NotEmpty(chars);
        Assert.All(chars, c => Assert.Equal('\0', c));
    }

    private static async Task RunHeld(Func<Task> start, Action<QueuedSynchronizationContext> whileHeld)
    {
        var context = new QueuedSynchronizationContext();
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(context);
        Task running;
        try
        {
            running = start();
            whileHeld(context);

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

    [Fact]
    public void Constructor_NullOrBadLength_Throws()
    {
        var opened = OpenKitPage();
        var (list, settings) = (opened.List, opened.Settings);
        var session = _sessions[0];
        var kit = new char[RecoveryKit.TextLength];
        var replacement = session.PrepareRecoveryKit(kit);

        Assert.Throws<ArgumentNullException>(() => new NewRecoveryKitViewModel(null!, settings, _navigation, kit, replacement));
        Assert.Throws<ArgumentNullException>(() => new NewRecoveryKitViewModel(list, null!, _navigation, kit, replacement));
        Assert.Throws<ArgumentNullException>(() => new NewRecoveryKitViewModel(list, settings, null!, kit, replacement));
        Assert.Throws<ArgumentNullException>(() => new NewRecoveryKitViewModel(list, settings, _navigation, null!, replacement));
        Assert.Throws<ArgumentNullException>(() => new NewRecoveryKitViewModel(list, settings, _navigation, kit, null!));
        Assert.Throws<ArgumentException>(() => new NewRecoveryKitViewModel(list, settings, _navigation, new char[RecoveryKit.TextLength - 1], replacement));
    }

    [Fact]
    public void Page_ShowsTheKit_AndDoneStartsDisabled()
    {
        var opened = OpenKitPage();

        Assert.Equal(opened.NewKit, new string(opened.Page.KitText));
        Assert.False(opened.Page.KitSaved);
        Assert.False(opened.Page.SaveFailed);
        Assert.False(opened.Page.IsBusy);
        Assert.False(opened.Page.DoneCommand.CanExecute(null));
        Assert.True(opened.Page.CancelCommand.CanExecute(null));

        opened.Page.KitSaved = true;

        Assert.True(opened.Page.DoneCommand.CanExecute(null));
    }

    [Fact]
    public async Task Done_WithoutCheck_IsIgnored()
    {
        var opened = OpenKitPage();
        var bytes = File.ReadAllBytes(opened.VaultPath);

        await opened.Page.DoneCommand.ExecuteAsync(null);

        Assert.Same(opened.Page, _navigation.Current);
        Assert.Equal(bytes, File.ReadAllBytes(opened.VaultPath));
        Assert.True(KitOpens(opened.VaultPath, opened.OldKit));
        Assert.Equal(SettingsOutcome.None, opened.Settings.Outcome);
    }

    [Fact]
    public async Task Done_Commits_NewKitOpensOldFails_ReturnsToSettingsWithOutcome()
    {
        var opened = OpenKitPage();
        var backup = opened.VaultPath + VaultFile.BackupSuffix;
        opened.Page.KitSaved = true;

        await opened.Page.DoneCommand.ExecuteAsync(null);

        Assert.Same(opened.Settings, _navigation.Current);
        Assert.Equal(SettingsOutcome.KitReplaced, opened.Settings.Outcome);
        Assert.False(opened.Page.SaveFailed);
        Assert.False(opened.Page.IsBusy);
        Assert.True(opened.List.IsOpen);

        Assert.True(KitOpens(opened.VaultPath, opened.NewKit));
        Assert.False(KitOpens(opened.VaultPath, opened.OldKit));
        Assert.False(KitOpens(backup, opened.OldKit));
        Assert.True(KitOpens(backup, opened.NewKit));
        Assert.True(PasswordOpens(opened.VaultPath, TestProfiles.Password));
    }

    [Fact]
    public void Cancel_WritesNothing_OldKitStays()
    {
        var opened = OpenKitPage();
        var bytes = File.ReadAllBytes(opened.VaultPath);
        opened.Page.KitSaved = true;

        opened.Page.CancelCommand.Execute(null);

        Assert.Same(opened.Settings, _navigation.Current);
        Assert.Equal(SettingsOutcome.None, opened.Settings.Outcome);
        Assert.Equal(bytes, File.ReadAllBytes(opened.VaultPath));
        Assert.True(KitOpens(opened.VaultPath, opened.OldKit));
        Assert.False(KitOpens(opened.VaultPath, opened.NewKit));
    }

    [Theory]
    [InlineData(LeaveBy.Done)]
    [InlineData(LeaveBy.Cancel)]
    [InlineData(LeaveBy.Lock)]
    public async Task OnNavigatedFrom_ZeroesTheKit(LeaveBy leaveBy)
    {
        var opened = OpenKitPage();
        Assert.Contains(opened.KitBuffer, c => c != '\0');
        opened.Page.KitSaved = true;

        switch (leaveBy)
        {
            case LeaveBy.Done:
                await opened.Page.DoneCommand.ExecuteAsync(null);
                break;
            case LeaveBy.Cancel:
                opened.Page.CancelCommand.Execute(null);
                break;
            default:
                opened.List.LockCommand.Execute(null);
                break;
        }

        Assert.NotSame(opened.Page, _navigation.Current);
        AssertZeroed(opened.KitBuffer);
        Assert.True(opened.Page.KitText.IsEmpty);

        // A page that was left cannot commit: the user can no longer have written that kit down from here.
        Assert.False(opened.Page.KitSaved);
        Assert.False(opened.Page.DoneCommand.CanExecute(null));
    }

    [Fact]
    public async Task Done_WriteFails_StaysWithFailedAndKitAndRetrySucceeds()
    {
        var opened = OpenKitPage();
        var bytes = File.ReadAllBytes(opened.VaultPath);
        opened.Page.KitSaved = true;

        using (new FileStream(opened.VaultPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await opened.Page.DoneCommand.ExecuteAsync(null);
        }

        Assert.Same(opened.Page, _navigation.Current);
        Assert.True(opened.Page.SaveFailed);
        Assert.False(opened.Page.IsBusy);
        Assert.True(opened.Page.KitSaved);
        Assert.Equal(opened.NewKit, new string(opened.Page.KitText));
        Assert.Equal(SettingsOutcome.None, opened.Settings.Outcome);
        Assert.Equal(bytes, File.ReadAllBytes(opened.VaultPath));
        Assert.True(KitOpens(opened.VaultPath, opened.OldKit));

        await opened.Page.DoneCommand.ExecuteAsync(null);

        Assert.Same(opened.Settings, _navigation.Current);
        Assert.False(opened.Page.SaveFailed);
        Assert.Equal(SettingsOutcome.KitReplaced, opened.Settings.Outcome);
        Assert.True(KitOpens(opened.VaultPath, opened.NewKit));
        Assert.False(KitOpens(opened.VaultPath, opened.OldKit));
    }

    [Fact]
    public async Task Done_LockAfterCommit_DoesNotNavigate()
    {
        var opened = OpenKitPage();
        opened.Page.KitSaved = true;
        var navigated = 0;

        await RunHeld(
            () => opened.Page.DoneCommand.ExecuteAsync(null),
            context =>
            {
                // The write ended and the page has not seen it: the UI thread is busy with the lock.
                Assert.True(SpinWait.SpinUntil(() => context.HasPending, WaitLimit));
                opened.List.LockCommand.Execute(null);
                _navigation.Navigated += (_, _) => navigated++;
            });

        Assert.Equal(0, navigated);
        Assert.IsType<UnlockViewModel>(_navigation.Current);
        Assert.Equal(SettingsOutcome.None, opened.Settings.Outcome);
        Assert.False(opened.Page.SaveFailed);
        AssertZeroed(opened.KitBuffer);

        // The write itself had already ended, so it stays on disk.
        Assert.True(KitOpens(opened.VaultPath, opened.NewKit));
        Assert.False(KitOpens(opened.VaultPath, opened.OldKit));
    }

    [Fact]
    public async Task Done_LockedBeforeTheWrite_WritesNothingAndSetsNothing()
    {
        var opened = OpenKitPage();
        var bytes = File.ReadAllBytes(opened.VaultPath);
        opened.Page.KitSaved = true;
        var done = opened.Page.DoneCommand;

        // A page that outlived the lock, asked once more: leaving the page cleared the check.
        opened.List.LockCommand.Execute(null);
        await done.ExecuteAsync(null);

        Assert.False(opened.Page.SaveFailed);
        Assert.Equal(bytes, File.ReadAllBytes(opened.VaultPath));
        Assert.True(KitOpens(opened.VaultPath, opened.OldKit));
    }

    [Fact]
    public async Task Done_LockRacesTheWrite_SetsNoFailure()
    {
        var opened = OpenKitPage();
        var bytes = File.ReadAllBytes(opened.VaultPath);
        opened.Page.KitSaved = true;

        // The lock comes after the check of the command and before its write can start: the session refuses the commit.
        _sessions[0].Dispose();
        await opened.Page.DoneCommand.ExecuteAsync(null);

        Assert.False(opened.Page.SaveFailed);
        Assert.Equal(bytes, File.ReadAllBytes(opened.VaultPath));
    }

    [Fact]
    public async Task Done_FileChangedSincePrepare_IsFailedAndWritesNothing()
    {
        var opened = OpenKitPage();
        var session = _sessions[0];
        session.Save(session.Content);
        var bytes = File.ReadAllBytes(opened.VaultPath);
        opened.Page.KitSaved = true;

        await opened.Page.DoneCommand.ExecuteAsync(null);

        // A newer save is never overwritten: the page stays, and the kit it shows does not open the file.
        Assert.Same(opened.Page, _navigation.Current);
        Assert.True(opened.Page.SaveFailed);
        Assert.Equal(bytes, File.ReadAllBytes(opened.VaultPath));
        Assert.True(KitOpens(opened.VaultPath, opened.OldKit));
        Assert.False(KitOpens(opened.VaultPath, opened.NewKit));
    }

    [Fact]
    public async Task Cancel_WhileRunning_IsBlocked()
    {
        var opened = OpenKitPage();
        opened.Page.KitSaved = true;

        await RunHeld(
            () => opened.Page.DoneCommand.ExecuteAsync(null),
            _ =>
            {
                Assert.True(opened.Page.IsBusy);
                Assert.False(opened.Page.CancelCommand.CanExecute(null));
                Assert.False(opened.Page.DoneCommand.CanExecute(null));

                // A direct call is ignored too: the kit is still there, and the page is still the current one.
                opened.Page.CancelCommand.Execute(null);
                Assert.Same(opened.Page, _navigation.Current);
                Assert.Equal(opened.NewKit, new string(opened.Page.KitText));

                // So is a second write.
                Assert.True(opened.Page.DoneCommand.ExecuteAsync(null).IsCompletedSuccessfully);
            });

        Assert.Same(opened.Settings, _navigation.Current);
        Assert.Equal(SettingsOutcome.KitReplaced, opened.Settings.Outcome);
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
