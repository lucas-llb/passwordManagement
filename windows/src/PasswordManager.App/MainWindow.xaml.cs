using Microsoft.UI.Xaml;
using Microsoft.Windows.ApplicationModel.Resources;

namespace PasswordManager.App;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Title = new ResourceLoader().GetString("AppTitle");
    }
}
