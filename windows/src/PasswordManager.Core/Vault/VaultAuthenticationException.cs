namespace PasswordManager.Core.Vault;

/// <summary>
/// Thrown when authenticated decryption of vault data fails: a wrong master password, a wrong recovery kit,
/// or tampered/corrupted bytes. It deliberately carries no reason (and no inner exception), so a caller
/// can never tell these cases apart and nothing about the secret or the data leaks through the error.
/// </summary>
public sealed class VaultAuthenticationException : Exception
{
    public VaultAuthenticationException()
        : base("Vault authentication failed.")
    {
    }
}
