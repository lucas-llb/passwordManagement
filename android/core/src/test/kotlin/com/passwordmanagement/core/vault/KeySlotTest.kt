package com.passwordmanagement.core.vault

import com.goterl.lazysodium.LazySodiumJava
import com.goterl.lazysodium.SodiumJava
import com.passwordmanagement.core.crypto.Argon2idKdf
import com.passwordmanagement.core.crypto.VaultKeyWrap
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertThrows
import org.junit.Test

class KeySlotTest {
    // ---- Known answer and layout ----

    @Test
    fun buildWrapAad_preambleAndSlot1_matchesKnownAnswerAad1() {
        val preamble = "PMVF".encodeToByteArray() + byteArrayOf(0x01, 0x00)

        assertArrayEquals(AAD1, KeySlot.buildWrapAad(preamble, SLOT1))
    }

    @Test
    fun buildWrapAad_slotAtOffsetInFile_usesFilePreambleAndSlotBytes() {
        assertArrayEquals(AAD2, KeySlot.buildWrapAad(F2, F2, 108))
    }

    @Test
    fun unwrapPasswordSlot_knownFileF1_returnsVaultKey() {
        val vaultKey = filled()

        keySlot.unwrapPasswordSlot(F1, 7, PASSWORD.toCharArray(), vaultKey)

        assertArrayEquals(VK, vaultKey)
    }

    @Test
    fun unwrapRecoveryKitSlot_knownFileF2SlotAtOffset108_returnsVaultKey() {
        val vaultKey = filled()

        keySlot.unwrapRecoveryKitSlot(F2, 108, SECRET, vaultKey)

        assertArrayEquals(VK, vaultKey)
    }

    @Test
    fun unwrap_inputs_areNotModified() {
        val file = F2.copyOf()
        val password = PASSWORD.toCharArray()
        val secret = SECRET.copyOf()

        keySlot.unwrapPasswordSlot(file, 7, password, ByteArray(32))
        keySlot.unwrapRecoveryKitSlot(file, 108, secret, ByteArray(32))

        assertArrayEquals(F2, file)
        assertArrayEquals(PASSWORD.toCharArray(), password)
        assertArrayEquals(SECRET, secret)
    }

    // ---- Wrong secret ----

    @Test
    fun unwrapPasswordSlot_oneCharacterChanged_throwsVaultAuthenticationExceptionAndZeroesOutput() {
        val password = PASSWORD.toCharArray().also { it[it.size - 1] = 'a' }
        val vaultKey = filled()

        val e = assertThrows(VaultAuthenticationException::class.java) {
            keySlot.unwrapPasswordSlot(F1, 7, password, vaultKey)
        }

        assertSameAsTamperedSalt(e)
        assertAllZero(vaultKey)
    }

    @Test
    fun unwrapRecoveryKitSlot_oneByteChanged_throwsVaultAuthenticationExceptionAndZeroesOutput() {
        val secret = SECRET.copyOf().also { it[0] = (it[0].toInt() xor 1).toByte() }
        val vaultKey = filled()

        val e = assertThrows(VaultAuthenticationException::class.java) {
            keySlot.unwrapRecoveryKitSlot(F2, 108, secret, vaultKey)
        }

        assertSameAsTamperedSalt(e)
        assertAllZero(vaultKey)
    }

    // ---- Tamper on F1 ----

    @Test
    fun unwrapPasswordSlot_eachBoundByteFlipped_throwsVaultAuthenticationExceptionAndZeroesOutput() {
        // Offsets that keep the slot format-valid: magic/version, memory and iterations low bytes,
        // salt, nonce and wrapped key.
        val offsets = (0..5) + listOf(11, 15) + (20..35) + (36..59) + (60..107)

        for (index in offsets) {
            val file = flip(F1, index)
            val vaultKey = filled()

            assertThrows("file[$index]", VaultAuthenticationException::class.java) {
                keySlot.unwrapPasswordSlot(file, 7, PASSWORD.toCharArray(), vaultKey)
            }

            assertAllZero(vaultKey, "file[$index]")
        }
    }

    // ---- Excluded from the AAD ----

    @Test
    fun unwrapPasswordSlot_slotCountChanged_stillReturnsVaultKey() {
        val file = F1.copyOf().also { it[6] = 5 }
        val vaultKey = filled()

        keySlot.unwrapPasswordSlot(file, 7, PASSWORD.toCharArray(), vaultKey)

        assertArrayEquals(VK, vaultKey)
    }

    @Test
    fun unwrapPasswordSlot_eachSlot2ByteChanged_stillReturnsVaultKey() {
        for (index in 108 until 209) {
            val vaultKey = filled()

            keySlot.unwrapPasswordSlot(flip(F2, index), 7, PASSWORD.toCharArray(), vaultKey)

            assertArrayEquals("file[$index]", VK, vaultKey)
        }
    }

    @Test
    fun unwrapRecoveryKitSlot_eachSlot1ByteChanged_stillReturnsVaultKey() {
        for (index in 7 until 108) {
            val vaultKey = filled()

            keySlot.unwrapRecoveryKitSlot(flip(F2, index), 108, SECRET, vaultKey)

            assertArrayEquals("file[$index]", VK, vaultKey)
        }
    }

    // ---- Round trip ----

    @Test
    fun createPasswordSlot_lowCost_writesExpectedLayout() {
        val slot = keySlot.createPasswordSlot(PASSWORD.toCharArray(), LOW_COST, VK)

        assertLayout(slot, 1)
    }

    @Test
    fun createRecoveryKitSlot_lowCost_writesExpectedLayout() {
        val slot = keySlot.createRecoveryKitSlot(SECRET, LOW_COST, VK)

        assertLayout(slot, 2)
    }

    @Test
    fun createPasswordSlot_embeddedAtOffset7_unwrapsToVaultKey() {
        val slot = keySlot.createPasswordSlot(PASSWORD.toCharArray(), LOW_COST, VK)
        val file = PREAMBLE + byteArrayOf(1) + slot
        val vaultKey = filled()

        keySlot.unwrapPasswordSlot(file, 7, PASSWORD.toCharArray(), vaultKey)

        assertArrayEquals(VK, vaultKey)
    }

    @Test
    fun createRecoveryKitSlot_embeddedAtOffset108_unwrapsToVaultKey() {
        val slot = keySlot.createRecoveryKitSlot(SECRET, LOW_COST, VK)
        val file = PREAMBLE + byteArrayOf(2) + SLOT1 + slot
        val vaultKey = filled()

        keySlot.unwrapRecoveryKitSlot(file, 108, SECRET, vaultKey)

        assertArrayEquals(VK, vaultKey)
    }

    @Test
    fun create_inputs_areNotModified() {
        val password = PASSWORD.toCharArray()
        val secret = SECRET.copyOf()
        val vaultKey = VK.copyOf()

        keySlot.createPasswordSlot(password, LOW_COST, vaultKey)
        keySlot.createRecoveryKitSlot(secret, LOW_COST, vaultKey)

        assertArrayEquals(PASSWORD.toCharArray(), password)
        assertArrayEquals(SECRET, secret)
        assertArrayEquals(VK, vaultKey)
    }

    @Test
    fun create_twiceWithSameInputs_usesFreshSaltNonceAndWrappedKey() {
        val pairs = listOf(
            keySlot.createPasswordSlot(PASSWORD.toCharArray(), LOW_COST, VK) to
                keySlot.createPasswordSlot(PASSWORD.toCharArray(), LOW_COST, VK),
            keySlot.createRecoveryKitSlot(SECRET, LOW_COST, VK) to
                keySlot.createRecoveryKitSlot(SECRET, LOW_COST, VK),
        )

        for ((first, second) in pairs) {
            assertFalse("salt", region(first, 13, 16).contentEquals(region(second, 13, 16)))
            assertFalse("nonce", region(first, 29, 24).contentEquals(region(second, 29, 24)))
            assertFalse("wrapped", region(first, 53, 48).contentEquals(region(second, 53, 48)))
        }
    }

    // ---- Arguments and format ----

    @Test
    fun createAndUnwrap_wrongVaultKeyLength_throwIllegalArgumentException() {
        for (length in listOf(31, 33)) {
            val vaultKey = ByteArray(length)

            assertThrows("create password vaultKey=$length", IllegalArgumentException::class.java) {
                keySlot.createPasswordSlot(PASSWORD.toCharArray(), LOW_COST, vaultKey)
            }
            assertThrows("create kit vaultKey=$length", IllegalArgumentException::class.java) {
                keySlot.createRecoveryKitSlot(SECRET, LOW_COST, vaultKey)
            }
            assertThrows("unwrap password vaultKey=$length", IllegalArgumentException::class.java) {
                keySlot.unwrapPasswordSlot(F1, 7, PASSWORD.toCharArray(), vaultKey)
            }
            assertThrows("unwrap kit vaultKey=$length", IllegalArgumentException::class.java) {
                keySlot.unwrapRecoveryKitSlot(F2, 108, SECRET, vaultKey)
            }
        }
    }

    @Test
    fun createAndUnwrap_wrongRecoveryKeyLength_throwIllegalArgumentException() {
        for (length in listOf(19, 21)) {
            val recoveryKey = ByteArray(length)

            assertThrows("create recoveryKey=$length", IllegalArgumentException::class.java) {
                keySlot.createRecoveryKitSlot(recoveryKey, LOW_COST, VK)
            }
            assertThrows("unwrap recoveryKey=$length", IllegalArgumentException::class.java) {
                keySlot.unwrapRecoveryKitSlot(F2, 108, recoveryKey, ByteArray(32))
            }
        }
    }

    @Test
    fun create_parametersOutOfRange_throwIllegalArgumentException() {
        for (parameters in listOf(KdfParameters(7, 2), KdfParameters(1024, 33))) {
            assertThrows("password $parameters", IllegalArgumentException::class.java) {
                keySlot.createPasswordSlot(PASSWORD.toCharArray(), parameters, VK)
            }
            assertThrows("kit $parameters", IllegalArgumentException::class.java) {
                keySlot.createRecoveryKitSlot(SECRET, parameters, VK)
            }
        }
    }

    @Test
    fun unwrap_slotOffsetBeforeFirstSlot_throwsIllegalArgumentException() {
        for (offset in listOf(-1, 0, 6)) {
            assertThrows("password offset=$offset", IllegalArgumentException::class.java) {
                keySlot.unwrapPasswordSlot(F1, offset, PASSWORD.toCharArray(), ByteArray(32))
            }
            assertThrows("kit offset=$offset", IllegalArgumentException::class.java) {
                keySlot.unwrapRecoveryKitSlot(F2, offset, SECRET, ByteArray(32))
            }
        }
    }

    @Test
    fun unwrap_slotTypeMismatch_throwsIllegalArgumentException() {
        assertThrows("password method on Slot2", IllegalArgumentException::class.java) {
            keySlot.unwrapPasswordSlot(F2, 108, PASSWORD.toCharArray(), ByteArray(32))
        }
        assertThrows("kit method on Slot1", IllegalArgumentException::class.java) {
            keySlot.unwrapRecoveryKitSlot(F2, 7, SECRET, ByteArray(32))
        }
    }

    @Test
    fun unwrap_truncatedFile_throwsVaultFormatException() {
        val file = F1.copyOf(107)

        assertThrows(VaultFormatException::class.java) {
            keySlot.unwrapPasswordSlot(file, 7, PASSWORD.toCharArray(), ByteArray(32))
        }
        assertThrows(VaultFormatException::class.java) {
            keySlot.unwrapRecoveryKitSlot(file, 7, SECRET, ByteArray(32))
        }
    }

    @Test
    fun unwrap_slotOffsetPastEndOfFile_throwsVaultFormatException() {
        for (offset in listOf(F1.size + 1, Int.MAX_VALUE - 10)) {
            assertThrows("password offset=$offset", VaultFormatException::class.java) {
                keySlot.unwrapPasswordSlot(F1, offset, PASSWORD.toCharArray(), ByteArray(32))
            }
            assertThrows("kit offset=$offset", VaultFormatException::class.java) {
                keySlot.unwrapRecoveryKitSlot(F1, offset, SECRET, ByteArray(32))
            }
        }
    }

    @Test
    fun unwrap_typeZeroSlot_throwsVaultFormatException() {
        val file = F1.copyOf().also { it[7] = 0 }

        assertThrows(VaultFormatException::class.java) {
            keySlot.unwrapPasswordSlot(file, 7, PASSWORD.toCharArray(), ByteArray(32))
        }
        assertThrows(VaultFormatException::class.java) {
            keySlot.unwrapRecoveryKitSlot(file, 7, SECRET, ByteArray(32))
        }
    }

    // ---- Helpers ----

    private fun assertSameAsTamperedSalt(actual: VaultAuthenticationException) {
        val tampered = assertThrows(VaultAuthenticationException::class.java) {
            keySlot.unwrapPasswordSlot(flip(F1, 20), 7, PASSWORD.toCharArray(), ByteArray(32))
        }

        assertEquals(tampered.javaClass, actual.javaClass)
        assertEquals("Vault authentication failed.", actual.message)
        assertEquals(tampered.message, actual.message)
        assertNull(actual.cause)
    }

    private fun assertLayout(slot: ByteArray, expectedType: Int) {
        assertEquals(101, slot.size)
        assertEquals(expectedType, slot[0].toInt())
        assertArrayEquals("slotLength", byteArrayOf(98, 0), region(slot, 1, 2))
        assertEquals("alg", 1, slot[3].toInt())
        assertArrayEquals("memory", byteArrayOf(0x00, 0x04, 0x00, 0x00), region(slot, 4, 4))
        assertArrayEquals("iterations", byteArrayOf(0x02, 0x00, 0x00, 0x00), region(slot, 8, 4))
        assertEquals("parallelism", 1, slot[12].toInt())
    }

    private fun region(bytes: ByteArray, start: Int, length: Int): ByteArray = bytes.copyOfRange(start, start + length)

    private fun flip(bytes: ByteArray, index: Int): ByteArray =
        bytes.copyOf().also { it[index] = (it[index].toInt() xor 1).toByte() }

    private fun filled(): ByteArray = ByteArray(32) { 0xAA.toByte() }

    private fun assertAllZero(bytes: ByteArray, message: String = "") {
        assertArrayEquals(message, ByteArray(bytes.size), bytes)
    }

    companion object {
        // Desktop libsodium: the host JVM cannot load lazysodium-android's .so.
        private val keySlot = LazySodiumJava(SodiumJava()).let { KeySlot(Argon2idKdf(it), VaultKeyWrap(it)) }

        private const val PASSWORD = "correct horse battery staple"
        private val SECRET = ByteArray(20) { (0x01 + it).toByte() }
        private val LOW_COST = KdfParameters(1024, 2)

        private val VK = ByteArray(32) { (0x20 + it).toByte() }
        private val N = ByteArray(24) { (0x40 + it).toByte() }

        // Known answers (W-03 plan), independent of libsodium. Copied verbatim; never computed here.
        private val AAD1 = hex("504d5646010001620001000400000200000001000102030405060708090a0b0c0d0e0f")
        private val AAD2 = hex("504d5646010002620001000400000200000001000102030405060708090a0b0c0d0e0f")

        private val W1 = hex(
            "2e8a17f179bdf5a651b7b489c0ce899222020515738dd5df49db643b04d135cc" +
                "002785a15a71b4953a359d825d34a848",
        )
        private val W2 = hex(
            "fa2d2e816d758690c33354f04cee974acc4fd17d6fcd7f7e914492971f8c5225" +
                "6ac12581922863d5d3e2b9e93c85a1f6",
        )

        // Slot k (101 bytes) = AADk[6..35) || N || Wk.
        private val PREAMBLE = AAD1.copyOfRange(0, 6)
        private val SLOT1 = AAD1.copyOfRange(6, 35) + N + W1
        private val SLOT2 = AAD2.copyOfRange(6, 35) + N + W2

        // F1 (108 bytes) = preamble | 01 | Slot1; F2 (209 bytes) = preamble | 02 | Slot1 | Slot2.
        private val F1 = PREAMBLE + byteArrayOf(1) + SLOT1
        private val F2 = PREAMBLE + byteArrayOf(2) + SLOT1 + SLOT2

        private fun hex(value: String): ByteArray =
            ByteArray(value.length / 2) { value.substring(it * 2, it * 2 + 2).toInt(16).toByte() }
    }
}
