using System.Security.Cryptography;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PasswordManager.Core.Passwords;
using PasswordManager.Core.Preferences;
using PasswordManager.Core.Vault;
using PasswordManager.ViewModels.Navigation;

namespace PasswordManager.ViewModels;

/// <summary>
/// The settings page, above the entry list: the auto-lock preferences, the change of the master password, a new Recovery
/// Kit, and the links to the export and import pages.
/// </summary>
/// <remarks>
/// <para>The preferences apply and are saved as soon as they change, through <see cref="AppSettings"/>, and are global:
/// they hold nothing about this vault. Opening the page writes nothing.</para>
/// <para>Both password forms really check the master password (Argon2id and a valid AEAD tag on the vault slot). The
/// progressive delay is the one of the unlock screen of the same profile, shared through the
/// <see cref="UnlockThrottle"/>, so this page is not a faster way to guess the password.</para>
/// <para>It does not override <see cref="PageViewModel.AllowsScreenCapture"/>: the page can never be captured. It sits
/// above an open <see cref="EntryListViewModel"/>, so <see cref="AutoLock"/> and the lock command cover it like any other
/// page of the vault.</para>
/// <para>A lock can come at any point of a running command: every await is followed by a check that the vault is open,
/// and a locked vault sets no page state and does not navigate. A lock waits for a password change that already started,
/// so the new password is the valid one. A password that was tried still counts for the delay.</para>
/// </remarks>
public sealed partial class SettingsViewModel : PageViewModel
{
    private readonly EntryListViewModel _vault;
    private readonly INavigationService _navigation;
    private readonly AppSettings _settings;

    // The kit being generated. This class owns it, and zeroes it, until the kit page takes it over.
    private char[]? _kitInProgress;

    /// <param name="vault">The list of the open vault whose credentials the page changes.</param>
    /// <param name="navigation">The page stack.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public SettingsViewModel(EntryListViewModel vault, INavigationService navigation)
    {
        ArgumentNullException.ThrowIfNull(vault);
        ArgumentNullException.ThrowIfNull(navigation);

        _vault = vault;
        _navigation = navigation;
        _settings = vault.Settings;

        // Each change handler compares against the current preferences, so showing them saves nothing.
        var current = _settings.Current;
        IdleTimeoutMinutes = current.IdleTimeoutMinutes;
        LockOnMinimize = current.LockOnMinimize;
    }

    /// <summary>The inactivity timeouts the user can choose, in minutes.</summary>
    public IReadOnlyList<int> IdleOptions { get; } = AppPreferences.AllowedIdleTimeoutMinutes;

    /// <summary>The inactivity timeout, in minutes: one of <see cref="IdleOptions"/>. A change applies and is saved at once.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not one of <see cref="IdleOptions"/>.</exception>
    [ObservableProperty]
    public partial int IdleTimeoutMinutes { get; set; }

    /// <summary>True to lock the vault when the window is minimized. A change applies and is saved at once.</summary>
    [ObservableProperty]
    public partial bool LockOnMinimize { get; set; }

    /// <summary>True when the last change of a preference could not be saved. The value still applies until the app closes; the next save that works clears this.</summary>
    [ObservableProperty]
    public partial bool SaveFailed { get; private set; }

    /// <summary>
    /// How hard the typed new password is to guess, from the last <see cref="UpdateStrength"/>. Advisory only: the hard
    /// rule stays <see cref="CreateProfileViewModel.MinimumPasswordLength"/>.
    /// </summary>
    [ObservableProperty]
    public partial PasswordStrengthLevel Strength { get; private set; }

    /// <summary>Why the new password was refused on the last attempt, or <see cref="NewPasswordError.None"/>.</summary>
    [ObservableProperty]
    public partial NewPasswordError PasswordError { get; private set; }

    /// <summary>How the last attempt ended, or <see cref="SettingsOutcome.None"/>.</summary>
    [ObservableProperty]
    public partial SettingsOutcome Outcome { get; private set; }

    /// <summary>
    /// How many whole seconds are left, rounded up, until another attempt is allowed after a wrong master password; 0
    /// means an attempt is allowed. It only changes in <see cref="RefreshRetryDelay"/> and after an attempt: the view
    /// calls that while it shows the countdown.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ChangePasswordCommand))]
    [NotifyCanExecuteChangedFor(nameof(GenerateKitCommand))]
    public partial int RetrySeconds { get; private set; }

    /// <summary>True while either password command runs. The preference controls stay enabled.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ChangePasswordCommand))]
    [NotifyCanExecuteChangedFor(nameof(GenerateKitCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenExportCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenImportCommand))]
    [NotifyCanExecuteChangedFor(nameof(GoBackCommand))]
    public partial bool IsBusy { get; private set; }

    /// <summary>Evaluates the strength of the typed new password.</summary>
    /// <param name="password">The current text of the password box. Not kept and not modified.</param>
    public void UpdateStrength(ReadOnlySpan<char> password) => Strength = PasswordStrength.Evaluate(password);

    /// <summary>Reads the clock again and updates <see cref="RetrySeconds"/>.</summary>
    public void RefreshRetryDelay() =>
        RetrySeconds = (int)Math.Ceiling(_vault.PasswordRetryDelay.TotalSeconds);

    /// <summary>The delay survives leaving the page (and the unlock screen), so a page that is shown picks it up.</summary>
    public override void OnNavigatedTo() => RefreshRetryDelay();

    /// <summary>Clears the result of the last attempt and the strength, so a page that is shown again starts clean.</summary>
    public override void OnNavigatedFrom()
    {
        Outcome = SettingsOutcome.None;
        PasswordError = NewPasswordError.None;
        Strength = PasswordStrengthLevel.None;
    }

    /// <summary>The kit page calls this right before it returns here, after the new kit was committed.</summary>
    internal void ReportKitReplaced() => Outcome = SettingsOutcome.KitReplaced;

    partial void OnIdleTimeoutMinutesChanged(int value) =>
        ApplyPreferences(new AppPreferences(value, _settings.Current.LockOnMinimize));

    partial void OnLockOnMinimizeChanged(bool value) =>
        ApplyPreferences(new AppPreferences(_settings.Current.IdleTimeoutMinutes, value));

    private void ApplyPreferences(AppPreferences preferences) => SaveFailed = !_settings.Update(preferences);

    /// <summary>
    /// Changes the master password. The session stays open, the content is not re-encrypted under another key, and the
    /// kit keeps working. During the delay, or while another attempt runs, the call is ignored. A new password that the
    /// rule refuses runs no Argon2id and counts for nothing. Argon2id runs off the UI thread; the state after it is set
    /// back on the caller's context. The three arrays are zeroed before this returns, whatever the outcome.
    /// </summary>
    /// <param name="request">The passwords. This method takes ownership of the arrays and zeroes them.</param>
    [RelayCommand(CanExecute = nameof(CanAttempt))]
    private async Task ChangePasswordAsync(ChangePasswordRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var current = request.CurrentPassword;
        var replacement = request.NewPassword;
        try
        {
            RefreshRetryDelay();
            if (!CanAttempt())
            {
                return;
            }

            Outcome = SettingsOutcome.None;
            PasswordError = NewPasswordRules.Check(replacement, request.Confirmation);
            if (PasswordError != NewPasswordError.None)
            {
                return;
            }

            IsBusy = true;
            try
            {
                // No ConfigureAwait(false): the state below must be set on the UI thread.
                await Task.Run(() => _vault.ChangeMasterPassword(current, replacement));

                _vault.RecordPasswordSuccess();
                ShowOutcome(SettingsOutcome.PasswordChanged);
            }
            catch (VaultAuthenticationException)
            {
                _vault.RecordPasswordFailure();
                ShowOutcome(SettingsOutcome.PasswordWrong);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or CryptographicException)
            {
                // Not counted: the ArgumentException can come from the new password, which says nothing about the current one.
                ShowOutcome(SettingsOutcome.PasswordFailed);
            }
            catch (InvalidOperationException)
            {
                // The vault was locked (or disposed) before the change started: the page is already gone.
            }
            finally
            {
                IsBusy = false;
            }
        }
        finally
        {
            Array.Clear(current);
            Array.Clear(replacement);
            Array.Clear(request.Confirmation);
        }
    }

    /// <summary>
    /// Checks the master password and generates a new kit, then shows it on the kit page. <b>Nothing is written here</b>:
    /// the current kit stays valid until the user finishes on that page. During the delay, or while another attempt runs,
    /// the call is ignored. Argon2id runs off the UI thread; the state after it is set back on the caller's context. The
    /// array is zeroed before this returns, whatever the outcome.
    /// </summary>
    /// <param name="password">The master password. This method takes ownership and zeroes it.</param>
    [RelayCommand(CanExecute = nameof(CanAttempt))]
    private async Task GenerateKitAsync(char[] password)
    {
        ArgumentNullException.ThrowIfNull(password);

        try
        {
            RefreshRetryDelay();
            if (!CanAttempt())
            {
                return;
            }

            Outcome = SettingsOutcome.None;
            PasswordError = NewPasswordError.None;

            RecoveryKitReplacement? prepared = null;
            IsBusy = true;
            try
            {
                // No ConfigureAwait(false): the state below must be set on the UI thread.
                await Task.Run(() => _vault.VerifyMasterPassword(password));
                _vault.RecordPasswordSuccess();
                Array.Clear(password);
                if (!_vault.IsOpen)
                {
                    return;
                }

                var kit = new char[RecoveryKit.TextLength];
                _kitInProgress = kit;
                prepared = await Task.Run(() => _vault.PrepareRecoveryKit(kit));
            }
            catch (Exception ex) when (ex is VaultAuthenticationException or ArgumentException)
            {
                _vault.RecordPasswordFailure();
                ShowOutcome(SettingsOutcome.KitWrong);
            }
            catch (CryptographicException)
            {
                ShowOutcome(SettingsOutcome.KitFailed);
            }
            catch (InvalidOperationException)
            {
                // The vault was locked (or disposed) before the check or the kit started: the page is already gone.
            }
            finally
            {
                IsBusy = false;
            }

            if (prepared is not null && _vault.IsOpen)
            {
                var page = new NewRecoveryKitViewModel(_vault, this, _navigation, _kitInProgress!, prepared);

                // The kit page owns the kit from here on; the finally below must not zero it.
                _kitInProgress = null;
                _navigation.NavigateTo(page);
            }
        }
        finally
        {
            Array.Clear(password);
            if (_kitInProgress is { } unused)
            {
                Array.Clear(unused);
                _kitInProgress = null;
            }
        }
    }

    /// <summary>Opens the export page above this one; its Back returns here. A direct <c>Execute</c> is ignored while busy.</summary>
    [RelayCommand(CanExecute = nameof(IsIdle))]
    private void OpenExport()
    {
        if (IsIdle())
        {
            _vault.OpenExportCommand.Execute(null);
        }
    }

    /// <summary>Opens the import page above this one; its Back returns here. A direct <c>Execute</c> is ignored while busy.</summary>
    [RelayCommand(CanExecute = nameof(IsIdle))]
    private void OpenImport()
    {
        if (IsIdle())
        {
            _vault.OpenImportCommand.Execute(null);
        }
    }

    /// <summary>Returns to the entry list. A direct <c>Execute</c> is ignored while busy.</summary>
    [RelayCommand(CanExecute = nameof(IsIdle))]
    private void GoBack()
    {
        if (IsIdle())
        {
            _navigation.GoBack();
        }
    }

    private bool IsIdle() => !IsBusy;

    private bool CanAttempt() => !IsBusy && RetrySeconds == 0;

    // A locked vault sets no state: the page is already gone.
    private void ShowOutcome(SettingsOutcome outcome)
    {
        if (_vault.IsOpen)
        {
            RefreshRetryDelay();
            Outcome = outcome;
        }
    }
}
