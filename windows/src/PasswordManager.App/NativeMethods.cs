using System.Runtime.InteropServices;

namespace PasswordManager.App;

// The Win32 calls the app needs and WinUI does not offer: excluding the window from screen capture (W-14), and the
// window messages behind auto-lock (W-18): the session-lock notification and minimize, read by a window procedure that
// is chained in front of the one WinUI installed.
internal static partial class NativeMethods
{
    public const uint WDA_NONE = 0x0;

    // Windows 10 2004 (19041) or later, the app's minimum: captures skip the window and show what is behind it.
    public const uint WDA_EXCLUDEFROMCAPTURE = 0x11;

    // GetWindowLongPtr/SetWindowLongPtr index of the window procedure.
    public const int GWLP_WNDPROC = -4;

    public const uint WM_SIZE = 0x0005;

    // wParam of WM_SIZE when the window was minimized.
    public const nuint SIZE_MINIMIZED = 1;

    // Sent to a window registered with WTSRegisterSessionNotification; wParam is the WTS_* event.
    public const uint WM_WTSSESSION_CHANGE = 0x02B1;

    public const nuint WTS_CONSOLE_DISCONNECT = 0x2;
    public const nuint WTS_REMOTE_DISCONNECT = 0x4;
    public const nuint WTS_SESSION_LOCK = 0x7;

    // WTSRegisterSessionNotification: only the events of the session the app runs in.
    public const uint NOTIFY_FOR_THIS_SESSION = 0;

    // The search path is set on each method, not on the assembly, so the XAML-generated entry point's own DLL loads
    // stay as they are (CA5392).
    [LibraryImport("user32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowDisplayAffinity(nint hWnd, uint dwAffinity);

    // The app is 64-bit only (x64, ARM64), where user32 exports SetWindowLongPtrW itself.
    [LibraryImport("user32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial nint SetWindowLongPtrW(nint hWnd, int nIndex, nint dwNewLong);

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial nint CallWindowProcW(nint lpPrevWndFunc, nint hWnd, uint msg, nuint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial nint DefWindowProcW(nint hWnd, uint msg, nuint wParam, nint lParam);

    [LibraryImport("wtsapi32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WTSRegisterSessionNotification(nint hWnd, uint dwFlags);

    [LibraryImport("wtsapi32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WTSUnRegisterSessionNotification(nint hWnd);
}
