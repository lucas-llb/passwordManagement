namespace PasswordManager.ViewModels;

/// <summary>
/// The master password and its confirmation, as typed on the profile creation page. It deliberately has no
/// <c>ToString</c> override and is not a record, so nothing prints the arrays.
/// </summary>
/// <remarks>
/// The create command of <see cref="CreateProfileViewModel"/> takes ownership of both arrays and zeroes them before it
/// returns, whatever the outcome; callers must not read them afterwards.
/// </remarks>
public sealed class NewMasterPassword
{
    /// <summary>Wraps the two arrays without copying them.</summary>
    /// <param name="password">The master password.</param>
    /// <param name="confirmation">The password typed a second time.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public NewMasterPassword(char[] password, char[] confirmation)
    {
        ArgumentNullException.ThrowIfNull(password);
        ArgumentNullException.ThrowIfNull(confirmation);

        Password = password;
        Confirmation = confirmation;
    }

    /// <summary>The master password. Zeroed by the create command.</summary>
    public char[] Password { get; }

    /// <summary>The confirmation. Zeroed by the create command.</summary>
    public char[] Confirmation { get; }
}
