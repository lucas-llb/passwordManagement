package com.passwordmanagement.core.vault

/**
 * Argon2id cost parameters stored in a v1 key slot. Parallelism is fixed at 1 by the format.
 * The salt is not part of this type; callers take it from the slot with
 * `bytes.copyOfRange(offset + VaultFormat.SLOT_SALT_OFFSET, offset + VaultFormat.SLOT_SALT_OFFSET + VaultFormat.SALT_SIZE)`.
 *
 * @property memoryKiB Argon2id memory cost in KiB (an unsigned 32-bit value, hence [Long]).
 * @property iterations Argon2id number of passes (an unsigned 32-bit value, hence [Long]).
 */
data class KdfParameters(val memoryKiB: Long, val iterations: Long) {
    /** True when both values are inside the bounds that the format allows on read. */
    val isInRange: Boolean
        get() = memoryKiB in VaultFormat.MIN_KDF_MEMORY_KIB..VaultFormat.MAX_KDF_MEMORY_KIB &&
            iterations in VaultFormat.MIN_KDF_ITERATIONS..VaultFormat.MAX_KDF_ITERATIONS

    companion object {
        /** Default parameters for new key slots (64 MiB, 3 passes). */
        val DEFAULT: KdfParameters =
            KdfParameters(VaultFormat.DEFAULT_KDF_MEMORY_KIB, VaultFormat.DEFAULT_KDF_ITERATIONS)

        /**
         * Reads and validates the KDF parameters of a v1 key slot (docs/vault-format.md, "Registro de slot").
         *
         * @param bytes buffer that holds the slot record.
         * @param offset index in [bytes] where the slot starts, so callers can read in place.
         * @throws IllegalArgumentException [offset] is negative.
         * @throws VaultFormatException the slot is truncated or any KDF field is invalid or out of range.
         */
        fun readFromSlot(bytes: ByteArray, offset: Int = 0): KdfParameters {
            require(offset >= 0) { "offset must not be negative." }

            // Long arithmetic: an offset near Int.MAX_VALUE must report a truncated slot, not overflow.
            if (bytes.size.toLong() - offset < VaultFormat.KDF_SLOT_SIZE) {
                throw VaultFormatException("Key slot is truncated.")
            }

            val slotType = readU8(bytes, offset + VaultFormat.SLOT_TYPE_OFFSET)
            if (slotType != VaultFormat.SLOT_TYPE_PASSWORD && slotType != VaultFormat.SLOT_TYPE_RECOVERY_KIT) {
                throw VaultFormatException("Key slot has an unsupported type.")
            }

            val slotLength = readU16(bytes, offset + VaultFormat.SLOT_LENGTH_OFFSET)
            if (slotLength != VaultFormat.KDF_SLOT_BODY_SIZE) {
                throw VaultFormatException("Key slot has an invalid length.")
            }

            if (readU8(bytes, offset + VaultFormat.SLOT_KDF_ALGORITHM_OFFSET) != VaultFormat.KDF_ARGON2ID) {
                throw VaultFormatException("Key slot uses an unsupported KDF algorithm.")
            }

            if (readU8(bytes, offset + VaultFormat.SLOT_KDF_PARALLELISM_OFFSET) != VaultFormat.DEFAULT_KDF_PARALLELISM) {
                throw VaultFormatException("Key slot uses an unsupported KDF parallelism.")
            }

            val parameters = KdfParameters(
                memoryKiB = readU32(bytes, offset + VaultFormat.SLOT_KDF_MEMORY_OFFSET),
                iterations = readU32(bytes, offset + VaultFormat.SLOT_KDF_ITERATIONS_OFFSET),
            )

            if (!parameters.isInRange) {
                throw VaultFormatException("Key slot KDF parameters are out of range.")
            }

            return parameters
        }

        private fun readU8(bytes: ByteArray, index: Int): Int = bytes[index].toInt() and 0xFF

        private fun readU16(bytes: ByteArray, index: Int): Int = readU8(bytes, index) or (readU8(bytes, index + 1) shl 8)

        private fun readU32(bytes: ByteArray, index: Int): Long =
            (readU8(bytes, index).toLong()) or
                (readU8(bytes, index + 1).toLong() shl 8) or
                (readU8(bytes, index + 2).toLong() shl 16) or
                (readU8(bytes, index + 3).toLong() shl 24)
    }
}
