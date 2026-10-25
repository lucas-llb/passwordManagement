package com.passwordmanagement.core.crypto

import com.goterl.lazysodium.interfaces.PwHash
import com.passwordmanagement.core.vault.KdfParameters
import com.passwordmanagement.core.vault.VaultFormat
import com.sun.jna.NativeLong
import java.nio.ByteBuffer
import java.nio.CharBuffer
import java.nio.charset.CodingErrorAction
import java.text.Normalizer

/**
 * Derives the 32-byte key-encryption key (KEK) of a key slot with Argon2id (libsodium via lazysodium).
 * Parallelism is fixed at 1 by the vault format v1 (docs/vault-format.md).
 *
 * Ownership: the caller's input arrays are never mutated. The caller owns and must zero its own
 * password/secret array and the `kek` array once it no longer needs them. The UTF-8 copy of the
 * password made here is zeroed before returning.
 *
 * GC limitation (AGENTS §3.5): when the password is not already in NFC, [Normalizer.normalize]
 * returns an immutable [String] that cannot be zeroed and stays in memory until collected. The JVM
 * has no span API to avoid it. The usual NFC input takes the no-copy path.
 */
class Argon2idKdf internal constructor(private val pwHash: PwHash.Native) {
    /** Uses the device libsodium, loaded once on first use. */
    constructor() : this(AndroidSodium.lazySodium)

    /**
     * Derives the KEK from a master password. The Argon2id input is `UTF-8(NFC(password))`,
     * with no trimming and no terminator.
     *
     * @param password the master password. Not modified; the caller zeros it.
     * @param parameters Argon2id cost parameters read from the key slot.
     * @param salt the slot salt, exactly [VaultFormat.SALT_SIZE] bytes.
     * @param kek receives the KEK; exactly [VaultFormat.KEY_SIZE] bytes. The caller zeros it.
     *   On failure during derivation it is zeroed before the exception propagates.
     * @throws IllegalArgumentException [parameters] are outside the format bounds, the salt or KEK
     *   length is wrong, or the password contains an invalid surrogate.
     * @throws IllegalStateException libsodium reported a derivation failure.
     */
    fun deriveKek(password: CharArray, parameters: KdfParameters, salt: ByteArray, kek: ByteArray) {
        validateArguments(parameters, salt, kek)

        val chars = CharBuffer.wrap(password)
        val source = if (Normalizer.isNormalized(chars, Normalizer.Form.NFC)) {
            chars
        } else {
            CharBuffer.wrap(Normalizer.normalize(chars, Normalizer.Form.NFC))
        }

        // UTF-8 upper bound, sized up front so the encoder never reallocates and leaves stray copies.
        val buf = ByteArray(source.length * 3)
        try {
            val out = ByteBuffer.wrap(buf)
            val encoder = Charsets.UTF_8.newEncoder()
                .onMalformedInput(CodingErrorAction.REPORT)
                .onUnmappableCharacter(CodingErrorAction.REPORT)

            // A lone or invalid surrogate is reported, never replaced with U+FFFD.
            require(!encoder.encode(source, out, true).isError) { "Password contains an invalid surrogate." }
            require(!encoder.flush(out).isError) { "Password contains an invalid surrogate." }

            derive(buf, out.position(), parameters, salt, kek)
        } finally {
            buf.fill(0)
        }
    }

    /**
     * Derives the KEK from raw secret bytes (for example the recovery-kit key), with no normalization.
     *
     * @param secret the secret bytes. Not modified; the caller zeros it.
     * @param parameters Argon2id cost parameters read from the key slot.
     * @param salt the slot salt, exactly [VaultFormat.SALT_SIZE] bytes.
     * @param kek receives the KEK; exactly [VaultFormat.KEY_SIZE] bytes. The caller zeros it.
     *   On failure during derivation it is zeroed before the exception propagates.
     * @throws IllegalArgumentException [parameters] are outside the format bounds, or the salt or KEK
     *   length is wrong.
     * @throws IllegalStateException libsodium reported a derivation failure.
     */
    fun deriveKek(secret: ByteArray, parameters: KdfParameters, salt: ByteArray, kek: ByteArray) {
        validateArguments(parameters, salt, kek)
        derive(secret, secret.size, parameters, salt, kek)
    }

    private fun validateArguments(parameters: KdfParameters, salt: ByteArray, kek: ByteArray) {
        require(parameters.isInRange) { "KDF parameters are outside the format bounds." }
        require(salt.size == VaultFormat.SALT_SIZE) { "Salt has an invalid length." }
        require(kek.size == VaultFormat.KEY_SIZE) { "KEK buffer has an invalid length." }
    }

    private fun derive(input: ByteArray, inputLength: Int, parameters: KdfParameters, salt: ByteArray, kek: ByteArray) {
        val succeeded = try {
            pwHash.cryptoPwHash(
                kek,
                kek.size,
                input,
                inputLength,
                salt,
                parameters.iterations,
                // libsodium takes the memory limit in bytes; the 1 GiB maximum fits a 32-bit NativeLong.
                NativeLong(parameters.memoryKiB * 1024),
                PwHash.Alg.PWHASH_ALG_ARGON2ID13,
            )
        } catch (e: Throwable) {
            kek.fill(0)
            throw e
        }

        if (!succeeded) {
            kek.fill(0)
            throw IllegalStateException("Argon2id key derivation failed.")
        }
    }
}
