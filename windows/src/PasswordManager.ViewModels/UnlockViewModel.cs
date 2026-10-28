using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PasswordManager.Core.Profiles;
using PasswordManager.Core.Vault;
using PasswordManager.ViewModels.Navigation;

namespace PasswordManager.ViewModels;

/// <summary>Unlock screen of one profile. A successful unlock replaces the whole stack with the entry list.</summary>
public sealed partial class UnlockViewModel : PageViewModel
{
    private readonly ProfileRepository _repository;
    private readonly INavigationService _navigation;
    private readonly UnlockThrottle _throttle;

    /// <param name="profile">The profile to unlock.</param>
    /// <param name="repository">The profile store, for the vault path.</param>
    /// <param name="navigation">The page stack.</param>
    /// <param name="throttle">The progressive delay after wrong passwords, shared by every unlock screen of the app.</param>
    public UnlockViewModel(
        Profile profile,
        ProfileRepository repository,
        INavigationService navigation,
        UnlockThrottle throttle)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(navigation);
        ArgumentNullException.ThrowIfNull(throttle);

        Profile = profile;
        _repository = repository;
        _navigation = navigation;
        _throttle = throttle;

        // Back is blocked while Argon2id runs: a successful unlock is the only thing that leaves this page then.
        UnlockCommand.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(IAsyncRelayCommand.IsRunning))
            {
                GoBackCommand.NotifyCanExecuteChanged();
            }
        };
    }

    /// <summary>The profile being unlocked.</summary>
    public Profile Profile { get; }

    /// <inheritdoc />
    public override bool AllowsScreenCapture => true;

    /// <summary>The failure of the last attempt; the view maps it to a text.</summary>
    [ObservableProperty]
    public partial UnlockError Error { get; private set; }

    /// <summary>
    /// How many whole seconds are left, rounded up, until another attempt is allowed after a wrong password; 0 means
    /// an attempt is allowed. It only changes in <see cref="RefreshRetryDelay"/> and after a wrong password: the view
    /// calls that while it shows the countdown.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UnlockCommand))]
    public partial int RetrySeconds { get; private set; }

    /// <summary>Reads the clock again and updates <see cref="RetrySeconds"/>.</summary>
    public void RefreshRetryDelay() =>
        RetrySeconds = (int)Math.Ceiling(_throttle.RemainingDelay(Profile.Id).TotalSeconds);

    /// <summary>The delay survives leaving the screen, so a page that is shown again picks it up.</summary>
    public override void OnNavigatedTo() => RefreshRetryDelay();

    /// <summary>
    /// Tries to unlock the vault with the master password. Argon2id runs off the UI thread; the navigation after it
    /// runs back on the caller's context. The array is zeroed before this returns, whatever the outcome. During the
    /// delay that follows a wrong password the call is ignored: no Argon2id runs and nothing is counted.
    /// </summary>
    /// <param name="password">The master password. This method takes ownership and zeroes it.</param>
    [RelayCommand(CanExecute = nameof(CanAttempt))]
    private async Task UnlockAsync(char[] password)
    {
        ArgumentNullException.ThrowIfNull(password);

        RefreshRetryDelay();
        if (RetrySeconds > 0)
        {
            Array.Clear(password);
            return;
        }

        Error = UnlockError.None;

        var session = new VaultSession(_repository.GetVaultPath(Profile.Id));
        var unlocked = false;
        try
        {
            // No ConfigureAwait(false): the navigation below must run on the UI thread.
            await Task.Run(() => session.Unlock(password));
            unlocked = true;
        }
        catch (VaultAuthenticationException)
        {
            Error = UnlockError.WrongPassword;
            _throttle.RecordFailure(Profile.Id);
            RefreshRetryDelay();
        }
        catch (Exception ex) when (ex is VaultFormatException or IOException or UnauthorizedAccessException)
        {
            Error = UnlockError.VaultUnreadable;
        }
        finally
        {
            Array.Clear(password);
            if (!unlocked)
            {
                session.Dispose();
            }
        }

        if (unlocked)
        {
            _throttle.RecordSuccess(Profile.Id);

            // The list becomes the root: Back can never return to this screen while the vault is open.
            _navigation.ResetTo(new EntryListViewModel(Profile, session, _repository, _navigation, _throttle));
        }
    }

    /// <summary>
    /// Returns to the profile list. Not available while Argon2id runs: the unlock leaves the page by itself when it
    /// succeeds, and a second step back would leave the profile list too. A direct <c>Execute</c> is ignored in that
    /// state as well. It stays available during the delay after a wrong password.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void GoBack()
    {
        if (CanGoBack())
        {
            _navigation.GoBack();
        }
    }

    private bool CanGoBack() => !UnlockCommand.IsRunning;

    private bool CanAttempt() => RetrySeconds == 0;
}
