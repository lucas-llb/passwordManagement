namespace PasswordManager.ViewModels;

/// <summary>How the last password attempt on the settings page ended. The view maps each value to its own text, beside its form.</summary>
public enum SettingsOutcome
{
    /// <summary>Nothing to show: no attempt yet, or the attempt was refused before it ran.</summary>
    None,

    /// <summary>The master password was changed.</summary>
    PasswordChanged,

    /// <summary>The current master password typed in the change form is wrong; it counts for the shared delay.</summary>
    PasswordWrong,

    /// <summary>The change failed; the file on disk and the session are unchanged.</summary>
    PasswordFailed,

    /// <summary>The master password typed in the kit form is wrong; it counts for the shared delay.</summary>
    KitWrong,

    /// <summary>The new kit could not be generated; the current kit stays valid.</summary>
    KitFailed,

    /// <summary>The new kit was written down and committed; the previous kit no longer opens the vault.</summary>
    KitReplaced,
}
