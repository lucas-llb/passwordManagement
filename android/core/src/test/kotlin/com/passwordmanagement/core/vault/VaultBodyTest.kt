package com.passwordmanagement.core.vault

import com.goterl.lazysodium.LazySodiumJava
import com.goterl.lazysodium.SodiumJava
import com.passwordmanagement.core.crypto.VaultBodyCipher
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertThrows
import org.junit.Assert.assertTrue
import org.junit.Test

class VaultBodyTest {
    // ---- Known answers ----

    @Test
    fun open_knownFileV0_returnsNoEntries() {
        assertTrue(body.open(V0, 108, VK).entries.isEmpty())
    }

    @Test
    fun open_knownFileV2AtOffset209_returnsNoEntries() {
        assertTrue(body.open(V2, 209, VK).entries.isEmpty())
    }

    @Test
    fun open_knownFileV1_returnsEntryAndIgnoresUnknownField() {
        val entries = body.open(V1, 108, VK).entries

        assertEquals(1, entries.size)
        val entry = entries[0]
        assertEquals("0b6f7c1e-2f5d-4a6b-9a8e-3c1d2e4f5a6b", entry.id)
        assertEquals("Café 🔑", entry.title)
        assertEquals("Café 🔑", entry.notes)
        assertEquals("user@example.com", entry.username)
        assertEquals("not-a-real-password", entry.password)
        assertEquals("https://example.com", entry.url)
        assertEquals("Personal", entry.folder)
        assertTrue(entry.favorite)
        assertEquals("2026-01-01T12:00:00.000Z", entry.createdAt)
        assertEquals("2026-01-02T12:00:00.000Z", entry.updatedAt)
    }

    // ---- Round trip and fresh nonce ----

    @Test
    fun seal_f1Header_writesLayoutAndRoundTrips() {
        assertRoundTrip(F1)
    }

    @Test
    fun seal_f2Header_writesLayoutAndRoundTrips() {
        assertRoundTrip(F2)
    }

    @Test
    fun seal_twiceWithSameInputs_usesFreshNonceAndCiphertext() {
        val first = body.seal(F1, VK, SAMPLE)
        val second = body.seal(F1, VK, SAMPLE)

        assertFalse("nonce", first.copyOfRange(108, 132).contentEquals(second.copyOfRange(108, 132)))
        assertFalse("ciphertext", first.copyOfRange(132, first.size).contentEquals(second.copyOfRange(132, second.size)))
        assertEquals(SAMPLE.entries, body.open(first, 108, VK).entries)
        assertEquals(SAMPLE.entries, body.open(second, 108, VK).entries)
    }

    @Test
    fun seal_inputs_areNotModified() {
        val header = F2.copyOf()
        val key = VK.copyOf()

        body.seal(header, key, SAMPLE)

        assertArrayEquals(F2, header)
        assertArrayEquals(VK, key)
    }

    // ---- Tamper ----

    @Test
    fun open_eachV0ByteFlippedByRegion_throwsVaultAuthenticationException() {
        val regions = listOf(
            "magic" to (0 until 4),
            "version" to (4 until 6),
            "slotCount" to (6 until 7),
            "slot1" to (7 until 108),
            "bodyNonce" to (108 until 132),
            "ciphertext" to (132 until 146),
            "tag" to (146 until 162),
        )

        assertTampered(V0, 108, regions)
    }

    @Test
    fun open_eachV2ByteFlippedByRegion_throwsVaultAuthenticationException() {
        // Slot 2 is bound by the body AAD, unlike AAD_wrap.
        val regions = listOf(
            "slot1" to (7 until 108),
            "slot2" to (108 until 209),
            "bodyNonce" to (209 until 233),
            "body" to (233 until 263),
        )

        assertTampered(V2, 209, regions)
    }

    @Test
    fun open_lastByteRemoved_throwsVaultAuthenticationException() {
        assertAuthenticationFailure("truncated") { body.open(V0.copyOf(V0.size - 1), 108, VK) }
    }

    @Test
    fun open_zeroByteAppended_throwsVaultAuthenticationException() {
        assertAuthenticationFailure("appended") { body.open(V0 + byteArrayOf(0), 108, VK) }
    }

    @Test
    fun open_wrongBodyOffset_throwsVaultAuthenticationException() {
        for (offset in listOf(107, 109)) {
            assertAuthenticationFailure("offset=$offset") { body.open(V0, offset, VK) }
        }
    }

    @Test
    fun open_wrongKey_throwsVaultAuthenticationException() {
        val key = VK.copyOf().also { it[0] = (it[0].toInt() xor 1).toByte() }

        assertAuthenticationFailure("wrong key") { body.open(V0, 108, key) }
    }

    // ---- Format and arguments ----

    @Test
    fun open_bodyShorterThanNonceAndTag_throwsVaultFormatException() {
        val e = assertThrows(VaultFormatException::class.java) {
            body.open(F1 + ByteArray(39), 108, VK)
        }

        assertEquals("Vault body is truncated.", e.message)
    }

    @Test
    fun open_emptyCiphertextWithGarbageTag_throwsVaultAuthenticationException() {
        assertAuthenticationFailure("empty body") { body.open(F1 + BN + ByteArray(16), 108, VK) }
    }

    @Test
    fun open_bodyOffsetPastEndOfFile_throwsVaultFormatException() {
        for (offset in listOf(1000, Int.MAX_VALUE, Int.MAX_VALUE - 10)) {
            assertThrows("offset=$offset", VaultFormatException::class.java) {
                body.open(V0, offset, VK)
            }
        }
    }

    @Test
    fun open_bodyOffsetBeforeFirstSlot_throwsIllegalArgumentException() {
        for (offset in listOf(-1, 0, 6)) {
            assertThrows("offset=$offset", IllegalArgumentException::class.java) {
                body.open(V0, offset, VK)
            }
        }
    }

    @Test
    fun sealAndOpen_wrongVaultKeyLength_throwIllegalArgumentException() {
        for (length in listOf(31, 33)) {
            val key = ByteArray(length)

            assertThrows("seal key=$length", IllegalArgumentException::class.java) {
                body.seal(F1, key, SAMPLE)
            }
            assertThrows("open key=$length", IllegalArgumentException::class.java) {
                body.open(V0, 108, key)
            }
        }
    }

    @Test
    fun seal_headerShorterThanPreambleAndSlotCount_throwsIllegalArgumentException() {
        assertThrows(IllegalArgumentException::class.java) {
            body.seal(ByteArray(6), VK, SAMPLE)
        }
    }

    // ---- Malformed JSON behind a valid tag ----

    @Test
    fun open_malformedJson_throwsVaultFormatExceptionWithNoCause() {
        val cases = listOf(
            "J1 not json" to "not json".encodeToByteArray(),
            "J2 empty" to ByteArray(0),
            "J3 null" to "null".encodeToByteArray(),
            "J4 null entries" to """{"entries":null}""".encodeToByteArray(),
            "J5 null element" to """{"entries":[null]}""".encodeToByteArray(),
            "J6 no title" to entryJson(""),
            "J7 null title" to entryJson(""""title":null,"""),
            "J8 trailing data" to """{"entries":[]}x""".encodeToByteArray(),
            "J9 invalid UTF-8" to entryJson(""""title":"""", byteArrayOf(0xFF.toByte()), """","""),
        )

        for ((name, plaintext) in cases) {
            val file = sealRaw(plaintext)
            if (name.startsWith("J2")) assertEquals(name, 148, file.size)

            val e = assertThrows(name, VaultFormatException::class.java) {
                body.open(file, 108, VK)
            }

            assertEquals(name, "Vault body is not valid JSON.", e.message)
            assertNull(name, e.cause)
        }
    }

    // ---- Helpers ----

    private fun assertRoundTrip(header: ByteArray) {
        val b = header.size
        val json = VaultJson.encodeToString(VaultContent.serializer(), SAMPLE).encodeToByteArray()

        val file = body.seal(header, VK, SAMPLE)

        assertArrayEquals("header", header, file.copyOfRange(0, b))
        assertEquals("length", b + 24 + json.size + 16, file.size)
        val plaintext = ByteArray(json.size)
        cipher.decrypt(VK, file.copyOfRange(b + 24, file.size), file.copyOfRange(0, b + 24), file.copyOfRange(b, b + 24), plaintext)
        assertArrayEquals("json", json, plaintext)
        assertEquals(SAMPLE.entries, body.open(file, b, VK).entries)
    }

    private fun assertTampered(file: ByteArray, bodyOffset: Int, regions: List<Pair<String, IntRange>>) {
        for ((name, range) in regions) {
            for (index in range) {
                val tampered = file.copyOf().also { it[index] = (it[index].toInt() xor 1).toByte() }

                assertAuthenticationFailure("$name[$index]") { body.open(tampered, bodyOffset, VK) }
            }
        }
    }

    private fun assertAuthenticationFailure(message: String, action: () -> Unit) {
        val e = assertThrows(message, VaultAuthenticationException::class.java) { action() }

        assertEquals(message, "Vault authentication failed.", e.message)
        assertNull(message, e.cause)
    }

    /** Seals raw plaintext behind a valid tag (F1 || BN, VK), bypassing serialization. */
    private fun sealRaw(plaintext: ByteArray): ByteArray {
        val ciphertext = ByteArray(plaintext.size + 16)
        cipher.encrypt(VK, plaintext, F1 + BN, BN, ciphertext)
        return F1 + BN + ciphertext
    }

    /** J6 with [inserted] placed right after the id field. */
    private fun entryJson(vararg inserted: Any): ByteArray {
        val middle = inserted.fold(ByteArray(0)) { acc, part ->
            acc + when (part) {
                is String -> part.encodeToByteArray()
                is ByteArray -> part
                else -> error("Unsupported part.")
            }
        }
        return """{"entries":[{"id":"0b6f7c1e-2f5d-4a6b-9a8e-3c1d2e4f5a6b",""".encodeToByteArray() +
            middle +
            """"createdAt":"2026-01-01T12:00:00.000Z","updatedAt":"2026-01-01T12:00:00.000Z"}]}""".encodeToByteArray()
    }

    companion object {
        // Desktop libsodium: the host JVM cannot load lazysodium-android's .so.
        private val cipher = VaultBodyCipher(LazySodiumJava(SodiumJava()))
        private val body = VaultBody(cipher)

        private val SAMPLE = VaultContent(
            entries = listOf(
                VaultEntry(
                    id = "0b6f7c1e-2f5d-4a6b-9a8e-3c1d2e4f5a6b",
                    title = "Example",
                    username = "user@example.com",
                    password = "not-a-real-password",
                    url = "https://example.com",
                    notes = "notes",
                    folder = "Personal",
                    favorite = true,
                    createdAt = "2026-01-01T12:00:00.000Z",
                    updatedAt = "2026-01-02T12:00:00.000Z",
                ),
            ),
        )

        // Known answers (W-04 plan), independent of libsodium. Copied verbatim; never computed here.
        private val VK = ByteArray(32) { (0x20 + it).toByte() }
        private val BN = ByteArray(24) { (0x60 + it).toByte() }

        // W-03 test files: F1 = preamble | 01 | Slot1 (108 B); F2 = preamble | 02 | Slot1 | Slot2 (209 B).
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
        private val F1 = PREAMBLE + byteArrayOf(1) + SLOT1
        private val F2 = PREAMBLE + byteArrayOf(2) + SLOT1 + SLOT2

        private val C0 = hex("93052548f103f2a4d923af544fdcf72089107a0fb053d81aed759ce00dcb")
        private val C2 = hex("93052548f103f2a4d923af544fdc6c3aa10b7fa64c287cbe96a51fa2167a")
        private val C1 = hex(
            "93052548f103f2a4d923af54698346fd55d68e250bd3bf5103135cd708f3b7e30ea51c27439d1d437edd0a5da4856260b3da6f1a3ce40fd132629ccc1fb2e5b2" +
                "31807aee6ac56b3adcc39ff20a65209b0615e4c192e03ac794b02ac9a24f875a92ec93cd9fbafcff9d107775a0637426d8b66d091649974137f55c6a5bb3fa7d" +
                "ec9854d75245f5530942f2a7348cffdd4ff3d15a5815c0cff2b24ad6e300a605d0e3bdadf93b65aa3044415a20dd8d2c1f9b148b07cfc1b206ede799c9954963" +
                "b98464d8b2b432dfebea1ccefdeaa07185a7cd90be735d729b07f283ce8d63fc2b4267572b47cbdc2e21d0bdc4bedfeadb9a3241186593b7ac1a60077157463a" +
                "ed365cb120e5a338425b9d98a8c0efe7cee22cfd114bfdddd1b6ddbd424191ea3d014b47e7e6fc4497a96920c43070b6c6444db0f0539d3ee775fa10272e3507" +
                "318b94f2bc7b40855ff53dac9d44ef5c558ef3d49473f8c5c4a6ea6bb7ea",
        )

        // V0 = F1 || BN || C0 (162 B); V1 = F1 || BN || C1 (482 B); V2 = F2 || BN || C2 (263 B).
        private val V0 = F1 + BN + C0
        private val V1 = F1 + BN + C1
        private val V2 = F2 + BN + C2

        private fun hex(value: String): ByteArray =
            ByteArray(value.length / 2) { value.substring(it * 2, it * 2 + 2).toInt(16).toByte() }
    }
}
