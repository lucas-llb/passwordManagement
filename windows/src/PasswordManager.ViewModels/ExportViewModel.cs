using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PasswordManager.Core.Vault;
using PasswordManager.ViewModels.Navigation;

namespace PasswordManager.ViewModels;

/// <summary>
/// The export page, above the entry list: the user types an export password twice, picks a file, and the open vault is
/// written there as a <c>.pmvault</c> protected by that password alone (a fresh Vault Key and a fresh salt; no master
/// password slot and no Recovery Kit). Nothing else is ever written: not a temp or cache file, and never into the
/// vaults folder of the app.
/// </summary>
/// <remarks>
/// <para>It does not override <see cref="PageViewModel.AllowsScreenCapture"/>: the export password and the page that
/// shows it can never be captured. It sits above an open <see cref="EntryListViewModel"/>, so <see cref="AutoLock"/>
/// and the lock command cover it like any other page of the vault.</para>
/// <para>A lock while Argon2id runs: the export holds the session gate, so a lock that comes later waits for it and the
/// bytes it sealed are then written (they are encrypted with the export password, at the place the user chose). If the
/// lock won, the export throws and nothing is written.</para>
/// </remarks>
public sealed partial class ExportViewModel : PageViewModel
{
    private readonly EntryListViewModel _vault;
    private readonly INavigationService _navigation;

    /// <param name="vault">The list of the open vault that builds the export.</param>
    /// <param name="navigation">The page stack.</param>
    public ExportViewModel(EntryListViewModel vault, INavigationService navigation)
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

    /// <summary>Why the export password was refused on the last attempt, or <see cref="NewPasswordError.None"/>.</summary>
    [ObservableProperty]
    public partial NewPasswordError PasswordError { get; private set; }

    /// <summary>How the last attempt ended, or <see cref="ExportOutcome.None"/>.</summary>
    [ObservableProperty]
    public partial ExportOutcome Outcome { get; private set; }

    /// <summary>The name, without folders, of the file that the last attempt saved; null unless <see cref="Outcome"/> is <see cref="ExportOutcome.Saved"/>.</summary>
    [ObservableProperty]
    public partial string? SavedFileName { get; private set; }

    /// <summary>
    /// Checks the password, asks the picker for a file and writes the export there. The rule is checked first: a
    /// refused password never opens the dialog. Argon2id and the write run off the UI thread; the state after them is set
    /// back on the caller's context. Both arrays are zeroed before this returns, whatever the outcome (refused, canceled,
    /// locked while the dialog was open, failed).
    /// </summary>
    /// <param name="request">The password, its confirmation and the picker. This method takes ownership of the arrays and zeroes them.</param>
    [RelayCommand]
    private async Task ExportAsync(ExportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var password = request.Password;
        var confirmation = request.Confirmation;
        try
        {
            PasswordError = NewPasswordError.None;
            Outcome = ExportOutcome.None;
            SavedFileName = null;

            PasswordError = NewPasswordRules.Check(password, confirmation);
            if (PasswordError != NewPasswordError.None)
            {
                return;
            }

            // The dialog can stay open for a long time: the vault may be locked meanwhile.
            var path = await request.Picker.PickAsync();
            if (path is null || !_vault.IsOpen)
            {
                return;
            }

            if (IsInVaultsFolder(path))
            {
                Outcome = ExportOutcome.VaultsFolder;
                return;
            }

            // No ConfigureAwait(false): the state below must be set on the UI thread.
            await Task.Run(() => VaultFile.WriteExport(path, _vault.Export(password)));

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
            Array.Clear(confirmation);
        }
    }

    /// <summary>Clears the result of the last attempt, so a page that is shown again starts clean.</summary>
    public override void OnNavigatedFrom()
    {
        PasswordError = NewPasswordError.None;
        Outcome = ExportOutcome.None;
        SavedFileName = null;
    }

    /// <summary>
    /// Returns to the entry list. Not available while an export runs, including while the save dialog is open; a direct
    /// <c>Execute</c> is ignored in that state as well.
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

    // True when the file would sit directly in the vaults folder, where it could replace <id>.pmvault or profiles.json.
    // Subfolders are fine. Both sides are full paths without a trailing separator; Windows paths ignore case.
    private bool IsInVaultsFolder(string path)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(path));
        if (folder is null)
        {
            return false;
        }

        var vaults = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_vault.VaultsDirectory));

        return string.Equals(Path.TrimEndingDirectorySeparator(folder), vaults, StringComparison.OrdinalIgnoreCase);
    }
}
