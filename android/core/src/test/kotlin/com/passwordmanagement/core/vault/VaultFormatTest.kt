package com.passwordmanagement.core.vault

import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class VaultFormatTest {
    @Test
    fun magic_isAsciiPmvf() {
        assertArrayEquals(byteArrayOf(0x50, 0x4D, 0x56, 0x46), VaultFormat.MAGIC)
        assertEquals(4, VaultFormat.MAGIC_SIZE)
        assertEquals(VaultFormat.MAGIC_SIZE, VaultFormat.MAGIC.size)
    }

    @Test
    fun magic_returnsFreshArray() {
        VaultFormat.MAGIC[0] = 0

        assertEquals(0x50, VaultFormat.MAGIC[0].toInt())
    }

    @Test
    fun version_kdf_extension_matchSpec() {
        assertEquals(1, VaultFormat.VERSION)
        assertEquals(1, VaultFormat.KDF_ARGON2ID)
        assertEquals(".pmvault", VaultFormat.FILE_EXTENSION)
    }

    @Test
    fun header_offsets_areLiteralAndContiguous() {
        assertEquals(4, VaultFormat.VERSION_OFFSET)
        assertEquals(6, VaultFormat.SLOT_COUNT_OFFSET)
        assertEquals(7, VaultFormat.FIRST_SLOT_OFFSET)
        assertEquals(6, VaultFormat.PREAMBLE_SIZE)
        assertEquals(1, VaultFormat.MIN_SLOT_COUNT)
        assertEquals(8, VaultFormat.MAX_SLOT_COUNT)

        assertEquals(VaultFormat.MAGIC_SIZE, VaultFormat.VERSION_OFFSET)
        assertEquals(VaultFormat.VERSION_OFFSET + 2, VaultFormat.SLOT_COUNT_OFFSET)
        assertEquals(VaultFormat.SLOT_COUNT_OFFSET + 1, VaultFormat.FIRST_SLOT_OFFSET)
        assertEquals(VaultFormat.SLOT_COUNT_OFFSET, VaultFormat.PREAMBLE_SIZE)
    }

    @Test
    fun slotTypes_areLiteral() {
        assertEquals(1, VaultFormat.SLOT_TYPE_PASSWORD)
        assertEquals(2, VaultFormat.SLOT_TYPE_RECOVERY_KIT)
        assertEquals(3, VaultFormat.SLOT_HEADER_SIZE)
    }

    @Test
    fun kdfSlot_fieldsAreLiteralAndContiguous_andEndAtSlotSize() {
        assertEquals(0, VaultFormat.SLOT_TYPE_OFFSET)
        assertEquals(1, VaultFormat.SLOT_LENGTH_OFFSET)
        assertEquals(3, VaultFormat.SLOT_KDF_ALGORITHM_OFFSET)
        assertEquals(4, VaultFormat.SLOT_KDF_MEMORY_OFFSET)
        assertEquals(8, VaultFormat.SLOT_KDF_ITERATIONS_OFFSET)
        assertEquals(12, VaultFormat.SLOT_KDF_PARALLELISM_OFFSET)
        assertEquals(13, VaultFormat.SLOT_SALT_OFFSET)
        assertEquals(29, VaultFormat.SLOT_WRAP_NONCE_OFFSET)
        assertEquals(53, VaultFormat.SLOT_WRAPPED_KEY_OFFSET)
        assertEquals(98, VaultFormat.KDF_SLOT_BODY_SIZE)
        assertEquals(101, VaultFormat.KDF_SLOT_SIZE)
        assertEquals(35, VaultFormat.WRAP_AAD_SIZE)

        // Offset chain with field sizes 1, 2, 1, 4, 4, 1, SALT, NONCE, WRAPPED_KEY.
        val end = VaultFormat.SLOT_TYPE_OFFSET + 1 +
            2 +
            1 +
            4 +
            4 +
            1 +
            VaultFormat.SALT_SIZE +
            VaultFormat.NONCE_SIZE +
            VaultFormat.WRAPPED_KEY_SIZE
        assertEquals(VaultFormat.KDF_SLOT_SIZE, end)
        assertEquals(VaultFormat.KDF_SLOT_SIZE - VaultFormat.SLOT_HEADER_SIZE, VaultFormat.KDF_SLOT_BODY_SIZE)
        assertEquals(VaultFormat.PREAMBLE_SIZE + VaultFormat.SLOT_WRAP_NONCE_OFFSET, VaultFormat.WRAP_AAD_SIZE)
    }

    @Test
    fun cryptoSizes_areLiteral_andConsistent() {
        assertEquals(16, VaultFormat.SALT_SIZE)
        assertEquals(32, VaultFormat.KEY_SIZE)
        assertEquals(24, VaultFormat.NONCE_SIZE)
        assertEquals(16, VaultFormat.TAG_SIZE)
        assertEquals(48, VaultFormat.WRAPPED_KEY_SIZE)

        assertEquals(VaultFormat.KEY_SIZE + VaultFormat.TAG_SIZE, VaultFormat.WRAPPED_KEY_SIZE)
    }

    @Test
    fun kdfBounds_areLiteral_andContainDefaults() {
        assertEquals(64L * 1024, VaultFormat.DEFAULT_KDF_MEMORY_KIB)
        assertEquals(3L, VaultFormat.DEFAULT_KDF_ITERATIONS)
        assertEquals(1, VaultFormat.DEFAULT_KDF_PARALLELISM)
        assertEquals(8L, VaultFormat.MIN_KDF_MEMORY_KIB)
        assertEquals(1048576L, VaultFormat.MAX_KDF_MEMORY_KIB)
        assertEquals(1L, VaultFormat.MIN_KDF_ITERATIONS)
        assertEquals(32L, VaultFormat.MAX_KDF_ITERATIONS)

        assertTrue(VaultFormat.MIN_KDF_MEMORY_KIB <= VaultFormat.DEFAULT_KDF_MEMORY_KIB)
        assertTrue(VaultFormat.DEFAULT_KDF_MEMORY_KIB <= VaultFormat.MAX_KDF_MEMORY_KIB)
        assertTrue(VaultFormat.MIN_KDF_ITERATIONS <= VaultFormat.DEFAULT_KDF_ITERATIONS)
        assertTrue(VaultFormat.DEFAULT_KDF_ITERATIONS <= VaultFormat.MAX_KDF_ITERATIONS)
    }

    @Test
    fun minFileSize_isOneSlotPlusBodyNonceAndTag() {
        assertEquals(148, VaultFormat.MIN_FILE_SIZE)
        assertEquals(
            VaultFormat.MIN_FILE_SIZE,
            VaultFormat.FIRST_SLOT_OFFSET + VaultFormat.KDF_SLOT_SIZE + VaultFormat.NONCE_SIZE + VaultFormat.TAG_SIZE,
        )
    }

    @Test
    fun recoveryKey_encodesExactlyIntoGroups() {
        assertEquals(20, VaultFormat.RECOVERY_KEY_SIZE)
        assertEquals(4, VaultFormat.RECOVERY_KEY_GROUP_LENGTH)
        assertEquals(8, VaultFormat.RECOVERY_KEY_GROUP_COUNT)
        assertEquals(
            VaultFormat.RECOVERY_KEY_SIZE * 8,
            VaultFormat.RECOVERY_KEY_GROUP_LENGTH * VaultFormat.RECOVERY_KEY_GROUP_COUNT * 5,
        )
        assertTrue(VaultFormat.RECOVERY_KEY_SIZE * 8 >= 128)
    }

    @Test
    fun recoveryKey_alphabet_has32DistinctCrockfordSymbols() {
        val alphabet = VaultFormat.RECOVERY_KEY_ALPHABET

        assertEquals("0123456789ABCDEFGHJKMNPQRSTVWXYZ", alphabet)
        assertEquals(32, alphabet.toSet().size)
        assertTrue(alphabet.all { it in '0'..'9' || it in 'A'..'Z' })
        assertTrue(listOf('I', 'L', 'O', 'U').none { it in alphabet })
    }

    @Test
    fun recoveryKey_separator_isHyphen() {
        assertEquals('-', VaultFormat.RECOVERY_KEY_SEPARATOR)
    }
}
