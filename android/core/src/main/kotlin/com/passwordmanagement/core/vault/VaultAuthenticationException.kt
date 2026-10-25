package com.passwordmanagement.core.vault

/**
 * Thrown when a key slot cannot be unwrapped: the password or recovery kit is wrong, or the slot
 * (AAD, nonce or wrapped key) was tampered with. It deliberately carries no reason, no cause and no
 * secret, so a wrong secret and a tampered file look exactly the same to the caller.
 */
class VaultAuthenticationException : Exception("Vault authentication failed.")
