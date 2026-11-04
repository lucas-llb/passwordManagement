using System.Security.Cryptography;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PasswordManager.Core.Vault;
using PasswordManager.ViewModels.Navigation;

namespace PasswordManager.ViewModels;

/// <summary>
/// The import page, above the entry list: the user picks a <c>.pmvault</c> (and types its password) or a CSV, sees a
/// preview, and confirms. Choosing a file only reads and decrypts it in memory. Confirm makes exactly one
/// <see cref="VaultSession.Save"/> with all the merged entries, so the <c>.bak</c> holds the version before the import.
/// The source file is never changed or deleted.
/// </summary>
/// <remarks>
/// <para>It does not override <see cref="PageViewModel.AllowsScreenCapture"/>: the page can never be captured. It sits
/// above an open <see cref="EntryListViewModel"/>, so <see cref="AutoLock"/> and the lock command cover it like any other
/// page of the vault.</para>
/// <para>A lock can come at any point of a running command: the page resets at once, every await is followed by a check
/// that the vault is open, and a locked vault sets no state. If the save already ran, it stays on disk, but the list is
/// not repopulated.</para>
/// <para>The file password belongs to the file, not to this vault, and whoever holds the file attacks it offline anyway,
/// so a wrong one causes no delay and the shared <see cref="UnlockThrottle"/> is not touched.</para>
/// <para>The <c>.pmvault</c> bytes are zeroed once decrypted, on a failed read and on leaving the page. The imported
/// strings are managed and cannot be zeroed (AGENTS.md §3.5): every reference is dropped after Confirm, on Back and on
/// lock.</para>
/// </remarks>
public sealed partial class ImportViewModel : PageViewModel
{
    private readonly EntryListViewModel _vault;
    private readonly INavigationService _navigation;
    private readonly TimeProvider _clock;

    // The .pmvault bytes while the password is asked. Pinned by VaultImport.ReadFile; only this class holds them.
    private byte[]? _file;

    // The validated entries, with their passwords, while the preview is shown.
    private IReadOnlyList<VaultEntry>? _incoming;

    /// <param name="vault">The list of the open vault that the entries are imported into.</param>
    /// <param name="navigation">The page stack.</param>
    /// <param name="clock">The source of the import time.</param>
    public ImportViewModel(EntryListViewModel vault, INavigationService navigation, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(vault);
        ArgumentNullException.ThrowIfNull(navigation);
        ArgumentNullException.ThrowIfNull(clock);

        _vault = vault;
        _navigation = navigation;
        _clock = clock;
        PreviewRows = [];
        RejectedLines = [];
        IgnoredColumns = [];
    }

    /// <summary>The step the user is on.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenVaultFileCommand))]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    public partial ImportStage Stage { get; private set; }

    /// <summary>Why the last step failed, or <see cref="ImportError.None"/>.</summary>
    [ObservableProperty]
    public partial ImportError Error { get; private set; }

    /// <summary>The 1-based line, or item position, where the file was refused; 0 when it has none. The text of <see cref="ImportError.MalformedCsv"/> shows it.</summary>
    [ObservableProperty]
    public partial int ErrorLine { get; private set; }

    /// <summary>The name, without folders, of the chosen file; null when there is none.</summary>
    [ObservableProperty]
    public partial string? FileName { get; private set; }

    /// <summary>True when the chosen file is a CSV. It stays on <see cref="ImportStage.Done"/>, for the reminder to delete it.</summary>
    [ObservableProperty]
    public partial bool IsCsv { get; private set; }

    /// <summary>The imported entries, in file order; empty unless <see cref="Stage"/> is <see cref="ImportStage.Preview"/>.</summary>
    [ObservableProperty]
    public partial ImportPreviewRow[] PreviewRows { get; private set; }

    /// <summary>The imported entries that match no vault entry.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WriteCount))]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    public partial int NewCount { get; private set; }

    /// <summary>The imported entries that match a vault entry.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WriteCount))]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    public partial int DuplicateCount { get; private set; }

    /// <summary>The items left out, by the 1-based line of a CSV record or the 1-based position of a <c>.pmvault</c> entry.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<int> RejectedLines { get; private set; }

    /// <summary>The CSV columns with a value that the app does not keep. Names only, never values.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<string> IgnoredColumns { get; private set; }

    /// <summary>What Confirm does with a duplicate. <see cref="DuplicatePolicy.Skip"/> by default.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WriteCount))]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    public partial DuplicatePolicy Policy { get; set; }

    /// <summary>How many imported entries Confirm would write with the current <see cref="Policy"/>.</summary>
    public int WriteCount => Policy == DuplicatePolicy.Skip ? NewCount : NewCount + DuplicateCount;

    /// <summary>Entries the last Confirm appended.</summary>
    [ObservableProperty]
    public partial int AddedCount { get; private set; }

    /// <summary>Vault entries the last Confirm updated in place.</summary>
    [ObservableProperty]
    public partial int ReplacedCount { get; private set; }

    /// <summary>Duplicates the last Confirm dropped.</summary>
    [ObservableProperty]
    public partial int SkippedCount { get; private set; }

    /// <summary>True while any command runs, including while the open dialog is shown.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ChooseFileCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenVaultFileCommand))]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    [NotifyCanExecuteChangedFor(nameof(GoBackCommand))]
    public partial bool IsBusy { get; private set; }

    /// <summary>Clears everything and zeroes the file bytes, whichever way the page is left.</summary>
    public override void OnNavigatedFrom() => Reset();

    /// <summary>
    /// Asks the picker for a file and reads it off the UI thread. A CSV goes to the preview; a <c>.pmvault</c> that looks
    /// like a vault goes to the password step. A canceled dialog keeps the current step and file; any other answer
    /// discards them first, so a failed read leaves nothing behind. A direct call while busy is ignored.
    /// </summary>
    /// <param name="picker">The open dialog.</param>
    [RelayCommand(CanExecute = nameof(CanChooseFile))]
    private async Task ChooseFileAsync(IImportFilePicker picker)
    {
        ArgumentNullException.ThrowIfNull(picker);

        if (!CanChooseFile())
        {
            return;
        }

        IsBusy = true;
        try
        {
            Error = ImportError.None;
            ErrorLine = 0;

            // The dialog can stay open for a long time: the vault may be locked meanwhile.
            var path = await picker.PickAsync();
            if (path is null || !_vault.IsOpen)
            {
                return;
            }

            Reset();
            var extension = Path.GetExtension(path);
            if (string.Equals(extension, VaultCsv.FileExtension, StringComparison.OrdinalIgnoreCase))
            {
                // No ConfigureAwait(false): the state below must be set on the UI thread.
                var now = _clock.GetUtcNow();
                var prepared = await Task.Run(() => Prepare(VaultCsv.ParseAndZero(VaultImport.ReadFile(path), now)));
                if (_vault.IsOpen)
                {
                    ShowPreview(prepared, Path.GetFileName(path), isCsv: true);
                }
            }
            else if (string.Equals(extension, VaultFormat.FileExtension, StringComparison.OrdinalIgnoreCase))
            {
                var file = await Task.Run(() => ReadVaultFile(path));
                if (_vault.IsOpen)
                {
                    _file = file;
                    FileName = Path.GetFileName(path);
                    Stage = ImportStage.Password;
                }
                else
                {
                    CryptographicOperations.ZeroMemory(file);
                }
            }
            else
            {
                Discard(ImportError.UnsupportedFile);
            }
        }
        catch (VaultImportException ex)
        {
            Discard(ToImportError(ex.Error), ex.Line);
        }
        catch (VaultFormatException)
        {
            Discard(ImportError.InvalidVaultFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Discard(ImportError.ReadFailed);
        }
        catch (InvalidOperationException)
        {
            // The vault was locked (or disposed) while the file was read: the page is already gone.
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Decrypts the <c>.pmvault</c> off the UI thread and goes to the preview. Argon2id and a valid tag on the slot and on
    /// the body are needed before any entry reaches memory. A wrong password and an altered file stay on the password
    /// step with one message, so the user can retry. The array is zeroed before this returns, whatever the outcome.
    /// </summary>
    /// <param name="password">The file password. This method takes ownership of the array and zeroes it.</param>
    [RelayCommand(CanExecute = nameof(CanOpenVaultFile))]
    private async Task OpenVaultFileAsync(char[] password)
    {
        ArgumentNullException.ThrowIfNull(password);

        if (!CanOpenVaultFile() || _file is not { } file)
        {
            Array.Clear(password);
            return;
        }

        IsBusy = true;
        try
        {
            Error = ImportError.None;
            ErrorLine = 0;

            var fileName = FileName;
            var prepared = await Task.Run(() => Prepare(VaultImport.FromContent(_vault.DecryptImport(file, password))));
            if (_vault.IsOpen)
            {
                ZeroFile();
                ShowPreview(prepared, fileName, isCsv: false);
            }
        }
        catch (Exception ex) when (ex is VaultAuthenticationException or ArgumentException)
        {
            if (_vault.IsOpen)
            {
                Error = ImportError.WrongPassword;
            }
        }
        catch (VaultImportException ex)
        {
            Discard(ToImportError(ex.Error), ex.Line);
        }
        catch (Exception ex) when (ex is VaultFormatException or CryptographicException)
        {
            Discard(ImportError.InvalidVaultFile);
        }
        catch (InvalidOperationException)
        {
            // The vault was locked (or disposed) while the file was decrypted: the page is already gone.
        }
        finally
        {
            Array.Clear(password);
            IsBusy = false;
        }
    }

    /// <summary>
    /// Merges the imported entries into the vault and saves once, off the UI thread, then rebuilds the list. A failed save
    /// leaves the vault and the list unchanged and stays on the preview, so the user can retry. A direct call that the
    /// command would refuse is ignored.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanConfirm))]
    private async Task ConfirmAsync()
    {
        if (!CanConfirm() || _incoming is not { } incoming)
        {
            return;
        }

        IsBusy = true;
        try
        {
            Error = ImportError.None;
            ErrorLine = 0;

            var policy = Policy;
            var now = _clock.GetUtcNow();
            var merged = await Task.Run(() =>
            {
                var result = VaultImport.Merge(_vault.CurrentEntries, incoming, policy, now);
                _vault.SaveEntries(result.Entries);
                return result;
            });

            if (_vault.ApplySavedEntries(merged.Entries))
            {
                AddedCount = merged.Added;
                ReplacedCount = merged.Replaced;
                SkippedCount = merged.Skipped;
                DropPreview();
                Stage = ImportStage.Done;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            if (_vault.IsOpen)
            {
                Error = ImportError.SaveFailed;
            }
        }
        catch (InvalidOperationException)
        {
            // The vault was locked (or disposed) before the save: the page is already gone, and nothing was written.
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Returns to the entry list, discarding whatever was not confirmed. Not available while a command runs, including
    /// while the open dialog is shown; a direct <c>Execute</c> is ignored in that state as well.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void GoBack()
    {
        if (CanGoBack())
        {
            _navigation.GoBack();
        }
    }

    private bool CanChooseFile() => !IsBusy;

    private bool CanOpenVaultFile() => Stage == ImportStage.Password && !IsBusy;

    private bool CanConfirm() => Stage == ImportStage.Preview && WriteCount > 0 && !IsBusy;

    private bool CanGoBack() => !IsBusy;

    private static ImportError ToImportError(ImportFileError error) => error switch
    {
        ImportFileError.TooLarge => ImportError.TooLarge,
        ImportFileError.TooManyEntries => ImportError.TooManyEntries,
        ImportFileError.InvalidEncoding => ImportError.InvalidEncoding,
        ImportFileError.InvalidHeader => ImportError.InvalidHeader,
        ImportFileError.MalformedCsv => ImportError.MalformedCsv,
        _ => ImportError.ReadFailed,
    };

    // Reads the file and checks the header, so a file that is not a vault fails before a password is asked.
    private static byte[] ReadVaultFile(string path)
    {
        var file = VaultImport.ReadFile(path);
        try
        {
            VaultHeader.Parse(file);
            return file;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(file);
            throw;
        }
    }

    // Off the UI thread: the plan and the rows can cover many entries.
    private Prepared Prepare(ImportFile file)
    {
        var plan = VaultImport.Plan(_vault.CurrentEntries, file.Entries);
        var rows = new ImportPreviewRow[file.Entries.Count];
        for (var i = 0; i < rows.Length; i++)
        {
            var entry = file.Entries[i];
            rows[i] = new ImportPreviewRow(entry.Title, entry.Username, entry.Url, plan.IsDuplicate[i]);
        }

        return new Prepared(file, plan, rows);
    }

    private void ShowPreview(Prepared prepared, string? fileName, bool isCsv)
    {
        _incoming = prepared.File.Entries;
        FileName = fileName;
        IsCsv = isCsv;
        PreviewRows = prepared.Rows;
        NewCount = prepared.Plan.NewCount;
        DuplicateCount = prepared.Plan.DuplicateCount;
        RejectedLines = prepared.File.RejectedLines;
        IgnoredColumns = prepared.File.IgnoredColumns;
        Policy = DuplicatePolicy.Skip;

        // Last, so a page that reacts to the step finds the rest in place.
        Stage = ImportStage.Preview;
    }

    // A failed read keeps nothing: the step goes back to choosing a file. A locked vault sets no state.
    private void Discard(ImportError error, int line = 0)
    {
        if (_vault.IsOpen)
        {
            Reset();
            Error = error;
            ErrorLine = line;
        }
    }

    private void Reset()
    {
        ZeroFile();
        DropPreview();
        IsCsv = false;
        AddedCount = 0;
        ReplacedCount = 0;
        SkippedCount = 0;
        Error = ImportError.None;
        ErrorLine = 0;
        Stage = ImportStage.ChooseFile;
    }

    private void DropPreview()
    {
        _incoming = null;
        FileName = null;
        PreviewRows = [];
        NewCount = 0;
        DuplicateCount = 0;
        RejectedLines = [];
        IgnoredColumns = [];
        Policy = DuplicatePolicy.Skip;
    }

    private void ZeroFile()
    {
        if (_file is { } file)
        {
            CryptographicOperations.ZeroMemory(file);
            _file = null;
        }
    }

    // A class and not a record, so no generated member can print the entries.
    private sealed class Prepared(ImportFile file, ImportPlan plan, ImportPreviewRow[] rows)
    {
        public ImportFile File { get; } = file;

        public ImportPlan Plan { get; } = plan;

        public ImportPreviewRow[] Rows { get; } = rows;
    }
}
