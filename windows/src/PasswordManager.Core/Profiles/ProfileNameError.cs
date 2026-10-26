namespace PasswordManager.Core.Profiles;

/// <summary>Result of <see cref="ProfileRepository.ValidateDisplayName"/>.</summary>
public enum ProfileNameError
{
    /// <summary>The name is acceptable.</summary>
    None,

    /// <summary>The name is empty after trimming.</summary>
    Empty,

    /// <summary>The name is longer than <see cref="ProfileRepository.MaxDisplayNameLength"/> UTF-16 units.</summary>
    TooLong,

    /// <summary>The name has a control character or a lone surrogate.</summary>
    InvalidCharacter,

    /// <summary>Another profile already has this name, ignoring case.</summary>
    Duplicate,
}
