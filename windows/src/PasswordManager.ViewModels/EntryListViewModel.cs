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
    }

    /// <summary>The entries in content order; empty once the vault is locked.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    public partial IReadOnlyList<VaultEntry> Entries { get; private set; }

    /// <summary>True when <see cref="Entries"/> is empty.</summary>
    public bool IsEmpty => Entries.Count == 0;

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
        Entries = [];
        _navigation.ResetTo(new ProfilesViewModel(_repository, _navigation, _throttle));
        _navigation.NavigateTo(new UnlockViewModel(_profile, _repository, _navigation, _throttle));
    }
}
