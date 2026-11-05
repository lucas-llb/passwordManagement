using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.Windows.ApplicationModel.Resources;
using PasswordManager.App.Views;
using PasswordManager.ViewModels;
using PasswordManager.ViewModels.Navigation;
using DataTransferClipboard = Windows.ApplicationModel.DataTransfer.Clipboard;

namespace PasswordManager.App;

public sealed partial class MainWindow : Window
{
    private static readonly TimeSpan IdleCheckInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ClipboardCheckInterval = TimeSpan.FromSeconds(1);

    // The marker WindowsClipboard puts on every guarded copy; nothing else in the app sets it.
    private const string GuardedCopyFormat = "ExcludeClipboardContentFromMonitorProcessing";

    // Relative to the app folder, which is also the install folder once packaged.
    private const string AppIconPath = "Assets/AppIcon.ico";

    // The window procedure is a static native callback, so it reaches the one window through these. The previous
    // procedure is kept after close, so a message already in flight still chains to it.
    private static MainWindow? s_instance;
    private static nint s_previousWindowProc;

    private readonly INavigationService _navigation;
    private readonly AutoLock _autoLock;
    private readonly ClipboardGuard _clipboard;
    private readonly nint _hwnd;
    private readonly DispatcherQueue _dispatcher;
    private readonly DispatcherQueueTimer _idleTimer;
    private readonly DispatcherQueueTimer _clipboardTimer;

    // Created once, so the window procedure only enqueues and never allocates.
    private readonly DispatcherQueueHandler _sessionLockedHandler;
    private readonly DispatcherQueueHandler _minimizedHandler;
    private readonly KeyEventHandler _keyActivityHandler;
    private readonly PointerEventHandler _pointerActivityHandler;
    private bool _unprotectPending;

    public MainWindow(INavigationService navigation, AutoLock autoLock, ClipboardGuard clipboard)
    {
        ArgumentNullException.ThrowIfNull(navigation);
        ArgumentNullException.ThrowIfNull(autoLock);
        ArgumentNullException.ThrowIfNull(clipboard);

        InitializeComponent();
        Title = new ResourceLoader().GetString("AppTitle");
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, AppIconPath));

        // The title bar follows the system app mode too, live, like the XAML content.
        AppWindow.TitleBar.PreferredTheme = TitleBarTheme.UseDefaultAppMode;
        _hwnd = Win32Interop.GetWindowFromWindowId(AppWindow.Id);

        _navigation = navigation;
        _autoLock = autoLock;
        _clipboard = clipboard;
        _dispatcher = DispatcherQueue;
        _sessionLockedHandler = _autoLock.OnSessionLocked;
        _minimizedHandler = _autoLock.OnMinimized;

        // Activity is input to this window only (W-18): input to other apps does not keep the vault open. Handled
        // events count too, so a control that consumes a key or a click still counts as input.
        _keyActivityHandler = OnKeyActivity;
        _pointerActivityHandler = OnPointerActivity;
        RootFrame.AddHandler(UIElement.KeyDownEvent, _keyActivityHandler, handledEventsToo: true);
        RootFrame.AddHandler(UIElement.PointerPressedEvent, _pointerActivityHandler, handledEventsToo: true);
        RootFrame.AddHandler(UIElement.PointerMovedEvent, _pointerActivityHandler, handledEventsToo: true);
        RootFrame.AddHandler(UIElement.PointerWheelChangedEvent, _pointerActivityHandler, handledEventsToo: true);

        // The idle timeout is checked once a second, and only while a vault is open.
        _idleTimer = _dispatcher.CreateTimer();
        _idleTimer.Interval = IdleCheckInterval;
        _idleTimer.IsRepeating = true;
        _idleTimer.Tick += OnIdleTick;
        _autoLock.PropertyChanged += OnAutoLockChanged;
        UpdateIdleTimer();

        // The copied password is cleared by the guard, which this timer asks once a second, and only while a copy waits (W-22).
        _clipboardTimer = _dispatcher.CreateTimer();
        _clipboardTimer.Interval = ClipboardCheckInterval;
        _clipboardTimer.IsRepeating = true;
        _clipboardTimer.Tick += OnClipboardTick;
        _clipboard.PropertyChanged += OnClipboardChanged;
        UpdateClipboardTimer();

        InstallWindowProc();

        _navigation.Navigated += OnNavigated;
        Closed += OnClosed;
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        ClearPendingCopy();
        UninstallWindowProc();

        _idleTimer.Stop();
        _idleTimer.Tick -= OnIdleTick;
        _autoLock.PropertyChanged -= OnAutoLockChanged;

        _clipboardTimer.Stop();
        _clipboardTimer.Tick -= OnClipboardTick;
        _clipboard.PropertyChanged -= OnClipboardChanged;

        RootFrame.RemoveHandler(UIElement.KeyDownEvent, _keyActivityHandler);
        RootFrame.RemoveHandler(UIElement.PointerPressedEvent, _pointerActivityHandler);
        RootFrame.RemoveHandler(UIElement.PointerMovedEvent, _pointerActivityHandler);
        RootFrame.RemoveHandler(UIElement.PointerWheelChangedEvent, _pointerActivityHandler);

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
            case ExportViewModel export:
                RootFrame.Navigate(typeof(ExportPage), export);
                break;
            case CsvExportViewModel csvExport:
                RootFrame.Navigate(typeof(CsvExportPage), csvExport);
                break;
            case ImportViewModel import:
                RootFrame.Navigate(typeof(ImportPage), import);
                break;
            case RecoveryKitViewModel kit:
                RootFrame.Navigate(typeof(RecoveryKitPage), kit);
                break;
            case SettingsViewModel settings:
                RootFrame.Navigate(typeof(SettingsPage), settings);
                break;
            case NewRecoveryKitViewModel newKit:
                RootFrame.Navigate(typeof(NewRecoveryKitPage), newKit);
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

    private void OnKeyActivity(object sender, KeyRoutedEventArgs e) => _autoLock.ReportActivity();

    private void OnPointerActivity(object sender, PointerRoutedEventArgs e) => _autoLock.ReportActivity();

    private void OnIdleTick(DispatcherQueueTimer sender, object args) => _autoLock.CheckIdle();

    private void OnAutoLockChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AutoLock.IsVaultOpen))
        {
            UpdateIdleTimer();
        }
    }

    private void UpdateIdleTimer()
    {
        if (_autoLock.IsVaultOpen)
        {
            _idleTimer.Start();
        }
        else
        {
            _idleTimer.Stop();
        }
    }

    // The guard resolves a copy on its own (clears it, or finds it gone), so the tick only has to ask. It never throws
    // because the clipboard is busy; anything else is a bug and is left to crash.
    private async void OnClipboardTick(DispatcherQueueTimer sender, object args) => await _clipboard.CheckAsync();

    private void OnClipboardChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ClipboardGuard.PendingCopy))
        {
            UpdateClipboardTimer();
        }
    }

    // On close, a copy still waiting would outlive the app: the unflushed content leaves with it, but a format another
    // program has already pasted (rendered) stays on the clipboard. So it is cleared here, synchronously, and only if
    // the clipboard still holds it: the content is still this process's, and carries the marker only the guard sets
    // (a plain Ctrl+C in another field of the app has none). A busy clipboard is left as it is; nothing else is caught.
    private void ClearPendingCopy()
    {
        if (_clipboard.PendingCopy == 0)
        {
            return;
        }

        var owner = GetClipboardOwner();
        if (owner == nint.Zero || GetWindowThreadProcessId(owner, out var ownerProcess) == 0
            || ownerProcess != (uint)Environment.ProcessId)
        {
            return;
        }

        try
        {
            if (DataTransferClipboard.GetContent().Contains(GuardedCopyFormat))
            {
                DataTransferClipboard.Clear();
            }
        }
        catch (COMException)
        {
        }
    }

    private void UpdateClipboardTimer()
    {
        if (_clipboard.PendingCopy != 0)
        {
            _clipboardTimer.Start();
        }
        else
        {
            _clipboardTimer.Stop();
        }
    }

    // Session lock and minimize arrive as messages to the top-level window, which WinUI does not surface (W-18). A
    // window procedure is chained in front of WinUI's, with user32 only, so there is no dependency on comctl32 v6 as
    // SetWindowSubclass would have. Both steps fail closed: the app does not start without them.
    private unsafe void InstallWindowProc()
    {
        if (!NativeMethods.WTSRegisterSessionNotification(_hwnd, NativeMethods.NOTIFY_FOR_THIS_SESSION))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        s_instance = this;
        delegate* unmanaged[Stdcall]<nint, uint, nuint, nint, nint> windowProc = &WindowProc;
        var previous = NativeMethods.SetWindowLongPtrW(_hwnd, NativeMethods.GWLP_WNDPROC, (nint)windowProc);
        if (previous == 0)
        {
            var error = Marshal.GetLastPInvokeError();
            s_instance = null;
            _ = NativeMethods.WTSUnRegisterSessionNotification(_hwnd);
            throw new Win32Exception(error);
        }

        s_previousWindowProc = previous;
    }

    // From here on messages go straight to WinUI's procedure; one already in flight still chains to it.
    private void UninstallWindowProc()
    {
        s_instance = null;
        _ = NativeMethods.SetWindowLongPtrW(_hwnd, NativeMethods.GWLP_WNDPROC, s_previousWindowProc);
        _ = NativeMethods.WTSUnRegisterSessionNotification(_hwnd);
    }

    // A managed exception escaping a native callback ends the process. So this only reads the message, enqueues the
    // work on the UI thread's queue (navigation never runs inside the message) and chains to WinUI's procedure.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint WindowProc(nint hwnd, uint message, nuint wParam, nint lParam)
    {
        if (s_instance is { } window)
        {
            if (message == NativeMethods.WM_WTSSESSION_CHANGE && IsSessionLock(wParam))
            {
                _ = window._dispatcher.TryEnqueue(DispatcherQueuePriority.High, window._sessionLockedHandler);
            }
            else if (message == NativeMethods.WM_SIZE && wParam == NativeMethods.SIZE_MINIMIZED)
            {
                _ = window._dispatcher.TryEnqueue(DispatcherQueuePriority.High, window._minimizedHandler);
            }
        }

        var previous = s_previousWindowProc;
        return previous != 0
            ? NativeMethods.CallWindowProcW(previous, hwnd, message, wParam, lParam)
            : NativeMethods.DefWindowProcW(hwnd, message, wParam, lParam);
    }

    // The session locked, or its console or remote connection went away: either way the user is no longer at the app.
    private static bool IsSessionLock(nuint wtsEvent) =>
        wtsEvent is NativeMethods.WTS_SESSION_LOCK
            or NativeMethods.WTS_CONSOLE_DISCONNECT
            or NativeMethods.WTS_REMOTE_DISCONNECT;

    // The clipboard checks of ClearPendingCopy, which runs on close. They live here rather than in NativeMethods because
    // only this window needs them.
    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint GetClipboardOwner();

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint GetWindowThreadProcessId(nint hWnd, out uint processId);
}
