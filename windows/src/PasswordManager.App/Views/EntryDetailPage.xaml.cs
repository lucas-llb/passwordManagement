using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using PasswordManager.ViewModels;

namespace PasswordManager.App.Views;

public sealed partial class EntryDetailPage : Page
{
    public EntryDetailPage()
    {
        InitializeComponent();
    }

    public EntryDetailViewModel ViewModel { get; private set; } = null!;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel = (EntryDetailViewModel)e.Parameter;
    }
}
