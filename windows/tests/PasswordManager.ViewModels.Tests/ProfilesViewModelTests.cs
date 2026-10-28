using PasswordManager.Core.Profiles;
using PasswordManager.Core.Vault;
using PasswordManager.ViewModels.Navigation;

namespace PasswordManager.ViewModels.Tests;

public sealed class ProfilesViewModelTests : IDisposable
{
    private readonly TestProfiles _profiles = new();
    private readonly NavigationService _navigation = new();

    public void Dispose() => _profiles.Dispose();

    [Fact]
    public void Constructor_NullArgument_Throws()
    {
        var repository = _profiles.Repository;
        var throttle = _profiles.Throttle;

        Assert.Throws<ArgumentNullException>(() => new ProfilesViewModel(null!, _navigation, throttle));
        Assert.Throws<ArgumentNullException>(() => new ProfilesViewModel(repository, null!, throttle));
        Assert.Throws<ArgumentNullException>(() => new ProfilesViewModel(repository, _navigation, null!));
    }

    [Fact]
    public void OnNavigatedTo_MissingFolder_IsEmptyWithoutFailure()
    {
        var repository = new ProfileRepository(Path.Combine(_profiles.DirectoryPath, "missing"));
        var viewModel = new ProfilesViewModel(repository, _navigation, _profiles.Throttle);

        _navigation.ResetTo(viewModel);

        Assert.Empty(viewModel.Profiles);
        Assert.True(viewModel.IsEmpty);
        Assert.False(viewModel.LoadFailed);
    }

    [Fact]
    public void OnNavigatedTo_ExistingFolderWithoutProfiles_IsEmptyWithoutFailure()
    {
        var viewModel = new ProfilesViewModel(_profiles.Repository, _navigation, _profiles.Throttle);

        _navigation.ResetTo(viewModel);

        Assert.True(viewModel.IsEmpty);
        Assert.False(viewModel.LoadFailed);
    }

    [Fact]
    public void OnNavigatedTo_TwoProfiles_ListsThemInFileOrder()
    {
        var first = _profiles.CreateProfile("Alice", TestProfiles.Password);
        var second = _profiles.CreateProfile("Bob", TestProfiles.Password);
        var viewModel = new ProfilesViewModel(_profiles.Repository, _navigation, _profiles.Throttle);
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
        var viewModel = new ProfilesViewModel(_profiles.Repository, _navigation, _profiles.Throttle);

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
        var viewModel = new ProfilesViewModel(_profiles.Repository, _navigation, _profiles.Throttle);
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
        var viewModel = new ProfilesViewModel(_profiles.Repository, _navigation, _profiles.Throttle);
        _navigation.ResetTo(viewModel);

        viewModel.OpenProfileCommand.Execute(bob);

        var unlock = Assert.IsType<UnlockViewModel>(_navigation.Current);
        Assert.Equal(bob, unlock.Profile);
        Assert.True(_navigation.CanGoBack);
    }

    public static TheoryData<string, ProfileNameError> InvalidRenames => new()
    {
        { string.Empty, ProfileNameError.Empty },
        { new string('a', ProfileRepository.MaxDisplayNameLength + 1), ProfileNameError.TooLong },
        { "a\u0001", ProfileNameError.InvalidCharacter },
        { "BOB", ProfileNameError.Duplicate },
    };

    private string ProfilesFilePath => Path.Combine(_profiles.DirectoryPath, ProfileRepository.ProfilesFileName);

    private byte[] ProfilesFileBytes() => File.ReadAllBytes(ProfilesFilePath);

    // The names of every file in the vaults folder, so a test can tell that nothing was added or removed.
    private string[] FileNames() =>
        [.. Directory.GetFiles(_profiles.DirectoryPath).Select(path => Path.GetFileName(path)).Order(StringComparer.Ordinal)];

    // Alice and Bob, with Alice's rename dialog open.
    private (ProfilesViewModel ViewModel, Profile Alice, Profile Bob) OpenAliceAndBob()
    {
        var alice = _profiles.CreateProfile("Alice", TestProfiles.Password);
        var bob = _profiles.CreateProfile("Bob", TestProfiles.Password);
        var viewModel = new ProfilesViewModel(_profiles.Repository, _navigation, _profiles.Throttle);
        _navigation.ResetTo(viewModel);

        return (viewModel, alice, bob);
    }

    [Fact]
    public void CreateProfile_NavigatesToCreateProfileWithBackStack()
    {
        var (viewModel, _, _) = OpenAliceAndBob();

        Assert.True(viewModel.CreateProfileCommand.CanExecute(null));
        viewModel.CreateProfileCommand.Execute(null);

        Assert.IsType<CreateProfileViewModel>(_navigation.Current);
        Assert.True(_navigation.CanGoBack);
        Assert.True(_navigation.GoBack());
        Assert.Same(viewModel, _navigation.Current);
    }

    [Fact]
    public void CreateProfile_ListFailedToLoad_CannotExecuteUntilFixed()
    {
        File.WriteAllText(ProfilesFilePath, "{ not json");
        var viewModel = new ProfilesViewModel(_profiles.Repository, _navigation, _profiles.Throttle);
        var canExecuteChanged = 0;
        viewModel.CreateProfileCommand.CanExecuteChanged += (_, _) => canExecuteChanged++;

        _navigation.ResetTo(viewModel);

        Assert.True(viewModel.LoadFailed);
        Assert.False(viewModel.CreateProfileCommand.CanExecute(null));
        Assert.True(canExecuteChanged > 0);

        File.Delete(ProfilesFilePath);
        canExecuteChanged = 0;
        viewModel.Refresh();

        Assert.False(viewModel.LoadFailed);
        Assert.True(viewModel.CreateProfileCommand.CanExecute(null));
        Assert.True(canExecuteChanged > 0);
    }

    [Fact]
    public void BeginRename_FillsTextWithCurrentName()
    {
        var (viewModel, alice, _) = OpenAliceAndBob();

        viewModel.BeginRenameCommand.Execute(alice);

        Assert.Equal(alice, viewModel.RenamingProfile);
        Assert.Equal("Alice", viewModel.RenameText);
        Assert.Equal(ProfileNameError.None, viewModel.RenameError);
        Assert.False(viewModel.ActionFailed);
    }

    [Fact]
    public void ConfirmRename_ValidName_RenamesKeepsIdAndVaultBytesAndRefreshes()
    {
        var (viewModel, alice, bob) = OpenAliceAndBob();
        var vaultBytes = File.ReadAllBytes(_profiles.Repository.GetVaultPath(alice.Id));
        viewModel.BeginRenameCommand.Execute(alice);

        viewModel.RenameText = "Alice Smith";
        viewModel.ConfirmRenameCommand.Execute(null);

        Assert.Null(viewModel.RenamingProfile);
        Assert.Equal(ProfileNameError.None, viewModel.RenameError);
        Assert.False(viewModel.ActionFailed);
        Assert.Equal(_profiles.Repository.List(), viewModel.Profiles);
        Assert.Equal([alice.Id, bob.Id], viewModel.Profiles.Select(p => p.Id));
        Assert.Equal(["Alice Smith", "Bob"], viewModel.Profiles.Select(p => p.DisplayName));
        Assert.Equal(vaultBytes, File.ReadAllBytes(_profiles.Repository.GetVaultPath(alice.Id)));
    }

    [Theory]
    [MemberData(nameof(InvalidRenames))]
    public void ConfirmRename_InvalidName_SetsErrorKeepsStateAndFile(string text, ProfileNameError expected)
    {
        var (viewModel, alice, bob) = OpenAliceAndBob();
        var listBytes = ProfilesFileBytes();
        viewModel.BeginRenameCommand.Execute(alice);

        viewModel.RenameText = text;
        viewModel.ConfirmRenameCommand.Execute(null);

        Assert.Equal(expected, viewModel.RenameError);
        Assert.Equal(alice, viewModel.RenamingProfile);
        Assert.Equal(text, viewModel.RenameText);
        Assert.False(viewModel.ActionFailed);
        Assert.Equal([alice, bob], viewModel.Profiles);
        Assert.Equal(listBytes, ProfilesFileBytes());
    }

    [Fact]
    public void ConfirmRename_AfterAnInvalidNameIsFixed_RenamesAndClearsTheError()
    {
        var (viewModel, alice, _) = OpenAliceAndBob();
        viewModel.BeginRenameCommand.Execute(alice);
        viewModel.RenameText = string.Empty;
        viewModel.ConfirmRenameCommand.Execute(null);
        Assert.Equal(ProfileNameError.Empty, viewModel.RenameError);

        viewModel.RenameText = "Alice Smith";
        viewModel.ConfirmRenameCommand.Execute(null);

        Assert.Null(viewModel.RenamingProfile);
        Assert.Equal(ProfileNameError.None, viewModel.RenameError);
        Assert.Equal("Alice Smith", viewModel.Profiles[0].DisplayName);
    }

    [Fact]
    public void ConfirmRename_WithoutBegin_DoesNothing()
    {
        var (viewModel, _, _) = OpenAliceAndBob();
        var listBytes = ProfilesFileBytes();

        viewModel.RenameText = "Somebody";
        viewModel.ConfirmRenameCommand.Execute(null);

        Assert.Null(viewModel.RenamingProfile);
        Assert.False(viewModel.ActionFailed);
        Assert.Equal(listBytes, ProfilesFileBytes());
    }

    [Fact]
    public void CancelRename_ClearsStateAndWritesNothing()
    {
        var (viewModel, alice, _) = OpenAliceAndBob();
        var listBytes = ProfilesFileBytes();
        viewModel.BeginRenameCommand.Execute(alice);
        viewModel.RenameText = string.Empty;
        viewModel.ConfirmRenameCommand.Execute(null);
        Assert.Equal(ProfileNameError.Empty, viewModel.RenameError);

        viewModel.CancelRenameCommand.Execute(null);

        Assert.Null(viewModel.RenamingProfile);
        Assert.Equal(ProfileNameError.None, viewModel.RenameError);
        Assert.Equal(string.Empty, viewModel.RenameText);
        Assert.Equal(listBytes, ProfilesFileBytes());
    }

    [Fact]
    public void ConfirmRename_ProfileDeletedMeanwhile_SetsActionFailedAndRefreshes()
    {
        var (viewModel, alice, bob) = OpenAliceAndBob();
        viewModel.BeginRenameCommand.Execute(alice);
        Assert.True(_profiles.Repository.Delete(alice.Id));

        viewModel.RenameText = "Alice Smith";
        viewModel.ConfirmRenameCommand.Execute(null);

        Assert.True(viewModel.ActionFailed);
        Assert.Null(viewModel.RenamingProfile);
        Assert.Equal([bob], viewModel.Profiles);
    }

    [Fact]
    public void BeginRename_AfterAFailedAction_ClearsActionFailed()
    {
        var (viewModel, alice, bob) = OpenAliceAndBob();
        viewModel.BeginRenameCommand.Execute(alice);
        Assert.True(_profiles.Repository.Delete(alice.Id));
        viewModel.ConfirmRenameCommand.Execute(null);
        Assert.True(viewModel.ActionFailed);

        viewModel.BeginRenameCommand.Execute(bob);

        Assert.False(viewModel.ActionFailed);
        Assert.Equal(bob, viewModel.RenamingProfile);
    }

    [Fact]
    public void RequestDelete_DeletesNothing()
    {
        var (viewModel, alice, bob) = OpenAliceAndBob();
        var files = FileNames();
        var listBytes = ProfilesFileBytes();

        viewModel.RequestDeleteCommand.Execute(alice);

        Assert.Equal(alice, viewModel.PendingDelete);
        Assert.Equal([alice, bob], viewModel.Profiles);
        Assert.Equal(files, FileNames());
        Assert.Equal(listBytes, ProfilesFileBytes());
        Assert.True(viewModel.ConfirmDeleteCommand.CanExecute(null));
    }

    [Fact]
    public void CancelDelete_ClearsPendingAndKeepsFiles()
    {
        var (viewModel, alice, bob) = OpenAliceAndBob();
        var files = FileNames();
        var listBytes = ProfilesFileBytes();
        viewModel.RequestDeleteCommand.Execute(alice);

        viewModel.CancelDeleteCommand.Execute(null);

        Assert.Null(viewModel.PendingDelete);
        Assert.False(viewModel.ConfirmDeleteCommand.CanExecute(null));
        Assert.Equal([alice, bob], viewModel.Profiles);
        Assert.Equal(files, FileNames());
        Assert.Equal(listBytes, ProfilesFileBytes());
    }

    [Fact]
    public void ConfirmDelete_RemovesEntryVaultBakAndTmpOnly()
    {
        var (viewModel, alice, bob) = OpenAliceAndBob();
        var repository = _profiles.Repository;
        var alicePath = repository.GetVaultPath(alice.Id);
        var bobPath = repository.GetVaultPath(bob.Id);
        var orphanPath = repository.GetVaultPath(Guid.NewGuid());
        File.WriteAllBytes(alicePath + VaultFile.TempSuffix, [1, 2, 3]);
        File.WriteAllBytes(bobPath + VaultFile.TempSuffix, [4, 5, 6]);
        File.WriteAllBytes(orphanPath, [7, 8, 9]);

        // CreateProfile saves the content once, which leaves the previous version in the .bak.
        Assert.True(File.Exists(alicePath + VaultFile.BackupSuffix));
        Assert.True(File.Exists(bobPath + VaultFile.BackupSuffix));

        viewModel.RequestDeleteCommand.Execute(alice);
        viewModel.ConfirmDeleteCommand.Execute(null);

        Assert.Null(viewModel.PendingDelete);
        Assert.False(viewModel.ActionFailed);
        Assert.Equal([bob], viewModel.Profiles);
        Assert.Equal([bob], repository.List());

        Assert.False(File.Exists(alicePath));
        Assert.False(File.Exists(alicePath + VaultFile.BackupSuffix));
        Assert.False(File.Exists(alicePath + VaultFile.TempSuffix));

        Assert.True(File.Exists(bobPath));
        Assert.True(File.Exists(bobPath + VaultFile.BackupSuffix));
        Assert.True(File.Exists(bobPath + VaultFile.TempSuffix));
        Assert.True(File.Exists(orphanPath));
    }

    [Fact]
    public void ConfirmDelete_LastProfile_LeavesAnEmptyList()
    {
        var alice = _profiles.CreateProfile("Alice", TestProfiles.Password);
        var viewModel = new ProfilesViewModel(_profiles.Repository, _navigation, _profiles.Throttle);
        _navigation.ResetTo(viewModel);

        viewModel.RequestDeleteCommand.Execute(alice);
        viewModel.ConfirmDeleteCommand.Execute(null);

        Assert.True(viewModel.IsEmpty);
        Assert.False(viewModel.LoadFailed);
        Assert.False(File.Exists(_profiles.Repository.GetVaultPath(alice.Id)));
    }

    [Fact]
    public void ConfirmDelete_WithoutRequest_CannotExecuteAndDoesNothing()
    {
        var (viewModel, alice, bob) = OpenAliceAndBob();
        var files = FileNames();
        var listBytes = ProfilesFileBytes();

        Assert.False(viewModel.ConfirmDeleteCommand.CanExecute(null));
        viewModel.ConfirmDeleteCommand.Execute(null);

        Assert.False(viewModel.ActionFailed);
        Assert.Equal([alice, bob], viewModel.Profiles);
        Assert.Equal(files, FileNames());
        Assert.Equal(listBytes, ProfilesFileBytes());
    }

    [Fact]
    public void ConfirmDelete_CorruptListAfterRequest_SetsActionFailedAndKeepsVaultFiles()
    {
        var (viewModel, alice, bob) = OpenAliceAndBob();
        var alicePath = _profiles.Repository.GetVaultPath(alice.Id);
        viewModel.RequestDeleteCommand.Execute(alice);
        File.WriteAllText(ProfilesFilePath, "{ not json");

        viewModel.ConfirmDeleteCommand.Execute(null);

        Assert.True(viewModel.ActionFailed);
        Assert.Null(viewModel.PendingDelete);
        Assert.True(viewModel.LoadFailed);
        Assert.True(File.Exists(alicePath));
        Assert.True(File.Exists(alicePath + VaultFile.BackupSuffix));
        Assert.True(File.Exists(_profiles.Repository.GetVaultPath(bob.Id)));
    }

    [Fact]
    public void RequestDelete_AfterAFailedAction_ClearsActionFailed()
    {
        var (viewModel, alice, bob) = OpenAliceAndBob();
        viewModel.RequestDeleteCommand.Execute(alice);
        File.WriteAllText(ProfilesFilePath, "{ not json");
        viewModel.ConfirmDeleteCommand.Execute(null);
        Assert.True(viewModel.ActionFailed);

        viewModel.RequestDeleteCommand.Execute(bob);

        Assert.False(viewModel.ActionFailed);
        Assert.Equal(bob, viewModel.PendingDelete);
    }
}
