using PasswordManager.Core.Vault;
using PasswordManager.ViewModels.Navigation;

namespace PasswordManager.ViewModels.Tests;

public sealed class RecoveryKitViewModelTests : IDisposable
{
    // Fictitious: 8 groups of 4 symbols of the kit alphabet, 39 characters.
    private const string KitText = "ABCD-EFGH-JKMN-PQRS-TVWX-YZ01-2345-6789";

    private readonly TestProfiles _profiles = new();
    private readonly NavigationService _navigation = new();

    public void Dispose() => _profiles.Dispose();

    private static char[] Kit() => KitText.ToCharArray();

    // Profiles -> Create -> Kit, the way a successful create leaves the stack.
    private (ProfilesViewModel ProfilesPage, RecoveryKitViewModel Page, char[] Kit) OpenKitPage()
    {
        var profilesPage = new ProfilesViewModel(_profiles.Repository, _navigation, _profiles.Throttle);
        _navigation.ResetTo(profilesPage);
        _navigation.NavigateTo(
            new CreateProfileViewModel(_profiles.Repository, _navigation, _profiles.Throttle, TestProfiles.LowCost));
        var kit = Kit();
        var page = new RecoveryKitViewModel(kit, _profiles.Repository, _navigation, _profiles.Throttle);
        _navigation.NavigateTo(page);

        return (profilesPage, page, kit);
    }

    [Fact]
    public void Constructor_NullOrWrongLength_Throws()
    {
        var repository = _profiles.Repository;
        var throttle = _profiles.Throttle;
        Assert.Throws<ArgumentNullException>(() => new RecoveryKitViewModel(null!, repository, _navigation, throttle));
        Assert.Throws<ArgumentNullException>(() => new RecoveryKitViewModel(Kit(), null!, _navigation, throttle));
        Assert.Throws<ArgumentNullException>(() => new RecoveryKitViewModel(Kit(), repository, null!, throttle));
        Assert.Throws<ArgumentNullException>(() => new RecoveryKitViewModel(Kit(), repository, _navigation, null!));

        foreach (var length in new[] { 0, RecoveryKit.TextLength - 1, RecoveryKit.TextLength + 1 })
        {
            Assert.Throws<ArgumentException>(
                () => new RecoveryKitViewModel(new char[length], _profiles.Repository, _navigation, _profiles.Throttle));
        }
    }

    [Fact]
    public void KitText_ShowsTheKitAndKitSavedStartsFalse()
    {
        var (_, page, _) = OpenKitPage();

        Assert.Equal(KitText, new string(page.KitText));
        Assert.False(page.KitSaved);
    }

    [Fact]
    public void Done_BeforeKitSaved_CannotExecuteAndStays()
    {
        var (_, page, _) = OpenKitPage();

        Assert.False(page.DoneCommand.CanExecute(null));

        // A direct Execute is ignored too.
        page.DoneCommand.Execute(null);

        Assert.Same(page, _navigation.Current);
        Assert.Equal(KitText, new string(page.KitText));
    }

    [Fact]
    public void Done_KitSavedThenUnchecked_CannotExecuteAgain()
    {
        var (_, page, _) = OpenKitPage();
        var canExecuteChanged = 0;
        page.DoneCommand.CanExecuteChanged += (_, _) => canExecuteChanged++;

        page.KitSaved = true;
        Assert.True(page.DoneCommand.CanExecute(null));
        page.KitSaved = false;

        Assert.False(page.DoneCommand.CanExecute(null));
        Assert.Equal(2, canExecuteChanged);
        page.DoneCommand.Execute(null);
        Assert.Same(page, _navigation.Current);
    }

    [Fact]
    public void Done_AfterKitSaved_ResetsToRefreshedProfileList()
    {
        var (oldProfilesPage, page, _) = OpenKitPage();
        Assert.True(_navigation.CanGoBack);

        // The profile appears after the old list was read, so only a list read again can show it.
        _profiles.CreateProfile("Alice", TestProfiles.Password);
        Assert.True(oldProfilesPage.IsEmpty);

        page.KitSaved = true;
        Assert.True(page.DoneCommand.CanExecute(null));
        page.DoneCommand.Execute(null);

        // A new list page, read from disk, with nothing to go back to (the stack was [Profiles, Create, Kit]).
        var profilesPage = Assert.IsType<ProfilesViewModel>(_navigation.Current);
        Assert.NotSame(oldProfilesPage, profilesPage);
        Assert.Equal("Alice", Assert.Single(profilesPage.Profiles).DisplayName);
        Assert.False(_navigation.CanGoBack);
    }

    [Fact]
    public void LeavingPage_ZeroesCallerArrayAndEmptiesKitText()
    {
        var (_, page, kit) = OpenKitPage();
        Assert.Contains(kit, c => c != '\0');

        page.KitSaved = true;
        page.DoneCommand.Execute(null);

        Assert.All(kit, c => Assert.Equal('\0', c));
        Assert.Equal(0, page.KitText.Length);
    }

    [Fact]
    public void LeavingPage_ByGoingBack_AlsoZeroesTheKit()
    {
        var (_, page, kit) = OpenKitPage();

        // Nothing offers Back on this page, but the navigation hook is what protects the kit, whatever calls it.
        Assert.True(_navigation.GoBack());

        Assert.All(kit, c => Assert.Equal('\0', c));
        Assert.Equal(0, page.KitText.Length);
    }
}
