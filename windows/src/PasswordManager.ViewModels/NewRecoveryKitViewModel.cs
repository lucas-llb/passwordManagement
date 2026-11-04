using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PasswordManager.Core.Vault;
using PasswordManager.ViewModels.Navigation;

namespace PasswordManager.ViewModels;

/// <summary>
/// Shows a new Recovery Kit once, above the settings page, and writes it only when the user says it was written down.
/// Until then the file on disk does not change, so the current kit keeps working: Cancel, Escape, a lock or closing the
/// app leave it valid.
/// </summary>
/// <remarks>
/// <para>It does not override <see cref="PageViewModel.AllowsScreenCapture"/>: the kit opens the vault, so it can never be
/// captured. It sits above an open <see cref="EntryListViewModel"/>, so <see cref="AutoLock"/> and the lock command cover
/// it like any other page of the vault. The kit text is zeroed as soon as the page is left, whichever way.</para>
/// <para>This is not the page shown after creating a profile (<see cref="RecoveryKitViewModel"/>): that one has no way out
/// and no write step.</para>
/// </remarks>
public sealed partial class NewRecoveryKitViewModel : PageViewModel
{
    private readonly EntryListViewModel _vault;
    private readonly SettingsViewModel _settingsPage;
    private readonly INavigationService _navigation;
    private readonly char[] _kitText;
    private readonly RecoveryKitReplacement _replacement;
    private bool _cleared;

    /// <summary>Takes ownership of <paramref name="kitText"/>: this page zeroes it when it is left.</summary>
    /// <param name="vault">The list of the open vault.</param>
    /// <param name="settingsPage">The settings page below this one, told about a kit that was committed.</param>
    /// <param name="navigation">The page stack.</param>
    /// <param name="kitText">The kit text, exactly <see cref="RecoveryKit.TextLength"/> chars. The caller must not use
    /// or zero the array afterwards.</param>
    /// <param name="replacement">The prepared file that holds this kit.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="kitText"/> does not have the length of a kit text.</exception>
    public NewRecoveryKitViewModel(
        EntryListViewModel vault,
        SettingsViewModel settingsPage,
        INavigationService navigation,
        char[] kitText,
        RecoveryKitReplacement replacement)
    {
        ArgumentNullException.ThrowIfNull(vault);
        ArgumentNullException.ThrowIfNull(settingsPage);
        ArgumentNullException.ThrowIfNull(navigation);
        ArgumentNullException.ThrowIfNull(kitText);
        ArgumentNullException.ThrowIfNull(replacement);

        if (kitText.Length != RecoveryKit.TextLength)
        {
            throw new ArgumentException("The recovery kit text has an invalid length.", nameof(kitText));
        }

        _vault = vault;
        _settingsPage = settingsPage;
        _navigation = navigation;
        _kitText = kitText;
        _replacement = replacement;
    }

    /// <summary>The kit text to show; empty once the page has been left.</summary>
    public ReadOnlySpan<char> KitText => _cleared ? default : _kitText;

    /// <summary>True once the user said the kit was written down. It enables <see cref="DoneCommand"/>, and it is cleared when the page is left.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DoneCommand))]
    public partial bool KitSaved { get; set; }

    /// <summary>True when the last write failed. The kit stays shown and the current kit stays valid, so the user can try again.</summary>
    [ObservableProperty]
    public partial bool SaveFailed { get; private set; }

    /// <summary>True while the write runs.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DoneCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    public partial bool IsBusy { get; private set; }

    /// <summary>Zeroes the kit. The page is left for good, so nothing shows it or commits it again.</summary>
    public override void OnNavigatedFrom()
    {
        Array.Clear(_kitText);
        _cleared = true;
        KitSaved = false;
    }

    /// <summary>
    /// Writes the new kit off the UI thread and returns to the settings page, which says that the kit was replaced. On a
    /// failed write the page stays, with the kit shown. Does nothing until <see cref="KitSaved"/> is set, or while a write
    /// runs, even through a direct <c>Execute</c>. A lock that came during the write leaves the page alone: the write
    /// already ended, so it stays on disk, and no state is set.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanFinish))]
    private async Task DoneAsync()
    {
        if (!CanFinish())
        {
            return;
        }

        var committed = false;
        SaveFailed = false;
        IsBusy = true;
        try
        {
            // No ConfigureAwait(false): the navigation below must run on the UI thread.
            await Task.Run(() => _vault.CommitRecoveryKit(_replacement));
            committed = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // Nothing was written. A locked vault is told apart below, because the page is already gone then.
        }
        finally
        {
            IsBusy = false;
        }

        if (!_vault.IsOpen)
        {
            return;
        }

        if (!committed)
        {
            SaveFailed = true;
            return;
        }

        _settingsPage.ReportKitReplaced();
        _navigation.GoBack();
    }

    /// <summary>
    /// Returns to the settings page with nothing written: the current kit stays valid. Not available while a write runs;
    /// a direct <c>Execute</c> is ignored in that state as well.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        if (CanCancel())
        {
            _navigation.GoBack();
        }
    }

    private bool CanFinish() => KitSaved && !IsBusy;

    private bool CanCancel() => !IsBusy;
}
