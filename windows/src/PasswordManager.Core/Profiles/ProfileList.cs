namespace PasswordManager.Core.Profiles;

/// <summary>Root object of <c>profiles.json</c>: <c>{"profiles":[...]}</c>.</summary>
internal sealed record ProfileList
{
    public required IReadOnlyList<Profile> Profiles { get; init; }
}
