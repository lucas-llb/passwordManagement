using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PasswordManager.Core.Vault;
using PasswordManager.ViewModels.Navigation;

namespace PasswordManager.ViewModels;

/// <summary>
/// The detail of one entry, always editable. It holds a draft of every user field; <see cref="SaveCommand"/> writes the
/// draft through <see cref="EntryListViewModel"/> (atomic save, <c>.bak</c>), and Back is blocked while the draft has
/// unsaved changes. A null entry opens an empty draft that the first save turns into a new entry.
/// </summary>
/// <remarks>Field values never go into an exception, a log or a <c>ToString</c>.</remarks>
public sealed partial class EntryDetailViewModel : PageViewModel
{
    // A WinUI TextBox with AcceptsReturn turns "\n" into "\r"; notes are compared and saved with "\n" only.
    private static readonly string LineFeed = new('\n', 1);

    private readonly EntryListViewModel _vault;
    private readonly INavigationService _navigation;
    private readonly TimeProvider _clock;
    private readonly bool _loaded;

    /// <param name="vault">The open vault that saves and removes the entry.</param>
    /// <param name="entry">The entry to show, or null for a new entry.</param>
    /// <param name="navigation">The page stack.</param>
    /// <param name="clock">The source of the creation and update timestamps.</param>
    public EntryDetailViewModel(
        EntryListViewModel vault,
        VaultEntry? entry,
        INavigationService navigation,
        TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(vault);
        ArgumentNullException.ThrowIfNull(navigation);
        ArgumentNullException.ThrowIfNull(clock);

        _vault = vault;
        _navigation = navigation;
        _clock = clock;
        Entry = entry;
        Title = string.Empty;
        Username = string.Empty;
        Password = string.Empty;
        Url = string.Empty;
        Notes = string.Empty;
        Folder = string.Empty;
        LoadDraft();
        _loaded = true;
        UpdateDirty();
    }

    /// <summary>The saved entry, or null while the entry is new (not saved yet).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNew))]
    [NotifyPropertyChangedFor(nameof(CreatedAt))]
    [NotifyPropertyChangedFor(nameof(UpdatedAt))]
    [NotifyCanExecuteChangedFor(nameof(RequestDeleteCommand))]
    public partial VaultEntry? Entry { get; private set; }

    /// <summary>True until the entry is saved for the first time.</summary>
    public bool IsNew => Entry is null;

    /// <summary>When the saved entry was created, or null while it is new.</summary>
    public DateTimeOffset? CreatedAt => Entry?.CreatedAt;

    /// <summary>When the saved entry was last changed, or null while it is new.</summary>
    public DateTimeOffset? UpdatedAt => Entry?.UpdatedAt;

    /// <summary>The title in the draft. Never null: null becomes an empty string.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTitleMissing))]
    public partial string Title { get; set; }

    /// <summary>The username in the draft. Never null.</summary>
    [ObservableProperty]
    public partial string Username { get; set; }

    /// <summary>The password in the draft. Never null. Hidden by the page until <see cref="IsPasswordVisible"/>.</summary>
    [ObservableProperty]
    public partial string Password { get; set; }

    /// <summary>The URL in the draft. Never null.</summary>
    [ObservableProperty]
    public partial string Url { get; set; }

    /// <summary>The notes in the draft. Never null. Line endings are normalized to "\n" when compared and saved.</summary>
    [ObservableProperty]
    public partial string Notes { get; set; }

    /// <summary>The folder in the draft. Never null; empty means no folder.</summary>
    [ObservableProperty]
    public partial string Folder { get; set; }

    /// <summary>The favorite flag in the draft.</summary>
    [ObservableProperty]
    public partial bool Favorite { get; set; }

    /// <summary>True when the draft differs from the saved entry (or from an empty draft for a new entry).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    [NotifyCanExecuteChangedFor(nameof(DiscardCommand))]
    [NotifyCanExecuteChangedFor(nameof(GoBackCommand))]
    public partial bool IsDirty { get; private set; }

    /// <summary>True when the title is empty or only whitespace; the entry cannot be saved then.</summary>
    public bool IsTitleMissing => string.IsNullOrWhiteSpace(Title);

    /// <summary>Whether the page shows the password in clear text. False by default.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPasswordHidden))]
    public partial bool IsPasswordVisible { get; set; }

    /// <summary>The opposite of <see cref="IsPasswordVisible"/>.</summary>
    public bool IsPasswordHidden => !IsPasswordVisible;

    /// <summary>
    /// True after a save or delete that could not be written; the vault on disk and the draft are unchanged. The next
    /// successful save, or <see cref="DiscardCommand"/>, clears it.
    /// </summary>
    [ObservableProperty]
    public partial bool SaveFailed { get; private set; }

    /// <summary>True between <see cref="RequestDeleteCommand"/> and the confirm or cancel of the delete dialog.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmDeleteCommand))]
    public partial bool IsDeletePending { get; private set; }

    /// <summary>
    /// Writes the draft: an edit keeps the <c>Id</c>, <c>CreatedAt</c> and every other field of the saved record; a new
    /// entry gets a new <c>Id</c>. <c>UpdatedAt</c> (and <c>CreatedAt</c> of a new entry) is the clock truncated to whole
    /// milliseconds, the precision stored on disk. Does nothing once the vault is locked.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    private void Save()
    {
        if (!CanSave())
        {
            return;
        }

        var now = TruncateToMilliseconds(_clock.GetUtcNow());
        var record = Entry is { } original
            ? original with
            {
                Title = Title,
                Username = Username,
                Password = Password,
                Url = Url,
                Notes = NormalizeNotes(Notes),
                Folder = Folder,
                Favorite = Favorite,
                UpdatedAt = now,
            }
            : new VaultEntry
            {
                Id = Guid.NewGuid(),
                Title = Title,
                Username = Username,
                Password = Password,
                Url = Url,
                Notes = NormalizeNotes(Notes),
                Folder = Folder,
                Favorite = Favorite,
                CreatedAt = now,
                UpdatedAt = now,
            };

        try
        {
            if (!_vault.Commit(record))
            {
                return;
            }
        }
        catch (Exception ex) when (IsWriteFailure(ex))
        {
            SaveFailed = true;
            return;
        }

        SaveFailed = false;
        Entry = record;
        UpdateDirty();
    }

    private bool CanSave() => IsDirty && !IsTitleMissing;

    /// <summary>Puts the saved values (or an empty draft for a new entry) back into the draft.</summary>
    [RelayCommand(CanExecute = nameof(IsDirty))]
    private void Discard()
    {
        LoadDraft();
        SaveFailed = false;
        UpdateDirty();
    }

    /// <summary>Returns to the list. Blocked while the draft has unsaved changes.</summary>
    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void GoBack()
    {
        // Execute does not check CanExecute, and Escape reaches the command through an accelerator.
        if (CanGoBack())
        {
            _navigation.GoBack();
        }
    }

    private bool CanGoBack() => !IsDirty;

    /// <summary>Opens the delete confirmation. Only a saved entry can be deleted.</summary>
    [RelayCommand(CanExecute = nameof(CanRequestDelete))]
    private void RequestDelete()
    {
        if (CanRequestDelete())
        {
            IsDeletePending = true;
        }
    }

    private bool CanRequestDelete() => !IsNew;

    /// <summary>
    /// Removes the entry from the vault and returns to the list, even with unsaved changes in the draft. Does nothing
    /// once the vault is locked; on a write failure it sets <see cref="SaveFailed"/> and stays.
    /// </summary>
    [RelayCommand(CanExecute = nameof(IsDeletePending))]
    private void ConfirmDelete()
    {
        if (!IsDeletePending || Entry is not { } entry)
        {
            return;
        }

        IsDeletePending = false;
        try
        {
            if (!_vault.Remove(entry.Id))
            {
                return;
            }
        }
        catch (Exception ex) when (IsWriteFailure(ex))
        {
            SaveFailed = true;
            return;
        }

        _navigation.GoBack();
    }

    /// <summary>Closes the delete confirmation without deleting.</summary>
    [RelayCommand]
    private void CancelDelete() => IsDeletePending = false;

    partial void OnTitleChanged(string value) => OnDraftChanged(value, v => Title = v);

    partial void OnUsernameChanged(string value) => OnDraftChanged(value, v => Username = v);

    partial void OnPasswordChanged(string value) => OnDraftChanged(value, v => Password = v);

    partial void OnUrlChanged(string value) => OnDraftChanged(value, v => Url = v);

    partial void OnNotesChanged(string value) => OnDraftChanged(value, v => Notes = v);

    partial void OnFolderChanged(string value) => OnDraftChanged(value, v => Folder = v);

    partial void OnFavoriteChanged(bool value) => UpdateDirty();

    // A two-way binding may push null; the draft keeps an empty string instead.
    private void OnDraftChanged(string? value, Action<string> set)
    {
        if (value is null)
        {
            set(string.Empty);
            return;
        }

        UpdateDirty();
    }

    private void LoadDraft()
    {
        Title = Entry?.Title ?? string.Empty;
        Username = Entry?.Username ?? string.Empty;
        Password = Entry?.Password ?? string.Empty;
        Url = Entry?.Url ?? string.Empty;
        Notes = Entry?.Notes ?? string.Empty;
        Folder = Entry?.Folder ?? string.Empty;
        Favorite = Entry?.Favorite ?? false;
    }

    private void UpdateDirty()
    {
        if (!_loaded)
        {
            return;
        }

        IsDirty =
            !string.Equals(Title, Entry?.Title ?? string.Empty, StringComparison.Ordinal)
            || !string.Equals(Username, Entry?.Username ?? string.Empty, StringComparison.Ordinal)
            || !string.Equals(Password, Entry?.Password ?? string.Empty, StringComparison.Ordinal)
            || !string.Equals(Url, Entry?.Url ?? string.Empty, StringComparison.Ordinal)
            || !string.Equals(NormalizeNotes(Notes), NormalizeNotes(Entry?.Notes ?? string.Empty), StringComparison.Ordinal)
            || !string.Equals(Folder, Entry?.Folder ?? string.Empty, StringComparison.Ordinal)
            || Favorite != (Entry?.Favorite ?? false);

        // Save also depends on the title, which can change while IsDirty stays the same.
        SaveCommand.NotifyCanExecuteChanged();
    }

    private static string NormalizeNotes(string notes) => notes.ReplaceLineEndings(LineFeed);

    private static DateTimeOffset TruncateToMilliseconds(DateTimeOffset value) =>
        new(value.Ticks - (value.Ticks % TimeSpan.TicksPerMillisecond), value.Offset);

    private static bool IsWriteFailure(Exception ex) => ex is IOException or UnauthorizedAccessException;

    /// <summary>Only the type name: the draft holds secrets.</summary>
    public override string ToString() => nameof(EntryDetailViewModel);
}
