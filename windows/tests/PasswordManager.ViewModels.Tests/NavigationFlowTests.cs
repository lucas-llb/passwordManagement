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
        navigation.ResetTo(new ProfilesViewModel(_profiles.Repository, navigation, _profiles.Throttle, _profiles.Clipboard, _profiles.Settings));
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
    public async Task OpenExport_PushesExportPage_BackReturnsToList()
    {
        var alice = _profiles.CreateProfile("Alice", TestProfiles.Password, TestProfiles.Entry("Mail"));
        var navigation = new NavigationService();
        navigation.ResetTo(new ProfilesViewModel(_profiles.Repository, navigation, _profiles.Throttle, _profiles.Clipboard, _profiles.Settings));
        var profilesPage = Assert.IsType<ProfilesViewModel>(navigation.Current);
        profilesPage.OpenProfileCommand.Execute(alice);
        await Assert.IsType<UnlockViewModel>(navigation.Current).UnlockCommand.ExecuteAsync(TestProfiles.Password.ToCharArray());
        var list = Assert.IsType<EntryListViewModel>(navigation.Current);

        // List -> Export: the stack is [List, Export].
        list.OpenExportCommand.Execute(null);
        var export = Assert.IsType<ExportViewModel>(navigation.Current);
        Assert.True(navigation.CanGoBack);
        Assert.True(list.IsOpen);

        // Export -> List: the vault is still open and the list is the root again.
        export.GoBackCommand.Execute(null);
        Assert.Same(list, navigation.Current);
        Assert.False(navigation.CanGoBack);
        Assert.True(list.IsOpen);
        Assert.NotEmpty(list.Entries);
    }

    [Fact]
    public async Task OpenCsvExport_PushesCsvPage_BackReturnsToExport()
    {
        var alice = _profiles.CreateProfile("Alice", TestProfiles.Password, TestProfiles.Entry("Mail"));
        var navigation = new NavigationService();
        navigation.ResetTo(new ProfilesViewModel(_profiles.Repository, navigation, _profiles.Throttle, _profiles.Clipboard, _profiles.Settings));
        Assert.IsType<ProfilesViewModel>(navigation.Current).OpenProfileCommand.Execute(alice);
        await Assert.IsType<UnlockViewModel>(navigation.Current).UnlockCommand.ExecuteAsync(TestProfiles.Password.ToCharArray());
        var list = Assert.IsType<EntryListViewModel>(navigation.Current);
        list.OpenExportCommand.Execute(null);
        var export = Assert.IsType<ExportViewModel>(navigation.Current);

        // Export -> CSV: the stack is [List, Export, CSV].
        export.OpenCsvExportCommand.Execute(null);
        var csv = Assert.IsType<CsvExportViewModel>(navigation.Current);
        Assert.True(navigation.CanGoBack);
        Assert.True(list.IsOpen);

        // CSV -> Export, then Export -> List: the vault stays open all the way.
        csv.GoBackCommand.Execute(null);
        Assert.Same(export, navigation.Current);
        Assert.True(navigation.CanGoBack);
        Assert.True(list.IsOpen);
        export.GoBackCommand.Execute(null);
        Assert.Same(list, navigation.Current);
        Assert.False(navigation.CanGoBack);
        Assert.True(list.IsOpen);
        Assert.NotEmpty(list.Entries);
    }

    [Fact]
    public async Task Lock_FromCsvExportPage_EndsOnUnlockWithProfilesBehind()
    {
        var alice = _profiles.CreateProfile("Alice", TestProfiles.Password, TestProfiles.Entry("Mail"));
        var navigation = new NavigationService();
        navigation.ResetTo(new ProfilesViewModel(_profiles.Repository, navigation, _profiles.Throttle, _profiles.Clipboard, _profiles.Settings));
        Assert.IsType<ProfilesViewModel>(navigation.Current).OpenProfileCommand.Execute(alice);
        await Assert.IsType<UnlockViewModel>(navigation.Current).UnlockCommand.ExecuteAsync(TestProfiles.Password.ToCharArray());
        var list = Assert.IsType<EntryListViewModel>(navigation.Current);
        list.OpenExportCommand.Execute(null);
        Assert.IsType<ExportViewModel>(navigation.Current).OpenCsvExportCommand.Execute(null);
        var csv = Assert.IsType<CsvExportViewModel>(navigation.Current);
        csv.Acknowledged = true;

        // The lock replaces the whole stack, export and CSV pages included, and the page forgets the acknowledgement.
        list.LockCommand.Execute(null);

        var unlock = Assert.IsType<UnlockViewModel>(navigation.Current);
        Assert.Equal(alice, unlock.Profile);
        Assert.False(list.IsOpen);
        Assert.Empty(list.Entries);
        Assert.False(csv.Acknowledged);
        Assert.True(navigation.GoBack());
        Assert.IsType<ProfilesViewModel>(navigation.Current);
        Assert.False(navigation.CanGoBack);
    }

    [Fact]
    public async Task Lock_FromExportPage_EndsOnUnlockWithProfilesBehind()
    {
        var alice = _profiles.CreateProfile("Alice", TestProfiles.Password, TestProfiles.Entry("Mail"));
        var navigation = new NavigationService();
        navigation.ResetTo(new ProfilesViewModel(_profiles.Repository, navigation, _profiles.Throttle, _profiles.Clipboard, _profiles.Settings));
        Assert.IsType<ProfilesViewModel>(navigation.Current).OpenProfileCommand.Execute(alice);
        await Assert.IsType<UnlockViewModel>(navigation.Current).UnlockCommand.ExecuteAsync(TestProfiles.Password.ToCharArray());
        var list = Assert.IsType<EntryListViewModel>(navigation.Current);
        list.OpenExportCommand.Execute(null);
        Assert.IsType<ExportViewModel>(navigation.Current);

        // The lock the window and the auto-lock use is the list's: it replaces the whole stack, export page included.
        list.LockCommand.Execute(null);

        var unlock = Assert.IsType<UnlockViewModel>(navigation.Current);
        Assert.Equal(alice, unlock.Profile);
        Assert.False(list.IsOpen);
        Assert.Empty(list.Entries);
        Assert.True(navigation.GoBack());
        Assert.IsType<ProfilesViewModel>(navigation.Current);
        Assert.False(navigation.CanGoBack);
    }

    [Fact]
    public async Task OpenImport_PushesImportPage_BackReturnsToList()
    {
        var alice = _profiles.CreateProfile("Alice", TestProfiles.Password, TestProfiles.Entry("Mail"));
        var navigation = new NavigationService();
        navigation.ResetTo(new ProfilesViewModel(_profiles.Repository, navigation, _profiles.Throttle, _profiles.Clipboard, _profiles.Settings));
        Assert.IsType<ProfilesViewModel>(navigation.Current).OpenProfileCommand.Execute(alice);
        await Assert.IsType<UnlockViewModel>(navigation.Current).UnlockCommand.ExecuteAsync(TestProfiles.Password.ToCharArray());
        var list = Assert.IsType<EntryListViewModel>(navigation.Current);

        // List -> Import: the stack is [List, Import].
        list.OpenImportCommand.Execute(null);
        var import = Assert.IsType<ImportViewModel>(navigation.Current);
        Assert.True(navigation.CanGoBack);
        Assert.True(list.IsOpen);

        // Import -> List: the vault is still open and the list is the root again.
        import.GoBackCommand.Execute(null);
        Assert.Same(list, navigation.Current);
        Assert.False(navigation.CanGoBack);
        Assert.True(list.IsOpen);
        Assert.NotEmpty(list.Entries);
    }

    [Fact]
    public async Task Lock_FromImportPage_EndsOnUnlockWithProfilesBehind()
    {
        var alice = _profiles.CreateProfile("Alice", TestProfiles.Password, TestProfiles.Entry("Mail"));
        var navigation = new NavigationService();
        navigation.ResetTo(new ProfilesViewModel(_profiles.Repository, navigation, _profiles.Throttle, _profiles.Clipboard, _profiles.Settings));
        Assert.IsType<ProfilesViewModel>(navigation.Current).OpenProfileCommand.Execute(alice);
        await Assert.IsType<UnlockViewModel>(navigation.Current).UnlockCommand.ExecuteAsync(TestProfiles.Password.ToCharArray());
        var list = Assert.IsType<EntryListViewModel>(navigation.Current);
        list.OpenImportCommand.Execute(null);
        Assert.IsType<ImportViewModel>(navigation.Current);

        // The lock replaces the whole stack, import page included.
        list.LockCommand.Execute(null);

        var unlock = Assert.IsType<UnlockViewModel>(navigation.Current);
        Assert.Equal(alice, unlock.Profile);
        Assert.False(list.IsOpen);
        Assert.Empty(list.Entries);
        Assert.True(navigation.GoBack());
        Assert.IsType<ProfilesViewModel>(navigation.Current);
        Assert.False(navigation.CanGoBack);
    }

    [Fact]
    public async Task OpenSettings_PushesSettings_BackReturnsToList()
    {
        var navigation = new NavigationService();
        var list = await OpenListOfAlice(navigation);

        // List -> Settings: the stack is [List, Settings].
        list.OpenSettingsCommand.Execute(null);
        var settings = Assert.IsType<SettingsViewModel>(navigation.Current);
        Assert.True(navigation.CanGoBack);
        Assert.True(list.IsOpen);

        // Settings -> List: the vault is still open and the list is the root again.
        settings.GoBackCommand.Execute(null);
        Assert.Same(list, navigation.Current);
        Assert.False(navigation.CanGoBack);
        Assert.True(list.IsOpen);
        Assert.NotEmpty(list.Entries);
    }

    [Fact]
    public async Task Settings_ExportLink_BackReturnsToSettings()
    {
        var navigation = new NavigationService();
        var list = await OpenListOfAlice(navigation);
        list.OpenSettingsCommand.Execute(null);
        var settings = Assert.IsType<SettingsViewModel>(navigation.Current);

        // Settings -> Export: the stack is [List, Settings, Export].
        settings.OpenExportCommand.Execute(null);
        var export = Assert.IsType<ExportViewModel>(navigation.Current);

        export.GoBackCommand.Execute(null);
        Assert.Same(settings, navigation.Current);
        Assert.True(navigation.CanGoBack);

        settings.GoBackCommand.Execute(null);
        Assert.Same(list, navigation.Current);
        Assert.False(navigation.CanGoBack);
        Assert.True(list.IsOpen);
    }

    [Fact]
    public async Task Settings_ImportLink_BackReturnsToSettings()
    {
        var navigation = new NavigationService();
        var list = await OpenListOfAlice(navigation);
        list.OpenSettingsCommand.Execute(null);
        var settings = Assert.IsType<SettingsViewModel>(navigation.Current);

        // Settings -> Import: the stack is [List, Settings, Import].
        settings.OpenImportCommand.Execute(null);
        var import = Assert.IsType<ImportViewModel>(navigation.Current);

        import.GoBackCommand.Execute(null);
        Assert.Same(settings, navigation.Current);
        Assert.True(navigation.CanGoBack);

        settings.GoBackCommand.Execute(null);
        Assert.Same(list, navigation.Current);
        Assert.False(navigation.CanGoBack);
        Assert.True(list.IsOpen);
    }

    [Fact]
    public async Task Lock_FromSettings_EndsOnUnlockWithProfilesBehind()
    {
        var navigation = new NavigationService();
        var list = await OpenListOfAlice(navigation);
        list.OpenSettingsCommand.Execute(null);
        Assert.IsType<SettingsViewModel>(navigation.Current);

        // The lock replaces the whole stack, settings page included.
        list.LockCommand.Execute(null);

        Assert.IsType<UnlockViewModel>(navigation.Current);
        Assert.False(list.IsOpen);
        Assert.Empty(list.Entries);
        Assert.True(navigation.GoBack());
        Assert.IsType<ProfilesViewModel>(navigation.Current);
        Assert.False(navigation.CanGoBack);
    }

    [Fact]
    public async Task Lock_FromNewKit_EndsOnUnlockWithProfilesBehind()
    {
        var navigation = new NavigationService();
        var list = await OpenListOfAlice(navigation);
        list.OpenSettingsCommand.Execute(null);
        var settings = Assert.IsType<SettingsViewModel>(navigation.Current);
        await settings.GenerateKitCommand.ExecuteAsync(TestProfiles.Password.ToCharArray());
        var kit = Assert.IsType<NewRecoveryKitViewModel>(navigation.Current);
        Assert.False(kit.KitText.IsEmpty);

        // The lock replaces the whole stack, and the kit page zeroes its kit on the way out.
        list.LockCommand.Execute(null);

        Assert.IsType<UnlockViewModel>(navigation.Current);
        Assert.True(kit.KitText.IsEmpty);
        Assert.False(list.IsOpen);
        Assert.Empty(list.Entries);
        Assert.True(navigation.GoBack());
        Assert.IsType<ProfilesViewModel>(navigation.Current);
        Assert.False(navigation.CanGoBack);
    }

    [Fact]
    public async Task Flow_ProfilesUnlockListDetail_CopyGoesThroughTheSharedGuard()
    {
        var alice = _profiles.CreateProfile("Alice", TestProfiles.Password, TestProfiles.Entry("Mail"));
        var navigation = new NavigationService();
        navigation.ResetTo(new ProfilesViewModel(_profiles.Repository, navigation, _profiles.Throttle, _profiles.Clipboard, _profiles.Settings));

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
        navigation.ResetTo(new ProfilesViewModel(_profiles.Repository, navigation, _profiles.Throttle, _profiles.Clipboard, _profiles.Settings));
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
        var profilesPage = new ProfilesViewModel(_profiles.Repository, navigation, _profiles.Throttle, _profiles.Clipboard, _profiles.Settings);
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

    // Profiles -> Unlock -> List, by running the commands as the app does.
    private async Task<EntryListViewModel> OpenListOfAlice(NavigationService navigation)
    {
        var alice = _profiles.CreateProfile("Alice", TestProfiles.Password, TestProfiles.Entry("Mail"));
        navigation.ResetTo(new ProfilesViewModel(_profiles.Repository, navigation, _profiles.Throttle, _profiles.Clipboard, _profiles.Settings));
        Assert.IsType<ProfilesViewModel>(navigation.Current).OpenProfileCommand.Execute(alice);
        await Assert.IsType<UnlockViewModel>(navigation.Current).UnlockCommand.ExecuteAsync(TestProfiles.Password.ToCharArray());

        return Assert.IsType<EntryListViewModel>(navigation.Current);
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
