namespace PasswordManager.Core.Vault;

/// <summary>
/// Position of one slot record inside a vault file.
/// </summary>
/// <param name="Type">The slot type byte.</param>
/// <param name="Offset">Offset of the record inside the file.</param>
/// <param name="Length">Length of the record, including its <see cref="VaultFormat.SlotHeaderSize"/>-byte header.</param>
public readonly record struct VaultSlotRange(byte Type, int Offset, int Length);
