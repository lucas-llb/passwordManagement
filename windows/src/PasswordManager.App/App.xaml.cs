#if DEBUG
using System.Globalization;
#endif
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using PasswordManager.Core.Profiles;
using PasswordManager.ViewModels;
using PasswordManager.ViewModels.Navigation;
using Windows.Storage;

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
        var settings = new AppSettings(repository.VaultsDirectory);

        // Before the window, so it sees every navigation. The saved preferences set it; the Debug switches
        // override them at startup only, and a change on the settings page replaces the options through settings.Changed.
        var autoLock = new AutoLock(navigation, TimeProvider.System, ResolveAutoLockOptions(settings.AutoLockOptions));
        settings.Changed += (_, _) => autoLock.ApplyOptions(settings.AutoLockOptions);

        // Before the window, which runs its timer, and shared with the pages, which copy through it (W-22).
        var clipboard = new ClipboardGuard(new WindowsClipboard(), TimeProvider.System, ResolveClipboardClearAfter());

        _window = new MainWindow(navigation, autoLock, clipboard);
        _window.Activate();

        navigation.ResetTo(new ProfilesViewModel(repository, navigation, throttle, clipboard, settings));
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
        return ProfileRepository.GetDefaultDirectory(GetPackagedLocalFolder());
    }

    // The packaged app keeps its vaults under ApplicationData.Current.LocalFolder; a loose exe (all E2E launches) has no
    // package identity and the call throws, so null keeps the unpackaged default. The path is never logged.
    // Debug builds only: --packaged-local-folder <absolute folder> stands in for LocalFolder so the E2E tests exercise
    // the packaged branch without installing the package. It is validated exactly like --vaults-dir.
    private static string? GetPackagedLocalFolder()
    {
#if DEBUG
        var arguments = Environment.GetCommandLineArgs();
        var index = Array.IndexOf(arguments, "--packaged-local-folder");
        if (index >= 0)
        {
            if (index + 1 >= arguments.Length || !Path.IsPathFullyQualified(arguments[index + 1]))
            {
                throw new InvalidOperationException("--packaged-local-folder needs an absolute folder path.");
            }

            return arguments[index + 1];
        }
#endif
        try
        {
            return ApplicationData.Current.LocalFolder.Path;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (COMException)
        {
            return null;
        }
    }

    // Debug builds only: the E2E tests pass --idle-timeout-seconds <n> (n > 0) and --lock-on-minimize so they do not
    // wait 5 minutes. They override the saved preferences at startup only: without a switch the saved value stands. A bad
    // value crashes the launch instead of falling back to the saved preferences.
    private static AutoLockOptions ResolveAutoLockOptions(AutoLockOptions saved)
    {
#if DEBUG
        var arguments = Environment.GetCommandLineArgs();
        var idleTimeout = saved.IdleTimeout;
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

        return new AutoLockOptions(idleTimeout, saved.LockOnMinimize || Array.IndexOf(arguments, "--lock-on-minimize") >= 0);
#else
        return saved;
#endif
    }

    // Debug builds only: the E2E tests pass --clipboard-clear-seconds <n> (n > 0) so they do not wait 30 seconds. A bad
    // value crashes the launch instead of falling back to the default.
    private static TimeSpan ResolveClipboardClearAfter()
    {
#if DEBUG
        var arguments = Environment.GetCommandLineArgs();
        var index = Array.IndexOf(arguments, "--clipboard-clear-seconds");
        if (index >= 0)
        {
            if (index + 1 >= arguments.Length
                || !int.TryParse(arguments[index + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
                || seconds <= 0)
            {
                throw new InvalidOperationException("--clipboard-clear-seconds needs a whole number of seconds above zero.");
            }

            return TimeSpan.FromSeconds(seconds);
        }
#endif
        return ClipboardGuard.DefaultClearAfter;
    }
}
