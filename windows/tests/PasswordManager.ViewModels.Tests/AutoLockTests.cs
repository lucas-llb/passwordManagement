using System.ComponentModel;
using System.Runtime.CompilerServices;
using CommunityToolkit.Mvvm.Input;
using PasswordManager.Core.Profiles;
using PasswordManager.Core.Vault;
using PasswordManager.ViewModels.Navigation;

namespace PasswordManager.ViewModels.Tests;

public sealed class AutoLockTests : IDisposable
{
    private const string WrongPassword = "a fictitious wrong passphrase";

    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(5);

    private readonly TestProfiles _profiles = new();
    private readonly NavigationService _navigation = new();

    public void Dispose() => _profiles.Dispose();

    private AutoLock CreateAutoLock(AutoLockOptions? options = null) =>
        new(_navigation, _profiles.Time, options ?? AutoLockOptions.Default);

    private static char[] Chars(string password) => password.ToCharArray();

    // Profiles -> Unlock, the way the app gets there.
    private (ProfilesViewModel ProfilesPage, UnlockViewModel Unlock) OpenUnlockOf(Profile profile)
    {
        var profilesPage = new ProfilesViewModel(_profiles.Repository, _navigation, _profiles.Throttle);
        _navigation.ResetTo(profilesPage);
        profilesPage.OpenProfileCommand.Execute(profile);

        return (profilesPage, Assert.IsType<UnlockViewModel>(_navigation.Current));
    }

    private Profile CreateProfile(KdfParameters? parameters = null) =>
        _profiles.CreateProfile(
            "Alice",
            TestProfiles.Password,
            parameters ?? TestProfiles.LowCost,
            TestProfiles.Entry("First", "first@example.com"),
            TestProfiles.Entry("Second", "second@example.com"));

    // Profiles -> Unlock -> list, by running the unlock command as the app does.
    private async Task<(Profile Profile, EntryListViewModel List)> OpenVault()
    {
        var profile = CreateProfile();
        var (_, unlock) = OpenUnlockOf(profile);

        await unlock.UnlockCommand.ExecuteAsync(Chars(TestProfiles.Password));

        return (profile, Assert.IsType<EntryListViewModel>(_navigation.Current));
    }

    // Completes when the command next reports IsRunning = false. Subscribed after the auto-lock, it runs after the
    // auto-lock's own handler, so once it completes the auto-lock has seen the end of the unlock.
    private static Task WhenUnlockEnds(UnlockViewModel unlock)
    {
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var command = unlock.UnlockCommand;

        void OnChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(IAsyncRelayCommand.IsRunning) && !command.IsRunning)
            {
                command.PropertyChanged -= OnChanged;
                ended.TrySetResult();
            }
        }

        command.PropertyChanged += OnChanged;
        return ended.Task;
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + WaitLimit;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "The condition did not hold within 5 s.");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public void Constructor_NullArgument_Throws()
    {
        var time = _profiles.Time;
        var options = AutoLockOptions.Default;

        Assert.Throws<ArgumentNullException>(() => new AutoLock(null!, time, options));
        Assert.Throws<ArgumentNullException>(() => new AutoLock(_navigation, null!, options));
        Assert.Throws<ArgumentNullException>(() => new AutoLock(_navigation, time, null!));
    }

    [Fact]
    public void Options_Default_IsFiveMinutesWithoutLockOnMinimize()
    {
        Assert.Equal(TimeSpan.FromMinutes(5), AutoLockOptions.Default.IdleTimeout);
        Assert.False(AutoLockOptions.Default.LockOnMinimize);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-60_000)]
    public void Options_IdleTimeoutNotPositive_Throws(int milliseconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new AutoLockOptions(TimeSpan.FromMilliseconds(milliseconds), lockOnMinimize: false));
    }

    [Fact]
    public void IsVaultOpen_OnProfilesAndOnUnlock_IsFalse()
    {
        var autoLock = CreateAutoLock();
        var profile = CreateProfile();
        var profilesPage = new ProfilesViewModel(_profiles.Repository, _navigation, _profiles.Throttle);

        _navigation.ResetTo(profilesPage);
        Assert.False(autoLock.IsVaultOpen);

        profilesPage.OpenProfileCommand.Execute(profile);
        Assert.IsType<UnlockViewModel>(_navigation.Current);
        Assert.False(autoLock.IsVaultOpen);
    }

    [Fact]
    public async Task IsVaultOpen_AfterUnlock_IsTrueAndStaysTrueOnTheDetailPage()
    {
        var autoLock = CreateAutoLock();

        var (_, list) = await OpenVault();
        Assert.True(autoLock.IsVaultOpen);

        list.OpenEntryCommand.Execute(list.Entries[0]);
        Assert.IsType<EntryDetailViewModel>(_navigation.Current);
        Assert.True(autoLock.IsVaultOpen);
    }

    [Fact]
    public async Task IsVaultOpen_AfterAManualLock_IsFalse()
    {
        var autoLock = CreateAutoLock();
        var (_, list) = await OpenVault();

        list.LockCommand.Execute(null);

        Assert.False(autoLock.IsVaultOpen);
    }

    [Fact]
    public async Task IsVaultOpen_RaisesPropertyChangedOnOpenAndOnLock()
    {
        var autoLock = CreateAutoLock();
        var readings = new List<bool>();
        autoLock.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AutoLock.IsVaultOpen))
            {
                readings.Add(autoLock.IsVaultOpen);
            }
        };

        var (_, list) = await OpenVault();
        list.LockCommand.Execute(null);

        Assert.Equal([true, false], readings);
    }

    [Fact]
    public async Task CheckIdle_JustBeforeTheTimeout_KeepsTheVaultOpen()
    {
        var autoLock = CreateAutoLock();
        var (_, list) = await OpenVault();

        _profiles.Time.Advance(Timeout - TimeSpan.FromMilliseconds(1));
        autoLock.CheckIdle();

        Assert.Same(list, _navigation.Current);
        Assert.True(list.IsOpen);
        Assert.True(autoLock.IsVaultOpen);
    }

    [Fact]
    public async Task CheckIdle_AtTheTimeout_LocksToTheUnlockPageOfTheSameProfile()
    {
        var autoLock = CreateAutoLock();
        var (profile, _) = await OpenVault();

        _profiles.Time.Advance(Timeout);
        autoLock.CheckIdle();

        var unlock = Assert.IsType<UnlockViewModel>(_navigation.Current);
        Assert.Equal(profile, unlock.Profile);
        Assert.True(_navigation.GoBack());
        Assert.IsType<ProfilesViewModel>(_navigation.Current);
        Assert.False(_navigation.CanGoBack);
    }

    [Fact]
    public async Task CheckIdle_AtTheTimeout_ClosesTheSessionAndDropsTheEntries()
    {
        var autoLock = CreateAutoLock();
        var (_, list) = await OpenVault();

        _profiles.Time.Advance(Timeout);
        autoLock.CheckIdle();

        Assert.Empty(list.Entries);
        Assert.False(list.IsOpen);
        Assert.False(autoLock.IsVaultOpen);
    }

    [Fact]
    public async Task ReportActivity_RestartsTheIdleClock()
    {
        var autoLock = CreateAutoLock();
        var (_, list) = await OpenVault();

        _profiles.Time.Advance(TimeSpan.FromMinutes(4));
        autoLock.ReportActivity();
        _profiles.Time.Advance(TimeSpan.FromMinutes(4));
        autoLock.CheckIdle();

        Assert.Same(list, _navigation.Current);
        Assert.True(list.IsOpen);

        _profiles.Time.Advance(TimeSpan.FromMinutes(1));
        autoLock.CheckIdle();

        Assert.False(list.IsOpen);
        Assert.IsType<UnlockViewModel>(_navigation.Current);
    }

    [Fact]
    public async Task CheckIdle_OnTheDetailPage_LocksAndLeavesNoVaultPageReachable()
    {
        var autoLock = CreateAutoLock();
        var (_, list) = await OpenVault();
        list.OpenEntryCommand.Execute(list.Entries[0]);
        Assert.IsType<EntryDetailViewModel>(_navigation.Current);

        _profiles.Time.Advance(Timeout);
        autoLock.CheckIdle();

        var visited = new List<PageViewModel> { _navigation.Current! };
        while (_navigation.GoBack())
        {
            visited.Add(_navigation.Current!);
        }

        Assert.Collection(
            visited,
            page => Assert.IsType<UnlockViewModel>(page),
            page => Assert.IsType<ProfilesViewModel>(page));
        Assert.False(list.IsOpen);
        Assert.Empty(list.Entries);
    }

    [Fact]
    public async Task CheckIdle_TimeBeforeTheVaultOpened_DoesNotCount()
    {
        var autoLock = CreateAutoLock();
        var profile = CreateProfile();
        var (_, unlock) = OpenUnlockOf(profile);

        _profiles.Time.Advance(TimeSpan.FromMinutes(10));
        await unlock.UnlockCommand.ExecuteAsync(Chars(TestProfiles.Password));
        autoLock.CheckIdle();

        var list = Assert.IsType<EntryListViewModel>(_navigation.Current);
        Assert.True(list.IsOpen);
        Assert.True(autoLock.IsVaultOpen);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    public async Task CheckIdle_WallClockJump_NeitherLocksEarlyNorDelaysTheLock(int hours)
    {
        var autoLock = CreateAutoLock();
        var (_, list) = await OpenVault();

        _profiles.Time.Advance(TimeSpan.FromMinutes(4));
        _profiles.Time.JumpWallClock(TimeSpan.FromHours(hours));
        autoLock.CheckIdle();
        Assert.True(list.IsOpen);

        _profiles.Time.Advance(TimeSpan.FromMinutes(1));
        autoLock.CheckIdle();
        Assert.False(list.IsOpen);
        Assert.IsType<UnlockViewModel>(_navigation.Current);
    }

    [Fact]
    public void Triggers_OnTheProfilesPage_DoNothing()
    {
        var autoLock = CreateAutoLock(new AutoLockOptions(Timeout, lockOnMinimize: true));
        CreateProfile();
        var profilesPage = new ProfilesViewModel(_profiles.Repository, _navigation, _profiles.Throttle);
        _navigation.ResetTo(profilesPage);
        var navigated = 0;
        _navigation.Navigated += (_, _) => navigated++;

        _profiles.Time.Advance(TimeSpan.FromMinutes(10));
        autoLock.CheckIdle();
        autoLock.OnSessionLocked();
        autoLock.OnMinimized();

        Assert.Equal(0, navigated);
        Assert.Same(profilesPage, _navigation.Current);
        Assert.False(_navigation.CanGoBack);
        Assert.False(autoLock.IsVaultOpen);
    }

    [Fact]
    public async Task Triggers_OnTheUnlockPage_DoNothingAndKeepTheErrorAndDelay()
    {
        var autoLock = CreateAutoLock(new AutoLockOptions(Timeout, lockOnMinimize: true));
        var profile = CreateProfile();
        var (_, unlock) = OpenUnlockOf(profile);
        await unlock.UnlockCommand.ExecuteAsync(Chars(WrongPassword));
        Assert.Equal(UnlockError.WrongPassword, unlock.Error);
        Assert.Equal(1, unlock.RetrySeconds);
        var navigated = 0;
        _navigation.Navigated += (_, _) => navigated++;

        _profiles.Time.Advance(TimeSpan.FromMinutes(10));
        autoLock.CheckIdle();
        autoLock.OnSessionLocked();
        autoLock.OnMinimized();

        Assert.Equal(0, navigated);
        Assert.Same(unlock, _navigation.Current);
        Assert.Equal(UnlockError.WrongPassword, unlock.Error);
        Assert.Equal(1, unlock.RetrySeconds);
        Assert.False(autoLock.IsVaultOpen);
    }

    [Fact]
    public async Task OnSessionLocked_WithAVaultOpen_LocksAtOnce()
    {
        var autoLock = CreateAutoLock();
        var (profile, list) = await OpenVault();

        autoLock.OnSessionLocked();

        var unlock = Assert.IsType<UnlockViewModel>(_navigation.Current);
        Assert.Equal(profile, unlock.Profile);
        Assert.False(list.IsOpen);
        Assert.Empty(list.Entries);
        Assert.False(autoLock.IsVaultOpen);
    }

    [Fact]
    public async Task OnMinimized_DefaultOptions_KeepsTheVaultOpen()
    {
        var autoLock = CreateAutoLock();
        var (_, list) = await OpenVault();

        autoLock.OnMinimized();

        Assert.Same(list, _navigation.Current);
        Assert.True(list.IsOpen);
        Assert.True(autoLock.IsVaultOpen);
    }

    [Fact]
    public async Task OnMinimized_WithLockOnMinimize_LocksTheVault()
    {
        var autoLock = CreateAutoLock(new AutoLockOptions(Timeout, lockOnMinimize: true));
        var (_, list) = await OpenVault();

        autoLock.OnMinimized();

        Assert.IsType<UnlockViewModel>(_navigation.Current);
        Assert.False(list.IsOpen);
        Assert.False(autoLock.IsVaultOpen);
    }

    [Fact]
    public async Task OnSessionLocked_WhileArgon2idRuns_LocksTheVaultThatUnlockOpensAndNothingLater()
    {
        // The real cost (64 MiB, 3 passes), so the unlock is still running when the trigger arrives.
        var autoLock = CreateAutoLock();
        var profile = CreateProfile(KdfParameters.Default);
        var (_, unlock) = OpenUnlockOf(profile);

        var running = unlock.UnlockCommand.ExecuteAsync(Chars(TestProfiles.Password));
        Assert.True(unlock.UnlockCommand.IsRunning);
        autoLock.OnSessionLocked();
        Assert.Same(unlock, _navigation.Current);
        await running;

        // The lock follows the end of the command, whose notification may come after the await resumes.
        await WaitUntil(() => _navigation.Current is UnlockViewModel relocked && relocked != unlock);
        var locked = Assert.IsType<UnlockViewModel>(_navigation.Current);
        Assert.Equal(profile, locked.Profile);
        Assert.False(autoLock.IsVaultOpen);

        // The one-shot is consumed: the next unlock stays open.
        var reopened = WhenUnlockEnds(locked);
        await locked.UnlockCommand.ExecuteAsync(Chars(TestProfiles.Password));
        await reopened.WaitAsync(WaitLimit, TestContext.Current.CancellationToken);

        var list = Assert.IsType<EntryListViewModel>(_navigation.Current);
        Assert.True(list.IsOpen);
        Assert.True(autoLock.IsVaultOpen);
    }

    [Fact]
    public async Task OnSessionLocked_WhileAWrongPasswordRuns_LeavesNothingPending()
    {
        var autoLock = CreateAutoLock();
        var profile = CreateProfile(KdfParameters.Default);
        var (_, unlock) = OpenUnlockOf(profile);

        var running = unlock.UnlockCommand.ExecuteAsync(Chars(WrongPassword));
        Assert.True(unlock.UnlockCommand.IsRunning);
        autoLock.OnSessionLocked();
        var ended = WhenUnlockEnds(unlock);
        await running;
        await ended.WaitAsync(WaitLimit, TestContext.Current.CancellationToken);

        Assert.Same(unlock, _navigation.Current);
        Assert.Equal(UnlockError.WrongPassword, unlock.Error);

        // Same page, same command: the trigger must not carry over to this second run.
        _profiles.Time.Advance(TimeSpan.FromSeconds(unlock.RetrySeconds));
        unlock.RefreshRetryDelay();
        var reopened = WhenUnlockEnds(unlock);
        await unlock.UnlockCommand.ExecuteAsync(Chars(TestProfiles.Password));
        await reopened.WaitAsync(WaitLimit, TestContext.Current.CancellationToken);

        var list = Assert.IsType<EntryListViewModel>(_navigation.Current);
        Assert.True(list.IsOpen);
        Assert.True(autoLock.IsVaultOpen);
    }

    [Fact]
    public async Task AutoLock_DoesNotTouchTheThrottle()
    {
        var autoLock = CreateAutoLock();
        var (profile, _) = await OpenVault();

        autoLock.OnSessionLocked();

        var unlock = Assert.IsType<UnlockViewModel>(_navigation.Current);
        Assert.Equal(0, unlock.RetrySeconds);
        Assert.Equal(UnlockError.None, unlock.Error);
        Assert.Equal(TimeSpan.Zero, _profiles.Throttle.RemainingDelay(profile.Id));
        Assert.True(unlock.UnlockCommand.CanExecute(Chars(TestProfiles.Password)));
    }

    [Fact]
    public async Task CheckIdle_AfterTheLock_LeavesNoReferenceToTheListOrItsEntries()
    {
        var autoLock = CreateAutoLock();
        var (_, unlock) = OpenUnlockOf(CreateProfile());
        await unlock.UnlockCommand.ExecuteAsync(Chars(TestProfiles.Password));
        var (listReference, entriesReference) = TakeWeakReferencesToTheOpenList();

        _profiles.Time.Advance(Timeout);
        autoLock.CheckIdle();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.False(autoLock.IsVaultOpen);
        Assert.False(listReference.IsAlive);
        Assert.False(entriesReference.IsAlive);
    }

    // Not inlined, so no strong reference to the list outlives this frame in the test method (Debug keeps locals).
    [MethodImpl(MethodImplOptions.NoInlining)]
    private (WeakReference List, WeakReference Entries) TakeWeakReferencesToTheOpenList()
    {
        var list = Assert.IsType<EntryListViewModel>(_navigation.Current);
        Assert.NotEmpty(list.Entries);

        return (new WeakReference(list), new WeakReference(list.Entries));
    }
}
