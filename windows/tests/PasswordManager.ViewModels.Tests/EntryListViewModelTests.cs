using PasswordManager.Core.Profiles;
using PasswordManager.Core.Vault;
using PasswordManager.ViewModels.Navigation;

namespace PasswordManager.ViewModels.Tests;

public sealed class EntryListViewModelTests : IDisposable
{
    private readonly TestProfiles _profiles = new();
    private readonly NavigationService _navigation = new();
    private readonly VaultEntry _first = TestProfiles.Entry("First", "first@example.com");
    private readonly VaultEntry _second = TestProfiles.Entry("Second", "second@example.com");

    public void Dispose() => _profiles.Dispose();

    // The list as the app builds it after an unlock: the session is open and the list is the root page.
    private (Profile Profile, VaultSession Session, EntryListViewModel List) OpenList(params VaultEntry[] entries)
    {
        var profile = _profiles.CreateProfile("Alice", TestProfiles.Password, entries);
        var session = _profiles.OpenSession(profile);
        var list = new EntryListViewModel(profile, session, _profiles.Repository, _navigation, _profiles.Throttle);
        _navigation.ResetTo(list);

        return (profile, session, list);
    }

    [Fact]
    public void Constructor_NullThrottle_Throws()
    {
        var profile = _profiles.CreateProfile("Alice", TestProfiles.Password);
        using var session = _profiles.OpenSession(profile);

        Assert.Throws<ArgumentNullException>(
            () => new EntryListViewModel(profile, session, _profiles.Repository, _navigation, null!));
    }

    [Fact]
    public void Entries_AreTheContentOfTheSessionInOrder()
    {
        var (_, session, list) = OpenList(_first, _second);

        Assert.Equal(session.Content.Entries, list.Entries);
        Assert.Equal(["First", "Second"], list.Entries.Select(e => e.Title));
        Assert.False(list.IsEmpty);
    }

    [Fact]
    public void Entries_EmptyVault_IsEmpty()
    {
        var (_, _, list) = OpenList();

        Assert.Empty(list.Entries);
        Assert.True(list.IsEmpty);
    }

    [Fact]
    public void OpenEntry_NavigatesToTheDetailOfThatEntry()
    {
        var (_, _, list) = OpenList(_first, _second);

        list.OpenEntryCommand.Execute(_second);

        var detail = Assert.IsType<EntryDetailViewModel>(_navigation.Current);
        Assert.Same(_second, detail.Entry);
        Assert.True(_navigation.CanGoBack);
    }

    [Fact]
    public void Lock_ClosesTheSessionAndClearsTheEntries()
    {
        var (_, session, list) = OpenList(_first, _second);

        list.LockCommand.Execute(null);

        Assert.False(session.IsUnlocked);
        Assert.Throws<ObjectDisposedException>(() => session.Content);
        Assert.Empty(list.Entries);
        Assert.True(list.IsEmpty);
    }

    [Fact]
    public void IsOpen_AfterTheOpen_IsTrue()
    {
        var (_, _, list) = OpenList(_first);

        Assert.True(list.IsOpen);
    }

    [Fact]
    public void IsOpen_AfterLock_IsFalse()
    {
        var (_, _, list) = OpenList(_first);

        list.LockCommand.Execute(null);

        Assert.False(list.IsOpen);
    }

    [Fact]
    public void Lock_ShowsTheUnlockPageOfTheSameProfile()
    {
        var (profile, _, list) = OpenList(_first);

        list.LockCommand.Execute(null);

        var unlock = Assert.IsType<UnlockViewModel>(_navigation.Current);
        Assert.Equal(profile, unlock.Profile);
        Assert.Equal(UnlockError.None, unlock.Error);
        Assert.True(_navigation.CanGoBack);
    }

    [Fact]
    public void Lock_BackFromUnlockReachesTheProfilesPageAndNeverTheList()
    {
        var (profile, _, list) = OpenList(_first);

        list.LockCommand.Execute(null);
        var wentBack = _navigation.GoBack();

        Assert.True(wentBack);
        var profilesPage = Assert.IsType<ProfilesViewModel>(_navigation.Current);
        Assert.Equal([profile], profilesPage.Profiles);
        Assert.False(_navigation.CanGoBack);
    }

    [Fact]
    public void Lock_FromTheDetailPageStack_StillEndsOnTheUnlockPage()
    {
        // The list is not the current page here, but its command can still run (for example, an auto-lock).
        var (_, session, list) = OpenList(_first);
        list.OpenEntryCommand.Execute(_first);

        list.LockCommand.Execute(null);

        Assert.False(session.IsUnlocked);
        Assert.IsType<UnlockViewModel>(_navigation.Current);
        Assert.True(_navigation.GoBack());
        Assert.IsType<ProfilesViewModel>(_navigation.Current);
        Assert.False(_navigation.CanGoBack);
    }
}
