using System.Buffers.Binary;

namespace PasswordManager.Core.Vault;

/// <summary>
/// Parses and builds the clear header of a v1 vault file: preamble plus slot records (docs/vault-format.md,
/// "Validação na leitura"). Every error message is fixed English text, identical on Android, and never contains
/// file content.
/// </summary>
public sealed class VaultHeader
{
    private const string TruncatedFileMessage = "Vault file is truncated.";
    private const string InvalidMagicMessage = "Vault file has an invalid magic.";
    private const string UnsupportedVersionMessage = "Vault format version is not supported.";
    private const string InvalidSlotCountMessage = "Vault file has an invalid slot count.";
    private const string TruncatedSlotMessage = "Key slot is truncated.";
    private const string InvalidSlotTypeMessage = "Key slot has an invalid type.";
    private const string DuplicatedSlotTypeMessage = "Key slot type is duplicated.";
    private const string NoPasswordSlotMessage = "Vault file has no password slot.";
    private const string TruncatedBodyMessage = "Vault body is truncated.";

    private VaultHeader(int bodyOffset, int passwordSlotOffset, int? recoveryKitSlotOffset, IReadOnlyList<VaultSlotRange> slots)
    {
        BodyOffset = bodyOffset;
        PasswordSlotOffset = passwordSlotOffset;
        RecoveryKitSlotOffset = recoveryKitSlotOffset;
        Slots = slots;
    }

    /// <summary>Offset <c>B</c> of the body nonce, which is also the header length.</summary>
    public int BodyOffset { get; }

    /// <summary>Offset of the type-1 (master password) slot record.</summary>
    public int PasswordSlotOffset { get; }

    /// <summary>Offset of the type-2 (recovery kit) slot record, or null when the file has none.</summary>
    public int? RecoveryKitSlotOffset { get; }

    /// <summary>Every slot record, unknown types included, in file order.</summary>
    public IReadOnlyList<VaultSlotRange> Slots { get; }

    /// <summary>
    /// Validates a whole vault file and locates its slots and body.
    /// </summary>
    /// <param name="file">The whole file bytes. Not modified.</param>
    /// <returns>The body offset, the slot offsets and the slot table.</returns>
    /// <exception cref="VaultFormatException">The bytes are not a valid v1 vault file.</exception>
    public static VaultHeader Parse(ReadOnlySpan<byte> file)
    {
        if (file.Length < VaultFormat.MinFileSize)
        {
            throw new VaultFormatException(TruncatedFileMessage);
        }

        if (!file[..VaultFormat.MagicSize].SequenceEqual(VaultFormat.Magic))
        {
            throw new VaultFormatException(InvalidMagicMessage);
        }

        if (BinaryPrimitives.ReadUInt16LittleEndian(file[VaultFormat.VersionOffset..]) != VaultFormat.Version)
        {
            throw new VaultFormatException(UnsupportedVersionMessage);
        }

        var header = WalkSlots(file);

        if (file.Length - header.BodyOffset < VaultFormat.NonceSize + VaultFormat.TagSize)
        {
            throw new VaultFormatException(TruncatedBodyMessage);
        }

        return header;
    }

    /// <summary>
    /// Builds a v1 header (<c>file[0..B)</c>) from slot records, ready for <see cref="VaultBody.Seal"/>.
    /// Records are sorted by type (stable) and copied byte for byte; unknown types are preserved.
    /// </summary>
    /// <param name="slots">The slot records, each including its 3-byte header. Not modified.</param>
    /// <returns>A new array holding magic, version, slot count and the sorted records.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="slots"/> or one of its elements is null.</exception>
    /// <exception cref="ArgumentException">The count is not 1..8, a record length is inconsistent, or the
    /// records do not form a valid v1 header.</exception>
    public static byte[] Build(IReadOnlyList<byte[]> slots)
    {
        ArgumentNullException.ThrowIfNull(slots);

        foreach (var record in slots)
        {
            if (record is null)
            {
                throw new ArgumentNullException(nameof(slots), "Slot record is null.");
            }
        }

        if (slots.Count is < VaultFormat.MinSlotCount or > VaultFormat.MaxSlotCount)
        {
            throw new ArgumentException("Slot count is out of range.", nameof(slots));
        }

        var total = 0;
        foreach (var record in slots)
        {
            if (record.Length < VaultFormat.SlotHeaderSize
                || VaultFormat.SlotHeaderSize + BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(VaultFormat.SlotLengthOffset)) != record.Length)
            {
                throw new ArgumentException("Slot record has an inconsistent length.", nameof(slots));
            }

            total += record.Length;
        }

        var header = new byte[VaultFormat.FirstSlotOffset + total];
        VaultFormat.Magic.CopyTo(header);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(VaultFormat.VersionOffset), VaultFormat.Version);
        header[VaultFormat.SlotCountOffset] = (byte)slots.Count;

        var offset = VaultFormat.FirstSlotOffset;
        foreach (var record in slots.OrderBy(record => record[VaultFormat.SlotTypeOffset]))
        {
            record.CopyTo(header, offset);
            offset += record.Length;
        }

        try
        {
            WalkSlots(header);
        }
        catch (VaultFormatException exception)
        {
            throw new ArgumentException("Slots do not form a valid v1 header.", nameof(slots), exception);
        }

        return header;
    }

    // Steps 4 and 5 of "Validação na leitura": slot count, then each record, then the password slot.
    private static VaultHeader WalkSlots(ReadOnlySpan<byte> file)
    {
        var count = file[VaultFormat.SlotCountOffset];
        if (count is < VaultFormat.MinSlotCount or > VaultFormat.MaxSlotCount)
        {
            throw new VaultFormatException(InvalidSlotCountMessage);
        }

        Span<bool> seen = stackalloc bool[256];
        var slots = new VaultSlotRange[count];
        int? passwordSlotOffset = null;
        int? recoveryKitSlotOffset = null;
        var offset = VaultFormat.FirstSlotOffset;

        for (var i = 0; i < count; i++)
        {
            var remaining = file.Length - offset;
            if (remaining < VaultFormat.SlotHeaderSize)
            {
                throw new VaultFormatException(TruncatedSlotMessage);
            }

            var length = VaultFormat.SlotHeaderSize
                + BinaryPrimitives.ReadUInt16LittleEndian(file[(offset + VaultFormat.SlotLengthOffset)..]);
            if (length > remaining)
            {
                throw new VaultFormatException(TruncatedSlotMessage);
            }

            var type = file[offset + VaultFormat.SlotTypeOffset];
            if (type == 0)
            {
                throw new VaultFormatException(InvalidSlotTypeMessage);
            }

            if (type is VaultFormat.SlotTypePassword or VaultFormat.SlotTypeRecoveryKit)
            {
                // The remainder of the file, not the exact record, so the messages match Android.
                KdfParameters.ReadFromSlot(file[offset..]);
            }

            if (seen[type])
            {
                throw new VaultFormatException(DuplicatedSlotTypeMessage);
            }

            seen[type] = true;
            if (type == VaultFormat.SlotTypePassword)
            {
                passwordSlotOffset = offset;
            }
            else if (type == VaultFormat.SlotTypeRecoveryKit)
            {
                recoveryKitSlotOffset = offset;
            }

            slots[i] = new VaultSlotRange(type, offset, length);
            offset += length;
        }

        if (passwordSlotOffset is not { } passwordOffset)
        {
            throw new VaultFormatException(NoPasswordSlotMessage);
        }

        return new VaultHeader(offset, passwordOffset, recoveryKitSlotOffset, slots.AsReadOnly());
    }
}
