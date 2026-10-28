using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using PasswordManager.Core.Passwords;
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

    // The strength bar: one step per level, error colors for the weak levels and paused colors for a fair one, all from
    // the theme.
    public static double StrengthValue(PasswordStrengthLevel level) => (int)level;

    public static bool IsWeak(PasswordStrengthLevel level) =>
        level is PasswordStrengthLevel.VeryWeak or PasswordStrengthLevel.Weak;

    public static bool IsFair(PasswordStrengthLevel level) => level == PasswordStrengthLevel.Fair;

    public static Visibility HasText(string? text) =>
        string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;

    private void OnLoaded(object sender, RoutedEventArgs e) => NameInput.Focus(FocusState.Programmatic);

    // The PasswordBox only offers a string; the view model reads it and keeps nothing of it.
    private void OnPasswordChanged(object sender, RoutedEventArgs e) => ViewModel.UpdateStrength(PasswordInput.Password);

    // The phrase goes in both boxes, so the user can create right away; focus stays on the button.
    private void OnSuggestClick(object sender, RoutedEventArgs e)
    {
        ViewModel.SuggestPassphraseCommand.Execute(null);
        if (ViewModel.Suggestion is { } phrase)
        {
            PasswordInput.Password = phrase;
            ConfirmInput.Password = phrase;
        }
    }

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
