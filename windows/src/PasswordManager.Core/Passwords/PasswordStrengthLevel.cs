namespace PasswordManager.Core.Passwords;

/// <summary>
/// Advisory strength of a password, from <see cref="PasswordStrength.Evaluate(ReadOnlySpan{char})"/>.
/// </summary>
public enum PasswordStrengthLevel
{
    /// <summary>The password is empty.</summary>
    None,

    /// <summary>Under 30 bits.</summary>
    VeryWeak,

    /// <summary>From 30 to under 45 bits.</summary>
    Weak,

    /// <summary>From 45 to under 60 bits.</summary>
    Fair,

    /// <summary>From 60 to under 80 bits.</summary>
    Strong,

    /// <summary>80 bits or more.</summary>
    VeryStrong,
}
