using Microsoft.UI.Xaml;
using PasswordManager.Core.Profiles;
using PasswordManager.ViewModels;
using PasswordManager.ViewModels.Navigation;

namespace PasswordManager.App;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
    }

    // Composition root: there is no DI container; the few services are created here and passed by constructor.
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var navigation = new NavigationService();
        var repository = new ProfileRepository(ProfileRepository.GetDefaultDirectory());

        _window = new MainWindow(navigation);
        _window.Activate();

        navigation.ResetTo(new ProfilesViewModel(repository, navigation));
    }
}
