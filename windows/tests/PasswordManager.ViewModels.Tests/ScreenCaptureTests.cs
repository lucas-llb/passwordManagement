using PasswordManager.Core.Vault;
using PasswordManager.ViewModels.Navigation;

namespace PasswordManager.ViewModels.Tests;

public sealed class ScreenCaptureTests : IDisposable
{
    private const string WrongPassword = "a fictitious wrong passphrase";

    private const string NewPassword = "a fictitious new passphrase";

    private readonly TestProfiles _profiles = new();
    private readonly NavigationService _navigation = new();

    public void Dispose() => _profiles.Dispose();

    [Fact]
    public void AllowsScreenCapture_ProfilesIsTrueAndUnlockIsFalse()
    {
        var profile = _profiles.CreateProfile("Alice", TestProfiles.Password);

        var profiles = new ProfilesViewModel(_profiles.Repository, _navigation, _profiles.Throttle, _profiles.Clipboard, _profiles.Settings);
        var unlock = new UnlockViewModel(profile, _profiles.Repository, _navigation, _profiles.Throttle, _profiles.Clipboard, _profiles.Settings);

        Assert.True(profiles.AllowsScreenCapture);
        Assert.False(unlock.AllowsScreenCapture);
    }

    [Fact]
    public void AllowsScreenCapture_EntryListAndDetail_AreFalse()
    {
        var profile = _profiles.CreateProfile("Alice", TestProfiles.Password, TestProfiles.Entry("Mail"));
        using var session = _profiles.OpenSession(profile);

        var list = new EntryListViewModel(profile, session, _profiles.Repository, _navigation, _profiles.Throttle, _profiles.Clipboard, _profiles.Settings);
        var detail = new EntryDetailViewModel(list, list.Entries[0], _navigation, _profiles.Time);
        var newEntry = new EntryDetailViewModel(list, null, _navigation, _profiles.Time);

        Assert.False(list.AllowsScreenCapture);
        Assert.False(detail.AllowsScreenCapture);
        Assert.False(newEntry.AllowsScreenCapture);
    }

    [Fact]
    public void AllowsScreenCapture_Export_IsFalse()
    {
        var profile = _profiles.CreateProfile("Alice", TestProfiles.Password, TestProfiles.Entry("Mail"));
        using var session = _profiles.OpenSession(profile);
        var list = new EntryListViewModel(profile, session, _profiles.Repository, _navigation, _profiles.Throttle, _profiles.Clipboard, _profiles.Settings);

        var export = new ExportViewModel(list, _navigation);

        Assert.False(export.AllowsScreenCapture);
    }

    [Fact]
    public void AllowsScreenCapture_CsvExport_IsFalse()
    {
        var profile = _profiles.CreateProfile("Alice", TestProfiles.Password, TestProfiles.Entry("Mail"));
        using var session = _profiles.OpenSession(profile);
        var list = new EntryListViewModel(profile, session, _profiles.Repository, _navigation, _profiles.Throttle, _profiles.Clipboard, _profiles.Settings);

        var csv = new CsvExportViewModel(list, _navigation);

        Assert.False(csv.AllowsScreenCapture);
    }

    [Fact]
    public void AllowsScreenCapture_Import_IsFalse()
    {
        var profile = _profiles.CreateProfile("Alice", TestProfiles.Password, TestProfiles.Entry("Mail"));
        using var session = _profiles.OpenSession(profile);
        var list = new EntryListViewModel(profile, session, _profiles.Repository, _navigation, _profiles.Throttle, _profiles.Clipboard, _profiles.Settings);

        var import = new ImportViewModel(list, _navigation, _profiles.Time);

        Assert.False(import.AllowsScreenCapture);
    }

    [Fact]
    public void AllowsScreenCapture_SettingsAndNewKit_AreFalse()
    {
        var profile = _profiles.CreateProfile("Alice", TestProfiles.Password, TestProfiles.Entry("Mail"));
        using var session = _profiles.OpenSession(profile);
        var list = new EntryListViewModel(profile, session, _profiles.Repository, _navigation, _profiles.Throttle, _profiles.Clipboard, _profiles.Settings);

        var settings = new SettingsViewModel(list, _navigation);
        var kitText = new char[RecoveryKit.TextLength];
        var kit = new NewRecoveryKitViewModel(list, settings, _navigation, kitText, session.PrepareRecoveryKit(kitText));

        Assert.False(settings.AllowsScreenCapture);
        Assert.False(kit.AllowsScreenCapture);
    }

    [Fact]
    public void AllowsScreenCapture_CreateProfile_IsFalse()
    {
        var create = new CreateProfileViewModel(_profiles.Repository, _navigation, _profiles.Throttle, _profiles.Clipboard, _profiles.Settings);

        Assert.False(create.AllowsScreenCapture);
    }

    [Fact]
    public void AllowsScreenCapture_RecoveryKit_IsFalse()
    {
        var kit = new RecoveryKitViewModel(
            new char[RecoveryKit.TextLength],
            _profiles.Repository,
            _navigation,
            _profiles.Throttle,
            _profiles.Clipboard,
            _profiles.Settings);

        Assert.False(kit.AllowsScreenCapture);
    }

    [Fact]
    public void AllowsScreenCapture_PageWithoutOverride_IsFalse()
    {
        var page = new RecordingPage("page", []);

        Assert.False(page.AllowsScreenCapture);
    }

    [Fact]
    public async Task Flow_CaptureIsAllowedOnlyOnTheProfilesPage()
    {
        var alice = _profiles.CreateProfile(
            "Alice",
            TestProfiles.Password,
            TestProfiles.Entry("Mail"),
            TestProfiles.Entry("Bank"));

        // The value of AllowsScreenCapture of the current page, on every change of page.
        var captured = new List<bool>();
        _navigation.Navigated += (_, _) => captured.Add(_navigation.Current?.AllowsScreenCapture ?? false);

        // Profiles: the root.
        _navigation.ResetTo(new ProfilesViewModel(_profiles.Repository, _navigation, _profiles.Throttle, _profiles.Clipboard, _profiles.Settings));
        var profilesPage = Assert.IsType<ProfilesViewModel>(_navigation.Current);

        // Profiles -> Unlock: the master password and its reveal button make it non-capturable.
        profilesPage.OpenProfileCommand.Execute(alice);
        var unlock = Assert.IsType<UnlockViewModel>(_navigation.Current);

        // A wrong password does not change the page, so it raises no event.
        await unlock.UnlockCommand.ExecuteAsync(WrongPassword.ToCharArray());

        // Unlock -> List: the vault is open, once the delay after the wrong password has run out.
        _profiles.Time.Advance(TimeSpan.FromSeconds(1));
        await unlock.UnlockCommand.ExecuteAsync(TestProfiles.Password.ToCharArray());
        var list = Assert.IsType<EntryListViewModel>(_navigation.Current);

        // List -> Detail -> List.
        list.OpenEntryCommand.Execute(list.Entries[0]);
        Assert.IsType<EntryDetailViewModel>(_navigation.Current).GoBackCommand.Execute(null);

        // Lock: the list is replaced by Profiles, then Unlock is pushed.
        list.LockCommand.Execute(null);

        // Unlock -> Profiles, with no vault open.
        Assert.True(_navigation.GoBack());

        // Profiles -> Create -> Kit -> Profiles: the master password and then the Recovery Kit are never capturable.
        _navigation.NavigateTo(
            new CreateProfileViewModel(_profiles.Repository, _navigation, _profiles.Throttle, _profiles.Clipboard, _profiles.Settings, TestProfiles.LowCost));
        var create = Assert.IsType<CreateProfileViewModel>(_navigation.Current);
        create.DisplayName = "Bob";
        await create.CreateCommand.ExecuteAsync(
            new NewMasterPassword(NewPassword.ToCharArray(), NewPassword.ToCharArray()));
        var kit = Assert.IsType<RecoveryKitViewModel>(_navigation.Current);
        kit.KitSaved = true;
        kit.DoneCommand.Execute(null);
        Assert.IsType<ProfilesViewModel>(_navigation.Current);

        Assert.Equal([true, false, false, false, false, true, false, true, false, false, true], captured);
    }
}
