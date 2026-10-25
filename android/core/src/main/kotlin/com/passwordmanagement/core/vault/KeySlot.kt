package com.passwordmanagement.core.vault

import com.passwordmanagement.core.crypto.AndroidSodium
import com.passwordmanagement.core.crypto.Argon2idKdf
import com.passwordmanagement.core.crypto.VaultKeyWrap
import java.security.SecureRandom

/**
 * Builds and opens v1 key slots (docs/vault-format.md, "Registro de slot"): a type-1 (password) or
 * type-2 (recovery kit) record that wraps the 32-byte Vault Key with a KEK derived by Argon2id.
 *
 * `AAD_wrap = file[0..6) || slot[0..29)`: magic, version, slot type, slot length, KDF parameters and
 * salt. It deliberately excludes `slotCount`, the other slots, `wrapNonce` and `wrappedVaultKey`, so a
 * slot can be replaced without touching the others.
 *
 * Ownership: the caller's input arrays are never mutated. The caller owns and must zero its own
 * password, recovery key and `vaultKey` arrays. Every KEK derived here is zeroed before returning.
 */
class KeySlot internal constructor(private val kdf: Argon2idKdf, private val wrap: VaultKeyWrap) {
    /** Uses the device libsodium, loaded once on first use. */
    constructor() : this(Argon2idKdf(), VaultKeyWrap(AndroidSodium.lazySodium))

    private val random = SecureRandom()

    /**
     * Creates a new type-1 (password) slot with a fresh random salt and wrap nonce.
     *
     * @param password the master password. Not modified; the caller zeros it.
     * @param parameters Argon2id cost parameters to store in the slot.
     * @param vaultKey the Vault Key to wrap, exactly [VaultFormat.KEY_SIZE] bytes. Not modified.
     * @return the [VaultFormat.KDF_SLOT_SIZE]-byte slot record (not secret).
     * @throws IllegalArgumentException the Vault Key length is wrong or [parameters] are out of range.
     */
    fun createPasswordSlot(password: CharArray, parameters: KdfParameters, vaultKey: ByteArray): ByteArray {
        validateCreate(parameters, vaultKey)
        return create(VaultFormat.SLOT_TYPE_PASSWORD, parameters, vaultKey) { salt, kek ->
            kdf.deriveKek(password, parameters, salt, kek)
        }
    }

    /**
     * Creates a new type-2 (recovery kit) slot with a fresh random salt and wrap nonce.
     *
     * @param recoveryKey the recovery-kit key, exactly [VaultFormat.RECOVERY_KEY_SIZE] bytes. Not
     *   modified; the caller zeros it.
     * @param parameters Argon2id cost parameters to store in the slot.
     * @param vaultKey the Vault Key to wrap, exactly [VaultFormat.KEY_SIZE] bytes. Not modified.
     * @return the [VaultFormat.KDF_SLOT_SIZE]-byte slot record (not secret).
     * @throws IllegalArgumentException a length is wrong or [parameters] are out of range.
     */
    fun createRecoveryKitSlot(recoveryKey: ByteArray, parameters: KdfParameters, vaultKey: ByteArray): ByteArray {
        validateCreate(parameters, vaultKey)
        require(recoveryKey.size == VaultFormat.RECOVERY_KEY_SIZE) { "Recovery key has an invalid length." }
        return create(VaultFormat.SLOT_TYPE_RECOVERY_KIT, parameters, vaultKey) { salt, kek ->
            kdf.deriveKek(recoveryKey, parameters, salt, kek)
        }
    }

    /**
     * Unwraps the Vault Key from the type-1 (password) slot at [slotOffset] in [file].
     *
     * @param file the vault bytes; `file[0..6)` is bound as the preamble. Not modified.
     * @param slotOffset index of the slot in [file], at least [VaultFormat.FIRST_SLOT_OFFSET].
     * @param password the master password. Not modified; the caller zeros it.
     * @param vaultKey receives the Vault Key; exactly [VaultFormat.KEY_SIZE] bytes. The caller zeros it.
     *   On an authentication failure it is zeroed before the exception propagates.
     * @throws IllegalArgumentException [slotOffset] is before the first slot, the Vault Key length is
     *   wrong, or the slot is not a password slot.
     * @throws VaultFormatException the slot is truncated or its KDF fields are invalid.
     * @throws VaultAuthenticationException the password is wrong or the slot was altered. No reason is given.
     */
    fun unwrapPasswordSlot(file: ByteArray, slotOffset: Int, password: CharArray, vaultKey: ByteArray) {
        validateUnwrap(file, slotOffset, vaultKey)
        unwrap(file, slotOffset, VaultFormat.SLOT_TYPE_PASSWORD, vaultKey) { parameters, salt, kek ->
            kdf.deriveKek(password, parameters, salt, kek)
        }
    }

    /**
     * Unwraps the Vault Key from the type-2 (recovery kit) slot at [slotOffset] in [file].
     *
     * @param file the vault bytes; `file[0..6)` is bound as the preamble. Not modified.
     * @param slotOffset index of the slot in [file], at least [VaultFormat.FIRST_SLOT_OFFSET].
     * @param recoveryKey the recovery-kit key, exactly [VaultFormat.RECOVERY_KEY_SIZE] bytes. Not
     *   modified; the caller zeros it.
     * @param vaultKey receives the Vault Key; exactly [VaultFormat.KEY_SIZE] bytes. The caller zeros it.
     *   On an authentication failure it is zeroed before the exception propagates.
     * @throws IllegalArgumentException [slotOffset] is before the first slot, a length is wrong, or the
     *   slot is not a recovery-kit slot.
     * @throws VaultFormatException the slot is truncated or its KDF fields are invalid.
     * @throws VaultAuthenticationException the kit is wrong or the slot was altered. No reason is given.
     */
    fun unwrapRecoveryKitSlot(file: ByteArray, slotOffset: Int, recoveryKey: ByteArray, vaultKey: ByteArray) {
        validateUnwrap(file, slotOffset, vaultKey)
        require(recoveryKey.size == VaultFormat.RECOVERY_KEY_SIZE) { "Recovery key has an invalid length." }
        unwrap(file, slotOffset, VaultFormat.SLOT_TYPE_RECOVERY_KIT, vaultKey) { parameters, salt, kek ->
            kdf.deriveKek(recoveryKey, parameters, salt, kek)
        }
    }

    private fun validateCreate(parameters: KdfParameters, vaultKey: ByteArray) {
        require(vaultKey.size == VaultFormat.KEY_SIZE) { "Vault Key has an invalid length." }
        require(parameters.isInRange) { "KDF parameters are outside the format bounds." }
    }

    private fun validateUnwrap(file: ByteArray, slotOffset: Int, vaultKey: ByteArray) {
        require(slotOffset >= VaultFormat.FIRST_SLOT_OFFSET) { "slotOffset is before the first slot." }

        // Long arithmetic: an offset near Int.MAX_VALUE must report a truncated slot, not overflow.
        if (file.size.toLong() - slotOffset < VaultFormat.KDF_SLOT_SIZE) {
            throw VaultFormatException("Key slot is truncated.")
        }

        require(vaultKey.size == VaultFormat.KEY_SIZE) { "Vault Key buffer has an invalid length." }
    }

    private inline fun create(
        slotType: Int,
        parameters: KdfParameters,
        vaultKey: ByteArray,
        deriveKek: (salt: ByteArray, kek: ByteArray) -> Unit,
    ): ByteArray {
        val slot = ByteArray(VaultFormat.KDF_SLOT_SIZE)
        slot[VaultFormat.SLOT_TYPE_OFFSET] = slotType.toByte()
        writeU16(slot, VaultFormat.SLOT_LENGTH_OFFSET, VaultFormat.KDF_SLOT_BODY_SIZE)
        slot[VaultFormat.SLOT_KDF_ALGORITHM_OFFSET] = VaultFormat.KDF_ARGON2ID.toByte()
        writeU32(slot, VaultFormat.SLOT_KDF_MEMORY_OFFSET, parameters.memoryKiB)
        writeU32(slot, VaultFormat.SLOT_KDF_ITERATIONS_OFFSET, parameters.iterations)
        slot[VaultFormat.SLOT_KDF_PARALLELISM_OFFSET] = VaultFormat.DEFAULT_KDF_PARALLELISM.toByte()

        // Salt and nonce are not secret; fresh CSPRNG values for every wrap.
        val salt = ByteArray(VaultFormat.SALT_SIZE).also { random.nextBytes(it) }
        val nonce = ByteArray(VaultFormat.NONCE_SIZE).also { random.nextBytes(it) }
        salt.copyInto(slot, VaultFormat.SLOT_SALT_OFFSET)
        nonce.copyInto(slot, VaultFormat.SLOT_WRAP_NONCE_OFFSET)

        val kek = ByteArray(VaultFormat.KEY_SIZE)
        try {
            deriveKek(salt, kek)
            val preamble = VaultFormat.MAGIC + byteArrayOf(VaultFormat.VERSION.toByte(), 0)
            val aad = buildWrapAad(preamble, slot)
            val wrapped = ByteArray(VaultFormat.WRAPPED_KEY_SIZE)
            wrap.wrap(kek, vaultKey, aad, nonce, wrapped)
            wrapped.copyInto(slot, VaultFormat.SLOT_WRAPPED_KEY_OFFSET)
        } finally {
            kek.fill(0)
        }
        return slot
    }

    private inline fun unwrap(
        file: ByteArray,
        slotOffset: Int,
        expectedType: Int,
        vaultKey: ByteArray,
        deriveKek: (parameters: KdfParameters, salt: ByteArray, kek: ByteArray) -> Unit,
    ) {
        // Reports format errors; the slot is already known to fit inside the file.
        val parameters = KdfParameters.readFromSlot(file, slotOffset)
        val slotType = file[slotOffset + VaultFormat.SLOT_TYPE_OFFSET].toInt() and 0xFF
        require(slotType == expectedType) { "Key slot type does not match the unlock method." }

        val salt = slice(file, slotOffset + VaultFormat.SLOT_SALT_OFFSET, VaultFormat.SALT_SIZE)
        val nonce = slice(file, slotOffset + VaultFormat.SLOT_WRAP_NONCE_OFFSET, VaultFormat.NONCE_SIZE)
        val wrapped = slice(file, slotOffset + VaultFormat.SLOT_WRAPPED_KEY_OFFSET, VaultFormat.WRAPPED_KEY_SIZE)
        // Bound from the actual file bytes, not the constants: a wrong preamble fails the AEAD.
        val aad = buildWrapAad(file, file, slotOffset)

        val kek = ByteArray(VaultFormat.KEY_SIZE)
        try {
            deriveKek(parameters, salt, kek)
            wrap.unwrap(kek, wrapped, aad, nonce, vaultKey)
        } finally {
            kek.fill(0)
        }
    }

    companion object {
        private const val SLOT_AAD_SIZE: Int = VaultFormat.SLOT_WRAP_NONCE_OFFSET

        /**
         * Builds `AAD_wrap = preamble[0..6) || slot[slotOffset..slotOffset + 29)` (35 bytes).
         *
         * @throws IllegalArgumentException [preamble] is shorter than [VaultFormat.PREAMBLE_SIZE], or the
         *   slot header and salt do not fit in [slot] at [slotOffset].
         */
        internal fun buildWrapAad(preamble: ByteArray, slot: ByteArray, slotOffset: Int = 0): ByteArray {
            require(preamble.size >= VaultFormat.PREAMBLE_SIZE) { "Preamble is too short." }
            require(slotOffset >= 0 && slot.size.toLong() - slotOffset >= SLOT_AAD_SIZE) { "Slot is too short." }

            val aad = ByteArray(VaultFormat.WRAP_AAD_SIZE)
            preamble.copyInto(aad, 0, 0, VaultFormat.PREAMBLE_SIZE)
            slot.copyInto(aad, VaultFormat.PREAMBLE_SIZE, slotOffset, slotOffset + SLOT_AAD_SIZE)
            return aad
        }

        private fun slice(bytes: ByteArray, start: Int, length: Int): ByteArray =
            bytes.copyOfRange(start, start + length)

        private fun writeU16(bytes: ByteArray, index: Int, value: Int) {
            bytes[index] = value.toByte()
            bytes[index + 1] = (value shr 8).toByte()
        }

        private fun writeU32(bytes: ByteArray, index: Int, value: Long) {
            for (i in 0 until 4) {
                bytes[index + i] = (value shr (8 * i)).toByte()
            }
        }
    }
}
