package com.passwordmanagement.core.vault

import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertThrows
import org.junit.Assert.assertTrue
import org.junit.Test

class VaultHeaderTest {
    // ---- Layouts ----

    @Test
    fun parse_v0_returnsBodyOffsetAndPasswordSlotOnly() {
        val header = VaultHeader.parse(V0)

        assertEquals(108, header.bodyOffset)
        assertEquals(7, header.passwordSlotOffset)
        assertNull(header.recoveryKitSlotOffset)
        assertEquals(listOf(VaultSlotRange(1, 7, 101)), header.slots)
    }

    @Test
    fun parse_v2_returnsBothSlots() {
        val header = VaultHeader.parse(V2)

        assertEquals(209, header.bodyOffset)
        assertEquals(7, header.passwordSlotOffset)
        assertEquals(108, header.recoveryKitSlotOffset)
        assertEquals(listOf(VaultSlotRange(1, 7, 101), VaultSlotRange(2, 108, 101)), header.slots)
    }

    @Test
    fun parse_f1WithMinimalBody_returnsBodyOffset108() {
        val file = F1 + Z40

        assertEquals(148, file.size)
        assertEquals(108, VaultHeader.parse(file).bodyOffset)
    }

    @Test
    fun parse_unknownSlotTypes_areSkippedAndRecorded() {
        val file = header(3, SLOT1, unknown(3, 5), unknown(255, 0)) + Z40

        val header = VaultHeader.parse(file)

        assertEquals(119, header.bodyOffset)
        assertEquals(7, header.passwordSlotOffset)
        assertNull(header.recoveryKitSlotOffset)
        assertEquals(
            listOf(VaultSlotRange(1, 7, 101), VaultSlotRange(3, 108, 8), VaultSlotRange(255, 116, 3)),
            header.slots,
        )
    }

    @Test
    fun parse_slotsOutOfOrder_areAccepted() {
        val file = header(2, unknown(3, 5), SLOT1) + Z40

        val header = VaultHeader.parse(file)

        assertEquals(15, header.passwordSlotOffset)
        assertEquals(116, header.bodyOffset)
    }

    @Test
    fun parse_input_isNotModified() {
        val file = V2.copyOf()

        VaultHeader.parse(file)

        assertArrayEquals(V2, file)
    }

    // ---- Step 1: file length ----

    @Test
    fun parse_shorterThanMinimumFile_throwsTruncated() {
        for (length in listOf(0, 1, 147)) {
            assertFormatError("length=$length", "Vault file is truncated.", V0.copyOf(length))
        }
    }

    @Test
    fun parse_shortFileWithBadMagic_reportsTruncatedFirst() {
        val file = V0.copyOf(147).also { it[0] = 'X'.code.toByte() }

        assertFormatError("bad magic", "Vault file is truncated.", file)
    }

    // ---- Step 2: magic ----

    @Test
    fun parse_eachMagicByteFlipped_throwsInvalidMagic() {
        for (index in 0 until 4) {
            assertFormatError("index=$index", "Vault file has an invalid magic.", flip(V0, index))
        }
    }

    @Test
    fun parse_badMagicAndVersion2_reportsMagicFirst() {
        val file = flip(V0, 0).also { it[4] = 2 }

        assertFormatError("magic+version", "Vault file has an invalid magic.", file)
    }

    // ---- Step 3: version ----

    @Test
    fun parse_unsupportedVersion_throwsNotSupported() {
        for ((low, high) in listOf(0x00 to 0x00, 0x02 to 0x00, 0x00 to 0x01, 0xFF to 0xFF)) {
            val file = V0.copyOf().also {
                it[4] = low.toByte()
                it[5] = high.toByte()
            }

            assertFormatError("version=$low,$high", "Vault format version is not supported.", file)
        }
    }

    @Test
    fun parse_version2AndSlotCount0_reportsVersionFirst() {
        val file = V0.copyOf().also {
            it[4] = 2
            it[6] = 0
        }

        assertFormatError("version+count", "Vault format version is not supported.", file)
    }

    // ---- Step 4: slot count ----

    @Test
    fun parse_slotCountOutOfRange_throwsInvalidSlotCount() {
        for (count in listOf(0, 9, 255)) {
            val file = V0.copyOf().also { it[6] = count.toByte() }

            assertFormatError("count=$count", "Vault file has an invalid slot count.", file)
        }
    }

    // ---- Step 5: slot walk ----

    @Test
    fun parse_slotCountPastLastSlot_throwsKeySlotTruncated() {
        // The second slot then reads bodyNonce bytes as its header.
        val file = V0.copyOf().also { it[6] = 2 }

        assertFormatError("count=2", "Key slot is truncated.", file)
    }

    @Test
    fun parse_fewerThanThreeBytesLeftForSlot_throwsKeySlotTruncated() {
        val file = header(3, SLOT1, unknown(3, 35)) + ByteArray(2)

        assertEquals(148, file.size)
        assertFormatError("2 bytes left", "Key slot is truncated.", file)
    }

    @Test
    fun parse_slotType0_throwsInvalidType() {
        val file = V0.copyOf().also { it[7] = 0 }

        assertFormatError("type 0", "Key slot has an invalid type.", file)
    }

    @Test
    fun parse_badKdfFieldsInPasswordOrRecoverySlot_throwKdfParametersMessage() {
        val cases = listOf(
            Triple("slotLength 97", 1, byteArrayOf(97, 0)) to "Key slot has an invalid length.",
            Triple("slotLength 99", 1, byteArrayOf(99, 0)) to "Key slot has an invalid length.",
            Triple("kdfAlgorithm 2", 3, byteArrayOf(2)) to "Key slot uses an unsupported KDF algorithm.",
            Triple("parallelism 2", 12, byteArrayOf(2)) to "Key slot uses an unsupported KDF parallelism.",
            Triple("memory 7", 4, byteArrayOf(7, 0, 0, 0)) to "Key slot KDF parameters are out of range.",
            Triple("iterations 33", 8, byteArrayOf(33, 0, 0, 0)) to "Key slot KDF parameters are out of range.",
        )

        for ((file, slotOffset) in listOf(V0 to 7, V2 to 108)) {
            for ((change, message) in cases) {
                val (name, fieldOffset, value) = change
                val tampered = file.copyOf().also { value.copyInto(it, slotOffset + fieldOffset) }

                assertFormatError("$name at $slotOffset", message, tampered)
            }
        }
    }

    @Test
    fun parse_duplicatedSlotType_throwsDuplicated() {
        val passwordTwice = V2.copyOf().also { it[108] = 1 }
        val unknownTwice = header(3, SLOT1, unknown(3, 0), unknown(3, 0)) + Z40

        assertFormatError("type 1 twice", "Key slot type is duplicated.", passwordTwice)
        assertFormatError("type 3 twice", "Key slot type is duplicated.", unknownTwice)
    }

    @Test
    fun parse_noPasswordSlot_throwsNoPasswordSlot() {
        val recoveryOnly = V0.copyOf().also { it[7] = 2 }
        val unknownOnly = header(1, unknown(3, 98)) + Z40

        assertFormatError("type 2 only", "Vault file has no password slot.", recoveryOnly)
        assertFormatError("type 3 only", "Vault file has no password slot.", unknownOnly)
    }

    // ---- Step 6: body length ----

    @Test
    fun parse_bodyShorterThanNonceAndTag_throwsBodyTruncated() {
        assertFormatError("F2 + 39", "Vault body is truncated.", F2 + ByteArray(39))
    }

    @Test
    fun parse_f2WithMinimalBody_parses() {
        assertEquals(209, VaultHeader.parse(F2 + Z40).bodyOffset)
    }

    @Test
    fun parse_f1With39BodyBytes_throwsFileTruncated() {
        assertFormatError("F1 + 39", "Vault file is truncated.", F1 + ByteArray(39))
    }

    // ---- Robustness ----

    @Test
    fun parse_everyPrefixOfV2_parsesOrThrowsVaultFormatExceptionOnly() {
        for (length in 0..V2.size) {
            assertParsesOrFormatError("length=$length", V2.copyOf(length))
        }
    }

    @Test
    fun parse_everyHeaderByteXored_parsesOrThrowsVaultFormatExceptionOnly() {
        for (mask in listOf(0x01, 0xFF)) {
            for (index in 0 until 209) {
                val file = V2.copyOf().also { it[index] = (it[index].toInt() xor mask).toByte() }

                assertParsesOrFormatError("mask=$mask index=$index", file)
            }
        }
    }

    // ---- Build ----

    @Test
    fun build_passwordSlotOnly_returnsF1() {
        assertArrayEquals(F1, VaultHeader.build(listOf(SLOT1)))
    }

    @Test
    fun build_twoSlotsInEitherOrder_returnsF2() {
        assertArrayEquals(F2, VaultHeader.build(listOf(SLOT1, SLOT2)))
        assertArrayEquals(F2, VaultHeader.build(listOf(SLOT2, SLOT1)))
    }

    @Test
    fun build_unknownSlotFirst_sortsByType() {
        val built = VaultHeader.build(listOf(unknown(3, 5), SLOT1))

        assertArrayEquals(header(2, SLOT1, unknown(3, 5)), built)
    }

    @Test
    fun build_slicesOfParsedV2_rebuildsV2Header() {
        val slots = VaultHeader.parse(V2).slots.map { V2.copyOfRange(it.offset, it.offset + it.length) }

        assertArrayEquals(V2.copyOf(209), VaultHeader.build(slots))
    }

    @Test
    fun build_inputs_areNotModified() {
        val first = SLOT2.copyOf()
        val second = SLOT1.copyOf()
        val slots = listOf(first, second)

        VaultHeader.build(slots)

        assertArrayEquals(SLOT2, first)
        assertArrayEquals(SLOT1, second)
        assertTrue(slots[0] === first && slots[1] === second)
    }

    @Test
    fun build_invalidCountOrRecordLength_throwsIllegalArgumentException() {
        val cases = listOf(
            "empty" to emptyList(),
            "9 records" to listOf(SLOT1) + (3..10).map { unknown(it, 0) },
            "2-byte record" to listOf(SLOT1, byteArrayOf(3, 0)),
            "extra byte" to listOf(SLOT1 + byteArrayOf(0)),
            "missing byte" to listOf(SLOT1.copyOf(SLOT1.size - 1)),
        )

        for ((name, slots) in cases) {
            val e = assertThrows(name, IllegalArgumentException::class.java) { VaultHeader.build(slots) }

            assertEquals(name, IllegalArgumentException::class.java, e.javaClass)
        }
    }

    @Test
    fun build_recordsNotFormingValidHeader_throwsIllegalArgumentExceptionWithFormatCause() {
        val algorithm2 = SLOT1.copyOf().also { it[3] = 2 }
        val cases = listOf(
            Triple("Slot1 twice", listOf(SLOT1, SLOT1), "Key slot type is duplicated."),
            Triple("Slot2 alone", listOf(SLOT2), "Vault file has no password slot."),
            Triple("type 0", listOf(byteArrayOf(0, 0, 0)), "Key slot has an invalid type."),
            Triple("kdfAlgorithm 2", listOf(algorithm2), "Key slot uses an unsupported KDF algorithm."),
        )

        for ((name, slots, causeMessage) in cases) {
            val e = assertThrows(name, IllegalArgumentException::class.java) { VaultHeader.build(slots) }

            assertEquals(name, IllegalArgumentException::class.java, e.javaClass)
            assertEquals(name, "Slots do not form a valid v1 header.", e.message)
            assertEquals(name, VaultFormatException::class.java, e.cause?.javaClass)
            assertEquals(name, causeMessage, e.cause?.message)
        }
    }

    // ---- Helpers ----

    private fun assertFormatError(name: String, message: String, file: ByteArray) {
        val e = assertThrows(name, VaultFormatException::class.java) { VaultHeader.parse(file) }

        assertEquals(name, VaultFormatException::class.java, e.javaClass)
        assertEquals(name, message, e.message)
    }

    private fun assertParsesOrFormatError(name: String, file: ByteArray) {
        try {
            VaultHeader.parse(file)
        } catch (e: VaultFormatException) {
            assertEquals(name, VaultFormatException::class.java, e.javaClass)
        } catch (e: Exception) {
            throw AssertionError("$name: unexpected ${e.javaClass.name}", e)
        }
    }

    private fun flip(file: ByteArray, index: Int): ByteArray =
        file.copyOf().also { it[index] = (it[index].toInt() xor 1).toByte() }

    companion object {
        private val Z40 = ByteArray(40)

        /** `[type, n LE16, n x 0xEE]`. */
        private fun unknown(type: Int, n: Int): ByteArray =
            byteArrayOf(type.toByte(), n.toByte(), (n shr 8).toByte()) + ByteArray(n) { 0xEE.toByte() }

        /** `PMVF 01 00 | count | records`. */
        private fun header(count: Int, vararg records: ByteArray): ByteArray =
            records.fold(PREAMBLE + byteArrayOf(count.toByte())) { acc, record -> acc + record }

        // Known answers (W-03/W-04 plans), independent of libsodium. Copied verbatim; never computed here.
        private val BN = ByteArray(24) { (0x60 + it).toByte() }
        private val WRAP_N = ByteArray(24) { (0x40 + it).toByte() }
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
        private val PREAMBLE = AAD1.copyOfRange(0, 6)
        private val SLOT1 = AAD1.copyOfRange(6, 35) + WRAP_N + W1
        private val SLOT2 = AAD2.copyOfRange(6, 35) + WRAP_N + W2

        // F1 = preamble | 01 | Slot1 (108 B); F2 = preamble | 02 | Slot1 | Slot2 (209 B).
        private val F1 = PREAMBLE + byteArrayOf(1) + SLOT1
        private val F2 = PREAMBLE + byteArrayOf(2) + SLOT1 + SLOT2

        private val C0 = hex("93052548f103f2a4d923af544fdcf72089107a0fb053d81aed759ce00dcb")
        private val C2 = hex("93052548f103f2a4d923af544fdc6c3aa10b7fa64c287cbe96a51fa2167a")

        // V0 = F1 || BN || C0 (162 B); V2 = F2 || BN || C2 (263 B).
        private val V0 = F1 + BN + C0
        private val V2 = F2 + BN + C2

        private fun hex(value: String): ByteArray =
            ByteArray(value.length / 2) { value.substring(it * 2, it * 2 + 2).toInt(16).toByte() }
    }
}
