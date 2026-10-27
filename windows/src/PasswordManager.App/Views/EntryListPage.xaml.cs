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

    private void OnEntryClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is VaultEntry entry)
        {
            ViewModel.OpenEntryCommand.Execute(entry);
        }
    }
}
