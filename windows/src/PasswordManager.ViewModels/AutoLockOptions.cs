namespace PasswordManager.ViewModels;

/// <summary>
/// When <see cref="AutoLock"/> locks an open vault. Nothing is persisted yet: the app builds the options at startup
/// (the settings screen comes later).
/// </summary>
public sealed record AutoLockOptions
{
    /// <param name="idleTimeout">How long the app window may go without input before the vault locks.</param>
    /// <param name="lockOnMinimize">Whether minimizing the window locks the vault.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="idleTimeout"/> is zero or negative.</exception>
    public AutoLockOptions(TimeSpan idleTimeout, bool lockOnMinimize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(idleTimeout, TimeSpan.Zero);

        IdleTimeout = idleTimeout;
        LockOnMinimize = lockOnMinimize;
    }

    /// <summary>The defaults of AGENTS §3.5: 5 minutes without input, and minimizing does not lock.</summary>
    public static AutoLockOptions Default { get; } = new(TimeSpan.FromMinutes(5), lockOnMinimize: false);

    /// <summary>How long the app window may go without input before the vault locks; always positive.</summary>
    public TimeSpan IdleTimeout { get; }

    /// <summary>Whether minimizing the window locks the vault.</summary>
    public bool LockOnMinimize { get; }
}
