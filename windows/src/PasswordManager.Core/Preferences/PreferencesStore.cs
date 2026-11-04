using System.Text.Json;
using PasswordManager.Core.Storage;

namespace PasswordManager.Core.Preferences;

/// <summary>
/// Reads and writes <c>settings.json</c>, which sits beside <c>profiles.json</c> in the vaults folder.
/// </summary>
/// <remarks>
/// <para><see cref="Load"/> never throws on a file problem and never writes. A missing, unreadable, oversized or
/// malformed file gives <see cref="AppPreferences.Default"/>. A valid file with an idle timeout outside
/// <see cref="AppPreferences.AllowedIdleTimeoutMinutes"/> keeps its <c>lockOnMinimize</c> and gets the default timeout.
/// A bad file stays on disk until the user changes a setting.</para>
/// <para><see cref="Save"/> replaces the file atomically and leaves no <c>.bak</c>. The folder must exist.</para>
/// </remarks>
public static class PreferencesStore
{
    /// <summary>Name of the preferences file inside the vaults folder.</summary>
    public const string FileName = "settings.json";

    /// <summary>Largest file <see cref="Load"/> reads, in bytes. A bigger file is treated as corrupt.</summary>
    public const int MaxFileBytes = 4 * 1024;

    /// <summary>
    /// Reads the preferences of a vaults folder.
    /// </summary>
    /// <param name="vaultsDirectory">The vaults folder. It may not exist.</param>
    /// <returns>The stored preferences, or the defaults for any problem with the file.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="vaultsDirectory"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="vaultsDirectory"/> is empty.</exception>
    public static AppPreferences Load(string vaultsDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(vaultsDirectory);

        byte[]? bytes;
        try
        {
            bytes = ReadBounded(Path.Combine(vaultsDirectory, FileName));
        }
        catch (IOException)
        {
            return AppPreferences.Default;
        }
        catch (UnauthorizedAccessException)
        {
            return AppPreferences.Default;
        }

        if (bytes is null)
        {
            return AppPreferences.Default;
        }

        PreferencesFile? file;
        try
        {
            file = JsonSerializer.Deserialize(bytes, PreferencesJsonContext.Default.PreferencesFile);
        }
        catch (JsonException)
        {
            // No inner exception: its message can hold file content.
            return AppPreferences.Default;
        }

        if (file is null)
        {
            return AppPreferences.Default;
        }

        var idle = AppPreferences.AllowedIdleTimeoutMinutes.Contains(file.IdleTimeoutMinutes)
            ? file.IdleTimeoutMinutes
            : AppPreferences.Default.IdleTimeoutMinutes;

        return new AppPreferences(idle, file.LockOnMinimize);
    }

    /// <summary>
    /// Writes the preferences of a vaults folder, replacing the file atomically.
    /// </summary>
    /// <param name="vaultsDirectory">The vaults folder. It must exist; it is not created.</param>
    /// <param name="preferences">The preferences to store.</param>
    /// <exception cref="ArgumentNullException"><paramref name="vaultsDirectory"/> or <paramref name="preferences"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="vaultsDirectory"/> is empty.</exception>
    /// <exception cref="IOException">The write failed; the previous file is unchanged.</exception>
    /// <exception cref="UnauthorizedAccessException">The write was denied; the previous file is unchanged.</exception>
    public static void Save(string vaultsDirectory, AppPreferences preferences)
    {
        ArgumentException.ThrowIfNullOrEmpty(vaultsDirectory);
        ArgumentNullException.ThrowIfNull(preferences);

        var file = new PreferencesFile
        {
            IdleTimeoutMinutes = preferences.IdleTimeoutMinutes,
            LockOnMinimize = preferences.LockOnMinimize,
        };

        var bytes = JsonSerializer.SerializeToUtf8Bytes(file, PreferencesJsonContext.Default.PreferencesFile);
        AtomicFile.Write(Path.Combine(vaultsDirectory, FileName), bytes, keepBackup: false);
    }

    /// <summary>
    /// Reads a file of at most <see cref="MaxFileBytes"/> bytes. It opens the file with <see cref="FileShare.Read"/>,
    /// so a file another process holds exclusively is reported as an error.
    /// </summary>
    /// <returns>The bytes, or null when the file is larger than <see cref="MaxFileBytes"/>.</returns>
    private static byte[]? ReadBounded(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaxFileBytes)
        {
            return null;
        }

        var bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        return bytes;
    }
}
