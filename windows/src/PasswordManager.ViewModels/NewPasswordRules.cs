namespace PasswordManager.ViewModels;

/// <summary>
/// The rule for a password that the user chooses and types twice: the master password of a new profile and the export
/// password. One place, so the two pages cannot drift apart.
/// </summary>
internal static class NewPasswordRules
{
    /// <summary>
    /// Checks a new password and its confirmation. <see cref="NewPasswordError.TooShort"/> wins over
    /// <see cref="NewPasswordError.Mismatch"/>: there is no point asking to retype a password that will be refused anyway.
    /// </summary>
    /// <param name="password">The password. Not modified.</param>
    /// <param name="confirmation">The password typed a second time. Not modified.</param>
    /// <returns><see cref="NewPasswordError.None"/> when the password is accepted.</returns>
    internal static NewPasswordError Check(ReadOnlySpan<char> password, ReadOnlySpan<char> confirmation)
    {
        if (CountScalarValues(password) < CreateProfileViewModel.MinimumPasswordLength)
        {
            return NewPasswordError.TooShort;
        }

        return password.SequenceEqual(confirmation) ? NewPasswordError.None : NewPasswordError.Mismatch;
    }

    private static int CountScalarValues(ReadOnlySpan<char> text)
    {
        var count = 0;
        var enumerator = text.EnumerateRunes();
        while (enumerator.MoveNext())
        {
            count++;
        }

        return count;
    }
}
