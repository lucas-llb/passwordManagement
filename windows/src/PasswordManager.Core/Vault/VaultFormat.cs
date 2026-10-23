namespace PasswordManager.Core.Vault;

/// <summary>
/// Constants of the vault file format. Source of truth: docs/vault-format.md.
/// Any change here must bump <see cref="Version"/> and be mirrored on Android.
/// </summary>
public static class VaultFormat
{
    public static ReadOnlySpan<byte> Magic => "PMVF"u8;

    public const ushort Version = 1;

    public const byte KdfArgon2id = 1;
    public const uint DefaultKdfMemoryKiB = 64 * 1024;
    public const uint DefaultKdfIterations = 3;
    public const byte DefaultKdfParallelism = 1;

    public const int SaltSize = 16;
    public const int KeySize = 32;
    public const int NonceSize = 24;
    public const int TagSize = 16;

    public const string FileExtension = ".pmvault";
}
