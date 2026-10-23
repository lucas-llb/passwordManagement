namespace PasswordManager.Core.Vault;

/// <summary>Plaintext body of the vault. Only exists in memory while unlocked.</summary>
public sealed record VaultContent
{
    public IReadOnlyList<VaultEntry> Entries { get; init; } = [];
}
