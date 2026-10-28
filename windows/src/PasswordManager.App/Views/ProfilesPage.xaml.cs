using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using PasswordManager.Core.Profiles;
using PasswordManager.ViewModels;

namespace PasswordManager.App.Views;

public sealed partial class ProfilesPage : Page
{
    // ShowAsync throws when a second ContentDialog opens, so a click while one is showing is ignored.
    private bool _dialogOpen;

    public ProfilesPage()
    {
        InitializeComponent();
    }

    public ProfilesViewModel ViewModel { get; private set; } = null!;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel = (ProfilesViewModel)e.Parameter;
    }

    // The empty-state text is for an empty list that was read fine; a failed read has its own message.
    public static Visibility ShowEmptyState(bool isEmpty, bool loadFailed) =>
        isEmpty && !loadFailed ? Visibility.Visible : Visibility.Collapsed;

    private void OnLoaded(object sender, RoutedEventArgs e) => FocusStart();

    // The list gets the keyboard focus when there is something in it, otherwise the create button. The enqueue lets the
    // list build its containers first (also after a dialog changed it).
    private void FocusStart() =>
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (ViewModel.IsEmpty)
            {
                CreateProfileButton.Focus(FocusState.Programmatic);
            }
            else
            {
                ProfilesList.Focus(FocusState.Programmatic);
            }
        });

    // A screen reader reads the name of an item container, not the ToString() of the profile behind it.
    private static void OnProfileContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (!args.InRecycleQueue && args.Item is Profile profile)
        {
            AutomationProperties.SetName(args.ItemContainer, profile.DisplayName);
        }
    }

    private void OnProfileClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is Profile profile)
        {
            ViewModel.OpenProfileCommand.Execute(profile);
        }
    }

    private async void OnRenameClick(object sender, RoutedEventArgs e)
    {
        if (_dialogOpen || ((FrameworkElement)sender).DataContext is not Profile profile)
        {
            return;
        }

        ViewModel.BeginRenameCommand.Execute(profile);
        var result = await ShowDialogAsync(RenameDialog);
        if (result != ContentDialogResult.Primary)
        {
            ViewModel.CancelRenameCommand.Execute(null);
        }
    }

    // A name that breaks a rule keeps the profile in RenamingProfile; cancelling the click keeps the dialog open to show
    // the error. Any other outcome (renamed, or the action failed) lets it close.
    private void OnRenamePrimaryClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        ViewModel.ConfirmRenameCommand.Execute(null);
        args.Cancel = ViewModel.RenamingProfile is not null;
    }

    private async void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (_dialogOpen || ((FrameworkElement)sender).DataContext is not Profile profile)
        {
            return;
        }

        ViewModel.RequestDeleteCommand.Execute(profile);
        var result = await ShowDialogAsync(DeleteDialog);
        if (result == ContentDialogResult.Primary)
        {
            ViewModel.ConfirmDeleteCommand.Execute(null);
        }
        else
        {
            ViewModel.CancelDeleteCommand.Execute(null);
        }
    }

    private async Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog)
    {
        _dialogOpen = true;
        try
        {
            dialog.XamlRoot = XamlRoot;
            return await dialog.ShowAsync();
        }
        finally
        {
            _dialogOpen = false;
            FocusStart();
        }
    }
}
