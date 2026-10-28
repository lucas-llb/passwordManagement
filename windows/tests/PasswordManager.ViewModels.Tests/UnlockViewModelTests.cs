using PasswordManager.Core.Profiles;
using PasswordManager.Core.Vault;
using PasswordManager.ViewModels.Navigation;

namespace PasswordManager.ViewModels.Tests;

public sealed class UnlockViewModelTests : IDisposable
{
    private const string WrongPassword = "a fictitious wrong passphrase";

    private readonly TestProfiles _profiles = new();
    private readonly NavigationService _navigation = new();

    public void Dispose() => _profiles.Dispose();

    // Profiles -> Unlock, the way the app gets there.
    private (Profile Profile, ProfilesViewModel ProfilesPage, UnlockViewModel Unlock) OpenUnlock(
        params string[] titles)
    {
        var profile = _profiles.CreateProfile(
            "Alice",
            TestProfiles.Password,
            [.. titles.Select(title => TestProfiles.Entry(title))]);

        return OpenUnlockOf(profile);
    }

    private (Profile Profile, ProfilesViewModel ProfilesPage, UnlockViewModel Unlock) OpenUnlockOf(Profile profile)
    {
        var profilesPage = new ProfilesViewModel(_profiles.Repository, _navigation, _profiles.Throttle);
        _navigation.ResetTo(profilesPage);
        profilesPage.OpenProfileCommand.Execute(profile);

        return (profile, profilesPage, Assert.IsType<UnlockViewModel>(_navigation.Current));
    }

    // Lets the delay after a wrong password run out and updates the page, the way the view's timer does.
    private void WaitOutDelay(UnlockViewModel unlock)
    {
        _profiles.Time.Advance(TimeSpan.FromSeconds(unlock.RetrySeconds));
        unlock.RefreshRetryDelay();
    }

    private static char[] Chars(string password) => password.ToCharArray();

    private static void AssertZeroed(char[] chars)
    {
        Assert.NotEmpty(chars);
        Assert.All(chars, c => Assert.Equal('\0', c));
    }

    [Fact]
    public async Task Unlock_RightPassword_NavigatesToTheEntryListAsRoot()
    {
        var (_, _, unlock) = OpenUnlock("First", "Second", "Third");

        await unlock.UnlockCommand.ExecuteAsync(Chars(TestProfiles.Password));

        var list = Assert.IsType<EntryListViewModel>(_navigation.Current);
        Assert.Equal(["First", "Second", "Third"], list.Entries.Select(e => e.Title));
        Assert.False(_navigation.CanGoBack);
        Assert.Equal(UnlockError.None, unlock.Error);
    }

    [Fact]
    public async Task Unlock_WrongPassword_SetsWrongPasswordAndStaysOnTheUnlockPage()
    {
        var (_, _, unlock) = OpenUnlock("First");

        await unlock.UnlockCommand.ExecuteAsync(Chars(WrongPassword));

        Assert.Equal(UnlockError.WrongPassword, unlock.Error);
        Assert.Same(unlock, _navigation.Current);
        Assert.True(_navigation.CanGoBack);
    }

    [Fact]
    public async Task Unlock_VaultFileDeleted_SetsVaultUnreadable()
    {
        var (profile, _, unlock) = OpenUnlock("First");
        File.Delete(_profiles.Repository.GetVaultPath(profile.Id));

        await unlock.UnlockCommand.ExecuteAsync(Chars(TestProfiles.Password));

        Assert.Equal(UnlockError.VaultUnreadable, unlock.Error);
        Assert.Same(unlock, _navigation.Current);
    }

    [Fact]
    public async Task Unlock_VaultFileNotAVault_SetsVaultUnreadable()
    {
        var (profile, _, unlock) = OpenUnlock("First");
        File.WriteAllBytes(_profiles.Repository.GetVaultPath(profile.Id), [0x01, 0x02, 0x03]);

        await unlock.UnlockCommand.ExecuteAsync(Chars(TestProfiles.Password));

        Assert.Equal(UnlockError.VaultUnreadable, unlock.Error);
        Assert.Same(unlock, _navigation.Current);
    }

    [Fact]
    public async Task Unlock_RightPassword_ZeroesThePasswordArray()
    {
        var (_, _, unlock) = OpenUnlock("First");
        var password = Chars(TestProfiles.Password);

        await unlock.UnlockCommand.ExecuteAsync(password);

        AssertZeroed(password);
    }

    [Fact]
    public async Task Unlock_WrongPassword_ZeroesThePasswordArray()
    {
        var (_, _, unlock) = OpenUnlock("First");
        var password = Chars(WrongPassword);

        await unlock.UnlockCommand.ExecuteAsync(password);

        AssertZeroed(password);
    }

    [Fact]
    public async Task Unlock_VaultUnreadable_ZeroesThePasswordArray()
    {
        var (profile, _, unlock) = OpenUnlock("First");
        File.Delete(_profiles.Repository.GetVaultPath(profile.Id));
        var password = Chars(TestProfiles.Password);

        await unlock.UnlockCommand.ExecuteAsync(password);

        AssertZeroed(password);
    }

    [Fact]
    public async Task Unlock_RightPasswordAfterAWrongOne_ClearsTheError()
    {
        var (_, _, unlock) = OpenUnlock("First");
        await unlock.UnlockCommand.ExecuteAsync(Chars(WrongPassword));
        Assert.Equal(UnlockError.WrongPassword, unlock.Error);
        WaitOutDelay(unlock);

        await unlock.UnlockCommand.ExecuteAsync(Chars(TestProfiles.Password));

        Assert.Equal(UnlockError.None, unlock.Error);
        Assert.IsType<EntryListViewModel>(_navigation.Current);
    }

    [Fact]
    public async Task Unlock_WrongPasswordTwice_KeepsTheError()
    {
        var (_, _, unlock) = OpenUnlock("First");
        await unlock.UnlockCommand.ExecuteAsync(Chars(WrongPassword));
        WaitOutDelay(unlock);

        await unlock.UnlockCommand.ExecuteAsync(Chars(WrongPassword));

        Assert.Equal(UnlockError.WrongPassword, unlock.Error);
        Assert.Same(unlock, _navigation.Current);
    }

    [Fact]
    public async Task Unlock_NullPassword_Throws()
    {
        var (_, _, unlock) = OpenUnlock("First");

        await Assert.ThrowsAsync<ArgumentNullException>(() => unlock.UnlockCommand.ExecuteAsync(null));
    }

    [Fact]
    public void GoBack_ReturnsToTheProfilesPage()
    {
        var (_, profilesPage, unlock) = OpenUnlock("First");

        unlock.GoBackCommand.Execute(null);

        Assert.Same(profilesPage, _navigation.Current);
        Assert.False(_navigation.CanGoBack);
    }

    [Fact]
    public void Constructor_NullArgument_Throws()
    {
        var profile = _profiles.CreateProfile("Alice", TestProfiles.Password);
        var repository = _profiles.Repository;
        var throttle = _profiles.Throttle;

        Assert.Throws<ArgumentNullException>(() => new UnlockViewModel(null!, repository, _navigation, throttle));
        Assert.Throws<ArgumentNullException>(() => new UnlockViewModel(profile, null!, _navigation, throttle));
        Assert.Throws<ArgumentNullException>(() => new UnlockViewModel(profile, repository, null!, throttle));
        Assert.Throws<ArgumentNullException>(() => new UnlockViewModel(profile, repository, _navigation, null!));
    }

    [Fact]
    public void RetrySeconds_NoAttemptYet_IsZeroAndTheCommandCanExecute()
    {
        var (_, _, unlock) = OpenUnlock("First");

        Assert.Equal(0, unlock.RetrySeconds);
        Assert.True(unlock.UnlockCommand.CanExecute(Chars(WrongPassword)));
    }

    [Fact]
    public async Task Unlock_WrongPassword_StartsAOneSecondDelayThatBlocksTheCommand()
    {
        var (_, _, unlock) = OpenUnlock("First");

        await unlock.UnlockCommand.ExecuteAsync(Chars(WrongPassword));

        Assert.Equal(1, unlock.RetrySeconds);
        Assert.False(unlock.UnlockCommand.CanExecute(Chars(TestProfiles.Password)));
    }

    [Fact]
    public async Task RefreshRetryDelay_AfterTheDelay_AllowsAnotherAttemptAndRaisesCanExecuteChanged()
    {
        var (_, _, unlock) = OpenUnlock("First");
        await unlock.UnlockCommand.ExecuteAsync(Chars(WrongPassword));
        var raised = 0;
        unlock.UnlockCommand.CanExecuteChanged += (_, _) => raised++;

        _profiles.Time.Advance(TimeSpan.FromSeconds(1));
        unlock.RefreshRetryDelay();

        Assert.Equal(0, unlock.RetrySeconds);
        Assert.True(unlock.UnlockCommand.CanExecute(Chars(TestProfiles.Password)));
        Assert.Equal(1, raised);
    }

    [Fact]
    public async Task RefreshRetryDelay_BeforeTheDelayEnds_KeepsTheCommandBlocked()
    {
        var (_, _, unlock) = OpenUnlock("First");
        await unlock.UnlockCommand.ExecuteAsync(Chars(WrongPassword));

        _profiles.Time.Advance(TimeSpan.FromMilliseconds(999));
        unlock.RefreshRetryDelay();

        Assert.Equal(1, unlock.RetrySeconds);
        Assert.False(unlock.UnlockCommand.CanExecute(Chars(TestProfiles.Password)));
    }

    [Fact]
    public async Task RetrySeconds_CountsDownRoundingUp()
    {
        var (_, _, unlock) = OpenUnlock("First");
        await unlock.UnlockCommand.ExecuteAsync(Chars(WrongPassword));
        WaitOutDelay(unlock);
        await unlock.UnlockCommand.ExecuteAsync(Chars(WrongPassword));
        Assert.Equal(2, unlock.RetrySeconds);
        var readings = new List<int>();

        foreach (var step in new[] { 200, 1300, 500 })
        {
            _profiles.Time.Advance(TimeSpan.FromMilliseconds(step));
            unlock.RefreshRetryDelay();
            readings.Add(unlock.RetrySeconds);
        }

        // After 0.2 s, 1.5 s and 2.0 s.
        Assert.Equal([2, 1, 0], readings);
    }

    [Fact]
    public async Task Unlock_SevenWrongPasswords_FollowTheDelaySequenceAndStayAtThirtySeconds()
    {
        var (_, _, unlock) = OpenUnlock("First");
        var delays = new List<int>();

        for (var attempt = 0; attempt < 7; attempt++)
        {
            await unlock.UnlockCommand.ExecuteAsync(Chars(WrongPassword));
            delays.Add(unlock.RetrySeconds);
            WaitOutDelay(unlock);
        }

        Assert.Equal([1, 2, 4, 8, 16, 30, 30], delays);
        Assert.Equal(0, unlock.RetrySeconds);
    }

    [Fact]
    public async Task Unlock_CalledDuringTheDelay_IsIgnoredAndCountsNothing()
    {
        var (_, _, unlock) = OpenUnlock("First");
        await unlock.UnlockCommand.ExecuteAsync(Chars(WrongPassword));
        var rightPassword = Chars(TestProfiles.Password);
        var wrongPassword = Chars(WrongPassword);

        // A direct call skips the disabled button: neither the right nor the wrong password may have any effect.
        await unlock.UnlockCommand.ExecuteAsync(rightPassword);
        await unlock.UnlockCommand.ExecuteAsync(wrongPassword);

        Assert.Same(unlock, _navigation.Current);
        AssertZeroed(rightPassword);
        AssertZeroed(wrongPassword);
        Assert.Equal(UnlockError.WrongPassword, unlock.Error);
        Assert.Equal(1, unlock.RetrySeconds);

        // The ignored wrong password was not counted: this is the second failure (2 s), not the third (4 s).
        WaitOutDelay(unlock);
        await unlock.UnlockCommand.ExecuteAsync(Chars(WrongPassword));
        Assert.Equal(2, unlock.RetrySeconds);
    }

    [Fact]
    public async Task Unlock_RightPasswordAfterTheDelay_ResetsTheCountAcrossLock()
    {
        var (_, _, unlock) = OpenUnlock("First");
        await unlock.UnlockCommand.ExecuteAsync(Chars(WrongPassword));
        WaitOutDelay(unlock);
        await unlock.UnlockCommand.ExecuteAsync(Chars(WrongPassword));
        Assert.Equal(2, unlock.RetrySeconds);
        WaitOutDelay(unlock);
        await unlock.UnlockCommand.ExecuteAsync(Chars(TestProfiles.Password));
        var list = Assert.IsType<EntryListViewModel>(_navigation.Current);

        list.LockCommand.Execute(null);
        var lockedUnlock = Assert.IsType<UnlockViewModel>(_navigation.Current);
        Assert.Equal(0, lockedUnlock.RetrySeconds);
        await lockedUnlock.UnlockCommand.ExecuteAsync(Chars(WrongPassword));

        // The first failure again (1 s), not the third (4 s).
        Assert.Equal(1, lockedUnlock.RetrySeconds);
    }

    [Fact]
    public async Task Unlock_VaultUnreadable_StartsNoDelay()
    {
        var (profile, _, unlock) = OpenUnlock("First");
        File.Delete(_profiles.Repository.GetVaultPath(profile.Id));

        await unlock.UnlockCommand.ExecuteAsync(Chars(TestProfiles.Password));

        Assert.Equal(UnlockError.VaultUnreadable, unlock.Error);
        Assert.Equal(0, unlock.RetrySeconds);
        Assert.True(unlock.UnlockCommand.CanExecute(Chars(TestProfiles.Password)));
        Assert.Equal(TimeSpan.Zero, _profiles.Throttle.RemainingDelay(profile.Id));
    }

    [Fact]
    public async Task OnNavigatedTo_ReopeningTheProfileAfterBack_KeepsTheDelay()
    {
        var (profile, profilesPage, unlock) = OpenUnlock("First");
        await unlock.UnlockCommand.ExecuteAsync(Chars(WrongPassword));
        unlock.GoBackCommand.Execute(null);
        Assert.Same(profilesPage, _navigation.Current);

        profilesPage.OpenProfileCommand.Execute(profile);

        var reopened = Assert.IsType<UnlockViewModel>(_navigation.Current);
        Assert.NotSame(unlock, reopened);
        Assert.Equal(1, reopened.RetrySeconds);
        Assert.False(reopened.UnlockCommand.CanExecute(Chars(TestProfiles.Password)));
    }

    [Fact]
    public async Task OnNavigatedTo_DelayEndedWhileAway_AllowsTheAttempt()
    {
        var (profile, profilesPage, unlock) = OpenUnlock("First");
        await unlock.UnlockCommand.ExecuteAsync(Chars(WrongPassword));
        unlock.GoBackCommand.Execute(null);
        _profiles.Time.Advance(TimeSpan.FromSeconds(5));

        profilesPage.OpenProfileCommand.Execute(profile);

        var reopened = Assert.IsType<UnlockViewModel>(_navigation.Current);
        Assert.Equal(0, reopened.RetrySeconds);
        Assert.True(reopened.UnlockCommand.CanExecute(Chars(TestProfiles.Password)));
    }

    [Fact]
    public async Task OnNavigatedTo_DelayOfAnotherProfile_DoesNotBlockThisOne()
    {
        var (_, profilesPage, unlock) = OpenUnlock("First");
        await unlock.UnlockCommand.ExecuteAsync(Chars(WrongPassword));
        unlock.GoBackCommand.Execute(null);
        var bob = _profiles.CreateProfile("Bob", TestProfiles.Password);

        profilesPage.Refresh();
        profilesPage.OpenProfileCommand.Execute(bob);

        var bobUnlock = Assert.IsType<UnlockViewModel>(_navigation.Current);
        Assert.Equal(0, bobUnlock.RetrySeconds);
        await bobUnlock.UnlockCommand.ExecuteAsync(Chars(TestProfiles.Password));
        Assert.IsType<EntryListViewModel>(_navigation.Current);
    }

    [Fact]
    public async Task GoBack_DuringTheDelay_IsAllowed()
    {
        var (_, profilesPage, unlock) = OpenUnlock("First");
        await unlock.UnlockCommand.ExecuteAsync(Chars(WrongPassword));
        Assert.Equal(1, unlock.RetrySeconds);

        Assert.True(unlock.GoBackCommand.CanExecute(null));
        unlock.GoBackCommand.Execute(null);

        Assert.Same(profilesPage, _navigation.Current);
    }

    [Fact]
    public async Task GoBack_WhileArgon2idRuns_IsBlockedUntilTheUnlockEnds()
    {
        // The real cost (64 MiB, 3 passes), so the unlock is still running when the checks below happen.
        var profile = _profiles.CreateProfile("Alice", TestProfiles.Password, KdfParameters.Default);
        var (_, profilesPage, unlock) = OpenUnlockOf(profile);

        var running = unlock.UnlockCommand.ExecuteAsync(Chars(WrongPassword));

        Assert.True(unlock.UnlockCommand.IsRunning);
        Assert.False(running.IsCompleted);
        Assert.False(unlock.GoBackCommand.CanExecute(null));
        unlock.GoBackCommand.Execute(null);
        Assert.Same(unlock, _navigation.Current);

        await running;

        Assert.False(unlock.UnlockCommand.IsRunning);
        Assert.True(unlock.GoBackCommand.CanExecute(null));
        unlock.GoBackCommand.Execute(null);
        Assert.Same(profilesPage, _navigation.Current);
    }
}
