package com.passwordmanagement.core.vault

import com.passwordmanagement.core.crypto.AndroidSodium
import com.passwordmanagement.core.crypto.VaultBodyCipher
import java.nio.charset.CharacterCodingException
import java.security.SecureRandom

/**
 * Seals and opens the v1 vault body (docs/vault-format.md): `header || bodyNonce || ciphertext || tag`,
 * where `bodyCiphertext = XChaCha20-Poly1305(VaultKey, bodyNonce, JSON UTF-8, AAD_body)` and
 * `AAD_body = file[0 .. B+24)`, the whole header (every slot, unknown ones too) plus the nonce.
 *
 * This class does not parse or validate the header: the caller (W-05) computes `B`. A wrong header
 * fails the AEAD anyway, because every header byte is bound.
 *
 * Ownership: the caller's input arrays are never mutated. The caller owns and must zero its own
 * `vaultKey` array. The UTF-8 plaintext buffers made here are zeroed before returning.
 *
 * GC limitation (AGENTS §3.5): kotlinx.serialization works on [String], so the JSON text produced
 * while sealing and decoded while opening is an immutable [String] that cannot be zeroed and stays in
 * memory until collected, as does the [VaultContent] model itself. Only the byte arrays are zeroed.
 */
class VaultBody internal constructor(private val cipher: VaultBodyCipher) {
    /** Uses the device libsodium, loaded once on first use. */
    constructor() : this(VaultBodyCipher(AndroidSodium.lazySodium))

    private val random = SecureRandom()

    /**
     * Serializes [content] and encrypts it after [header] with a fresh random body nonce.
     *
     * @param header `file[0..B)`, everything before the body nonce. Not modified.
     * @param vaultKey the Vault Key, exactly [VaultFormat.KEY_SIZE] bytes. Not modified.
     * @param content the vault entries to seal.
     * @return the whole file bytes: `header || bodyNonce || ciphertext || tag`.
     * @throws IllegalArgumentException [header] is shorter than [VaultFormat.FIRST_SLOT_OFFSET] or the
     *   Vault Key length is wrong.
     */
    fun seal(header: ByteArray, vaultKey: ByteArray, content: VaultContent): ByteArray {
        require(header.size >= VaultFormat.FIRST_SLOT_OFFSET) { "Header is too short." }
        require(vaultKey.size == VaultFormat.KEY_SIZE) { "Vault Key has an invalid length." }

        val plaintext = VaultJson.encodeToString(VaultContent.serializer(), content).encodeToByteArray()
        try {
            // Not secret; a fresh CSPRNG nonce on every write (docs/vault-format.md).
            val nonce = ByteArray(VaultFormat.NONCE_SIZE).also { random.nextBytes(it) }
            val aad = header + nonce
            val ciphertext = ByteArray(plaintext.size + VaultFormat.TAG_SIZE)
            cipher.encrypt(vaultKey, plaintext, aad, nonce, ciphertext)
            return aad + ciphertext
        } finally {
            plaintext.fill(0)
        }
    }

    /**
     * Decrypts and parses the body of [file].
     *
     * @param file the whole vault bytes. Not modified.
     * @param bodyOffset `B`, the offset of the body nonce, computed by the caller; at least
     *   [VaultFormat.FIRST_SLOT_OFFSET].
     * @param vaultKey the Vault Key, exactly [VaultFormat.KEY_SIZE] bytes. Not modified.
     * @return the decrypted vault content.
     * @throws IllegalArgumentException [bodyOffset] is before the first slot or the Vault Key length is wrong.
     * @throws VaultFormatException the body is truncated, or the authenticated JSON is not a valid vault
     *   body. The message is fixed and there is no cause, so no plaintext can leak.
     * @throws VaultAuthenticationException the key is wrong or any byte of the file was altered. No
     *   reason is given.
     */
    fun open(file: ByteArray, bodyOffset: Int, vaultKey: ByteArray): VaultContent {
        require(bodyOffset >= VaultFormat.FIRST_SLOT_OFFSET) { "bodyOffset is before the first slot." }

        // Long arithmetic: an offset near Int.MAX_VALUE must report a truncated body, not overflow.
        if (file.size.toLong() - bodyOffset < VaultFormat.NONCE_SIZE + VaultFormat.TAG_SIZE) {
            throw VaultFormatException("Vault body is truncated.")
        }

        require(vaultKey.size == VaultFormat.KEY_SIZE) { "Vault Key has an invalid length." }

        val ciphertextOffset = bodyOffset + VaultFormat.NONCE_SIZE
        val aad = file.copyOfRange(0, ciphertextOffset)
        val nonce = file.copyOfRange(bodyOffset, ciphertextOffset)
        val ciphertext = file.copyOfRange(ciphertextOffset, file.size)
        val plaintext = ByteArray(ciphertext.size - VaultFormat.TAG_SIZE)
        try {
            cipher.decrypt(vaultKey, ciphertext, aad, nonce, plaintext)
            return parse(plaintext)
        } finally {
            plaintext.fill(0)
        }
    }

    private fun parse(plaintext: ByteArray): VaultContent =
        // kotlinx messages quote the JSON input (plaintext secrets): fixed message, no cause.
        try {
            VaultJson.decodeFromString(VaultContent.serializer(), plaintext.decodeToString(throwOnInvalidSequence = true))
        } catch (_: IllegalArgumentException) {
            throw VaultFormatException(INVALID_JSON_MESSAGE)
        } catch (_: CharacterCodingException) {
            throw VaultFormatException(INVALID_JSON_MESSAGE)
        }

    private companion object {
        const val INVALID_JSON_MESSAGE = "Vault body is not valid JSON."
    }
}
