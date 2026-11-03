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

// The plaintext CSV export: the warning, the acknowledgement and the master password come before the save dialog. The
// window is excluded from capture (the view model does not allow it), like every page of an open vault.
public sealed partial class CsvExportPage : Page
{
    private static readonly TimeSpan CountdownInterval = TimeSpan.FromMilliseconds(250);

    private DispatcherQueueTimer? _countdown;

    public CsvExportPage()
    {
        InitializeComponent();
    }

    public CsvExportViewModel ViewModel { get; private set; } = null!;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel = (CsvExportViewModel)e.Parameter;
    }

    // The messages are shown through x:Bind functions on the outcome.
    public static Visibility IsWrongPassword(ExportOutcome outcome) =>
        outcome == ExportOutcome.WrongPassword ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility IsSaved(ExportOutcome outcome) =>
        outcome == ExportOutcome.Saved ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility IsFailed(ExportOutcome outcome) =>
        outcome == ExportOutcome.Failed ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility IsVaultsFolder(ExportOutcome outcome) =>
        outcome == ExportOutcome.VaultsFolder ? Visibility.Visible : Visibility.Collapsed;

    // An attempt needs the acknowledgement, no export in flight and no delay left after a wrong password. The button and
    // Enter share this, and it mirrors the CanExecute of the command.
    public static bool CanSubmit(bool acknowledged, bool isRunning, int retrySeconds) =>
        acknowledged && !isRunning && retrySeconds == 0;

    // The countdown text: the view model only exposes the number, the sentence and its format live in the resw (the same
    // one of the unlock screen, whose delay this page shares).
    public static string RetryText(int seconds) =>
        string.Format(CultureInfo.CurrentCulture, new ResourceLoader().GetString("UnlockRetryCountdown"), seconds);

    public static Visibility IsCountingDown(int seconds) => seconds > 0 ? Visibility.Visible : Visibility.Collapsed;

    // The confirmation text: the view model only exposes the file name, the sentence and its format live in the resw.
    public static string DoneText(string? fileName) =>
        string.IsNullOrEmpty(fileName)
            ? string.Empty
            : string.Format(CultureInfo.CurrentCulture, new ResourceLoader().GetString("CsvExportDone"), fileName);

    // The focus starts on the acknowledgement: the warning is read first, and Space is the first step.
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        AcknowledgeCheck.Focus(FocusState.Programmatic);

        // The delay of an earlier attempt, or of the unlock screen of the same profile, may still be running.
        ViewModel.RefreshRetryDelay();
        StartCountdownIfNeeded();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => _countdown?.Stop();

    // IsChecked is a bool?, so the checkbox is synced by hand instead of a two-way bind.
    private void OnAcknowledgeChanged(object sender, RoutedEventArgs e) =>
        ViewModel.Acknowledged = AcknowledgeCheck.IsChecked == true;

    private async void OnSaveClick(object sender, RoutedEventArgs e) => await ExportAsync();

    // Enter in the box exports, like the button; it is handled so that nothing else reacts to the key. Without the
    // acknowledgement, while an export runs or during a delay, the key does nothing: the box is not read and keeps its text.
    private async void OnPasswordKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter)
        {
            return;
        }

        e.Handled = true;
        if (CanSubmit(ViewModel.Acknowledged, ViewModel.ExportCommand.IsRunning, ViewModel.RetrySeconds))
        {
            await ExportAsync();
        }
    }

    // The command zeroes the array it receives; the PasswordBox only offers a string, which is emptied here as far as it
    // can be. The finally covers a call the command rejected (for example, one already running). The picker is owned by
    // this window; the command opens it only after the master password was checked.
    private async Task ExportAsync()
    {
        var password = PasswordInput.Password.ToCharArray();
        PasswordInput.Password = string.Empty;
        try
        {
            var picker = WindowsExportFilePicker.ForCsv(XamlRoot.ContentIslandEnvironment.AppWindowId);
            await ViewModel.ExportCommand.ExecuteAsync(new CsvExportRequest(password, picker));
        }
        finally
        {
            Array.Clear(password);
        }

        // A wrong password starts a delay.
        StartCountdownIfNeeded();
    }

    // The view model is synchronous about the clock: this timer asks it to read the clock again while a delay is counting
    // down, and stops by itself when the delay ends.
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
