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

// The settings page: the auto-lock preferences, the change of the master password, a new Recovery Kit and the links to
// export and import. The window is excluded from capture (the view model does not allow it), like every page of an open
// vault. The password boxes follow the CSV export page: each is emptied as soon as it was read.
public sealed partial class SettingsPage : Page
{
    private static readonly TimeSpan CountdownInterval = TimeSpan.FromMilliseconds(250);

    private DispatcherQueueTimer? _countdown;
    private bool _loaded;

    public SettingsPage()
    {
        InitializeComponent();
    }

    public SettingsViewModel ViewModel { get; private set; } = null!;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel = (SettingsViewModel)e.Parameter;
        ShowIdleOptions();
    }

    // The messages are shown through x:Bind functions on the outcome. The two form errors (too short, mismatch) reuse the
    // functions of the create page.
    public static Visibility IsPasswordWrong(SettingsOutcome outcome) =>
        outcome == SettingsOutcome.PasswordWrong ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility IsPasswordFailed(SettingsOutcome outcome) =>
        outcome == SettingsOutcome.PasswordFailed ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility IsPasswordChanged(SettingsOutcome outcome) =>
        outcome == SettingsOutcome.PasswordChanged ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility IsKitWrong(SettingsOutcome outcome) =>
        outcome == SettingsOutcome.KitWrong ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility IsKitFailed(SettingsOutcome outcome) =>
        outcome == SettingsOutcome.KitFailed ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility IsKitReplaced(SettingsOutcome outcome) =>
        outcome == SettingsOutcome.KitReplaced ? Visibility.Visible : Visibility.Collapsed;

    // An attempt needs no command in flight and no delay left after a wrong password. The buttons and Enter share this,
    // and it mirrors the CanExecute of the commands.
    public static bool CanSubmit(bool isBusy, int retrySeconds) => !isBusy && retrySeconds == 0;

    // Plain strings keep the accessible name of each item equal to its text. The items are the allowed timeouts, in the
    // order of the view model; the current one is selected.
    private void ShowIdleOptions()
    {
        var format = new ResourceLoader().GetString("SettingsIdleOption");
        var minutes = ViewModel.IdleOptions;
        List<string> options = [];
        var selected = -1;
        for (var index = 0; index < minutes.Count; index++)
        {
            options.Add(string.Format(CultureInfo.CurrentCulture, format, minutes[index]));
            if (minutes[index] == ViewModel.IdleTimeoutMinutes)
            {
                selected = index;
            }
        }

        IdleCombo.ItemsSource = options;
        IdleCombo.SelectedIndex = selected;
    }

    // A choice applies and is saved at once. Showing the current one again changes nothing, so opening the page writes nothing.
    private void OnIdleSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var index = IdleCombo.SelectedIndex;
        if (index >= 0)
        {
            ViewModel.IdleTimeoutMinutes = ViewModel.IdleOptions[index];
        }
    }

    // The focus starts on the first control. Coming back from the new kit page that was just concluded, it starts on the
    // kit button instead, and the message that the kit was replaced is scrolled into view.
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _loaded = true;

        if (ViewModel.Outcome == SettingsOutcome.KitReplaced)
        {
            KitGenerateButton.Focus(FocusState.Programmatic);
            KitReplacedText.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
        }
        else
        {
            IdleCombo.Focus(FocusState.Programmatic);
        }

        // The delay of an earlier attempt, or of the unlock screen of the same profile, may still be running.
        ViewModel.RefreshRetryDelay();
        StartCountdownIfNeeded();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _loaded = false;
        _countdown?.Stop();
    }

    // The PasswordBox only offers a string; the view model reads it and keeps nothing of it.
    private void OnNewPasswordChanged(object sender, RoutedEventArgs e) => ViewModel.UpdateStrength(NewInput.Password);

    private async void OnChangePasswordClick(object sender, RoutedEventArgs e) => await ChangePasswordAsync();

    private async void OnKitGenerateClick(object sender, RoutedEventArgs e) => await GenerateKitAsync();

    // Enter in the last box changes the password, like the button; it is handled so that nothing else reacts to the key.
    // While a command runs or during a delay the key does nothing: the boxes are not read and keep their text.
    private async void OnConfirmKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter)
        {
            return;
        }

        e.Handled = true;
        if (CanSubmit(ViewModel.IsBusy, ViewModel.RetrySeconds))
        {
            await ChangePasswordAsync();
        }
    }

    // Enter in the kit box generates the kit, with the same rule.
    private async void OnKitPasswordKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter)
        {
            return;
        }

        e.Handled = true;
        if (CanSubmit(ViewModel.IsBusy, ViewModel.RetrySeconds))
        {
            await GenerateKitAsync();
        }
    }

    // The command zeroes the arrays it receives; the PasswordBoxes only offer strings, which are emptied here as far as
    // they can be, after every attempt. The finally covers a call the command rejected (for example, one already running).
    private async Task ChangePasswordAsync()
    {
        var current = CurrentInput.Password.ToCharArray();
        var replacement = NewInput.Password.ToCharArray();
        var confirmation = ConfirmInput.Password.ToCharArray();
        CurrentInput.Password = string.Empty;
        NewInput.Password = string.Empty;
        ConfirmInput.Password = string.Empty;
        try
        {
            await ViewModel.ChangePasswordCommand.ExecuteAsync(new ChangePasswordRequest(current, replacement, confirmation));
        }
        finally
        {
            Array.Clear(current);
            Array.Clear(replacement);
            Array.Clear(confirmation);
        }

        // A wrong password starts a delay.
        StartCountdownIfNeeded();
    }

    // A right password leaves this page for the kit page, which is why the countdown is only started when the page is
    // still loaded.
    private async Task GenerateKitAsync()
    {
        var password = KitPasswordInput.Password.ToCharArray();
        KitPasswordInput.Password = string.Empty;
        try
        {
            await ViewModel.GenerateKitCommand.ExecuteAsync(password);
        }
        finally
        {
            Array.Clear(password);
        }

        StartCountdownIfNeeded();
    }

    // The view model is synchronous about the clock: this timer asks it to read the clock again while a delay is counting
    // down, and stops by itself when the delay ends.
    private void StartCountdownIfNeeded()
    {
        if (!_loaded || ViewModel.RetrySeconds == 0)
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
