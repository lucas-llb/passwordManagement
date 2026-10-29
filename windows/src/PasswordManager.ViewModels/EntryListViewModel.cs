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

    // The entries with their folded search fields (title, username, URL, folder), built once; cleared on lock.
    private (VaultEntry Entry, string[] Fields)[] _index;

    public EntryListViewModel(
        Profile profile,
        VaultSession session,
        ProfileRepository repository,
        INavigationService navigation,
        UnlockThrottle throttle)
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

    [RelayCommand]
    private void OpenEntry(VaultEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        _navigation.NavigateTo(new EntryDetailViewModel(entry, _navigation));
    }

    /// <summary>
    /// Locks the vault and shows the unlock screen of the same profile, with Back leading to the profile list.
    /// </summary>
    [RelayCommand]
    private void Lock()
    {
        _session.Dispose();
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
