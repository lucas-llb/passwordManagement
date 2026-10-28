using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PasswordManager.Core.Passwords;
using PasswordManager.Core.Profiles;
using PasswordManager.Core.Vault;
using PasswordManager.ViewModels.Navigation;

namespace PasswordManager.ViewModels;

/// <summary>
/// Profile creation: a name, a master password and its confirmation. The inputs are checked before Argon2id runs; a
/// valid form builds the vault off the UI thread, stores the profile and shows its Recovery Kit once
/// (<see cref="RecoveryKitViewModel"/>). It also keeps the advisory strength of the typed password and can suggest a
/// passphrase.
/// </summary>
/// <remarks>
/// It does not override <see cref="PageViewModel.AllowsScreenCapture"/>: the master password and its reveal button
/// can never be captured.
/// </remarks>
public sealed partial class CreateProfileViewModel : PageViewModel
{
    /// <summary>Shortest master password accepted, in Unicode scalar values (AGENTS.md §3.4).</summary>
    public const int MinimumPasswordLength = 12;

    private readonly ProfileRepository _repository;
    private readonly INavigationService _navigation;
    private readonly KdfParameters _kdfParameters;

    /// <param name="repository">The profile store.</param>
    /// <param name="navigation">The page stack.</param>
    /// <param name="kdfParameters">Argon2id cost for the new vault. The app passes nothing and gets
    /// <see cref="KdfParameters.Default"/>; lower values are for tests only.</param>
    public CreateProfileViewModel(
        ProfileRepository repository,
        INavigationService navigation,
        KdfParameters? kdfParameters = null)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(navigation);

        _repository = repository;
        _navigation = navigation;
        _kdfParameters = kdfParameters ?? KdfParameters.Default;
        DisplayName = string.Empty;

        // Back and the suggestion are blocked while a create runs: a create that succeeds is the only thing that leaves
        // this page, and the suggestion must not change under the password that is being used.
        CreateCommand.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(IAsyncRelayCommand.IsRunning))
            {
                GoBackCommand.NotifyCanExecuteChanged();
                SuggestPassphraseCommand.NotifyCanExecuteChanged();
            }
        };
    }

    /// <summary>The name typed by the user (two-way bound, so every keystroke lands here).</summary>
    [ObservableProperty]
    public partial string DisplayName { get; set; }

    /// <summary>The rule the name broke on the last attempt, or <see cref="ProfileNameError.None"/>.</summary>
    [ObservableProperty]
    public partial ProfileNameError NameError { get; private set; }

    /// <summary>Why the master password was refused on the last attempt, or <see cref="NewPasswordError.None"/>.</summary>
    [ObservableProperty]
    public partial NewPasswordError PasswordError { get; private set; }

    /// <summary>True when the profile list or the disk failed on the last attempt, so nothing was created.</summary>
    [ObservableProperty]
    public partial bool CreateFailed { get; private set; }

    /// <summary>
    /// How hard the typed master password is to guess, from the last <see cref="UpdateStrength"/>. Advisory only: the
    /// hard rule stays <see cref="MinimumPasswordLength"/>.
    /// </summary>
    [ObservableProperty]
    public partial PasswordStrengthLevel Strength { get; private set; }

    /// <summary>
    /// The passphrase that <see cref="SuggestPassphraseCommand"/> generated, or null. The view puts it in both password
    /// boxes. It is a managed string, the same accepted limit as the text of a <c>PasswordBox</c>.
    /// </summary>
    [ObservableProperty]
    public partial string? Suggestion { get; private set; }

    /// <summary>
    /// Validates the form and, when it is valid, creates the profile and shows its Recovery Kit. Argon2id runs off the
    /// UI thread; the navigation after it runs back on the caller's context. Both arrays are zeroed before this
    /// returns, whatever the outcome; the kit is handed to the kit page, which zeroes it when it is left.
    /// </summary>
    /// <param name="input">The master password and its confirmation. This method takes ownership and zeroes them.</param>
    [RelayCommand]
    private async Task CreateAsync(NewMasterPassword input)
    {
        ArgumentNullException.ThrowIfNull(input);

        char[]? kitText = null;
        char[]? shownKit = null;
        try
        {
            NameError = ProfileNameError.None;
            PasswordError = NewPasswordError.None;
            CreateFailed = false;

            // The user can keep typing while Argon2id runs: the name that was validated is the one that is stored.
            var name = DisplayName;

            if (!TryValidateName(name, out var nameError))
            {
                CreateFailed = true;
                return;
            }

            NameError = nameError;
            PasswordError = CheckPassword(input);
            if (NameError != ProfileNameError.None || PasswordError != NewPasswordError.None)
            {
                return;
            }

            kitText = new char[RecoveryKit.TextLength];
            var password = input.Password;
            var recoveryKit = kitText;
            var parameters = _kdfParameters;
            try
            {
                // No ConfigureAwait(false): the navigation below must run on the UI thread.
                await Task.Run(() =>
                {
                    var vault = VaultSession.Create(password, recoveryKit, parameters);
                    _repository.Create(name, vault);
                });

                // The kit page owns the kit from here on; the finally below must not zero it.
                shownKit = kitText;
                kitText = null;
            }
            catch (ArgumentException)
            {
                // The name was taken in between (another process), or the password has a lone surrogate, which the
                // key derivation refuses. Validating again tells the two apart.
                if (TryValidateName(name, out var recheck))
                {
                    NameError = recheck;
                    CreateFailed = recheck == ProfileNameError.None;
                }
                else
                {
                    CreateFailed = true;
                }
            }
            catch (Exception ex) when (ex is ProfileStoreException or IOException or UnauthorizedAccessException)
            {
                CreateFailed = true;
            }
        }
        finally
        {
            if (kitText is not null)
            {
                Array.Clear(kitText);
            }

            Array.Clear(input.Password);
            Array.Clear(input.Confirmation);
        }

        if (shownKit is not null)
        {
            _navigation.NavigateTo(new RecoveryKitViewModel(shownKit, _repository, _navigation));
        }
    }

    /// <summary>
    /// Evaluates the strength of the typed password and drops the suggestion when the user typed something else.
    /// </summary>
    /// <param name="password">The current text of the password box. Not kept and not modified.</param>
    public void UpdateStrength(ReadOnlySpan<char> password)
    {
        Strength = PasswordStrength.Evaluate(password);

        // An empty box keeps it: the view empties the boxes after a create attempt, and the phrase is still the one shown.
        if (Suggestion is { } suggestion && !password.IsEmpty && !password.SequenceEqual(suggestion.AsSpan()))
        {
            Suggestion = null;
        }
    }

    /// <summary>
    /// Generates a passphrase (CSPRNG, EFF short wordlist) into <see cref="Suggestion"/>. Not available while a create
    /// runs; a direct <c>Execute</c> is ignored in that state as well.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void SuggestPassphrase()
    {
        if (!CanGoBack())
        {
            return;
        }

        var phrase = Passphrase.Generate();
        try
        {
            Suggestion = new string(phrase);
        }
        finally
        {
            Array.Clear(phrase);
        }
    }

    /// <inheritdoc />
    public override void OnNavigatedFrom()
    {
        Suggestion = null;
        Strength = PasswordStrengthLevel.None;
    }

    /// <summary>
    /// Returns to the profile list. Not available while a create runs: the create leaves the page by itself when it
    /// succeeds, and a second step back would leave the profile list too. A direct <c>Execute</c> is ignored in that
    /// state as well.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void GoBack()
    {
        if (CanGoBack())
        {
            _navigation.GoBack();
        }
    }

    // Also the condition of the suggestion command: both wait until no create is running.
    private bool CanGoBack() => !CreateCommand.IsRunning;

    // TooShort wins over Mismatch: there is no point asking to retype a password that will be refused anyway.
    private static NewPasswordError CheckPassword(NewMasterPassword input)
    {
        if (CountScalarValues(input.Password) < MinimumPasswordLength)
        {
            return NewPasswordError.TooShort;
        }

        return input.Password.AsSpan().SequenceEqual(input.Confirmation)
            ? NewPasswordError.None
            : NewPasswordError.Mismatch;
    }

    private static int CountScalarValues(ReadOnlySpan<char> text)
    {
        var count = 0;
        var enumerator = text.EnumerateRunes();
        while (enumerator.MoveNext())
        {
            count++;
        }

        return count;
    }

    private bool TryValidateName(string name, out ProfileNameError error)
    {
        try
        {
            error = _repository.ValidateDisplayName(name);
            return true;
        }
        catch (Exception ex) when (ex is ProfileStoreException or IOException or UnauthorizedAccessException)
        {
            error = ProfileNameError.None;
            return false;
        }
    }
}
