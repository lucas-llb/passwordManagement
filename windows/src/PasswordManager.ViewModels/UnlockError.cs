namespace PasswordManager.ViewModels;

/// <summary>Why the last unlock attempt failed. The view maps each value to its own text.</summary>
public enum UnlockError
{
    /// <summary>No failure to show: no attempt yet, or the last one is running or succeeded.</summary>
    None,

    /// <summary>Wrong master password, or the file was tampered with (the two cannot be told apart).</summary>
    WrongPassword,

    /// <summary>The vault file is missing, unreadable or not a valid vault.</summary>
    VaultUnreadable,
}
