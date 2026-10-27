using PasswordManager.Core.Profiles;
using PasswordManager.ViewModels.Navigation;

namespace PasswordManager.ViewModels.Tests;

public sealed class ProfilesViewModelTests : IDisposable
{
    private readonly TestProfiles _profiles = new();
    private readonly NavigationService _navigation = new();

    public void Dispose() => _profiles.Dispose();

    [Fact]
    public void OnNavigatedTo_MissingFolder_IsEmptyWithoutFailure()
    {
        var repository = new ProfileRepository(Path.Combine(_profiles.DirectoryPath, "missing"));
        var viewModel = new ProfilesViewModel(repository, _navigation);

        _navigation.ResetTo(viewModel);

        Assert.Empty(viewModel.Profiles);
        Assert.True(viewModel.IsEmpty);
        Assert.False(viewModel.LoadFailed);
    }

    [Fact]
    public void OnNavigatedTo_ExistingFolderWithoutProfiles_IsEmptyWithoutFailure()
    {
        var viewModel = new ProfilesViewModel(_profiles.Repository, _navigation);

        _navigation.ResetTo(viewModel);

        Assert.True(viewModel.IsEmpty);
        Assert.False(viewModel.LoadFailed);
    }

    [Fact]
    public void OnNavigatedTo_TwoProfiles_ListsThemInFileOrder()
    {
        var first = _profiles.CreateProfile("Alice", TestProfiles.Password);
        var second = _profiles.CreateProfile("Bob", TestProfiles.Password);
        var viewModel = new ProfilesViewModel(_profiles.Repository, _navigation);
        Assert.True(viewModel.IsEmpty);

        _navigation.ResetTo(viewModel);

        Assert.Equal([first, second], viewModel.Profiles);
        Assert.False(viewModel.IsEmpty);
        Assert.False(viewModel.LoadFailed);
    }

    [Fact]
    public void OnNavigatedTo_CorruptProfilesFile_FailsAndIsEmpty()
    {
        Directory.CreateDirectory(_profiles.DirectoryPath);
        File.WriteAllText(Path.Combine(_profiles.DirectoryPath, ProfileRepository.ProfilesFileName), "{ not json");
        var viewModel = new ProfilesViewModel(_profiles.Repository, _navigation);

        _navigation.ResetTo(viewModel);

        Assert.True(viewModel.LoadFailed);
        Assert.Empty(viewModel.Profiles);
        Assert.True(viewModel.IsEmpty);
    }

    [Fact]
    public void Refresh_AfterTheFileIsFixed_ClearsLoadFailed()
    {
        var path = Path.Combine(_profiles.DirectoryPath, ProfileRepository.ProfilesFileName);
        File.WriteAllText(path, "{ not json");
        var viewModel = new ProfilesViewModel(_profiles.Repository, _navigation);
        _navigation.ResetTo(viewModel);
        Assert.True(viewModel.LoadFailed);

        File.Delete(path);
        var profile = _profiles.CreateProfile("Alice", TestProfiles.Password);
        viewModel.Refresh();

        Assert.False(viewModel.LoadFailed);
        Assert.Equal([profile], viewModel.Profiles);
    }

    [Fact]
    public void OpenProfile_NavigatesToUnlockOfThatProfile()
    {
        _profiles.CreateProfile("Alice", TestProfiles.Password);
        var bob = _profiles.CreateProfile("Bob", TestProfiles.Password);
        var viewModel = new ProfilesViewModel(_profiles.Repository, _navigation);
        _navigation.ResetTo(viewModel);

        viewModel.OpenProfileCommand.Execute(bob);

        var unlock = Assert.IsType<UnlockViewModel>(_navigation.Current);
        Assert.Equal(bob, unlock.Profile);
        Assert.True(_navigation.CanGoBack);
    }
}
