package com.passwordmanagement.core.vault

/**
 * Constants of the vault file format. Source of truth: docs/vault-format.md.
 * Any change here must bump [VERSION] and be mirrored on Windows.
 */
object VaultFormat {
    val MAGIC: ByteArray get() = "PMVF".encodeToByteArray()
    const val MAGIC_SIZE: Int = 4

    const val VERSION: Int = 1
    const val VERSION_OFFSET: Int = 4
    const val SLOT_COUNT_OFFSET: Int = 6
    const val FIRST_SLOT_OFFSET: Int = 7
    const val PREAMBLE_SIZE: Int = 6
    const val MIN_SLOT_COUNT: Int = 1
    const val MAX_SLOT_COUNT: Int = 8

    const val SLOT_TYPE_PASSWORD: Int = 1
    const val SLOT_TYPE_RECOVERY_KIT: Int = 2
    const val SLOT_HEADER_SIZE: Int = 3
    const val SLOT_TYPE_OFFSET: Int = 0
    const val SLOT_LENGTH_OFFSET: Int = 1
    const val SLOT_KDF_ALGORITHM_OFFSET: Int = 3
    const val SLOT_KDF_MEMORY_OFFSET: Int = 4
    const val SLOT_KDF_ITERATIONS_OFFSET: Int = 8
    const val SLOT_KDF_PARALLELISM_OFFSET: Int = 12
    const val SLOT_SALT_OFFSET: Int = 13
    const val SLOT_WRAP_NONCE_OFFSET: Int = 29
    const val SLOT_WRAPPED_KEY_OFFSET: Int = 53
    const val KDF_SLOT_BODY_SIZE: Int = 98
    const val KDF_SLOT_SIZE: Int = 101
    const val WRAP_AAD_SIZE: Int = 35

    const val KDF_ARGON2ID: Int = 1
    const val DEFAULT_KDF_MEMORY_KIB: Long = 64L * 1024
    const val DEFAULT_KDF_ITERATIONS: Long = 3
    const val DEFAULT_KDF_PARALLELISM: Int = 1
    const val MIN_KDF_MEMORY_KIB: Long = 8
    const val MAX_KDF_MEMORY_KIB: Long = 1048576
    const val MIN_KDF_ITERATIONS: Long = 1
    const val MAX_KDF_ITERATIONS: Long = 32

    const val SALT_SIZE: Int = 16
    const val KEY_SIZE: Int = 32
    const val NONCE_SIZE: Int = 24
    const val TAG_SIZE: Int = 16
    const val WRAPPED_KEY_SIZE: Int = 48
    const val MIN_FILE_SIZE: Int = 148

    const val RECOVERY_KEY_SIZE: Int = 20
    const val RECOVERY_KEY_ALPHABET: String = "0123456789ABCDEFGHJKMNPQRSTVWXYZ"
    const val RECOVERY_KEY_GROUP_LENGTH: Int = 4
    const val RECOVERY_KEY_GROUP_COUNT: Int = 8
    const val RECOVERY_KEY_SEPARATOR: Char = '-'

    const val FILE_EXTENSION: String = ".pmvault"
}
