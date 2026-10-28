using PasswordManager.Core.Passwords;
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
    private (ProfilesViewModel ProfilesPage, CreateProfileViewModel Create) OpenCreate(
        KdfParameters? parameters = null,
        ProfileRepository? repository = null)
    {
        repository ??= _profiles.Repository;
        var profilesPage = new ProfilesViewModel(repository, _navigation);
        _navigation.ResetTo(profilesPage);
        var create = new CreateProfileViewModel(repository, _navigation, parameters ?? TestProfiles.LowCost);
        _navigation.NavigateTo(create);

        return (profilesPage, create);
    }

    // The kit page that a successful create leaves as the current page.
    private RecoveryKitViewModel CurrentKit() => Assert.IsType<RecoveryKitViewModel>(_navigation.Current);

    // The kit text has the shape of the format: 8 groups of 4 Crockford base32 symbols, separated by hyphens.
    private static void AssertKitShape(ReadOnlySpan<char> kitText)
    {
        Assert.Equal(RecoveryKit.TextLength, kitText.Length);
        for (var i = 0; i < kitText.Length; i++)
        {
            if (i % 5 == 4)
            {
                Assert.Equal('-', kitText[i]);
            }
            else
            {
                Assert.True(VaultFormat.RecoveryKeyAlphabet.Contains(kitText[i], StringComparison.Ordinal));
            }
        }
    }

    private static NewMasterPassword Input(string password, string? confirmation = null) =>
        new(password.ToCharArray(), (confirmation ?? password).ToCharArray());

    private static void AssertZeroed(char[] chars)
    {
        Assert.NotEmpty(chars);
        Assert.All(chars, c => Assert.Equal('\0', c));
    }

    [Fact]
    public async Task Create_ValidInput_WritesProfileThenShowsKitPage()
    {
        var (profilesPage, create) = OpenCreate();
        Assert.Same(create, _navigation.Current);
        Assert.True(_navigation.CanGoBack);
        Assert.True(profilesPage.IsEmpty);

        create.DisplayName = "Alice";
        await create.CreateCommand.ExecuteAsync(Input(NewPassword));

        // The stack was [Profiles, Create]: the kit page is pushed on top, and the profile is on disk.
        var kit = CurrentKit();
        Assert.True(_navigation.CanGoBack);
        var profile = Assert.Single(_profiles.Repository.List());
        Assert.Equal("Alice", profile.DisplayName);
        Assert.Equal(ProfileNameError.None, create.NameError);
        Assert.Equal(NewPasswordError.None, create.PasswordError);
        Assert.False(create.CreateFailed);

        using var session = _profiles.OpenSession(profile, NewPassword);
        Assert.Empty(session.Content.Entries);

        // The kit on the page is the real one: it opens the vault that was just written.
        AssertKitShape(kit.KitText);
        var vaultKey = new byte[VaultFormat.KeySize];
        try
        {
            RecoveryKit.Unlock(File.ReadAllBytes(_profiles.Repository.GetVaultPath(profile.Id)), kit.KitText, vaultKey);
            Assert.Contains(vaultKey, b => b != 0);
        }
        finally
        {
            Array.Clear(vaultKey);
        }

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
        var (_, create) = OpenCreate();
        create.DisplayName = "Alice";
        var password = "abcdefghijk" + Emoji;
        Assert.Equal(CreateProfileViewModel.MinimumPasswordLength + 1, password.Length);

        await create.CreateCommand.ExecuteAsync(Input(password));

        Assert.Equal(NewPasswordError.None, create.PasswordError);
        CurrentKit();
        using var session = _profiles.OpenSession(Assert.Single(_profiles.Repository.List()), password);
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
        var (_, create) = OpenCreate();
        create.DisplayName = string.Empty;
        await create.CreateCommand.ExecuteAsync(Input("short"));
        Assert.Equal(ProfileNameError.Empty, create.NameError);
        Assert.Equal(NewPasswordError.TooShort, create.PasswordError);

        create.DisplayName = "Alice";
        await create.CreateCommand.ExecuteAsync(Input(NewPassword));

        Assert.Equal(ProfileNameError.None, create.NameError);
        Assert.Equal(NewPasswordError.None, create.PasswordError);
        CurrentKit();
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
        var (_, create) = OpenCreate(repository: repository);
        create.DisplayName = "Alice";
        Assert.True(create.GoBackCommand.CanExecute(null));
        var canExecuteChanged = 0;
        create.GoBackCommand.CanExecuteChanged += (_, _) => canExecuteChanged++;
        var navigations = 0;
        _navigation.Navigated += (_, _) => navigations++;

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
            Assert.Equal(0, navigations);
        }
        finally
        {
            gate.Release.Set();
            await running;
        }

        // The create itself left the page, exactly once, for the kit page (stack [Profiles, Create, Kit]).
        CurrentKit();
        Assert.Equal(1, navigations);
        Assert.True(_navigation.CanGoBack);
        Assert.Single(repository.List());
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

        var profile = Assert.Single(_profiles.Repository.List());
        var file = File.ReadAllBytes(_profiles.Repository.GetVaultPath(profile.Id));
        var header = VaultHeader.Parse(file);
        Assert.Equal(KdfParameters.Default, KdfParameters.ReadFromSlot(file.AsSpan(header.PasswordSlotOffset)));
    }

    [Theory]
    [InlineData("", PasswordStrengthLevel.None)]
    [InlineData("aaaaaaaaaaaa", PasswordStrengthLevel.VeryWeak)]
    [InlineData("Tq7#vZ2m!Kx9$pWd", PasswordStrengthLevel.VeryStrong)]
    public void UpdateStrength_SetsLevel(string password, PasswordStrengthLevel expected)
    {
        var (_, create) = OpenCreate();
        Assert.Equal(PasswordStrengthLevel.None, create.Strength);

        create.UpdateStrength(password);

        Assert.Equal(expected, create.Strength);
    }

    [Fact]
    public void UpdateStrength_FollowsEveryChange()
    {
        var (_, create) = OpenCreate();
        var changed = new List<string?>();
        create.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        create.UpdateStrength("Tq7#vZ2m!Kx9$pWd");
        create.UpdateStrength("aaaaaaaaaaaa");
        create.UpdateStrength(string.Empty);

        Assert.Equal(PasswordStrengthLevel.None, create.Strength);
        Assert.Equal(3, changed.Count(name => name == nameof(CreateProfileViewModel.Strength)));
    }

    [Fact]
    public void SuggestPassphrase_SetsSixWordSuggestion()
    {
        var (_, create) = OpenCreate();
        Assert.Null(create.Suggestion);

        create.SuggestPassphraseCommand.Execute(null);

        var suggestion = Assert.IsType<string>(create.Suggestion);

        // Six words of 3 to 5 lowercase letters, joined by hyphens. The one word with a hyphen (yo-yo) only adds
        // parts of 2 letters, and the shortest phrase still has 6 * 3 + 5 characters.
        Assert.True(suggestion.Length >= 23, "The shortest six-word phrase has 23 characters.");
        Assert.All(suggestion, c => Assert.True(c == '-' || char.IsAsciiLetterLower(c)));
        var parts = suggestion.Split('-');
        Assert.InRange(parts.Length, Passphrase.DefaultWordCount, 2 * Passphrase.DefaultWordCount);
        Assert.All(parts, part => Assert.InRange(part.Length, 2, 5));
    }

    [Fact]
    public void SuggestPassphrase_Twice_ReplacesIt()
    {
        var (_, create) = OpenCreate();

        create.SuggestPassphraseCommand.Execute(null);
        var first = create.Suggestion;
        create.SuggestPassphraseCommand.Execute(null);

        Assert.NotNull(first);
        Assert.NotNull(create.Suggestion);

        // About 62 bits each, so two equal phrases would mean the generator is not random.
        Assert.NotEqual(first, create.Suggestion);
    }

    [Fact]
    public void UpdateStrength_DifferentText_ClearsSuggestion()
    {
        var (_, create) = OpenCreate();
        create.SuggestPassphraseCommand.Execute(null);

        create.UpdateStrength("something the user typed instead");

        Assert.Null(create.Suggestion);
    }

    [Fact]
    public void UpdateStrength_EmptyOrSameText_KeepsSuggestion()
    {
        var (_, create) = OpenCreate();
        create.SuggestPassphraseCommand.Execute(null);
        var suggestion = create.Suggestion;
        Assert.NotNull(suggestion);

        // The view puts the suggestion in the box, which reports it back; and it empties the boxes after an attempt.
        create.UpdateStrength(suggestion);
        Assert.Equal(suggestion, create.Suggestion);
        Assert.InRange(create.Strength, PasswordStrengthLevel.Strong, PasswordStrengthLevel.VeryStrong);

        create.UpdateStrength(string.Empty);
        Assert.Equal(suggestion, create.Suggestion);
        Assert.Equal(PasswordStrengthLevel.None, create.Strength);
    }

    [Fact]
    public async Task Create_WithSuggestion_VaultUnlocksWithIt()
    {
        var (_, create) = OpenCreate();
        create.DisplayName = "Alice";
        create.SuggestPassphraseCommand.Execute(null);
        var suggestion = Assert.IsType<string>(create.Suggestion);

        await create.CreateCommand.ExecuteAsync(Input(suggestion));

        CurrentKit();
        Assert.Equal(NewPasswordError.None, create.PasswordError);
        using var session = _profiles.OpenSession(Assert.Single(_profiles.Repository.List()), suggestion);
        Assert.Empty(session.Content.Entries);
    }

    [Fact]
    public async Task Create_WeakButTwelveRunes_IsAccepted()
    {
        var (_, create) = OpenCreate();
        create.DisplayName = "Alice";
        const string weak = "aaaaaaaaaaaa";

        // The meter is advisory: the only rule that blocks is the minimum length.
        create.UpdateStrength(weak);
        Assert.Equal(PasswordStrengthLevel.VeryWeak, create.Strength);
        await create.CreateCommand.ExecuteAsync(Input(weak));

        Assert.Equal(NewPasswordError.None, create.PasswordError);
        CurrentKit();
        using var session = _profiles.OpenSession(Assert.Single(_profiles.Repository.List()), weak);
        Assert.Empty(session.Content.Entries);
    }

    [Fact]
    public void LeavingPage_ClearsSuggestionAndStrength()
    {
        var (profilesPage, create) = OpenCreate();
        create.SuggestPassphraseCommand.Execute(null);
        create.UpdateStrength(create.Suggestion!);
        Assert.NotNull(create.Suggestion);
        Assert.InRange(create.Strength, PasswordStrengthLevel.Strong, PasswordStrengthLevel.VeryStrong);

        create.GoBackCommand.Execute(null);

        Assert.Same(profilesPage, _navigation.Current);
        Assert.Null(create.Suggestion);
        Assert.Equal(PasswordStrengthLevel.None, create.Strength);
    }

    [Fact]
    public async Task SuggestPassphrase_WhileCreateRuns_CannotExecute()
    {
        using var gate = new GatedTimeProvider();
        var repository = new ProfileRepository(_profiles.DirectoryPath, gate);
        var (_, create) = OpenCreate(repository: repository);
        create.DisplayName = "Alice";
        Assert.True(create.SuggestPassphraseCommand.CanExecute(null));
        var canExecuteChanged = 0;
        create.SuggestPassphraseCommand.CanExecuteChanged += (_, _) => canExecuteChanged++;

        var running = create.CreateCommand.ExecuteAsync(Input(NewPassword));
        try
        {
            Assert.True(gate.Entered.Wait(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
            Assert.True(create.CreateCommand.IsRunning);
            Assert.False(create.SuggestPassphraseCommand.CanExecute(null));
            Assert.True(canExecuteChanged > 0);

            // A direct Execute is ignored too: nothing changes under the password that is being used.
            create.SuggestPassphraseCommand.Execute(null);
            Assert.Null(create.Suggestion);
        }
        finally
        {
            gate.Release.Set();
            await running;
        }

        Assert.False(create.CreateCommand.IsRunning);
        Assert.True(create.SuggestPassphraseCommand.CanExecute(null));
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
