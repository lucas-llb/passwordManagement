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

    public UnlockViewModel(Profile profile, ProfileRepository repository, INavigationService navigation)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(navigation);

        Profile = profile;
        _repository = repository;
        _navigation = navigation;
    }

    /// <summary>The profile being unlocked.</summary>
    public Profile Profile { get; }

    /// <summary>The failure of the last attempt; the view maps it to a text.</summary>
    [ObservableProperty]
    public partial UnlockError Error { get; private set; }

    /// <summary>
    /// Tries to unlock the vault with the master password. Argon2id runs off the UI thread; the navigation after it
    /// runs back on the caller's context. The array is zeroed before this returns, whatever the outcome.
    /// </summary>
    /// <param name="password">The master password. This method takes ownership and zeroes it.</param>
    [RelayCommand]
    private async Task UnlockAsync(char[] password)
    {
        ArgumentNullException.ThrowIfNull(password);

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
            // The list becomes the root: Back can never return to this screen while the vault is open.
            _navigation.ResetTo(new EntryListViewModel(Profile, session, _repository, _navigation));
        }
    }

    [RelayCommand]
    private void GoBack() => _navigation.GoBack();
}
