package com.passwordmanagement.core.crypto

import com.goterl.lazysodium.interfaces.AEAD
import com.passwordmanagement.core.vault.VaultAuthenticationException
import com.passwordmanagement.core.vault.VaultFormat

/**
 * Wraps and unwraps the 32-byte Vault Key of a key slot with XChaCha20-Poly1305 (libsodium via
 * lazysodium), using the combined mode: `wrappedVaultKey = ciphertext || tag` (48 bytes).
 * The caller supplies the KEK (derived with [Argon2idKdf]), the 35-byte `AAD_wrap` and the 24-byte
 * `wrapNonce` as defined in docs/vault-format.md.
 *
 * Every length is checked with `require` before any native call: lazysodium performs no bounds
 * checks, so a short array would be an out-of-bounds read or write in native code.
 *
 * Ownership: the caller's input arrays are never mutated. The caller owns and must zero its own `kek`
 * and `vaultKey` arrays once it no longer needs them.
 */
internal class VaultKeyWrap(private val aead: AEAD.Native) {
    /**
     * Encrypts [vaultKey] into [wrappedVaultKey] (`ciphertext || tag`).
     *
     * @param kek the key-encryption key, exactly [VaultFormat.KEY_SIZE] bytes. Not modified.
     * @param vaultKey the Vault Key, exactly [VaultFormat.KEY_SIZE] bytes. Not modified.
     * @param aad the additional authenticated data, exactly [VaultFormat.WRAP_AAD_SIZE] bytes.
     * @param nonce the wrap nonce, exactly [VaultFormat.NONCE_SIZE] bytes. Never reuse a nonce with a KEK.
     * @param wrappedVaultKey receives the result; exactly [VaultFormat.WRAPPED_KEY_SIZE] bytes.
     * @throws IllegalArgumentException a length is wrong.
     * @throws IllegalStateException libsodium reported an encryption failure.
     */
    fun wrap(kek: ByteArray, vaultKey: ByteArray, aad: ByteArray, nonce: ByteArray, wrappedVaultKey: ByteArray) {
        require(kek.size == VaultFormat.KEY_SIZE) { "KEK has an invalid length." }
        require(vaultKey.size == VaultFormat.KEY_SIZE) { "Vault Key has an invalid length." }
        require(aad.size == VaultFormat.WRAP_AAD_SIZE) { "AAD has an invalid length." }
        require(nonce.size == VaultFormat.NONCE_SIZE) { "Nonce has an invalid length." }
        require(wrappedVaultKey.size == VaultFormat.WRAPPED_KEY_SIZE) { "Wrapped Vault Key buffer has an invalid length." }

        val succeeded = aead.cryptoAeadXChaCha20Poly1305IetfEncrypt(
            wrappedVaultKey,
            null,
            vaultKey,
            vaultKey.size.toLong(),
            aad,
            aad.size.toLong(),
            null,
            nonce,
            kek,
        )
        check(succeeded) { "Vault Key wrapping failed." }
    }

    /**
     * Decrypts [wrappedVaultKey] into [vaultKey].
     *
     * @param kek the key-encryption key, exactly [VaultFormat.KEY_SIZE] bytes. Not modified.
     * @param wrappedVaultKey the wrapped Vault Key (`ciphertext || tag`), exactly
     *   [VaultFormat.WRAPPED_KEY_SIZE] bytes. Not modified.
     * @param aad the additional authenticated data, exactly [VaultFormat.WRAP_AAD_SIZE] bytes.
     * @param nonce the wrap nonce, exactly [VaultFormat.NONCE_SIZE] bytes.
     * @param vaultKey receives the Vault Key; exactly [VaultFormat.KEY_SIZE] bytes. The caller zeros it.
     *   On any failure after the lengths are validated it is zeroed before the exception propagates.
     * @throws IllegalArgumentException a length is wrong.
     * @throws VaultAuthenticationException the KEK is wrong or the AAD, nonce or wrapped key was altered.
     *   No reason is given.
     */
    fun unwrap(kek: ByteArray, wrappedVaultKey: ByteArray, aad: ByteArray, nonce: ByteArray, vaultKey: ByteArray) {
        require(kek.size == VaultFormat.KEY_SIZE) { "KEK has an invalid length." }
        require(wrappedVaultKey.size == VaultFormat.WRAPPED_KEY_SIZE) { "Wrapped Vault Key has an invalid length." }
        require(aad.size == VaultFormat.WRAP_AAD_SIZE) { "AAD has an invalid length." }
        require(nonce.size == VaultFormat.NONCE_SIZE) { "Nonce has an invalid length." }
        require(vaultKey.size == VaultFormat.KEY_SIZE) { "Vault Key buffer has an invalid length." }

        val authenticated = try {
            aead.cryptoAeadXChaCha20Poly1305IetfDecrypt(
                vaultKey,
                null,
                null,
                wrappedVaultKey,
                wrappedVaultKey.size.toLong(),
                aad,
                aad.size.toLong(),
                nonce,
                kek,
            )
        } catch (e: Throwable) {
            vaultKey.fill(0)
            throw e
        }

        if (!authenticated) {
            vaultKey.fill(0)
            throw VaultAuthenticationException()
        }
    }
}
