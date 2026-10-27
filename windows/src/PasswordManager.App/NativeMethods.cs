using System.Runtime.InteropServices;

namespace PasswordManager.App;

// The one Win32 call the app needs and WinUI does not offer: excluding the window from screen capture (W-14).
internal static partial class NativeMethods
{
    public const uint WDA_NONE = 0x0;

    // Windows 10 2004 (19041) or later, the app's minimum: captures skip the window and show what is behind it.
    public const uint WDA_EXCLUDEFROMCAPTURE = 0x11;

    // The search path is set on the method, not on the assembly, so the XAML-generated entry point's own DLL loads
    // stay as they are (CA5392).
    [LibraryImport("user32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowDisplayAffinity(nint hWnd, uint dwAffinity);
}
