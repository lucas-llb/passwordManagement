package com.passwordmanagement.core.vault

/**
 * Thrown when vault file bytes violate the v1 format (docs/vault-format.md).
 * Messages are in English and never contain secrets or file content.
 */
class VaultFormatException(
    message: String? = null,
    cause: Throwable? = null,
) : Exception(message, cause)
