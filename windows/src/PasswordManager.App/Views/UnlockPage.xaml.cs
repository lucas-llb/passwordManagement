using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using PasswordManager.ViewModels;
using Windows.System;

namespace PasswordManager.App.Views;

public sealed partial class UnlockPage : Page
{
    public UnlockPage()
    {
        InitializeComponent();
    }

    public UnlockViewModel ViewModel { get; private set; } = null!;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel = (UnlockViewModel)e.Parameter;
    }

    // The error texts are shown through x:Bind functions on the error state.
    public static Visibility IsWrongPassword(UnlockError error) =>
        error == UnlockError.WrongPassword ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility IsVaultUnreadable(UnlockError error) =>
        error == UnlockError.VaultUnreadable ? Visibility.Visible : Visibility.Collapsed;

    public static bool IsNot(bool value) => !value;

    private void OnLoaded(object sender, RoutedEventArgs e) => PasswordInput.Focus(FocusState.Programmatic);

    private async void OnUnlockClick(object sender, RoutedEventArgs e) => await UnlockAsync();

    // Enter in the box unlocks, like the button; it is handled so that nothing else reacts to the key. While an unlock
    // runs, the button is disabled, so Enter does nothing either.
    private async void OnPasswordKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter)
        {
            return;
        }

        e.Handled = true;
        if (!ViewModel.UnlockCommand.IsRunning)
        {
            await UnlockAsync();
        }
    }

    // The command zeroes the array it receives; the PasswordBox only offers a string, which is emptied here as far as
    // it can be. The finally covers a call the command rejected (for example, one already running).
    private async Task UnlockAsync()
    {
        var password = PasswordInput.Password.ToCharArray();
        PasswordInput.Password = string.Empty;
        try
        {
            await ViewModel.UnlockCommand.ExecuteAsync(password);
        }
        finally
        {
            Array.Clear(password);
        }
    }
}
