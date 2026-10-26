namespace PasswordManager.Core.Profiles;

/// <summary>
/// One entry of <c>profiles.json</c>: the clear-text metadata of a profile. The vault itself lives in
/// <c>&lt;Id&gt;.pmvault</c> and stays encrypted.
/// </summary>
public sealed record Profile
{
    /// <summary>Identifier of the profile. File names are derived from it and never from <see cref="DisplayName"/>.</summary>
    public required Guid Id { get; init; }

    /// <summary>Name shown on the profile selection screen: NFC-normalized, trimmed, at most 64 UTF-16 units.</summary>
    public required string DisplayName { get; init; }

    /// <summary>Creation instant, with millisecond precision.</summary>
    public required DateTimeOffset CreatedAt { get; init; }
}
