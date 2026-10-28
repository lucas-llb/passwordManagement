using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PasswordManager.Core.Profiles;
using PasswordManager.ViewModels.Navigation;

namespace PasswordManager.ViewModels;

/// <summary>Profile selection: lists the profiles and opens the unlock screen of the chosen one.</summary>
public sealed partial class ProfilesViewModel : PageViewModel
{
    private readonly ProfileRepository _repository;
    private readonly INavigationService _navigation;

    public ProfilesViewModel(ProfileRepository repository, INavigationService navigation)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(navigation);

        _repository = repository;
        _navigation = navigation;
        Profiles = [];
    }

    /// <summary>The profiles in file (creation) order; empty when there are none or the list could not be read.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    public partial IReadOnlyList<Profile> Profiles { get; private set; }

    /// <summary>True when <see cref="Profiles"/> is empty.</summary>
    public bool IsEmpty => Profiles.Count == 0;

    /// <summary>True when the profile list exists but could not be read (corrupt or unreadable).</summary>
    [ObservableProperty]
    public partial bool LoadFailed { get; private set; }

    /// <inheritdoc />
    public override bool AllowsScreenCapture => true;

    /// <inheritdoc />
    public override void OnNavigatedTo() => Refresh();

    /// <summary>Reads the profile list again.</summary>
    public void Refresh()
    {
        try
        {
            Profiles = _repository.List();
            LoadFailed = false;
        }
        catch (Exception ex) when (ex is ProfileStoreException or IOException or UnauthorizedAccessException)
        {
            Profiles = [];
            LoadFailed = true;
        }
    }

    [RelayCommand]
    private void OpenProfile(Profile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        _navigation.NavigateTo(new UnlockViewModel(profile, _repository, _navigation));
    }
}
