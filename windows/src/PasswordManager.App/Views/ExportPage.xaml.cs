using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.Windows.ApplicationModel.Resources;
using PasswordManager.ViewModels;
using VirtualKey = Windows.System.VirtualKey;

namespace PasswordManager.App.Views;

public sealed partial class ExportPage : Page
{
    public ExportPage()
    {
        InitializeComponent();
    }

    public ExportViewModel ViewModel { get; private set; } = null!;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel = (ExportViewModel)e.Parameter;
    }

    // The messages are shown through x:Bind functions on the password error and on the outcome.
    public static Visibility IsTooShort(NewPasswordError error) =>
        error == NewPasswordError.TooShort ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility IsMismatch(NewPasswordError error) =>
        error == NewPasswordError.Mismatch ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility IsSaved(ExportOutcome outcome) =>
        outcome == ExportOutcome.Saved ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility IsFailed(ExportOutcome outcome) =>
        outcome == ExportOutcome.Failed ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility IsVaultsFolder(ExportOutcome outcome) =>
        outcome == ExportOutcome.VaultsFolder ? Visibility.Visible : Visibility.Collapsed;

    public static bool IsNot(bool value) => !value;

    // The confirmation text: the view model only exposes the file name, the sentence and its format live in the resw.
    public static string DoneText(string? fileName) =>
        string.IsNullOrEmpty(fileName)
            ? string.Empty
            : string.Format(CultureInfo.CurrentCulture, new ResourceLoader().GetString("ExportDone"), fileName);

    private void OnLoaded(object sender, RoutedEventArgs e) => PasswordInput.Focus(FocusState.Programmatic);

    private async void OnSaveClick(object sender, RoutedEventArgs e) => await ExportAsync();

    // Enter in the confirmation exports, like the button; it is handled so that nothing else reacts to the key. While an
    // export runs, the key does nothing: the boxes are not read and keep their text.
    private async void OnConfirmKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter)
        {
            return;
        }

        e.Handled = true;
        if (!ViewModel.ExportCommand.IsRunning)
        {
            await ExportAsync();
        }
    }

    // The command zeroes both arrays it receives; the PasswordBoxes only offer strings, which are emptied here as far as
    // they can be. The finally covers a call the command rejected (for example, one already running). The picker is
    // owned by this window; the command opens it only after the password rule passed.
    private async Task ExportAsync()
    {
        var password = PasswordInput.Password.ToCharArray();
        var confirmation = ConfirmInput.Password.ToCharArray();
        PasswordInput.Password = string.Empty;
        ConfirmInput.Password = string.Empty;
        try
        {
            var picker = WindowsExportFilePicker.ForVault(XamlRoot.ContentIslandEnvironment.AppWindowId);
            await ViewModel.ExportCommand.ExecuteAsync(new ExportRequest(password, confirmation, picker));
        }
        finally
        {
            Array.Clear(password);
            Array.Clear(confirmation);
        }
    }
}
