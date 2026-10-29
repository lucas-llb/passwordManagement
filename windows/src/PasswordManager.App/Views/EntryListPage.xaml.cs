using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.Windows.ApplicationModel.Resources;
using PasswordManager.Core.Vault;
using PasswordManager.ViewModels;

namespace PasswordManager.App.Views;

public sealed partial class EntryListPage : Page
{
    // The folder names behind the combo box items 1..n; item 0 is "all folders".
    private IReadOnlyList<string> _folders = [];

    public EntryListPage()
    {
        InitializeComponent();
    }

    public EntryListViewModel ViewModel { get; private set; } = null!;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel = (EntryListViewModel)e.Parameter;
        ShowFolders();
    }

    // The list gets the keyboard focus when it has something to show; with nothing listed (an empty vault, or a search that
    // matches nothing) focus starts on the Lock button. The enqueue lets the list build its containers first.
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (ViewModel.FilteredEntries.Count == 0)
        {
            LockButton.Focus(FocusState.Programmatic);
        }
        else
        {
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => EntriesList.Focus(FocusState.Programmatic));
        }
    }

    // A screen reader reads the name of an item container, not the ToString() of the entry behind it.
    private static void OnEntryContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (!args.InRecycleQueue && args.Item is VaultEntry entry)
        {
            AutomationProperties.SetName(args.ItemContainer, entry.Title);
        }
    }

    private void OnEntryClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is VaultEntry entry)
        {
            ViewModel.OpenEntryCommand.Execute(entry);
        }
    }

    // Plain strings keep the accessible name of each item equal to its text. Item 0 is "all folders" and the others are the
    // vault's folders in view-model order; the folder chosen before the detail page is selected again.
    private void ShowFolders()
    {
        _folders = ViewModel.Folders;
        List<string> options = [new ResourceLoader().GetString("EntriesFolderAll"), .. _folders];
        var selected = ViewModel.SelectedFolder is { } folder ? options.IndexOf(folder, 1) : -1;
        FolderCombo.ItemsSource = options;
        FolderCombo.SelectedIndex = Math.Max(selected, 0);
    }

    private void OnFolderSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var index = FolderCombo.SelectedIndex;
        if (index >= 0)
        {
            ViewModel.SelectedFolder = index == 0 ? null : _folders[index - 1];
        }
    }
}
