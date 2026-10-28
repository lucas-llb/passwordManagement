using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PasswordManager.Core.Profiles;
using PasswordManager.Core.Vault;
using PasswordManager.ViewModels.Navigation;

namespace PasswordManager.ViewModels;

/// <summary>
/// Shows the Recovery Kit of a profile that was just created, once. The page has no way back: it can only be left by
/// saying the kit was written down and pressing the done command, which returns to a refreshed profile list.
/// </summary>
/// <remarks>
/// It does not override <see cref="PageViewModel.AllowsScreenCapture"/>: the kit opens the vault, so it can never be
/// captured. The kit text is zeroed as soon as the page is left.
/// </remarks>
public sealed partial class RecoveryKitViewModel : PageViewModel
{
    private readonly char[] _kitText;
    private readonly ProfileRepository _repository;
    private readonly INavigationService _navigation;
    private readonly UnlockThrottle _throttle;
    private bool _cleared;

    /// <summary>Takes ownership of <paramref name="kitText"/>: this page zeroes it when it is left.</summary>
    /// <param name="kitText">The kit text, exactly <see cref="RecoveryKit.TextLength"/> chars. The caller must not use
    /// or zero the array afterwards.</param>
    /// <param name="repository">The profile store, for the list shown after the page.</param>
    /// <param name="navigation">The page stack.</param>
    /// <param name="throttle">The unlock delay, passed on to the profile list shown after the page.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="kitText"/> does not have the length of a kit text.</exception>
    public RecoveryKitViewModel(
        char[] kitText,
        ProfileRepository repository,
        INavigationService navigation,
        UnlockThrottle throttle)
    {
        ArgumentNullException.ThrowIfNull(kitText);
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(navigation);
        ArgumentNullException.ThrowIfNull(throttle);

        if (kitText.Length != RecoveryKit.TextLength)
        {
            throw new ArgumentException("The recovery kit text has an invalid length.", nameof(kitText));
        }

        _kitText = kitText;
        _repository = repository;
        _navigation = navigation;
        _throttle = throttle;
    }

    /// <summary>The kit text to show; empty once the page has been left.</summary>
    public ReadOnlySpan<char> KitText => _cleared ? default : _kitText;

    /// <summary>True once the user said the kit was written down. It enables <see cref="DoneCommand"/>.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DoneCommand))]
    public partial bool KitSaved { get; set; }

    /// <summary>Zeroes the kit. The page is left for good, so nothing shows it again.</summary>
    public override void OnNavigatedFrom()
    {
        Array.Clear(_kitText);
        _cleared = true;
    }

    /// <summary>
    /// Leaves the page for a refreshed profile list, with nothing to go back to. Does nothing until
    /// <see cref="KitSaved"/> is set, even through a direct <c>Execute</c>.
    /// </summary>
    [RelayCommand(CanExecute = nameof(KitSaved))]
    private void Done()
    {
        if (!KitSaved)
        {
            return;
        }

        _navigation.ResetTo(new ProfilesViewModel(_repository, _navigation, _throttle));
    }
}
