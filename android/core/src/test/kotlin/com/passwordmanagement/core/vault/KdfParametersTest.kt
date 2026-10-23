package com.passwordmanagement.core.vault

import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertSame
import org.junit.Assert.assertThrows
import org.junit.Assert.assertTrue
import org.junit.Test
import java.nio.ByteBuffer
import java.nio.ByteOrder

class KdfParametersTest {
    @Test
    fun readFromSlot_literalLittleEndianBytes_returnsMemoryAndIterations() {
        val slot = ByteArray(101)
        slot[0] = 0x01
        slot[1] = 0x62
        slot[2] = 0x00
        slot[3] = 0x01
        byteArrayOf(0x00, 0x00, 0x01, 0x00).copyInto(slot, destinationOffset = 4)
        byteArrayOf(0x03, 0x00, 0x00, 0x00).copyInto(slot, destinationOffset = 8)
        slot[12] = 0x01

        val parameters = KdfParameters.readFromSlot(slot)

        assertEquals(65536L, parameters.memoryKiB)
        assertEquals(3L, parameters.iterations)
    }

    @Test
    fun readFromSlot_passwordAndRecoveryKitSlotTypes_areAccepted() {
        for (slotType in listOf(1, 2)) {
            val slot = buildSlot(slotType = slotType)

            val parameters = KdfParameters.readFromSlot(slot)

            assertEquals("slotType=$slotType", KdfParameters(1024, 2), parameters)
        }
    }

    @Test
    fun readFromSlot_edgeValues_areAccepted() {
        val edges = listOf(
            8L to 1L,
            1048576L to 32L,
            8L to 32L,
            1048576L to 1L,
        )
        for ((memoryKiB, iterations) in edges) {
            val slot = buildSlot(memoryKiB = memoryKiB, iterations = iterations)

            val parameters = KdfParameters.readFromSlot(slot)

            assertEquals(KdfParameters(memoryKiB, iterations), parameters)
            assertTrue(parameters.isInRange)
        }
    }

    @Test
    fun readFromSlot_slotLongerThanRecord_readsFromTheStart() {
        val slot = ByteArray(VaultFormat.KDF_SLOT_SIZE + 40)
        buildSlot().copyInto(slot)
        slot.fill(0xFF.toByte(), VaultFormat.KDF_SLOT_SIZE, slot.size)

        val parameters = KdfParameters.readFromSlot(slot)

        assertEquals(KdfParameters(1024, 2), parameters)
    }

    @Test
    fun readFromSlot_saltSlicedAtSlotSaltOffset_isTheSaltBytes() {
        val salt = ByteArray(VaultFormat.SALT_SIZE) { (0xA0 + it).toByte() }

        val slot = buildSlot(salt = salt)

        assertArrayEquals(
            salt,
            slot.copyOfRange(VaultFormat.SLOT_SALT_OFFSET, VaultFormat.SLOT_SALT_OFFSET + VaultFormat.SALT_SIZE),
        )
    }

    @Test
    fun readFromSlot_slotEmbeddedAfterPreamble_readsAtOffset() {
        val salt = ByteArray(VaultFormat.SALT_SIZE) { (0x10 + it).toByte() }
        val offset = VaultFormat.FIRST_SLOT_OFFSET
        val buffer = ByteArray(offset + VaultFormat.KDF_SLOT_SIZE)
        // Preamble bytes that would be rejected if the reader ignored the offset.
        buffer.fill(0xFF.toByte(), 0, offset)
        buildSlot(slotType = 2, memoryKiB = 4096, iterations = 5, salt = salt).copyInto(buffer, destinationOffset = offset)

        val parameters = KdfParameters.readFromSlot(buffer, offset = offset)

        assertEquals(KdfParameters(4096, 5), parameters)
        assertArrayEquals(
            salt,
            buffer.copyOfRange(
                offset + VaultFormat.SLOT_SALT_OFFSET,
                offset + VaultFormat.SLOT_SALT_OFFSET + VaultFormat.SALT_SIZE,
            ),
        )
    }

    @Test
    fun readFromSlot_offsetLeavesTooFewBytes_throwsVaultFormatException() {
        val slot = buildSlot()

        assertThrows(VaultFormatException::class.java) { KdfParameters.readFromSlot(slot, offset = 1) }
        assertThrows(VaultFormatException::class.java) { KdfParameters.readFromSlot(slot, offset = slot.size) }
        assertThrows(VaultFormatException::class.java) { KdfParameters.readFromSlot(slot, offset = slot.size + 10) }
        assertThrows(VaultFormatException::class.java) { KdfParameters.readFromSlot(slot, offset = Int.MAX_VALUE) }
    }

    @Test
    fun readFromSlot_negativeOffset_throwsIllegalArgumentException() {
        val slot = buildSlot()

        assertThrows(IllegalArgumentException::class.java) { KdfParameters.readFromSlot(slot, offset = -1) }
    }

    @Test
    fun default_matchesVaultFormatDefaults() {
        assertEquals(VaultFormat.DEFAULT_KDF_MEMORY_KIB, KdfParameters.DEFAULT.memoryKiB)
        assertEquals(VaultFormat.DEFAULT_KDF_ITERATIONS, KdfParameters.DEFAULT.iterations)
        assertEquals(65536L, KdfParameters.DEFAULT.memoryKiB)
        assertEquals(3L, KdfParameters.DEFAULT.iterations)
        assertTrue(KdfParameters.DEFAULT.isInRange)
    }

    @Test
    fun isInRange_values_followFormatBounds() {
        val cases = listOf(
            Triple(0L, 0L, false),
            Triple(7L, 3L, false),
            Triple(8L, 1L, true),
            Triple(1048576L, 32L, true),
            Triple(1048577L, 3L, false),
            Triple(65536L, 0L, false),
            Triple(65536L, 33L, false),
        )
        for ((memoryKiB, iterations, expected) in cases) {
            assertEquals(
                "memoryKiB=$memoryKiB iterations=$iterations",
                expected,
                KdfParameters(memoryKiB, iterations).isInRange,
            )
        }
    }

    @Test
    fun isInRange_zeroedParameters_isFalse() {
        // Stands in for the C# `default(KdfParameters)`.
        assertFalse(KdfParameters(0, 0).isInRange)
    }

    @Test
    fun readFromSlot_truncatedSlot_throwsVaultFormatException() {
        for (length in listOf(0, 100)) {
            val slot = buildSlot().copyOfRange(0, length)

            assertThrows("length=$length", VaultFormatException::class.java) { KdfParameters.readFromSlot(slot) }
        }
    }

    @Test
    fun readFromSlot_invalidSlotType_throwsVaultFormatException() {
        for (slotType in listOf(0, 3, 255)) {
            val slot = buildSlot(slotType = slotType)

            assertThrows("slotType=$slotType", VaultFormatException::class.java) { KdfParameters.readFromSlot(slot) }
        }
    }

    @Test
    fun readFromSlot_invalidSlotLength_throwsVaultFormatException() {
        for (slotLength in listOf(97, 99)) {
            val slot = buildSlot(slotLength = slotLength)

            assertThrows("slotLength=$slotLength", VaultFormatException::class.java) { KdfParameters.readFromSlot(slot) }
        }
    }

    @Test
    fun readFromSlot_invalidKdfAlgorithm_throwsVaultFormatException() {
        for (algorithm in listOf(0, 2)) {
            val slot = buildSlot(algorithm = algorithm)

            assertThrows("algorithm=$algorithm", VaultFormatException::class.java) { KdfParameters.readFromSlot(slot) }
        }
    }

    @Test
    fun readFromSlot_invalidParallelism_throwsVaultFormatException() {
        for (parallelism in listOf(0, 2)) {
            val slot = buildSlot(parallelism = parallelism)

            assertThrows("parallelism=$parallelism", VaultFormatException::class.java) {
                KdfParameters.readFromSlot(slot)
            }
        }
    }

    @Test
    fun readFromSlot_memoryOutOfRange_throwsVaultFormatException() {
        for (memoryKiB in listOf(7L, 1048577L, 0xFFFFFFFFL)) {
            val slot = buildSlot(memoryKiB = memoryKiB)

            assertThrows("memoryKiB=$memoryKiB", VaultFormatException::class.java) { KdfParameters.readFromSlot(slot) }
        }
    }

    @Test
    fun readFromSlot_iterationsOutOfRange_throwsVaultFormatException() {
        for (iterations in listOf(0L, 33L, 0xFFFFFFFFL)) {
            val slot = buildSlot(iterations = iterations)

            assertThrows("iterations=$iterations", VaultFormatException::class.java) { KdfParameters.readFromSlot(slot) }
        }
    }

    @Test
    fun vaultFormatException_constructors_keepMessageAndCause() {
        val inner = IllegalStateException("inner")

        assertNull(VaultFormatException().message)
        assertNull(VaultFormatException().cause)
        assertEquals("boom", VaultFormatException("boom").message)

        val wrapped = VaultFormatException("boom", inner)
        assertEquals("boom", wrapped.message)
        assertSame(inner, wrapped.cause)
    }

    // Builds a 101-byte slot by hand (independent of the code under test).
    private fun buildSlot(
        slotType: Int = 1,
        slotLength: Int = 98,
        algorithm: Int = 1,
        memoryKiB: Long = 1024,
        iterations: Long = 2,
        parallelism: Int = 1,
        salt: ByteArray = ByteArray(16),
    ): ByteArray {
        val slot = ByteArray(101)
        val buffer = ByteBuffer.wrap(slot).order(ByteOrder.LITTLE_ENDIAN)
        buffer.put(0, slotType.toByte())
        buffer.putShort(1, slotLength.toShort())
        buffer.put(3, algorithm.toByte())
        buffer.putInt(4, memoryKiB.toInt())
        buffer.putInt(8, iterations.toInt())
        buffer.put(12, parallelism.toByte())
        salt.copyInto(slot, destinationOffset = 13)
        return slot
    }
}
