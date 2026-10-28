using PasswordManager.ViewModels.Navigation;

namespace PasswordManager.ViewModels.Tests;

public sealed class ScreenCaptureTests : IDisposable
{
    private const string WrongPassword = "a fictitious wrong passphrase";

    private readonly TestProfiles _profiles = new();
    private readonly NavigationService _navigation = new();

    public void Dispose() => _profiles.Dispose();

    [Fact]
    public void AllowsScreenCapture_ProfilesAndUnlock_AreTrue()
    {
        var profile = _profiles.CreateProfile("Alice", TestProfiles.Password);

        var profiles = new ProfilesViewModel(_profiles.Repository, _navigation);
        var unlock = new UnlockViewModel(profile, _profiles.Repository, _navigation);

        Assert.True(profiles.AllowsScreenCapture);
        Assert.True(unlock.AllowsScreenCapture);
    }

    [Fact]
    public void AllowsScreenCapture_EntryListAndDetail_AreFalse()
    {
        var profile = _profiles.CreateProfile("Alice", TestProfiles.Password, TestProfiles.Entry("Mail"));
        using var session = _profiles.OpenSession(profile);

        var list = new EntryListViewModel(profile, session, _profiles.Repository, _navigation);
        var detail = new EntryDetailViewModel(list.Entries[0], _navigation);

        Assert.False(list.AllowsScreenCapture);
        Assert.False(detail.AllowsScreenCapture);
    }

    [Fact]
    public void AllowsScreenCapture_CreateProfile_IsFalse()
    {
        var create = new CreateProfileViewModel(_profiles.Repository, _navigation);

        Assert.False(create.AllowsScreenCapture);
    }

    [Fact]
    public void AllowsScreenCapture_PageWithoutOverride_IsFalse()
    {
        var page = new RecordingPage("page", []);

        Assert.False(page.AllowsScreenCapture);
    }

    [Fact]
    public async Task Flow_CaptureIsAllowedOnlyWhileNoVaultIsOpen()
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
        _navigation.ResetTo(new ProfilesViewModel(_profiles.Repository, _navigation));
        var profilesPage = Assert.IsType<ProfilesViewModel>(_navigation.Current);

        // Profiles -> Unlock.
        profilesPage.OpenProfileCommand.Execute(alice);
        var unlock = Assert.IsType<UnlockViewModel>(_navigation.Current);

        // A wrong password does not change the page, so it raises no event.
        await unlock.UnlockCommand.ExecuteAsync(WrongPassword.ToCharArray());

        // Unlock -> List: the vault is open.
        await unlock.UnlockCommand.ExecuteAsync(TestProfiles.Password.ToCharArray());
        var list = Assert.IsType<EntryListViewModel>(_navigation.Current);

        // List -> Detail -> List.
        list.OpenEntryCommand.Execute(list.Entries[0]);
        Assert.IsType<EntryDetailViewModel>(_navigation.Current).GoBackCommand.Execute(null);

        // Lock: the list is replaced by Profiles, then Unlock is pushed.
        list.LockCommand.Execute(null);

        // Unlock -> Profiles, with no vault open.
        Assert.True(_navigation.GoBack());

        Assert.Equal([true, true, false, false, false, true, true, true], captured);
    }
}
