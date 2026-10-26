package com.passwordmanagement.core.vault

/**
 * Thrown when a key slot cannot be unwrapped or the vault body cannot be decrypted: the password,
 * recovery kit or Vault Key is wrong, or the file was tampered with (a slot's AAD, nonce or wrapped
 * key, or any header, body nonce, ciphertext or tag byte bound by the body AEAD). It deliberately
 * carries no reason, no cause and no secret, so a wrong secret and a tampered file look exactly the
 * same to the caller.
 */
class VaultAuthenticationException : Exception("Vault authentication failed.")
