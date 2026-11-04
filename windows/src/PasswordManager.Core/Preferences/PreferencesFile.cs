namespace PasswordManager.Core.Preferences;

/// <summary>
/// On-disk shape of <see cref="PreferencesStore.FileName"/>. A missing key keeps its default; unknown keys are ignored.
/// </summary>
internal sealed class PreferencesFile
{
    public int IdleTimeoutMinutes { get; set; } = AppPreferences.Default.IdleTimeoutMinutes;

    public bool LockOnMinimize { get; set; } = AppPreferences.Default.LockOnMinimize;
}
