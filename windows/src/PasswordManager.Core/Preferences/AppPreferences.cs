namespace PasswordManager.Core.Preferences;

/// <summary>
/// App-wide preferences kept in <see cref="PreferencesStore.FileName"/>: the inactivity timeout and lock on minimize.
/// </summary>
/// <remarks>
/// <para>The timeout only takes a value from <see cref="AllowedIdleTimeoutMinutes"/>. There is no "never": an idle
/// session always locks (AGENTS.md §3.5).</para>
/// <para>Preferences are global, not per profile, and hold nothing else (no profile id, path or date).</para>
/// </remarks>
public sealed record AppPreferences
{
    /// <summary>Inactivity timeouts the user can choose, in minutes.</summary>
    public static IReadOnlyList<int> AllowedIdleTimeoutMinutes { get; } = [1, 2, 5, 10, 15, 30, 60];

    /// <summary>Five minutes of inactivity, and no lock on minimize.</summary>
    public static AppPreferences Default { get; } = new(5, false);

    /// <summary>
    /// Creates validated preferences.
    /// </summary>
    /// <param name="idleTimeoutMinutes">One of <see cref="AllowedIdleTimeoutMinutes"/>.</param>
    /// <param name="lockOnMinimize">True to lock the vault when the window is minimized.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="idleTimeoutMinutes"/> is not an allowed value.</exception>
    public AppPreferences(int idleTimeoutMinutes, bool lockOnMinimize)
    {
        if (!AllowedIdleTimeoutMinutes.Contains(idleTimeoutMinutes))
        {
            throw new ArgumentOutOfRangeException(nameof(idleTimeoutMinutes));
        }

        IdleTimeoutMinutes = idleTimeoutMinutes;
        LockOnMinimize = lockOnMinimize;
    }

    /// <summary>Minutes without activity before the vault locks.</summary>
    public int IdleTimeoutMinutes { get; }

    /// <summary>True to lock the vault when the window is minimized.</summary>
    public bool LockOnMinimize { get; }
}
