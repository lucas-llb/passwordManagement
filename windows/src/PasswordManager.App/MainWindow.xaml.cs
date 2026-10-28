using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.Windows.ApplicationModel.Resources;
using PasswordManager.App.Views;
using PasswordManager.ViewModels;
using PasswordManager.ViewModels.Navigation;

namespace PasswordManager.App;

public sealed partial class MainWindow : Window
{
    private readonly INavigationService _navigation;
    private readonly nint _hwnd;
    private bool _unprotectPending;

    public MainWindow(INavigationService navigation)
    {
        ArgumentNullException.ThrowIfNull(navigation);

        InitializeComponent();
        Title = new ResourceLoader().GetString("AppTitle");

        // The title bar follows the system app mode too, live, like the XAML content.
        AppWindow.TitleBar.PreferredTheme = TitleBarTheme.UseDefaultAppMode;
        _hwnd = Win32Interop.GetWindowFromWindowId(AppWindow.Id);

        _navigation = navigation;
        _navigation.Navigated += OnNavigated;
        Closed += OnClosed;
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _navigation.Navigated -= OnNavigated;
        CompositionTarget.Rendered -= OnRenderedUnprotect;
    }

    // One typeof literal per Frame.Navigate call keeps the trim analyzer able to see which page types are used.
    private void OnNavigated(object? sender, EventArgs e)
    {
        // Fail-closed: a page is excluded from screen capture unless it says it shows nothing from an open vault (W-14).
        var allow = _navigation.Current?.AllowsScreenCapture ?? false;

        // Protect before Frame.Navigate, so a vault page is never rendered while the window can be captured.
        if (!allow)
        {
            Protect();
        }

        switch (_navigation.Current)
        {
            case ProfilesViewModel profiles:
                RootFrame.Navigate(typeof(ProfilesPage), profiles);
                break;
            case CreateProfileViewModel create:
                RootFrame.Navigate(typeof(CreateProfilePage), create);
                break;
            case UnlockViewModel unlock:
                RootFrame.Navigate(typeof(UnlockPage), unlock);
                break;
            case EntryListViewModel list:
                RootFrame.Navigate(typeof(EntryListPage), list);
                break;
            case EntryDetailViewModel detail:
                RootFrame.Navigate(typeof(EntryDetailPage), detail);
                break;
            default:
                throw new InvalidOperationException("No page is registered for the current ViewModel.");
        }

        if (allow)
        {
            ScheduleUnprotect();
        }
    }

    // If the window cannot be excluded, the vault page must not be shown: the exception stops the navigation.
    private void Protect()
    {
        if (!NativeMethods.SetWindowDisplayAffinity(_hwnd, NativeMethods.WDA_EXCLUDEFROMCAPTURE))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
    }

    // Unprotect only after the first frame rendered with the new page, so the last frame of a vault page (still on
    // screen until then) can never be captured.
    private void ScheduleUnprotect()
    {
        if (_unprotectPending)
        {
            return;
        }

        _unprotectPending = true;
        CompositionTarget.Rendered += OnRenderedUnprotect;
    }

    private void OnRenderedUnprotect(object? sender, RenderedEventArgs e)
    {
        CompositionTarget.Rendered -= OnRenderedUnprotect;
        _unprotectPending = false;

        // A protected page may have come in since the unprotect was scheduled; it keeps the window excluded.
        if (_navigation.Current?.AllowsScreenCapture == true)
        {
            // A failure leaves the window excluded, which is the safe state, so it is ignored.
            _ = NativeMethods.SetWindowDisplayAffinity(_hwnd, NativeMethods.WDA_NONE);
        }
    }
}
