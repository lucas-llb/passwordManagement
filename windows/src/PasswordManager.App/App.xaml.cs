#if DEBUG
using System.Globalization;
#endif
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
        var throttle = new UnlockThrottle(TimeProvider.System);

        // Before the window, so it sees every navigation; nothing is persisted yet (the settings screen comes later).
        var autoLock = new AutoLock(navigation, TimeProvider.System, ResolveAutoLockOptions());

        _window = new MainWindow(navigation, autoLock);
        _window.Activate();

        navigation.ResetTo(new ProfilesViewModel(repository, navigation, throttle));
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

    // Debug builds only: the E2E tests pass --idle-timeout-seconds <n> (n > 0) and --lock-on-minimize so they do not
    // wait 5 minutes. A bad value crashes the launch instead of falling back to the defaults.
    private static AutoLockOptions ResolveAutoLockOptions()
    {
#if DEBUG
        var arguments = Environment.GetCommandLineArgs();
        var idleTimeout = AutoLockOptions.Default.IdleTimeout;
        var index = Array.IndexOf(arguments, "--idle-timeout-seconds");
        if (index >= 0)
        {
            if (index + 1 >= arguments.Length
                || !int.TryParse(arguments[index + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
                || seconds <= 0)
            {
                throw new InvalidOperationException("--idle-timeout-seconds needs a whole number of seconds above zero.");
            }

            idleTimeout = TimeSpan.FromSeconds(seconds);
        }

        return new AutoLockOptions(idleTimeout, Array.IndexOf(arguments, "--lock-on-minimize") >= 0);
#else
        return AutoLockOptions.Default;
#endif
    }
}
