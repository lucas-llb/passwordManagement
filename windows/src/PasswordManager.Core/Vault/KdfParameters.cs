using System.Buffers.Binary;

namespace PasswordManager.Core.Vault;

/// <summary>
/// Argon2id cost parameters stored in a v1 key slot. Parallelism is fixed at 1 by the format.
/// The salt is not part of this type; callers slice it from the slot at
/// <see cref="VaultFormat.SlotSaltOffset"/> with length <see cref="VaultFormat.SaltSize"/>.
/// </summary>
/// <param name="MemoryKiB">Argon2id memory cost in KiB.</param>
/// <param name="Iterations">Argon2id number of passes.</param>
public readonly record struct KdfParameters(uint MemoryKiB, uint Iterations)
{
    /// <summary>Default parameters for new key slots (64 MiB, 3 passes).</summary>
    public static KdfParameters Default { get; } =
        new(VaultFormat.DefaultKdfMemoryKiB, VaultFormat.DefaultKdfIterations);

    /// <summary>True when both values are inside the bounds that the format allows on read.</summary>
    public bool IsInRange =>
        MemoryKiB is >= VaultFormat.MinKdfMemoryKiB and <= VaultFormat.MaxKdfMemoryKiB
        && Iterations is >= VaultFormat.MinKdfIterations and <= VaultFormat.MaxKdfIterations;

    /// <summary>
    /// Reads and validates the KDF parameters of a v1 key slot.
    /// </summary>
    /// <param name="slot">A slot record starting at its offset 0 (docs/vault-format.md, "Registro de slot").</param>
    /// <exception cref="VaultFormatException">The slot is truncated or any KDF field is invalid or out of range.</exception>
    public static KdfParameters ReadFromSlot(ReadOnlySpan<byte> slot)
    {
        if (slot.Length < VaultFormat.KdfSlotSize)
        {
            throw new VaultFormatException("Key slot is truncated.");
        }

        var slotType = slot[VaultFormat.SlotTypeOffset];
        if (slotType != VaultFormat.SlotTypePassword && slotType != VaultFormat.SlotTypeRecoveryKit)
        {
            throw new VaultFormatException("Key slot has an unsupported type.");
        }

        var slotLength = BinaryPrimitives.ReadUInt16LittleEndian(slot[VaultFormat.SlotLengthOffset..]);
        if (slotLength != VaultFormat.KdfSlotBodySize)
        {
            throw new VaultFormatException("Key slot has an invalid length.");
        }

        if (slot[VaultFormat.SlotKdfAlgorithmOffset] != VaultFormat.KdfArgon2id)
        {
            throw new VaultFormatException("Key slot uses an unsupported KDF algorithm.");
        }

        if (slot[VaultFormat.SlotKdfParallelismOffset] != VaultFormat.DefaultKdfParallelism)
        {
            throw new VaultFormatException("Key slot uses an unsupported KDF parallelism.");
        }

        var parameters = new KdfParameters(
            BinaryPrimitives.ReadUInt32LittleEndian(slot[VaultFormat.SlotKdfMemoryOffset..]),
            BinaryPrimitives.ReadUInt32LittleEndian(slot[VaultFormat.SlotKdfIterationsOffset..]));

        if (!parameters.IsInRange)
        {
            throw new VaultFormatException("Key slot KDF parameters are out of range.");
        }

        return parameters;
    }
}
