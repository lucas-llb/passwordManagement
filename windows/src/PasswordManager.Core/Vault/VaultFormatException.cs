namespace PasswordManager.Core.Vault;

/// <summary>
/// Thrown when vault file bytes violate the v1 format (docs/vault-format.md).
/// Messages are in English and never contain secrets or file content.
/// </summary>
public sealed class VaultFormatException : Exception
{
    public VaultFormatException()
    {
    }

    public VaultFormatException(string message)
        : base(message)
    {
    }

    public VaultFormatException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
