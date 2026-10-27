using CommunityToolkit.Mvvm.Input;
using PasswordManager.Core.Vault;
using PasswordManager.ViewModels.Navigation;

namespace PasswordManager.ViewModels;

/// <summary>Read-only view of one entry. It does not expose the password: the editor owns that (W-20).</summary>
public sealed partial class EntryDetailViewModel : PageViewModel
{
    private readonly INavigationService _navigation;

    public EntryDetailViewModel(VaultEntry entry, INavigationService navigation)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(navigation);

        Entry = entry;
        _navigation = navigation;
    }

    /// <summary>The entry being shown.</summary>
    public VaultEntry Entry { get; }

    [RelayCommand]
    private void GoBack() => _navigation.GoBack();
}
