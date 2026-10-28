using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using PasswordManager.ViewModels;

namespace PasswordManager.App.Views;

// Shows the Recovery Kit once. The window is excluded from capture (the view model does not allow it), and the page has
// no way back: only Concluir leaves it, after the checkbox.
public sealed partial class RecoveryKitPage : Page
{
    public RecoveryKitPage()
    {
        InitializeComponent();
    }

    public RecoveryKitViewModel ViewModel { get; private set; } = null!;

    // The TextBlock needs a string: the same accepted limit as the text of a PasswordBox. It is emptied when the page is
    // left; the view model zeroes its own array then.
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel = (RecoveryKitViewModel)e.Parameter;
        KitTextBlock.Text = new string(ViewModel.KitText);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        KitTextBlock.Text = string.Empty;
        base.OnNavigatedFrom(e);
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => KitSavedCheck.Focus(FocusState.Programmatic);

    // IsChecked is a bool?, so the checkbox is synced by hand instead of a two-way bind.
    private void OnKitSavedChanged(object sender, RoutedEventArgs e) => ViewModel.KitSaved = KitSavedCheck.IsChecked == true;
}
