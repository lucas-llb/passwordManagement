using Microsoft.UI.Xaml;
using Microsoft.Windows.ApplicationModel.Resources;
using PasswordManager.App.Views;
using PasswordManager.ViewModels;
using PasswordManager.ViewModels.Navigation;

namespace PasswordManager.App;

public sealed partial class MainWindow : Window
{
    private readonly INavigationService _navigation;

    public MainWindow(INavigationService navigation)
    {
        ArgumentNullException.ThrowIfNull(navigation);

        InitializeComponent();
        Title = new ResourceLoader().GetString("AppTitle");

        _navigation = navigation;
        _navigation.Navigated += OnNavigated;
        Closed += OnClosed;
    }

    private void OnClosed(object sender, WindowEventArgs args) => _navigation.Navigated -= OnNavigated;

    // One typeof literal per Frame.Navigate call keeps the trim analyzer able to see which page types are used.
    private void OnNavigated(object? sender, EventArgs e)
    {
        switch (_navigation.Current)
        {
            case ProfilesViewModel profiles:
                RootFrame.Navigate(typeof(ProfilesPage), profiles);
                break;
            case UnlockViewModel unlock:
                RootFrame.Navigate(typeof(UnlockPage), unlock);
                break;
            case EntryListViewModel list:
                RootFrame.Navigate(typeof(EntryListPage), list);
                break;
            case EntryDetailViewModel detail:
                RootFrame.Navigate(typeof(EntryDetailPage), detail);
                break;
            default:
                throw new InvalidOperationException("No page is registered for the current ViewModel.");
        }
    }
}
