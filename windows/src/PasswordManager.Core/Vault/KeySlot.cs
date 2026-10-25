using System.Buffers.Binary;
using System.Security.Cryptography;
using PasswordManager.Core.Crypto;

namespace PasswordManager.Core.Vault;

/// <summary>
/// Creates and unwraps v1 KDF key slots (type 1 = master password, type 2 = recovery kit), each of which wraps
/// the 32-byte Vault Key under a KEK derived with Argon2id (docs/vault-format.md, "Registro de slot").
/// The wrap AAD is <c>AAD_wrap = file[0..6) || slot[0..29)</c>: it binds magic, version, slot type, slot length,
/// KDF parameters and salt, and deliberately excludes <c>slotCount</c>, the other slots, the nonce and the wrapped key.
/// </summary>
/// <remarks>
/// Ownership: the caller's input buffers are never mutated. The caller owns and must zero its own
/// password, recovery key and <c>vaultKey</c> buffers once it no longer needs them. The KEK lives on the stack
/// and is zeroed before returning. Slot bytes are not secret and are returned as a new array.
/// The magic and version are not validated here (that is the file reader's job); a wrong preamble fails the AEAD.
/// </remarks>
public static class KeySlot
{
    /// <summary>
    /// Creates a new type-1 (master password) slot with a fresh random salt and nonce.
    /// </summary>
    /// <param name="password">The master password. Not modified; the caller zeros it.</param>
    /// <param name="parameters">Argon2id cost parameters to store in the slot.</param>
    /// <param name="vaultKey">The Vault Key, exactly <see cref="VaultFormat.KeySize"/> bytes. Not modified.</param>
    /// <returns>The <see cref="VaultFormat.KdfSlotSize"/>-byte slot record.</returns>
    /// <exception cref="ArgumentException">The Vault Key length is wrong, or the password contains an invalid surrogate.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="parameters"/> is outside the format bounds.</exception>
    public static byte[] CreatePasswordSlot(ReadOnlySpan<char> password, KdfParameters parameters, ReadOnlySpan<byte> vaultKey)
    {
        ValidateCreateArguments(parameters, vaultKey);

        var slot = NewSlot(VaultFormat.SlotTypePassword, parameters);
        Span<byte> kek = stackalloc byte[VaultFormat.KeySize];
        try
        {
            Argon2idKdf.DeriveKek(password, parameters, slot.AsSpan(VaultFormat.SlotSaltOffset, VaultFormat.SaltSize), kek);
            WrapInto(slot, kek, vaultKey);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(kek);
        }

        return slot;
    }

    /// <summary>
    /// Creates a new type-2 (recovery kit) slot with a fresh random salt and nonce.
    /// </summary>
    /// <param name="recoveryKey">The decoded recovery-kit key, exactly <see cref="VaultFormat.RecoveryKeySize"/> bytes. Not modified; the caller zeros it.</param>
    /// <param name="parameters">Argon2id cost parameters to store in the slot.</param>
    /// <param name="vaultKey">The Vault Key, exactly <see cref="VaultFormat.KeySize"/> bytes. Not modified.</param>
    /// <returns>The <see cref="VaultFormat.KdfSlotSize"/>-byte slot record.</returns>
    /// <exception cref="ArgumentException">The Vault Key or recovery key length is wrong.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="parameters"/> is outside the format bounds.</exception>
    public static byte[] CreateRecoveryKitSlot(ReadOnlySpan<byte> recoveryKey, KdfParameters parameters, ReadOnlySpan<byte> vaultKey)
    {
        ValidateRecoveryKey(recoveryKey);
        ValidateCreateArguments(parameters, vaultKey);

        var slot = NewSlot(VaultFormat.SlotTypeRecoveryKit, parameters);
        Span<byte> kek = stackalloc byte[VaultFormat.KeySize];
        try
        {
            Argon2idKdf.DeriveKek(recoveryKey, parameters, slot.AsSpan(VaultFormat.SlotSaltOffset, VaultFormat.SaltSize), kek);
            WrapInto(slot, kek, vaultKey);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(kek);
        }

        return slot;
    }

    /// <summary>
    /// Unwraps the Vault Key from the type-1 (master password) slot that starts at <paramref name="slotOffset"/>.
    /// </summary>
    /// <param name="file">The vault file bytes; the AAD uses its first 6 bytes. Not modified.</param>
    /// <param name="slotOffset">Offset of the slot record inside <paramref name="file"/>.</param>
    /// <param name="password">The master password. Not modified; the caller zeros it.</param>
    /// <param name="vaultKey">Receives the Vault Key; exactly <see cref="VaultFormat.KeySize"/> bytes. The caller zeros it.
    /// It is zeroed before a <see cref="VaultAuthenticationException"/> propagates.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="slotOffset"/> is before the first slot.</exception>
    /// <exception cref="ArgumentException">The Vault Key length is wrong, or the slot is not a password slot.</exception>
    /// <exception cref="VaultFormatException">The slot is truncated or its KDF fields are invalid.</exception>
    /// <exception cref="VaultAuthenticationException">Wrong password, or the slot or preamble was tampered with.</exception>
    public static void UnwrapPasswordSlot(ReadOnlySpan<byte> file, int slotOffset, ReadOnlySpan<char> password, Span<byte> vaultKey)
    {
        var slot = SliceSlot(file, slotOffset);
        ValidateVaultKey(vaultKey);
        var parameters = ReadParameters(slot, VaultFormat.SlotTypePassword);

        Span<byte> kek = stackalloc byte[VaultFormat.KeySize];
        try
        {
            Argon2idKdf.DeriveKek(password, parameters, slot.Slice(VaultFormat.SlotSaltOffset, VaultFormat.SaltSize), kek);
            UnwrapFrom(file, slot, kek, vaultKey);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(kek);
        }
    }

    /// <summary>
    /// Unwraps the Vault Key from the type-2 (recovery kit) slot that starts at <paramref name="slotOffset"/>.
    /// </summary>
    /// <param name="file">The vault file bytes; the AAD uses its first 6 bytes. Not modified.</param>
    /// <param name="slotOffset">Offset of the slot record inside <paramref name="file"/>.</param>
    /// <param name="recoveryKey">The decoded recovery-kit key, exactly <see cref="VaultFormat.RecoveryKeySize"/> bytes. Not modified; the caller zeros it.</param>
    /// <param name="vaultKey">Receives the Vault Key; exactly <see cref="VaultFormat.KeySize"/> bytes. The caller zeros it.
    /// It is zeroed before a <see cref="VaultAuthenticationException"/> propagates.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="slotOffset"/> is before the first slot.</exception>
    /// <exception cref="ArgumentException">The Vault Key or recovery key length is wrong, or the slot is not a recovery-kit slot.</exception>
    /// <exception cref="VaultFormatException">The slot is truncated or its KDF fields are invalid.</exception>
    /// <exception cref="VaultAuthenticationException">Wrong recovery key, or the slot or preamble was tampered with.</exception>
    public static void UnwrapRecoveryKitSlot(ReadOnlySpan<byte> file, int slotOffset, ReadOnlySpan<byte> recoveryKey, Span<byte> vaultKey)
    {
        var slot = SliceSlot(file, slotOffset);
        ValidateVaultKey(vaultKey);
        ValidateRecoveryKey(recoveryKey);
        var parameters = ReadParameters(slot, VaultFormat.SlotTypeRecoveryKit);

        Span<byte> kek = stackalloc byte[VaultFormat.KeySize];
        try
        {
            Argon2idKdf.DeriveKek(recoveryKey, parameters, slot.Slice(VaultFormat.SlotSaltOffset, VaultFormat.SaltSize), kek);
            UnwrapFrom(file, slot, kek, vaultKey);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(kek);
        }
    }

    /// <summary>
    /// Builds <c>AAD_wrap = preamble[0..6) || slot[0..29)</c>.
    /// </summary>
    /// <param name="preamble">At least <see cref="VaultFormat.PreambleSize"/> bytes (magic and version).</param>
    /// <param name="slot">At least <see cref="VaultFormat.SlotWrapNonceOffset"/> bytes of the slot record.</param>
    /// <param name="aad">Receives the AAD; exactly <see cref="VaultFormat.WrapAadSize"/> bytes.</param>
    /// <exception cref="ArgumentException">Any buffer is too short, or <paramref name="aad"/> has the wrong length.</exception>
    internal static void BuildWrapAad(ReadOnlySpan<byte> preamble, ReadOnlySpan<byte> slot, Span<byte> aad)
    {
        if (preamble.Length < VaultFormat.PreambleSize)
        {
            throw new ArgumentException("Preamble is too short.", nameof(preamble));
        }

        if (slot.Length < VaultFormat.SlotWrapNonceOffset)
        {
            throw new ArgumentException("Slot is too short.", nameof(slot));
        }

        if (aad.Length != VaultFormat.WrapAadSize)
        {
            throw new ArgumentException("Wrap AAD buffer has an invalid length.", nameof(aad));
        }

        preamble[..VaultFormat.PreambleSize].CopyTo(aad);
        slot[..VaultFormat.SlotWrapNonceOffset].CopyTo(aad[VaultFormat.PreambleSize..]);
    }

    private static void ValidateCreateArguments(KdfParameters parameters, ReadOnlySpan<byte> vaultKey)
    {
        ValidateVaultKey(vaultKey);

        if (!parameters.IsInRange)
        {
            throw new ArgumentOutOfRangeException(nameof(parameters), "KDF parameters are outside the format bounds.");
        }
    }

    private static void ValidateVaultKey(ReadOnlySpan<byte> vaultKey)
    {
        if (vaultKey.Length != VaultFormat.KeySize)
        {
            throw new ArgumentException("Vault Key has an invalid length.", nameof(vaultKey));
        }
    }

    private static void ValidateRecoveryKey(ReadOnlySpan<byte> recoveryKey)
    {
        if (recoveryKey.Length != VaultFormat.RecoveryKeySize)
        {
            throw new ArgumentException("Recovery key has an invalid length.", nameof(recoveryKey));
        }
    }

    private static byte[] NewSlot(byte slotType, KdfParameters parameters)
    {
        var slot = new byte[VaultFormat.KdfSlotSize];
        slot[VaultFormat.SlotTypeOffset] = slotType;
        BinaryPrimitives.WriteUInt16LittleEndian(slot.AsSpan(VaultFormat.SlotLengthOffset), VaultFormat.KdfSlotBodySize);
        slot[VaultFormat.SlotKdfAlgorithmOffset] = VaultFormat.KdfArgon2id;
        BinaryPrimitives.WriteUInt32LittleEndian(slot.AsSpan(VaultFormat.SlotKdfMemoryOffset), parameters.MemoryKiB);
        BinaryPrimitives.WriteUInt32LittleEndian(slot.AsSpan(VaultFormat.SlotKdfIterationsOffset), parameters.Iterations);
        slot[VaultFormat.SlotKdfParallelismOffset] = VaultFormat.DefaultKdfParallelism;
        RandomNumberGenerator.Fill(slot.AsSpan(VaultFormat.SlotSaltOffset, VaultFormat.SaltSize));
        RandomNumberGenerator.Fill(slot.AsSpan(VaultFormat.SlotWrapNonceOffset, VaultFormat.NonceSize));
        return slot;
    }

    private static void WrapInto(byte[] slot, ReadOnlySpan<byte> kek, ReadOnlySpan<byte> vaultKey)
    {
        Span<byte> preamble = stackalloc byte[VaultFormat.PreambleSize];
        VaultFormat.Magic.CopyTo(preamble);
        BinaryPrimitives.WriteUInt16LittleEndian(preamble[VaultFormat.VersionOffset..], VaultFormat.Version);

        Span<byte> aad = stackalloc byte[VaultFormat.WrapAadSize];
        BuildWrapAad(preamble, slot, aad);

        VaultKeyWrap.Wrap(
            kek,
            vaultKey,
            aad,
            slot.AsSpan(VaultFormat.SlotWrapNonceOffset, VaultFormat.NonceSize),
            slot.AsSpan(VaultFormat.SlotWrappedKeyOffset, VaultFormat.WrappedKeySize));
    }

    private static void UnwrapFrom(ReadOnlySpan<byte> file, ReadOnlySpan<byte> slot, ReadOnlySpan<byte> kek, Span<byte> vaultKey)
    {
        Span<byte> aad = stackalloc byte[VaultFormat.WrapAadSize];
        BuildWrapAad(file, slot, aad);

        VaultKeyWrap.Unwrap(
            kek,
            slot.Slice(VaultFormat.SlotWrappedKeyOffset, VaultFormat.WrappedKeySize),
            aad,
            slot.Slice(VaultFormat.SlotWrapNonceOffset, VaultFormat.NonceSize),
            vaultKey);
    }

    private static ReadOnlySpan<byte> SliceSlot(ReadOnlySpan<byte> file, int slotOffset)
    {
        if (slotOffset < VaultFormat.FirstSlotOffset)
        {
            throw new ArgumentOutOfRangeException(nameof(slotOffset), "Slot offset is before the first slot.");
        }

        // Checked before slicing; slotOffset >= 7 and file.Length >= 0, so the subtraction cannot overflow.
        if (file.Length - slotOffset < VaultFormat.KdfSlotSize)
        {
            throw new VaultFormatException("Key slot is truncated.");
        }

        return file.Slice(slotOffset, VaultFormat.KdfSlotSize);
    }

    private static KdfParameters ReadParameters(ReadOnlySpan<byte> slot, byte expectedSlotType)
    {
        var parameters = KdfParameters.ReadFromSlot(slot);

        if (slot[VaultFormat.SlotTypeOffset] != expectedSlotType)
        {
            throw new ArgumentException("Key slot type does not match the unlock method.");
        }

        return parameters;
    }
}
