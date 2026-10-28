using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PasswordManager.Core.Profiles;
using PasswordManager.ViewModels.Navigation;

namespace PasswordManager.ViewModels;

/// <summary>
/// Profile selection: lists the profiles, opens the unlock screen of the chosen one and starts the create, rename and
/// delete flows. Rename and delete have two steps (begin or request, then confirm), so the confirmation is part of
/// the ViewModel and not only a detail of the view.
/// </summary>
public sealed partial class ProfilesViewModel : PageViewModel
{
    private readonly ProfileRepository _repository;
    private readonly INavigationService _navigation;
    private readonly UnlockThrottle _throttle;

    /// <param name="repository">The profile store.</param>
    /// <param name="navigation">The page stack.</param>
    /// <param name="throttle">The unlock delay, passed on to the unlock screens this page opens.</param>
    public ProfilesViewModel(ProfileRepository repository, INavigationService navigation, UnlockThrottle throttle)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(navigation);
        ArgumentNullException.ThrowIfNull(throttle);

        _repository = repository;
        _navigation = navigation;
        _throttle = throttle;
        Profiles = [];
        RenameText = string.Empty;
    }

    /// <summary>The profiles in file (creation) order; empty when there are none or the list could not be read.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    public partial IReadOnlyList<Profile> Profiles { get; private set; }

    /// <summary>True when <see cref="Profiles"/> is empty.</summary>
    public bool IsEmpty => Profiles.Count == 0;

    /// <summary>True when the profile list exists but could not be read (corrupt or unreadable).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateProfileCommand))]
    public partial bool LoadFailed { get; private set; }

    /// <summary>
    /// True when the last rename or delete could not be done (a corrupt list, a disk failure or a profile that is
    /// gone). The next <see cref="BeginRenameCommand"/> or <see cref="RequestDeleteCommand"/> clears it.
    /// </summary>
    [ObservableProperty]
    public partial bool ActionFailed { get; private set; }

    /// <summary>The profile whose rename dialog is open, or null.</summary>
    [ObservableProperty]
    public partial Profile? RenamingProfile { get; private set; }

    /// <summary>The name typed in the rename dialog (two-way bound, so every keystroke lands here).</summary>
    [ObservableProperty]
    public partial string RenameText { get; set; }

    /// <summary>The rule the typed name broke on the last confirm, or <see cref="ProfileNameError.None"/>.</summary>
    [ObservableProperty]
    public partial ProfileNameError RenameError { get; private set; }

    /// <summary>
    /// The profile whose delete confirmation is open, or null. Nothing is deleted until
    /// <see cref="ConfirmDeleteCommand"/> runs.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmDeleteCommand))]
    public partial Profile? PendingDelete { get; private set; }

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

        _navigation.NavigateTo(new UnlockViewModel(profile, _repository, _navigation, _throttle));
    }

    // Nothing to create into while the list cannot be read: Create and ValidateDisplayName would throw.
    [RelayCommand(CanExecute = nameof(CanCreateProfile))]
    private void CreateProfile() => _navigation.NavigateTo(new CreateProfileViewModel(_repository, _navigation, _throttle));

    private bool CanCreateProfile() => !LoadFailed;

    [RelayCommand]
    private void BeginRename(Profile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        ActionFailed = false;
        RenameError = ProfileNameError.None;
        RenameText = profile.DisplayName;
        RenamingProfile = profile;
    }

    /// <summary>
    /// Renames the profile of the open dialog. A name that breaks a rule sets <see cref="RenameError"/> and keeps the
    /// dialog open (<see cref="RenamingProfile"/> stays set); any other outcome closes it and reads the list again.
    /// </summary>
    [RelayCommand]
    private void ConfirmRename()
    {
        if (RenamingProfile is not { } profile)
        {
            return;
        }

        try
        {
            var error = _repository.ValidateDisplayName(RenameText, profile.Id);
            if (error != ProfileNameError.None)
            {
                RenameError = error;
                return;
            }

            _repository.Rename(profile.Id, RenameText);
        }
        catch (Exception ex) when (IsActionFailure(ex))
        {
            ActionFailed = true;
        }

        ClearRename();
        Refresh();
    }

    [RelayCommand]
    private void CancelRename() => ClearRename();

    [RelayCommand]
    private void RequestDelete(Profile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        ActionFailed = false;
        PendingDelete = profile;
    }

    /// <summary>
    /// Deletes the profile that <see cref="RequestDeleteCommand"/> recorded: its <c>profiles.json</c> entry and its
    /// vault files. Does nothing when no delete is pending. The list is read again whatever the outcome.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasPendingDelete))]
    private void ConfirmDelete()
    {
        if (PendingDelete is not { } profile)
        {
            return;
        }

        try
        {
            _repository.Delete(profile.Id);
        }
        catch (Exception ex) when (IsActionFailure(ex))
        {
            ActionFailed = true;
        }
        finally
        {
            PendingDelete = null;
            Refresh();
        }
    }

    private bool HasPendingDelete() => PendingDelete is not null;

    [RelayCommand]
    private void CancelDelete() => PendingDelete = null;

    private void ClearRename()
    {
        RenamingProfile = null;
        RenameText = string.Empty;
        RenameError = ProfileNameError.None;
    }

    private static bool IsActionFailure(Exception ex) =>
        ex is ProfileStoreException or IOException or UnauthorizedAccessException or KeyNotFoundException;
}
