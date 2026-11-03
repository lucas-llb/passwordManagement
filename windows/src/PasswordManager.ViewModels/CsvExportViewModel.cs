using System.Security.Cryptography;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PasswordManager.Core.Vault;
using PasswordManager.ViewModels.Navigation;

namespace PasswordManager.ViewModels;

/// <summary>
/// The CSV export page, above the export page: after the user says that anyone can read the file and types the master
/// password again, the entries of the open vault are written as a plaintext CSV (<see cref="VaultCsv"/>) to the file the
/// user picks. Nothing else is ever written: not a temp or cache file, and never into the vaults folder of the app.
/// </summary>
/// <remarks>
/// <para>The master password is really checked (Argon2id and a valid AEAD tag on the vault slot), not compared to
/// anything stored. The progressive delay is the one of the unlock screen of the same profile, shared through the
/// <see cref="UnlockThrottle"/>, so this page is not a faster way to guess the password.</para>
/// <para>It does not override <see cref="PageViewModel.AllowsScreenCapture"/>: the page can never be captured. It sits
/// above an open <see cref="EntryListViewModel"/>, so <see cref="AutoLock"/> and the lock command cover it like any other
/// page of the vault.</para>
/// <para>A lock after the content was read: the plaintext is still written to the file the user chose, seconds after
/// they asked for it. If the lock came first, the export throws and nothing is written.</para>
/// </remarks>
public sealed partial class CsvExportViewModel : PageViewModel
{
    private readonly EntryListViewModel _vault;
    private readonly INavigationService _navigation;

    /// <param name="vault">The list of the open vault whose entries are exported.</param>
    /// <param name="navigation">The page stack.</param>
    public CsvExportViewModel(EntryListViewModel vault, INavigationService navigation)
    {
        ArgumentNullException.ThrowIfNull(vault);
        ArgumentNullException.ThrowIfNull(navigation);

        _vault = vault;
        _navigation = navigation;

        // Back is blocked while an export runs, including the time the save dialog is open.
        ExportCommand.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(IAsyncRelayCommand.IsRunning))
            {
                GoBackCommand.NotifyCanExecuteChanged();
            }
        };
    }

    /// <summary>
    /// True once the user said they understand that the file is readable by anyone. It enables
    /// <see cref="ExportCommand"/>, and it is cleared when the page is left.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExportCommand))]
    public partial bool Acknowledged { get; set; }

    /// <summary>
    /// How many whole seconds are left, rounded up, until another attempt is allowed after a wrong master password; 0
    /// means an attempt is allowed. It only changes in <see cref="RefreshRetryDelay"/> and after a wrong password: the
    /// view calls that while it shows the countdown.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExportCommand))]
    public partial int RetrySeconds { get; private set; }

    /// <summary>How the last attempt ended, or <see cref="ExportOutcome.None"/>.</summary>
    [ObservableProperty]
    public partial ExportOutcome Outcome { get; private set; }

    /// <summary>The name, without folders, of the file that the last attempt saved; null unless <see cref="Outcome"/> is <see cref="ExportOutcome.Saved"/>.</summary>
    [ObservableProperty]
    public partial string? SavedFileName { get; private set; }

    /// <summary>Reads the clock again and updates <see cref="RetrySeconds"/>.</summary>
    public void RefreshRetryDelay() =>
        RetrySeconds = (int)Math.Ceiling(_vault.PasswordRetryDelay.TotalSeconds);

    /// <summary>The delay survives leaving the page (and the unlock screen), so a page that is shown picks it up.</summary>
    public override void OnNavigatedTo() => RefreshRetryDelay();

    /// <summary>Clears the result of the last attempt and the acknowledgement, so a page that is shown again starts clean.</summary>
    public override void OnNavigatedFrom()
    {
        Outcome = ExportOutcome.None;
        SavedFileName = null;
        Acknowledged = false;
    }

    /// <summary>
    /// Checks the master password, asks the picker for a file and writes the CSV there. The password is checked first:
    /// a wrong one never opens the dialog, and counts as a failure for the delay shared with the unlock screen. The
    /// check and the write run off the UI thread; the state after them is set back on the caller's context. Without the
    /// acknowledgement, or during the delay, the call is ignored: no Argon2id runs and nothing is counted. The password
    /// array is zeroed before this returns, whatever the outcome (and as soon as it was checked, before the dialog
    /// opens); the CSV bytes are zeroed right after they are written.
    /// </summary>
    /// <param name="request">The master password and the picker. This method takes ownership of the array and zeroes it.</param>
    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task ExportAsync(CsvExportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var password = request.MasterPassword;
        try
        {
            Outcome = ExportOutcome.None;
            SavedFileName = null;

            RefreshRetryDelay();
            if (!Acknowledged || RetrySeconds > 0)
            {
                return;
            }

            if (!await VerifyMasterPasswordAsync(password))
            {
                return;
            }

            // The dialog can stay open for a long time: the password is not needed any more, and the vault may be locked meanwhile.
            var path = await request.Picker.PickAsync();
            if (path is null || !_vault.IsOpen)
            {
                return;
            }

            if (ExportDestination.IsInVaultsFolder(path, _vault.VaultsDirectory))
            {
                Outcome = ExportOutcome.VaultsFolder;
                return;
            }

            // No ConfigureAwait(false): the state below must be set on the UI thread.
            await Task.Run(() =>
            {
                var csv = _vault.ExportCsv();
                try
                {
                    VaultCsv.WriteFile(path, csv);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(csv);
                }
            });

            Outcome = ExportOutcome.Saved;
            SavedFileName = Path.GetFileName(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Outcome = ExportOutcome.Failed;
        }
        catch (InvalidOperationException)
        {
            // The vault was locked (or disposed) while the export ran: the page is already gone, and nothing was written.
        }
        finally
        {
            Array.Clear(password);
        }
    }

    /// <summary>
    /// Returns to the export page. Not available while an export runs, including while the save dialog is open; a
    /// direct <c>Execute</c> is ignored in that state as well.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void GoBack()
    {
        if (CanGoBack())
        {
            _navigation.GoBack();
        }
    }

    private bool CanGoBack() => !ExportCommand.IsRunning;

    private bool CanExport() => Acknowledged && RetrySeconds == 0;

    // Runs Argon2id off the UI thread. A wrong password is counted for the shared delay; the right one clears it and is
    // zeroed at once. Returns true only for the right password.
    private async Task<bool> VerifyMasterPasswordAsync(char[] password)
    {
        try
        {
            await Task.Run(() => _vault.VerifyMasterPassword(password));
        }
        catch (Exception ex) when (ex is VaultAuthenticationException or ArgumentException)
        {
            _vault.RecordPasswordFailure();
            RefreshRetryDelay();
            Outcome = ExportOutcome.WrongPassword;
            return false;
        }

        _vault.RecordPasswordSuccess();
        Array.Clear(password);
        return true;
    }
}
