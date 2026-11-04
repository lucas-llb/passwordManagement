namespace PasswordManager.ViewModels;

/// <summary>
/// What the settings page hands to <see cref="SettingsViewModel.ChangePasswordCommand"/>: the current master password,
/// the new one and its confirmation. It deliberately has no <c>ToString</c> override and is not a record, so nothing
/// prints the arrays.
/// </summary>
/// <remarks>
/// The command takes ownership of the three arrays and zeroes them before it returns, whatever the outcome; callers must
/// not read them afterwards.
/// </remarks>
public sealed class ChangePasswordRequest
{
    /// <summary>Wraps the arrays without copying them.</summary>
    /// <param name="currentPassword">The current master password.</param>
    /// <param name="newPassword">The new master password.</param>
    /// <param name="confirmation">The new master password, typed a second time.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public ChangePasswordRequest(char[] currentPassword, char[] newPassword, char[] confirmation)
    {
        ArgumentNullException.ThrowIfNull(currentPassword);
        ArgumentNullException.ThrowIfNull(newPassword);
        ArgumentNullException.ThrowIfNull(confirmation);

        CurrentPassword = currentPassword;
        NewPassword = newPassword;
        Confirmation = confirmation;
    }

    /// <summary>The current master password. Zeroed by the command.</summary>
    public char[] CurrentPassword { get; }

    /// <summary>The new master password. Zeroed by the command.</summary>
    public char[] NewPassword { get; }

    /// <summary>The confirmation of the new master password. Zeroed by the command.</summary>
    public char[] Confirmation { get; }
}
