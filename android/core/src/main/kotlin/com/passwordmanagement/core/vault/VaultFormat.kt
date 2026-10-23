package com.passwordmanagement.core.vault

/**
 * Constants of the vault file format. Source of truth: docs/vault-format.md.
 * Any change here must bump [VERSION] and be mirrored on Windows.
 */
object VaultFormat {
    val MAGIC: ByteArray get() = "PMVF".encodeToByteArray()

    const val VERSION: Int = 1

    const val KDF_ARGON2ID: Int = 1
    const val DEFAULT_KDF_MEMORY_KIB: Long = 64L * 1024
    const val DEFAULT_KDF_ITERATIONS: Long = 3
    const val DEFAULT_KDF_PARALLELISM: Int = 1

    const val SALT_SIZE: Int = 16
    const val KEY_SIZE: Int = 32
    const val NONCE_SIZE: Int = 24
    const val TAG_SIZE: Int = 16

    const val FILE_EXTENSION: String = ".pmvault"
}
