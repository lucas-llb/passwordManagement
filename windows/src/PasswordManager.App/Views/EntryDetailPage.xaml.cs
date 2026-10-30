using System.Globalization;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.Windows.ApplicationModel.Resources;
using PasswordManager.ViewModels;

namespace PasswordManager.App.Views;

public sealed partial class EntryDetailPage : Page
{
    // ShowAsync throws when a second ContentDialog opens, so a click while one is showing is ignored.
    private bool _dialogOpen;

    // Set once the page is left (Back, a delete, or a lock), so a dialog that closes with the page does not take the focus.
    private bool _leaving;

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

    // An automatic lock (or any other navigation) must never leave the delete dialog over the next page. Hiding it ends
    // OnDeleteClick's wait with None, which counts as Cancel.
    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _leaving = true;
        DetailDeleteDialog.Hide();
        base.OnNavigatedFrom(e);
    }

    // The dates are local time, in the resw format (current culture). A new entry has none yet.
    public static string CreatedText(DateTimeOffset? value) =>
        value is { } time
            ? string.Format(CultureInfo.CurrentCulture, new ResourceLoader().GetString("DetailCreatedAt"), time.LocalDateTime)
            : string.Empty;

    public static string UpdatedText(DateTimeOffset? value) =>
        value is { } time
            ? string.Format(CultureInfo.CurrentCulture, new ResourceLoader().GetString("DetailUpdatedAt"), time.LocalDateTime)
            : string.Empty;

    // The dates and the delete button belong to a saved entry only.
    public static Visibility ShowWhenSaved(bool isNew) => isNew ? Visibility.Collapsed : Visibility.Visible;

    private void OnLoaded(object sender, RoutedEventArgs e) => DetailTitle.Focus(FocusState.Programmatic);

    private async void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (_dialogOpen || !ViewModel.RequestDeleteCommand.CanExecute(null))
        {
            return;
        }

        ViewModel.RequestDeleteCommand.Execute(null);
        var result = await ShowDeleteDialogAsync();
        if (result == ContentDialogResult.Primary)
        {
            ViewModel.ConfirmDeleteCommand.Execute(null);
        }
        else
        {
            ViewModel.CancelDeleteCommand.Execute(null);
        }

        FocusDelete();
    }

    private async Task<ContentDialogResult> ShowDeleteDialogAsync()
    {
        _dialogOpen = true;
        try
        {
            DetailDeleteDialog.XamlRoot = XamlRoot;
            return await DetailDeleteDialog.ShowAsync();
        }
        finally
        {
            _dialogOpen = false;
        }
    }

    // After a cancel (or a delete that could not be written) the focus goes back to the button that opened the dialog.
    // A delete that succeeded has left the page, and so has a lock; neither takes the focus.
    private void FocusDelete()
    {
        if (_leaving)
        {
            return;
        }

        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => DetailDelete.Focus(FocusState.Programmatic));
    }
}
