using PasswordManager.ViewModels.Navigation;

namespace PasswordManager.ViewModels.Tests;

public sealed class NavigationFlowTests : IDisposable
{
    private readonly TestProfiles _profiles = new();

    public void Dispose() => _profiles.Dispose();

    [Fact]
    public async Task Flow_ProfilesUnlockListDetailBackLock_FollowsTheStackRules()
    {
        var alice = _profiles.CreateProfile(
            "Alice",
            TestProfiles.Password,
            TestProfiles.Entry("Mail"),
            TestProfiles.Entry("Bank"));
        _profiles.CreateProfile("Bob", TestProfiles.Password);
        var navigation = new NavigationService();

        // Profiles: the root, listing both profiles.
        navigation.ResetTo(new ProfilesViewModel(_profiles.Repository, navigation, _profiles.Throttle));
        var profilesPage = Assert.IsType<ProfilesViewModel>(navigation.Current);
        Assert.Equal(2, profilesPage.Profiles.Count);
        Assert.False(navigation.CanGoBack);

        // Profiles -> Unlock.
        profilesPage.OpenProfileCommand.Execute(alice);
        var unlock = Assert.IsType<UnlockViewModel>(navigation.Current);
        Assert.Equal(alice, unlock.Profile);
        Assert.True(navigation.CanGoBack);

        // Unlock -> List: the list is the root, so Back cannot reach the unlock page.
        await unlock.UnlockCommand.ExecuteAsync(TestProfiles.Password.ToCharArray());
        var list = Assert.IsType<EntryListViewModel>(navigation.Current);
        Assert.Equal(["Mail", "Bank"], list.Entries.Select(e => e.Title));
        Assert.False(navigation.CanGoBack);

        // List -> Detail.
        list.OpenEntryCommand.Execute(list.Entries[1]);
        var detail = Assert.IsType<EntryDetailViewModel>(navigation.Current);
        Assert.Equal("Bank", detail.Entry.Title);
        Assert.True(navigation.CanGoBack);

        // Detail -> List.
        detail.GoBackCommand.Execute(null);
        Assert.Same(list, navigation.Current);
        Assert.False(navigation.CanGoBack);

        // Lock: Unlock of the same profile, with Back leading to Profiles and never to the list.
        list.LockCommand.Execute(null);
        var lockedUnlock = Assert.IsType<UnlockViewModel>(navigation.Current);
        Assert.Equal(alice, lockedUnlock.Profile);
        Assert.Empty(list.Entries);
        Assert.True(navigation.CanGoBack);

        Assert.True(navigation.GoBack());
        Assert.IsType<ProfilesViewModel>(navigation.Current);
        Assert.False(navigation.CanGoBack);
        Assert.False(navigation.GoBack());
        Assert.IsNotType<EntryListViewModel>(navigation.Current);
    }
}
