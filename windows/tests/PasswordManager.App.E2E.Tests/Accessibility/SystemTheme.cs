using System.Globalization;
using Microsoft.Win32;

namespace PasswordManager.App.E2E.Tests.Accessibility;

// The user's real "app mode" setting (Settings > Personalization > Colors). The theme E2E test switches it for a few
// seconds; nothing else in the Personalize key is ever touched. Before the switch, the original value goes to a marker
// file, so a run killed mid-switch is undone by the next one (RestoreInterrupted).
internal static class SystemTheme
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string ValueName = "AppsUseLightTheme";
    private const string Missing = "missing";

    public static string MarkerPath { get; } = Path.Combine(Path.GetTempPath(), "pm-e2e-theme-restore");

    // A missing value means light, as in Windows.
    public static bool AppsUseDarkTheme
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
            return key?.GetValue(ValueName) is int value && value == 0;
        }
    }

    public static IDisposable Override(bool dark)
    {
        if (File.Exists(MarkerPath))
        {
            throw new InvalidOperationException($"An interrupted theme switch is pending; call RestoreInterrupted first ({MarkerPath}).");
        }

        var original = Read();
        File.WriteAllText(MarkerPath, original?.ToString(CultureInfo.InvariantCulture) ?? Missing);
        var restore = new Restore(original);
        try
        {
            Write(dark ? 0 : 1);
            Broadcast();
            return restore;
        }
        catch
        {
            restore.Dispose();
            throw;
        }
    }

    public static void RestoreInterrupted()
    {
        if (!File.Exists(MarkerPath))
        {
            return;
        }

        var text = File.ReadAllText(MarkerPath).Trim();
        int? original = text == Missing ? null : int.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture);
        new Restore(original).Dispose();
    }

    private static int? Read()
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
        return key?.GetValue(ValueName) as int?;
    }

    private static void Write(int? value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath, writable: true);
        if (value is { } v)
        {
            key.SetValue(ValueName, v, RegistryValueKind.DWord);
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }

    // The same notification Settings sends; WinUI re-reads the app mode and repaints with the other theme ramp.
    private static void Broadcast() =>
        _ = NativeMethods.SendMessageTimeoutW(
            NativeMethods.HWND_BROADCAST, NativeMethods.WM_SETTINGCHANGE, 0, "ImmersiveColorSet",
            NativeMethods.SMTO_ABORTIFHUNG, 5000, out _);

    private sealed class Restore(int? original) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Write(original);
            Broadcast();
            File.Delete(MarkerPath);
        }
    }
}
