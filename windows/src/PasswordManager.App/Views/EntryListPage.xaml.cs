using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using PasswordManager.Core.Vault;
using PasswordManager.ViewModels;

namespace PasswordManager.App.Views;

public sealed partial class EntryListPage : Page
{
    public EntryListPage()
    {
        InitializeComponent();
    }

    public EntryListViewModel ViewModel { get; private set; } = null!;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel = (EntryListViewModel)e.Parameter;
    }

    // The list gets the keyboard focus when there is something in it; an empty vault leaves it on the Lock button.
    // The enqueue lets the list build its containers first.
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (ViewModel.IsEmpty)
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
}
