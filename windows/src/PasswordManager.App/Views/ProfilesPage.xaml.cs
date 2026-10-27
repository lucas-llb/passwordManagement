using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using PasswordManager.Core.Profiles;
using PasswordManager.ViewModels;

namespace PasswordManager.App.Views;

public sealed partial class ProfilesPage : Page
{
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

    private void OnProfileClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is Profile profile)
        {
            ViewModel.OpenProfileCommand.Execute(profile);
        }
    }
}
