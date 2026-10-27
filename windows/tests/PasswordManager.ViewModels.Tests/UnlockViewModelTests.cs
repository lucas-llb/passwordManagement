using PasswordManager.Core.Profiles;
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
        var profilesPage = new ProfilesViewModel(_profiles.Repository, _navigation);
        _navigation.ResetTo(profilesPage);
        profilesPage.OpenProfileCommand.Execute(profile);

        return (profile, profilesPage, Assert.IsType<UnlockViewModel>(_navigation.Current));
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

        await unlock.UnlockCommand.ExecuteAsync(Chars(TestProfiles.Password));

        Assert.Equal(UnlockError.None, unlock.Error);
        Assert.IsType<EntryListViewModel>(_navigation.Current);
    }

    [Fact]
    public async Task Unlock_WrongPasswordTwice_KeepsTheError()
    {
        var (_, _, unlock) = OpenUnlock("First");
        await unlock.UnlockCommand.ExecuteAsync(Chars(WrongPassword));

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
}
