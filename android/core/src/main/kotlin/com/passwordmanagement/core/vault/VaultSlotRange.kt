package com.passwordmanagement.core.vault

/**
 * Position of one key slot record inside a vault file, as found by [VaultHeader.parse].
 *
 * @property type the slot type byte (1 = password, 2 = recovery kit, 3..255 = unknown, kept as is).
 * @property offset index of the slot's type byte in the file.
 * @property length size of the whole record, including the 3-byte slot header (`3 + slotLength`).
 */
data class VaultSlotRange(val type: Int, val offset: Int, val length: Int)
