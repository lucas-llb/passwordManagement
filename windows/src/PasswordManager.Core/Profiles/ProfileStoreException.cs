namespace PasswordManager.Core.Profiles;

/// <summary>
/// Thrown when <c>profiles.json</c> is not valid (docs/vault-format.md, "profiles.json").
/// Messages are in English and never contain profile names or file content.
/// </summary>
public sealed class ProfileStoreException : Exception
{
    public ProfileStoreException()
    {
    }

    public ProfileStoreException(string message)
        : base(message)
    {
    }

    public ProfileStoreException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
