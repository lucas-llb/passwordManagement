using System.Collections;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PasswordManager.Core.Profiles;
using PasswordManager.Core.Vault;
using PasswordManager.ViewModels.Navigation;

namespace PasswordManager.ViewModels;

/// <summary>The unlocked vault: lists the entries and owns the open <see cref="VaultSession"/> until it is locked.</summary>
public sealed partial class EntryListViewModel : PageViewModel
{
    private readonly Profile _profile;
    private readonly VaultSession _session;
    private readonly ProfileRepository _repository;
    private readonly INavigationService _navigation;
    private readonly UnlockThrottle _throttle;
    private readonly TimeProvider _clock;

    // The entries with their folded search fields (title, username, URL, folder), rebuilt after every save; cleared on lock.
    private (VaultEntry Entry, string[] Fields)[] _index;

    public EntryListViewModel(
        Profile profile,
        VaultSession session,
        ProfileRepository repository,
        INavigationService navigation,
        UnlockThrottle throttle,
        TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(navigation);
        ArgumentNullException.ThrowIfNull(throttle);

        _profile = profile;
        _session = session;
        _repository = repository;
        _navigation = navigation;
        _throttle = throttle;
        _clock = clock ?? TimeProvider.System;
        Entries = session.Content.Entries;
        _index = BuildIndex(Entries);
        Folders = DistinctFolders(Entries);
        FilteredEntries = AsList(Entries);
        SearchText = string.Empty;
    }

    /// <summary>The entries in content order; empty once the vault is locked.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    [NotifyPropertyChangedFor(nameof(HasNoMatches))]
    public partial IReadOnlyList<VaultEntry> Entries { get; private set; }

    /// <summary>True when <see cref="Entries"/> is empty.</summary>
    public bool IsEmpty => Entries.Count == 0;

    /// <summary>
    /// The live search text. It is matched against the title, username, URL and folder (case and accent insensitive);
    /// every word has to match. Never null: null becomes an empty string.
    /// </summary>
    [ObservableProperty]
    public partial string SearchText { get; set; }

    /// <summary>When true, only favorite entries are listed.</summary>
    [ObservableProperty]
    public partial bool FavoritesOnly { get; set; }

    /// <summary>The folder to list (ordinal match), or null for all folders.</summary>
    [ObservableProperty]
    public partial string? SelectedFolder { get; set; }

    /// <summary>The distinct, non-empty folder names of the vault, sorted for the current culture; empty once locked.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<string> Folders { get; private set; }

    /// <summary>
    /// The entries that match <see cref="SearchText"/>, <see cref="FavoritesOnly"/> and <see cref="SelectedFolder"/>,
    /// in content order. It is always a non-generic <see cref="IList"/>, so the list control keeps indexed virtualization.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoMatches))]
    public partial IReadOnlyList<VaultEntry> FilteredEntries { get; private set; }

    /// <summary>True when the vault has entries but none of them passes the search and the filters.</summary>
    public bool HasNoMatches => !IsEmpty && FilteredEntries.Count == 0;

    /// <summary>True until the vault is locked; <see cref="AutoLock"/> uses it to know whether a vault is open.</summary>
    public bool IsOpen => _session.IsUnlocked;

    /// <summary>
    /// The entry the user last opened, so the list page can give it the focus back after Back. After a save it is the
    /// new record; null after a delete and after the lock.
    /// </summary>
    public VaultEntry? LastOpened { get; private set; }

    [RelayCommand]
    private void OpenEntry(VaultEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        LastOpened = entry;
        _navigation.NavigateTo(new EntryDetailViewModel(this, entry, _navigation, _clock));
    }

    /// <summary>Opens the detail page with an empty draft; the first save adds the entry at the end of the vault.</summary>
    [RelayCommand]
    private void NewEntry() => _navigation.NavigateTo(new EntryDetailViewModel(this, null, _navigation, _clock));

    /// <summary>
    /// Saves <paramref name="entry"/> into the vault: it replaces the entry with the same <c>Id</c> in place, or is
    /// appended. The write is synchronous and atomic; then the entries, the search index and the folders are rebuilt
    /// and the filter is applied again.
    /// </summary>
    /// <returns>False, with nothing written, when the vault is already locked.</returns>
    /// <exception cref="IOException">The write failed; the vault on disk and this list are unchanged.</exception>
    /// <exception cref="UnauthorizedAccessException">The write was denied; the vault and this list are unchanged.</exception>
    internal bool Commit(VaultEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (!IsOpen)
        {
            return false;
        }

        var current = _session.Content.Entries;
        var updated = new List<VaultEntry>(current.Count + 1);
        var replaced = false;
        foreach (var existing in current)
        {
            if (!replaced && existing.Id == entry.Id)
            {
                updated.Add(entry);
                replaced = true;
            }
            else
            {
                updated.Add(existing);
            }
        }

        if (!replaced)
        {
            updated.Add(entry);
        }

        Write([.. updated]);
        LastOpened = entry;
        return true;
    }

    /// <summary>
    /// Removes the entry with <paramref name="id"/> from the vault, with the same write and rebuild as
    /// <see cref="Commit"/>.
    /// </summary>
    /// <returns>False, with nothing written, when the vault is already locked.</returns>
    /// <exception cref="IOException">The write failed; the vault on disk and this list are unchanged.</exception>
    /// <exception cref="UnauthorizedAccessException">The write was denied; the vault and this list are unchanged.</exception>
    internal bool Remove(Guid id)
    {
        if (!IsOpen)
        {
            return false;
        }

        Write([.. _session.Content.Entries.Where(existing => existing.Id != id)]);
        LastOpened = null;
        return true;
    }

    // Saves the new entries (on failure the session and the disk stay unchanged), then rebuilds the list from them.
    private void Write(VaultEntry[] entries)
    {
        _session.Save(_session.Content with { Entries = entries });

        Entries = entries;
        _index = BuildIndex(entries);
        Folders = DistinctFolders(entries);
        if (SelectedFolder is { } folder && !Folders.Contains(folder, StringComparer.Ordinal))
        {
            // Clearing the folder applies the filter again.
            SelectedFolder = null;
            return;
        }

        ApplyFilter();
    }

    /// <summary>
    /// Locks the vault and shows the unlock screen of the same profile, with Back leading to the profile list.
    /// </summary>
    [RelayCommand]
    private void Lock()
    {
        _session.Dispose();
        LastOpened = null;
        _index = [];
        Entries = [];
        Folders = [];
        SearchText = string.Empty;
        FavoritesOnly = false;
        SelectedFolder = null;
        FilteredEntries = Array.Empty<VaultEntry>();
        _navigation.ResetTo(new ProfilesViewModel(_repository, _navigation, _throttle));
        _navigation.NavigateTo(new UnlockViewModel(_profile, _repository, _navigation, _throttle));
    }

    partial void OnSearchTextChanged(string value)
    {
        if (value is null)
        {
            SearchText = string.Empty;
            return;
        }

        ApplyFilter();
    }

    partial void OnFavoritesOnlyChanged(bool value) => ApplyFilter();

    partial void OnSelectedFolderChanged(string? value) => ApplyFilter();

    private void ApplyFilter()
    {
        var terms = EntrySearch.Terms(SearchText);
        var folder = SelectedFolder;

        if (terms.Length == 0 && !FavoritesOnly && folder is null)
        {
            FilteredEntries = AsList(Entries);
            return;
        }

        var result = new List<VaultEntry>();
        foreach (var (entry, fields) in _index)
        {
            if (FavoritesOnly && !entry.Favorite)
            {
                continue;
            }

            if (folder is not null && !string.Equals(entry.Folder, folder, StringComparison.Ordinal))
            {
                continue;
            }

            if (EntrySearch.Matches(fields, terms))
            {
                result.Add(entry);
            }
        }

        FilteredEntries = result.ToArray();
    }

    private static (VaultEntry Entry, string[] Fields)[] BuildIndex(IReadOnlyList<VaultEntry> entries)
    {
        var index = new (VaultEntry Entry, string[] Fields)[entries.Count];
        for (var i = 0; i < index.Length; i++)
        {
            var entry = entries[i];
            index[i] = (
                entry,
                [
                    EntrySearch.Fold(entry.Title),
                    EntrySearch.Fold(entry.Username),
                    EntrySearch.Fold(entry.Url),
                    EntrySearch.Fold(entry.Folder),
                ]);
        }

        return index;
    }

    private static string[] DistinctFolders(IReadOnlyList<VaultEntry> entries) =>
        entries
            .Select(entry => entry.Folder)
            .Where(folder => folder.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.CurrentCulture)
            .ToArray();

    // The list control only keeps indexed virtualization over a non-generic IList.
    private static IReadOnlyList<VaultEntry> AsList(IReadOnlyList<VaultEntry> entries) =>
        entries is IList ? entries : entries.ToArray();
}
