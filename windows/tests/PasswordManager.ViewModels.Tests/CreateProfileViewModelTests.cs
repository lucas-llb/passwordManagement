using PasswordManager.Core.Profiles;
using PasswordManager.Core.Vault;
using PasswordManager.ViewModels.Navigation;

namespace PasswordManager.ViewModels.Tests;

public sealed class CreateProfileViewModelTests : IDisposable
{
    // Fictitious. 27 characters.
    private const string NewPassword = "a fictitious new passphrase";

    private const string OtherPassword = "another fictitious passphrase";

    // An emoji is one scalar value and two UTF-16 units.
    private const string Emoji = "\U0001F600";

    // VaultSession.Create rejects these (out of the format bounds). A test that reaches the key derivation with them
    // gets CreateFailed, so it proves that the command stopped before Argon2id.
    private static readonly KdfParameters UnusableKdf = new(0, 0);

    private readonly TestProfiles _profiles = new();
    private readonly NavigationService _navigation = new();

    public void Dispose() => _profiles.Dispose();

    public static TheoryData<string, ProfileNameError> InvalidNames => new()
    {
        { string.Empty, ProfileNameError.Empty },
        { new string('a', ProfileRepository.MaxDisplayNameLength + 1), ProfileNameError.TooLong },
        { "a\u0001", ProfileNameError.InvalidCharacter },
        { "TAKEN", ProfileNameError.Duplicate },
    };

    private string ProfilesFilePath => Path.Combine(_profiles.DirectoryPath, ProfileRepository.ProfilesFileName);

    // The names of every file in the vaults folder, so a test can tell that nothing was added or removed.
    private string[] FileNames() =>
        [.. Directory.GetFiles(_profiles.DirectoryPath).Select(path => Path.GetFileName(path)).Order(StringComparer.Ordinal)];

    // Profiles -> Create, the way the app gets there, but with cheap Argon2id parameters.
    private (ProfilesViewModel ProfilesPage, CreateProfileViewModel Create) OpenCreate(KdfParameters? parameters = null)
    {
        var profilesPage = new ProfilesViewModel(_profiles.Repository, _navigation);
        _navigation.ResetTo(profilesPage);
        var create = new CreateProfileViewModel(_profiles.Repository, _navigation, parameters ?? TestProfiles.LowCost);
        _navigation.NavigateTo(create);

        return (profilesPage, create);
    }

    private static NewMasterPassword Input(string password, string? confirmation = null) =>
        new(password.ToCharArray(), (confirmation ?? password).ToCharArray());

    private static void AssertZeroed(char[] chars)
    {
        Assert.NotEmpty(chars);
        Assert.All(chars, c => Assert.Equal('\0', c));
    }

    [Fact]
    public async Task Create_ValidInput_WritesProfileAndVaultThenGoesBackToRefreshedList()
    {
        var (profilesPage, create) = OpenCreate();
        Assert.Same(create, _navigation.Current);
        Assert.True(_navigation.CanGoBack);
        Assert.True(profilesPage.IsEmpty);

        create.DisplayName = "Alice";
        await create.CreateCommand.ExecuteAsync(Input(NewPassword));

        // The stack was [Profiles, Create]: now only Profiles is left, and it shows the new profile.
        Assert.Same(profilesPage, _navigation.Current);
        Assert.False(_navigation.CanGoBack);
        var profile = Assert.Single(profilesPage.Profiles);
        Assert.Equal("Alice", profile.DisplayName);
        Assert.Equal(ProfileNameError.None, create.NameError);
        Assert.Equal(NewPasswordError.None, create.PasswordError);
        Assert.False(create.CreateFailed);

        using var session = _profiles.OpenSession(profile, NewPassword);
        Assert.Empty(session.Content.Entries);

        // Only the list and the vault: no .bak (the vault was written once) and no .tmp.
        Assert.Equal(
            new[] { ProfileRepository.ProfilesFileName, Path.GetFileName(_profiles.Repository.GetVaultPath(profile.Id)) }
                .Order(StringComparer.Ordinal),
            FileNames());
    }

    [Theory]
    [InlineData("Alice", NewPassword, NewPassword)]
    [InlineData("Alice", "short", "short")]
    [InlineData("Alice", NewPassword, OtherPassword)]
    [InlineData("", NewPassword, NewPassword)]
    public async Task Create_ZeroesBothArrays(string name, string password, string confirmation)
    {
        var (_, create) = OpenCreate();
        create.DisplayName = name;
        var input = Input(password, confirmation);

        await create.CreateCommand.ExecuteAsync(input);

        AssertZeroed(input.Password);
        AssertZeroed(input.Confirmation);
    }

    [Fact]
    public async Task Create_ElevenRunes_TooShortAndWritesNothing()
    {
        var (_, create) = OpenCreate();
        create.DisplayName = "Alice";
        var password = "abcdefghij" + Emoji;
        Assert.Equal(CreateProfileViewModel.MinimumPasswordLength, password.Length);

        await create.CreateCommand.ExecuteAsync(Input(password));

        Assert.Equal(NewPasswordError.TooShort, create.PasswordError);
        Assert.False(create.CreateFailed);
        Assert.Same(create, _navigation.Current);
        Assert.Empty(FileNames());
    }

    [Fact]
    public async Task Create_TwelveRunes_IsAccepted()
    {
        var (profilesPage, create) = OpenCreate();
        create.DisplayName = "Alice";
        var password = "abcdefghijk" + Emoji;
        Assert.Equal(CreateProfileViewModel.MinimumPasswordLength + 1, password.Length);

        await create.CreateCommand.ExecuteAsync(Input(password));

        Assert.Equal(NewPasswordError.None, create.PasswordError);
        Assert.Same(profilesPage, _navigation.Current);
        using var session = _profiles.OpenSession(Assert.Single(profilesPage.Profiles), password);
        Assert.Empty(session.Content.Entries);
    }

    [Fact]
    public async Task Create_ConfirmationDiffers_MismatchAndWritesNothing()
    {
        var (_, create) = OpenCreate();
        create.DisplayName = "Alice";

        await create.CreateCommand.ExecuteAsync(Input(NewPassword, OtherPassword));

        Assert.Equal(NewPasswordError.Mismatch, create.PasswordError);
        Assert.Equal(ProfileNameError.None, create.NameError);
        Assert.False(create.CreateFailed);
        Assert.Same(create, _navigation.Current);
        Assert.Empty(FileNames());
    }

    [Fact]
    public async Task Create_ShortAndDifferent_ReportsTooShort()
    {
        var (_, create) = OpenCreate();
        create.DisplayName = "Alice";

        await create.CreateCommand.ExecuteAsync(Input("short", "other"));

        Assert.Equal(NewPasswordError.TooShort, create.PasswordError);
    }

    [Fact]
    public async Task Create_ErrorsFromAnAttempt_AreClearedByTheNextOne()
    {
        var (profilesPage, create) = OpenCreate();
        create.DisplayName = string.Empty;
        await create.CreateCommand.ExecuteAsync(Input("short"));
        Assert.Equal(ProfileNameError.Empty, create.NameError);
        Assert.Equal(NewPasswordError.TooShort, create.PasswordError);

        create.DisplayName = "Alice";
        await create.CreateCommand.ExecuteAsync(Input(NewPassword));

        Assert.Equal(ProfileNameError.None, create.NameError);
        Assert.Equal(NewPasswordError.None, create.PasswordError);
        Assert.Same(profilesPage, _navigation.Current);
    }

    [Theory]
    [MemberData(nameof(InvalidNames))]
    public async Task Create_InvalidName_SetsNameErrorAndWritesNoVault(string name, ProfileNameError expected)
    {
        _profiles.CreateProfile("Taken", TestProfiles.Password);
        var files = FileNames();
        var listBytes = File.ReadAllBytes(ProfilesFilePath);
        var (_, create) = OpenCreate();
        create.DisplayName = name;

        await create.CreateCommand.ExecuteAsync(Input(NewPassword));

        Assert.Equal(expected, create.NameError);
        Assert.Equal(NewPasswordError.None, create.PasswordError);
        Assert.False(create.CreateFailed);
        Assert.Same(create, _navigation.Current);
        Assert.Equal(files, FileNames());
        Assert.Equal(listBytes, File.ReadAllBytes(ProfilesFilePath));
    }

    [Theory]
    [InlineData("Alice", "short", "short")]
    [InlineData("Alice", NewPassword, OtherPassword)]
    [InlineData("", NewPassword, NewPassword)]
    public async Task Create_InvalidInput_NeverReachesKeyDerivation(string name, string password, string confirmation)
    {
        var (_, create) = OpenCreate(UnusableKdf);
        create.DisplayName = name;

        await create.CreateCommand.ExecuteAsync(Input(password, confirmation));

        Assert.False(create.CreateFailed);
        Assert.True(create.NameError != ProfileNameError.None || create.PasswordError != NewPasswordError.None);
        Assert.Empty(FileNames());
    }

    [Fact]
    public async Task Create_ValidInputButUnusableKdf_SetsCreateFailedAndWritesNothing()
    {
        var (_, create) = OpenCreate(UnusableKdf);
        create.DisplayName = "Alice";

        await create.CreateCommand.ExecuteAsync(Input(NewPassword));

        Assert.True(create.CreateFailed);
        Assert.Equal(ProfileNameError.None, create.NameError);
        Assert.Same(create, _navigation.Current);
        Assert.Empty(FileNames());
    }

    [Fact]
    public async Task Create_PasswordWithLoneSurrogate_SetsCreateFailedAndWritesNothing()
    {
        var (_, create) = OpenCreate();
        create.DisplayName = "Alice";
        var input = Input("\uD800" + "abcdefghijk");

        await create.CreateCommand.ExecuteAsync(input);

        Assert.True(create.CreateFailed);
        Assert.Equal(ProfileNameError.None, create.NameError);
        Assert.Equal(NewPasswordError.None, create.PasswordError);
        Assert.Same(create, _navigation.Current);
        Assert.Empty(FileNames());
        AssertZeroed(input.Password);
        AssertZeroed(input.Confirmation);
    }

    [Fact]
    public async Task Create_CorruptProfileList_SetsCreateFailedAndWritesNoVault()
    {
        File.WriteAllText(ProfilesFilePath, "{ not json");
        var (_, create) = OpenCreate();
        create.DisplayName = "Alice";
        var input = Input(NewPassword);

        await create.CreateCommand.ExecuteAsync(input);

        Assert.True(create.CreateFailed);
        Assert.Equal(ProfileNameError.None, create.NameError);
        Assert.Same(create, _navigation.Current);
        Assert.Equal([ProfileRepository.ProfilesFileName], FileNames());
        Assert.Equal("{ not json", File.ReadAllText(ProfilesFilePath));
        AssertZeroed(input.Password);
        AssertZeroed(input.Confirmation);
    }

    [Fact]
    public void GoBack_ReturnsWithoutWriting()
    {
        var (profilesPage, create) = OpenCreate();
        create.DisplayName = "Alice";

        create.GoBackCommand.Execute(null);

        Assert.Same(profilesPage, _navigation.Current);
        Assert.False(_navigation.CanGoBack);
        Assert.Empty(FileNames());
    }

    [Fact]
    public async Task GoBack_WhileCreateRuns_CannotExecuteAndCreateLeavesThePageOnlyOnce()
    {
        using var gate = new GatedTimeProvider();
        var repository = new ProfileRepository(_profiles.DirectoryPath, gate);
        var profilesPage = new ProfilesViewModel(repository, _navigation);
        _navigation.ResetTo(profilesPage);
        var create = new CreateProfileViewModel(repository, _navigation, TestProfiles.LowCost);
        _navigation.NavigateTo(create);
        create.DisplayName = "Alice";
        Assert.True(create.GoBackCommand.CanExecute(null));
        var canExecuteChanged = 0;
        create.GoBackCommand.CanExecuteChanged += (_, _) => canExecuteChanged++;

        // The gate holds the create inside its background work (the repository reads the clock there), after Argon2id.
        var running = create.CreateCommand.ExecuteAsync(Input(NewPassword));
        try
        {
            Assert.True(gate.Entered.Wait(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
            Assert.True(create.CreateCommand.IsRunning);
            Assert.False(create.GoBackCommand.CanExecute(null));
            Assert.True(canExecuteChanged > 0);

            // A direct Execute (a keyboard accelerator, say) is ignored too.
            create.GoBackCommand.Execute(null);
            Assert.Same(create, _navigation.Current);
        }
        finally
        {
            gate.Release.Set();
            await running;
        }

        // The create itself went back, exactly once: the list is the root and nothing is left to return to.
        Assert.Same(profilesPage, _navigation.Current);
        Assert.False(_navigation.CanGoBack);
        Assert.Single(profilesPage.Profiles);
        Assert.False(create.CreateCommand.IsRunning);
        Assert.True(create.GoBackCommand.CanExecute(null));
    }

    [Fact]
    public async Task GoBack_AfterAFailedCreate_CanExecuteAgain()
    {
        var (profilesPage, create) = OpenCreate(UnusableKdf);
        create.DisplayName = "Alice";

        await create.CreateCommand.ExecuteAsync(Input(NewPassword));

        Assert.True(create.CreateFailed);
        Assert.True(create.GoBackCommand.CanExecute(null));
        create.GoBackCommand.Execute(null);
        Assert.Same(profilesPage, _navigation.Current);
    }

    [Fact]
    public async Task Create_DefaultConstructor_UsesDefaultKdfParameters()
    {
        var profilesPage = new ProfilesViewModel(_profiles.Repository, _navigation);
        _navigation.ResetTo(profilesPage);
        var create = new CreateProfileViewModel(_profiles.Repository, _navigation);
        _navigation.NavigateTo(create);
        create.DisplayName = "Alice";

        // One real Argon2id run per slot at 64 MiB and 3 passes.
        await create.CreateCommand.ExecuteAsync(Input(NewPassword));

        var profile = Assert.Single(profilesPage.Profiles);
        var file = File.ReadAllBytes(_profiles.Repository.GetVaultPath(profile.Id));
        var header = VaultHeader.Parse(file);
        Assert.Equal(KdfParameters.Default, KdfParameters.ReadFromSlot(file.AsSpan(header.PasswordSlotOffset)));
    }

    // A clock that stops the caller until the test lets it go. ProfileRepository.Create reads it inside the create
    // command's background work, so the test can hold a create in flight.
    private sealed class GatedTimeProvider : TimeProvider, IDisposable
    {
        public ManualResetEventSlim Entered { get; } = new(initialState: false);

        public ManualResetEventSlim Release { get; } = new(initialState: false);

        public override DateTimeOffset GetUtcNow()
        {
            Entered.Set();

            // A bounded wait, so a failed test cannot leave a thread parked forever.
            Release.Wait(TimeSpan.FromSeconds(30));

            return System.GetUtcNow();
        }

        public void Dispose()
        {
            Entered.Dispose();
            Release.Dispose();
        }
    }
}
