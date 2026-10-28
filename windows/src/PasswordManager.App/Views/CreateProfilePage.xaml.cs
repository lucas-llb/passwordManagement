using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using PasswordManager.ViewModels;
using Windows.System;

namespace PasswordManager.App.Views;

public sealed partial class CreateProfilePage : Page
{
    public CreateProfilePage()
    {
        InitializeComponent();
    }

    public CreateProfileViewModel ViewModel { get; private set; } = null!;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel = (CreateProfileViewModel)e.Parameter;
    }

    // The password error texts are shown through x:Bind functions on the error state.
    public static Visibility IsTooShort(NewPasswordError error) =>
        error == NewPasswordError.TooShort ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility IsMismatch(NewPasswordError error) =>
        error == NewPasswordError.Mismatch ? Visibility.Visible : Visibility.Collapsed;

    public static bool IsNot(bool value) => !value;

    private void OnLoaded(object sender, RoutedEventArgs e) => NameInput.Focus(FocusState.Programmatic);

    private async void OnCreateClick(object sender, RoutedEventArgs e) => await CreateAsync();

    // Enter in the confirmation creates, like the button; it is handled so that nothing else reacts to the key. While a
    // create runs, the button is disabled, so Enter does nothing either.
    private async void OnConfirmKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter)
        {
            return;
        }

        e.Handled = true;
        if (!ViewModel.CreateCommand.IsRunning)
        {
            await CreateAsync();
        }
    }

    // The command zeroes both arrays it receives; the PasswordBoxes only offer strings, which are emptied here as far as
    // they can be. The finally covers a call the command rejected (for example, one already running).
    private async Task CreateAsync()
    {
        var password = PasswordInput.Password.ToCharArray();
        var confirmation = ConfirmInput.Password.ToCharArray();
        PasswordInput.Password = string.Empty;
        ConfirmInput.Password = string.Empty;
        try
        {
            await ViewModel.CreateCommand.ExecuteAsync(new NewMasterPassword(password, confirmation));
        }
        finally
        {
            Array.Clear(password);
            Array.Clear(confirmation);
        }
    }
}
