using System.Runtime.InteropServices;

// Every P/Invoke of this assembly (only NativeMethods has them) loads its DLL from System32 alone (CA5392); the
// attribute is not valid on a class, so it is set for the assembly.
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

namespace PasswordManager.App.E2E.Tests;

// The few Win32 calls the E2E tests need and FlaUI does not offer: the foreground check that guards every keystroke,
// the sequence that brings the app window to the front, the settings broadcast of the theme test, and the display
// affinity, window rect and window move of the capture protection test.
internal static partial class NativeMethods
{
    public const nint HWND_BROADCAST = 0xFFFF;
    public const uint WM_SETTINGCHANGE = 0x001A;
    public const uint SMTO_ABORTIFHUNG = 0x2;
    public const uint WDA_NONE = 0x0;
    public const uint WDA_EXCLUDEFROMCAPTURE = 0x11;
    public const uint SWP_NOSIZE = 0x1;
    public const uint SWP_NOZORDER = 0x4;
    public const uint SWP_NOACTIVATE = 0x10;
    public const int SM_XVIRTUALSCREEN = 76;
    public const int SM_CXVIRTUALSCREEN = 78;

    [LibraryImport("user32.dll")]
    public static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    public static partial uint GetWindowThreadProcessId(nint hWnd, out uint processId);

    [LibraryImport("kernel32.dll")]
    public static partial uint GetCurrentThreadId();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AttachThreadInput(uint idAttach, uint idAttachTo, [MarshalAs(UnmanagedType.Bool)] bool attach);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool BringWindowToTop(nint hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetForegroundWindow(nint hWnd);

    [LibraryImport("user32.dll", EntryPoint = "SendMessageTimeoutW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint SendMessageTimeoutW(
        nint hWnd, uint msg, nuint wParam, string lParam, uint flags, uint timeout, out nuint result);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetWindowDisplayAffinity(nint hWnd, out uint affinity);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetWindowRect(nint hWnd, out RECT rect);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [LibraryImport("user32.dll")]
    public static partial int GetSystemMetrics(int index);

    // A plain SetForegroundWindow is refused when the calling process does not own the foreground; attaching this
    // thread's input to the foreground window's thread lifts that restriction for the duration of the call.
    public static void BringToForeground(nint hWnd)
    {
        var foregroundThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        var currentThread = GetCurrentThreadId();
        var attached = foregroundThread != 0
            && foregroundThread != currentThread
            && AttachThreadInput(currentThread, foregroundThread, attach: true);
        try
        {
            BringWindowToTop(hWnd);
            SetForegroundWindow(hWnd);
        }
        finally
        {
            if (attached)
            {
                AttachThreadInput(currentThread, foregroundThread, attach: false);
            }
        }
    }
}

// A window rect in screen coordinates; physical pixels, as the test process is PerMonitorV2.
[StructLayout(LayoutKind.Sequential)]
internal struct RECT
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;
}
