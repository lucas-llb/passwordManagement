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
        var repository = new ProfileRepository(ResolveVaultsDirectory());

        _window = new MainWindow(navigation);
        _window.Activate();

        navigation.ResetTo(new ProfilesViewModel(repository, navigation));
    }

    // Debug builds only: the E2E tests pass --vaults-dir <absolute folder> so they never touch the real vaults folder.
    // A bad value crashes the launch instead of falling back to the real folder.
    private static string ResolveVaultsDirectory()
    {
#if DEBUG
        var arguments = Environment.GetCommandLineArgs();
        var index = Array.IndexOf(arguments, "--vaults-dir");
        if (index >= 0)
        {
            if (index + 1 >= arguments.Length || !Path.IsPathFullyQualified(arguments[index + 1]))
            {
                throw new InvalidOperationException("--vaults-dir needs an absolute folder path.");
            }

            return arguments[index + 1];
        }
#endif
        return ProfileRepository.GetDefaultDirectory();
    }
}
