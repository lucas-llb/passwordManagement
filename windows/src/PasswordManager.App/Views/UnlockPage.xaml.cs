using System.Globalization;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.Windows.ApplicationModel.Resources;
using PasswordManager.ViewModels;
using VirtualKey = Windows.System.VirtualKey;

namespace PasswordManager.App.Views;

public sealed partial class UnlockPage : Page
{
    private static readonly TimeSpan CountdownInterval = TimeSpan.FromMilliseconds(250);

    private DispatcherQueueTimer? _countdown;

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

    // An attempt needs no unlock in flight and no delay left after a wrong password. The button and Enter share this.
    public static bool CanSubmit(bool isRunning, int retrySeconds) => !isRunning && retrySeconds == 0;

    // The countdown text: the view model only exposes the number, the sentence and its format live in the resw.
    public static string RetryText(int seconds) =>
        string.Format(CultureInfo.CurrentCulture, new ResourceLoader().GetString("UnlockRetryCountdown"), seconds);

    public static Visibility IsCountingDown(int seconds) => seconds > 0 ? Visibility.Visible : Visibility.Collapsed;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        PasswordInput.Focus(FocusState.Programmatic);

        // The delay of a previous visit to this profile may still be running.
        ViewModel.RefreshRetryDelay();
        StartCountdownIfNeeded();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => _countdown?.Stop();

    private async void OnUnlockClick(object sender, RoutedEventArgs e) => await UnlockAsync();

    // Enter in the box unlocks, like the button; it is handled so that nothing else reacts to the key. While an unlock
    // runs or a delay is counting down, the key does nothing: the box is not read and keeps its text.
    private async void OnPasswordKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter)
        {
            return;
        }

        e.Handled = true;
        if (CanSubmit(ViewModel.UnlockCommand.IsRunning, ViewModel.RetrySeconds))
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

        // A wrong password starts a delay; a success has already left the page and has none.
        StartCountdownIfNeeded();
    }

    // The view model is synchronous: this timer asks it to read the clock again while a delay is counting down, and
    // stops by itself when the delay ends.
    private void StartCountdownIfNeeded()
    {
        if (ViewModel.RetrySeconds == 0)
        {
            return;
        }

        if (_countdown is null)
        {
            _countdown = DispatcherQueue.CreateTimer();
            _countdown.Interval = CountdownInterval;
            _countdown.IsRepeating = true;
            _countdown.Tick += OnCountdownTick;
        }

        if (!_countdown.IsRunning)
        {
            _countdown.Start();
        }
    }

    private void OnCountdownTick(DispatcherQueueTimer sender, object args)
    {
        ViewModel.RefreshRetryDelay();
        if (ViewModel.RetrySeconds == 0)
        {
            sender.Stop();
        }
    }
}
