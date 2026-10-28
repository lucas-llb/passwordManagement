using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PasswordManager.Core.Profiles;
using PasswordManager.Core.Vault;
using PasswordManager.ViewModels.Navigation;

namespace PasswordManager.ViewModels;

/// <summary>
/// Profile creation: a name, a master password and its confirmation. The inputs are checked before Argon2id runs; a
/// valid form builds the vault off the UI thread, stores the profile and goes back to the profile list.
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

        // Back is blocked while a create runs, so a create that succeeds is the only thing that leaves this page.
        CreateCommand.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(IAsyncRelayCommand.IsRunning))
            {
                GoBackCommand.NotifyCanExecuteChanged();
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
    /// Validates the form and, when it is valid, creates the profile and goes back to the list. Argon2id runs off the
    /// UI thread; the navigation after it runs back on the caller's context. Both arrays are zeroed before this
    /// returns, whatever the outcome.
    /// </summary>
    /// <param name="input">The master password and its confirmation. This method takes ownership and zeroes them.</param>
    [RelayCommand]
    private async Task CreateAsync(NewMasterPassword input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var created = false;
        char[]? kitText = null;
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

            // W-16 shows the kit; until then it is discarded.
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
                created = true;
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

        if (created)
        {
            _navigation.GoBack();
        }
    }

    /// <summary>
    /// Returns to the profile list. Not available while a create runs: the create goes back by itself when it succeeds,
    /// and a second step back would leave the profile list too. A direct <c>Execute</c> is ignored in that state as well.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void GoBack()
    {
        if (CanGoBack())
        {
            _navigation.GoBack();
        }
    }

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
