namespace PasswordManager.ViewModels;

/// <summary>
/// Why the new password typed on the profile creation page (the master password) or on the export page (the export
/// password) was refused. The view maps each value to its own text.
/// </summary>
public enum NewPasswordError
{
    /// <summary>No failure to show: no attempt yet, or the password was accepted.</summary>
    None,

    /// <summary>Shorter than <see cref="CreateProfileViewModel.MinimumPasswordLength"/> characters. Wins over <see cref="Mismatch"/>.</summary>
    TooShort,

    /// <summary>The confirmation differs from the password.</summary>
    Mismatch,
}
