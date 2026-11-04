using PasswordManager.Core.Preferences;

namespace PasswordManager.ViewModels;

/// <summary>
/// The app-wide preferences in use: it loads <see cref="PreferencesStore.FileName"/> once at startup, hands out the current values, and
/// saves a change. One instance is created by the app and passed by constructor, like <see cref="UnlockThrottle"/>.
/// </summary>
/// <remarks>
/// <para>A change applies at once: <see cref="Update"/> sets <see cref="Current"/>, raises <see cref="Changed"/>
/// (the app wires it to <see cref="AutoLock.ApplyOptions"/>) and only then saves. A save that fails does not undo the
/// change: the value stays until the app closes, and <see cref="Update"/> reports the failure.</para>
/// <para>It has no thread affinity, like <see cref="AutoLock"/>: callers (the UI thread) serialize the calls.</para>
/// </remarks>
public sealed class AppSettings
{
    private readonly string _vaultsDirectory;

    /// <summary>
    /// Loads the preferences of a vaults folder. A missing or bad file gives <see cref="AppPreferences.Default"/> and
    /// is left as it is (see <see cref="PreferencesStore.Load"/>).
    /// </summary>
    /// <param name="vaultsDirectory">The vaults folder that holds <see cref="PreferencesStore.FileName"/>; the app passes
    /// <c>ProfileRepository.VaultsDirectory</c>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="vaultsDirectory"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="vaultsDirectory"/> is empty.</exception>
    public AppSettings(string vaultsDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(vaultsDirectory);

        _vaultsDirectory = vaultsDirectory;
        Current = PreferencesStore.Load(vaultsDirectory);
    }

    /// <summary>The preferences in effect: the loaded ones, or the last <see cref="Update"/>.</summary>
    public AppPreferences Current { get; private set; }

    /// <summary>The auto-lock options that <see cref="Current"/> stands for.</summary>
    public AutoLockOptions AutoLockOptions => new(TimeSpan.FromMinutes(Current.IdleTimeoutMinutes), Current.LockOnMinimize);

    /// <summary>Raised by <see cref="Update"/> after <see cref="Current"/> changed, before the save.</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// Makes <paramref name="preferences"/> the current ones and saves them. Preferences equal to <see cref="Current"/>
    /// are ignored: nothing is raised and nothing is written.
    /// </summary>
    /// <param name="preferences">The new preferences.</param>
    /// <returns>True when the change was saved (or there was nothing to save); false when the file could not be
    /// written. The value still applies until the app closes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="preferences"/> is null.</exception>
    public bool Update(AppPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);

        if (preferences == Current)
        {
            return true;
        }

        Current = preferences;
        Changed?.Invoke(this, EventArgs.Empty);

        try
        {
            PreferencesStore.Save(_vaultsDirectory, preferences);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
