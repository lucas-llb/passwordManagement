package com.passwordmanagement.core.vault

/**
 * The parsed v1 header of a vault file (docs/vault-format.md, "Validação na leitura").
 *
 * @property bodyOffset `B`, the offset of the body nonce (the end of the last slot).
 * @property passwordSlotOffset offset of the type-1 (password) slot.
 * @property recoveryKitSlotOffset offset of the type-2 (recovery kit) slot, or null when there is none.
 * @property slots every slot record, in file order (unknown types included).
 */
class VaultHeader internal constructor(
    val bodyOffset: Int,
    val passwordSlotOffset: Int,
    val recoveryKitSlotOffset: Int?,
    val slots: List<VaultSlotRange>,
) {
    private class SlotWalk(
        val end: Int,
        val passwordSlotOffset: Int,
        val recoveryKitSlotOffset: Int?,
        val slots: List<VaultSlotRange>,
    )

    companion object {
        /**
         * Parses and validates a whole vault file, in the fixed order shared with Windows. Messages
         * are fixed English text and never contain file content. Slot order is not checked.
         *
         * @param file the whole vault bytes. Not modified.
         * @throws VaultFormatException the file is truncated, has a bad magic, an unsupported version,
         *   an invalid slot count, a malformed, duplicated or invalid key slot, no password slot, or a
         *   body shorter than nonce plus tag.
         */
        fun parse(file: ByteArray): VaultHeader {
            if (file.size < VaultFormat.MIN_FILE_SIZE) {
                throw VaultFormatException("Vault file is truncated.")
            }

            // Read once: each access to VaultFormat.MAGIC allocates a new array.
            val magic = VaultFormat.MAGIC
            for (i in 0 until VaultFormat.MAGIC_SIZE) {
                if (file[i] != magic[i]) throw VaultFormatException("Vault file has an invalid magic.")
            }

            if (readU16(file, VaultFormat.VERSION_OFFSET) != VaultFormat.VERSION) {
                throw VaultFormatException("Vault format version is not supported.")
            }

            val walk = walkSlots(file)

            if (file.size - walk.end < VaultFormat.NONCE_SIZE + VaultFormat.TAG_SIZE) {
                throw VaultFormatException("Vault body is truncated.")
            }

            return VaultHeader(walk.end, walk.passwordSlotOffset, walk.recoveryKitSlotOffset, walk.slots)
        }

        /**
         * Builds `file[0..B)` of a v1 file from slot records, ready for [VaultBody.seal]. The records
         * are sorted by type (stable), so the password slot comes first.
         *
         * @param slots 1 to 8 whole slot records (`type || slotLength || data`). Not modified.
         * @return `magic || version || slotCount || records`.
         * @throws IllegalArgumentException the count is out of range, a record's length does not match
         *   its `slotLength`, or the records do not form a valid v1 header (the cause is the
         *   [VaultFormatException]).
         */
        fun build(slots: List<ByteArray>): ByteArray {
            require(slots.size in VaultFormat.MIN_SLOT_COUNT..VaultFormat.MAX_SLOT_COUNT) {
                "Slot count must be between 1 and 8."
            }
            for (record in slots) {
                require(
                    record.size >= VaultFormat.SLOT_HEADER_SIZE &&
                        VaultFormat.SLOT_HEADER_SIZE + readU16(record, VaultFormat.SLOT_LENGTH_OFFSET) == record.size,
                ) { "Slot record length does not match its slotLength." }
            }

            val sorted = slots.sortedBy { readU8(it, VaultFormat.SLOT_TYPE_OFFSET) }
            val header = ByteArray(VaultFormat.FIRST_SLOT_OFFSET + sorted.sumOf { it.size })
            VaultFormat.MAGIC.copyInto(header, 0)
            header[VaultFormat.VERSION_OFFSET] = VaultFormat.VERSION.toByte()
            header[VaultFormat.VERSION_OFFSET + 1] = 0
            header[VaultFormat.SLOT_COUNT_OFFSET] = sorted.size.toByte()
            var offset = VaultFormat.FIRST_SLOT_OFFSET
            for (record in sorted) {
                record.copyInto(header, offset)
                offset += record.size
            }

            try {
                walkSlots(header)
            } catch (e: VaultFormatException) {
                // Format messages hold no secrets, so the cause can be kept.
                throw IllegalArgumentException("Slots do not form a valid v1 header.", e)
            }
            return header
        }

        /** Steps 4 and 5 of the parse order: slot count, the slot walk and the password-slot check. */
        private fun walkSlots(file: ByteArray): SlotWalk {
            val count = readU8(file, VaultFormat.SLOT_COUNT_OFFSET)
            if (count !in VaultFormat.MIN_SLOT_COUNT..VaultFormat.MAX_SLOT_COUNT) {
                throw VaultFormatException("Vault file has an invalid slot count.")
            }

            val seen = BooleanArray(256)
            val slots = ArrayList<VaultSlotRange>(count)
            var passwordSlotOffset = -1
            var recoveryKitSlotOffset: Int? = null
            var offset = VaultFormat.FIRST_SLOT_OFFSET
            repeat(count) {
                val remaining = file.size - offset
                if (remaining < VaultFormat.SLOT_HEADER_SIZE) throw VaultFormatException("Key slot is truncated.")
                val length = VaultFormat.SLOT_HEADER_SIZE + readU16(file, offset + VaultFormat.SLOT_LENGTH_OFFSET)
                if (length > remaining) throw VaultFormatException("Key slot is truncated.")

                val type = readU8(file, offset + VaultFormat.SLOT_TYPE_OFFSET)
                if (type == 0) throw VaultFormatException("Key slot has an invalid type.")
                if (type == VaultFormat.SLOT_TYPE_PASSWORD || type == VaultFormat.SLOT_TYPE_RECOVERY_KIT) {
                    // The remainder of the file, not the exact record, so both platforms give the same message.
                    KdfParameters.readFromSlot(file, offset)
                }
                if (seen[type]) throw VaultFormatException("Key slot type is duplicated.")
                seen[type] = true

                when (type) {
                    VaultFormat.SLOT_TYPE_PASSWORD -> passwordSlotOffset = offset
                    VaultFormat.SLOT_TYPE_RECOVERY_KIT -> recoveryKitSlotOffset = offset
                }
                slots.add(VaultSlotRange(type, offset, length))
                offset += length
            }

            if (passwordSlotOffset < 0) throw VaultFormatException("Vault file has no password slot.")
            return SlotWalk(offset, passwordSlotOffset, recoveryKitSlotOffset, slots.toList())
        }

        private fun readU8(bytes: ByteArray, index: Int): Int = bytes[index].toInt() and 0xFF

        private fun readU16(bytes: ByteArray, index: Int): Int = readU8(bytes, index) or (readU8(bytes, index + 1) shl 8)
    }
}
