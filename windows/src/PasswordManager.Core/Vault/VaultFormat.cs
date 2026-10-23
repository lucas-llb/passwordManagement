namespace PasswordManager.Core.Vault;

/// <summary>
/// Constants of the vault file format v1. Source of truth: docs/vault-format.md.
/// Any change here must bump <see cref="Version"/>, update docs/vault-format.md and be mirrored on Android.
/// </summary>
public static class VaultFormat
{
    public static ReadOnlySpan<byte> Magic => "PMVF"u8;

    public const int MagicSize = 4;
    public const ushort Version = 1;
    public const int VersionOffset = 4;
    public const int SlotCountOffset = 6;
    public const int FirstSlotOffset = 7;
    public const int PreambleSize = 6;
    public const int MinSlotCount = 1;
    public const int MaxSlotCount = 8;

    public const byte SlotTypePassword = 1;
    public const byte SlotTypeRecoveryKit = 2;
    public const int SlotHeaderSize = 3;
    public const int SlotTypeOffset = 0;
    public const int SlotLengthOffset = 1;
    public const int SlotKdfAlgorithmOffset = 3;
    public const int SlotKdfMemoryOffset = 4;
    public const int SlotKdfIterationsOffset = 8;
    public const int SlotKdfParallelismOffset = 12;
    public const int SlotSaltOffset = 13;
    public const int SlotWrapNonceOffset = 29;
    public const int SlotWrappedKeyOffset = 53;
    public const int KdfSlotBodySize = 98;
    public const int KdfSlotSize = 101;
    public const int WrapAadSize = 35;

    public const byte KdfArgon2id = 1;
    public const uint DefaultKdfMemoryKiB = 65536;
    public const uint DefaultKdfIterations = 3;
    public const byte DefaultKdfParallelism = 1;
    public const uint MinKdfMemoryKiB = 8;
    public const uint MaxKdfMemoryKiB = 1048576;
    public const uint MinKdfIterations = 1;
    public const uint MaxKdfIterations = 32;

    public const int SaltSize = 16;
    public const int KeySize = 32;
    public const int NonceSize = 24;
    public const int TagSize = 16;
    public const int WrappedKeySize = 48;
    public const int MinFileSize = 148;

    public const int RecoveryKeySize = 20;
    public const string RecoveryKeyAlphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    public const int RecoveryKeyGroupLength = 4;
    public const int RecoveryKeyGroupCount = 8;
    public const char RecoveryKeySeparator = '-';

    public const string FileExtension = ".pmvault";
}
