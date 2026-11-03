using PasswordManager.Core.Profiles;
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
        navigation.ResetTo(new ProfilesViewModel(_profiles.Repository, navigation, _profiles.Throttle, _profiles.Clipboard));
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
        Assert.Equal("Bank", detail.Entry?.Title);
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

    [Fact]
    public async Task Flow_ProfilesUnlockListDetail_CopyGoesThroughTheSharedGuard()
    {
        var alice = _profiles.CreateProfile("Alice", TestProfiles.Password, TestProfiles.Entry("Mail"));
        var navigation = new NavigationService();
        navigation.ResetTo(new ProfilesViewModel(_profiles.Repository, navigation, _profiles.Throttle, _profiles.Clipboard));

        var detail = await OpenFirstEntry(navigation, alice);
        detail.CopyPasswordCommand.Execute(null);

        Assert.Equal("not-a-real-password", _profiles.ClipboardHost.Text);
        Assert.True(_profiles.ClipboardHost.IsMarkedPrivate);
        Assert.True(detail.IsPasswordCopied);

        // The same guard clears it: the page and the timer of the window see one instance.
        _profiles.Time.Advance(ClipboardGuard.DefaultClearAfter);
        await _profiles.Clipboard.CheckAsync();
        Assert.Null(_profiles.ClipboardHost.Text);
        Assert.False(detail.IsPasswordCopied);
    }

    [Fact]
    public async Task Flow_LockUnlockDetail_CopyStillWorksAndTheLockCleared()
    {
        var alice = _profiles.CreateProfile("Alice", TestProfiles.Password, TestProfiles.Entry("Mail"));
        var navigation = new NavigationService();
        navigation.ResetTo(new ProfilesViewModel(_profiles.Repository, navigation, _profiles.Throttle, _profiles.Clipboard));
        var detail = await OpenFirstEntry(navigation, alice);
        detail.CopyPasswordCommand.Execute(null);
        detail.GoBackCommand.Execute(null);
        var list = Assert.IsType<EntryListViewModel>(navigation.Current);

        // Lock: the copy leaves on the next tick, not 30 s later.
        list.LockCommand.Execute(null);
        await _profiles.Clipboard.CheckAsync();
        Assert.Null(_profiles.ClipboardHost.Text);

        // Unlock again: a new list, a new detail, and the same guard.
        var unlock = Assert.IsType<UnlockViewModel>(navigation.Current);
        await unlock.UnlockCommand.ExecuteAsync(TestProfiles.Password.ToCharArray());
        var reopened = Assert.IsType<EntryListViewModel>(navigation.Current);
        reopened.OpenEntryCommand.Execute(reopened.Entries[0]);
        var again = Assert.IsType<EntryDetailViewModel>(navigation.Current);
        again.CopyPasswordCommand.Execute(null);

        Assert.Equal("not-a-real-password", _profiles.ClipboardHost.Text);
        Assert.True(again.IsPasswordCopied);
        Assert.Equal(2, _profiles.ClipboardHost.SetCalls);
    }

    [Fact]
    public async Task Flow_CreateKitProfilesUnlockDetail_CopyStillWorks()
    {
        var navigation = new NavigationService();
        var profilesPage = new ProfilesViewModel(_profiles.Repository, navigation, _profiles.Throttle, _profiles.Clipboard);
        navigation.ResetTo(profilesPage);

        // Profiles -> Create -> Kit, with the real Argon2id cost: the page opens its own create page.
        profilesPage.CreateProfileCommand.Execute(null);
        var create = Assert.IsType<CreateProfileViewModel>(navigation.Current);
        create.DisplayName = "Carol";
        await create.CreateCommand.ExecuteAsync(
            new NewMasterPassword(TestProfiles.Password.ToCharArray(), TestProfiles.Password.ToCharArray()));
        var kit = Assert.IsType<RecoveryKitViewModel>(navigation.Current);

        // Kit -> Profiles -> Unlock -> List -> a new entry.
        kit.KitSaved = true;
        kit.DoneCommand.Execute(null);
        var refreshed = Assert.IsType<ProfilesViewModel>(navigation.Current);
        refreshed.OpenProfileCommand.Execute(Assert.Single(refreshed.Profiles));
        var unlock = Assert.IsType<UnlockViewModel>(navigation.Current);
        await unlock.UnlockCommand.ExecuteAsync(TestProfiles.Password.ToCharArray());
        var list = Assert.IsType<EntryListViewModel>(navigation.Current);
        list.NewEntryCommand.Execute(null);
        var detail = Assert.IsType<EntryDetailViewModel>(navigation.Current);
        detail.Password = "senha-ficticia-3";
        detail.CopyPasswordCommand.Execute(null);

        Assert.Equal("senha-ficticia-3", _profiles.ClipboardHost.Text);
        Assert.True(_profiles.ClipboardHost.IsMarkedPrivate);
        Assert.True(detail.IsPasswordCopied);
    }

    // Profiles -> Unlock -> List -> the first entry, by running the commands as the app does.
    private static async Task<EntryDetailViewModel> OpenFirstEntry(NavigationService navigation, Profile profile)
    {
        var profilesPage = Assert.IsType<ProfilesViewModel>(navigation.Current);
        profilesPage.OpenProfileCommand.Execute(profile);
        var unlock = Assert.IsType<UnlockViewModel>(navigation.Current);
        await unlock.UnlockCommand.ExecuteAsync(TestProfiles.Password.ToCharArray());
        var list = Assert.IsType<EntryListViewModel>(navigation.Current);
        list.OpenEntryCommand.Execute(list.Entries[0]);

        return Assert.IsType<EntryDetailViewModel>(navigation.Current);
    }
}
