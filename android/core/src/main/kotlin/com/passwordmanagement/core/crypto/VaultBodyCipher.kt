package com.passwordmanagement.core.crypto

import com.goterl.lazysodium.interfaces.AEAD
import com.passwordmanagement.core.vault.VaultAuthenticationException
import com.passwordmanagement.core.vault.VaultFormat

/**
 * Encrypts and decrypts the vault body with XChaCha20-Poly1305 (libsodium via lazysodium), using the
 * combined mode: `bodyCiphertext = ciphertext || tag`. The caller supplies the 32-byte Vault Key, the
 * `AAD_body = file[0 .. B+24)` (any length) and the 24-byte `bodyNonce` as defined in
 * docs/vault-format.md.
 *
 * Every length is checked with `require` before any native call: lazysodium performs no bounds
 * checks, so a wrong plaintext/ciphertext size relation would be an out-of-bounds read or write in
 * native code.
 *
 * Ownership: the caller's input arrays are never mutated. The caller owns and must zero its own
 * `vaultKey` and plaintext arrays once it no longer needs them.
 */
internal class VaultBodyCipher(private val aead: AEAD.Native) {
    /**
     * Encrypts [plaintext] into [ciphertext] (`ciphertext || tag`).
     *
     * @param vaultKey the Vault Key, exactly [VaultFormat.KEY_SIZE] bytes. Not modified.
     * @param plaintext the UTF-8 JSON body. Not modified.
     * @param aad the additional authenticated data, any length. Not modified.
     * @param nonce the body nonce, exactly [VaultFormat.NONCE_SIZE] bytes. Never reuse a nonce with a key.
     * @param ciphertext receives the result; exactly `plaintext.size +` [VaultFormat.TAG_SIZE] bytes.
     * @throws IllegalArgumentException a length is wrong.
     * @throws IllegalStateException libsodium reported an encryption failure.
     */
    fun encrypt(vaultKey: ByteArray, plaintext: ByteArray, aad: ByteArray, nonce: ByteArray, ciphertext: ByteArray) {
        require(vaultKey.size == VaultFormat.KEY_SIZE) { "Vault Key has an invalid length." }
        require(nonce.size == VaultFormat.NONCE_SIZE) { "Nonce has an invalid length." }
        require(ciphertext.size - VaultFormat.TAG_SIZE == plaintext.size) { "Ciphertext buffer has an invalid length." }

        val succeeded = aead.cryptoAeadXChaCha20Poly1305IetfEncrypt(
            ciphertext,
            null,
            plaintext,
            plaintext.size.toLong(),
            aad,
            aad.size.toLong(),
            null,
            nonce,
            vaultKey,
        )
        check(succeeded) { "Vault body encryption failed." }
    }

    /**
     * Decrypts [ciphertext] into [plaintext].
     *
     * @param vaultKey the Vault Key, exactly [VaultFormat.KEY_SIZE] bytes. Not modified.
     * @param ciphertext the body (`ciphertext || tag`), at least [VaultFormat.TAG_SIZE] bytes. Not modified.
     * @param aad the additional authenticated data, any length. Not modified.
     * @param nonce the body nonce, exactly [VaultFormat.NONCE_SIZE] bytes. Not modified.
     * @param plaintext receives the JSON body; exactly `ciphertext.size -` [VaultFormat.TAG_SIZE] bytes.
     *   The caller zeros it. On any failure after the lengths are validated it is zeroed before the
     *   exception propagates.
     * @throws IllegalArgumentException a length is wrong.
     * @throws VaultAuthenticationException the key is wrong or the AAD, nonce or ciphertext was altered.
     *   No reason is given.
     */
    fun decrypt(vaultKey: ByteArray, ciphertext: ByteArray, aad: ByteArray, nonce: ByteArray, plaintext: ByteArray) {
        require(vaultKey.size == VaultFormat.KEY_SIZE) { "Vault Key has an invalid length." }
        require(nonce.size == VaultFormat.NONCE_SIZE) { "Nonce has an invalid length." }
        require(ciphertext.size >= VaultFormat.TAG_SIZE) { "Ciphertext has an invalid length." }
        require(plaintext.size == ciphertext.size - VaultFormat.TAG_SIZE) { "Plaintext buffer has an invalid length." }

        val authenticated = try {
            aead.cryptoAeadXChaCha20Poly1305IetfDecrypt(
                plaintext,
                null,
                null,
                ciphertext,
                ciphertext.size.toLong(),
                aad,
                aad.size.toLong(),
                nonce,
                vaultKey,
            )
        } catch (e: Throwable) {
            plaintext.fill(0)
            throw e
        }

        if (!authenticated) {
            plaintext.fill(0)
            throw VaultAuthenticationException()
        }
    }
}
