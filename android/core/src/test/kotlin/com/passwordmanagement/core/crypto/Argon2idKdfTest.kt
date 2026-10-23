package com.passwordmanagement.core.crypto

import com.goterl.lazysodium.LazySodiumJava
import com.goterl.lazysodium.SodiumJava
import com.passwordmanagement.core.vault.KdfParameters
import com.passwordmanagement.core.vault.VaultFormat
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertThrows
import org.junit.Test
import java.nio.ByteBuffer
import java.nio.ByteOrder

class Argon2idKdfTest {
    @Test
    fun deriveKek_asciiPasswordMinimumParameters_matchesKnownAnswer() {
        val kek = ByteArray(32)

        kdf.deriveKek(ASCII_PASSWORD.toCharArray(), KdfParameters(8, 1), S1, kek)

        assertArrayEquals(K1, kek)
    }

    @Test
    fun deriveKek_asciiPasswordLowCost_matchesKnownAnswer() {
        val kek = ByteArray(32)

        kdf.deriveKek(ASCII_PASSWORD.toCharArray(), LOW_COST, S1, kek)

        assertArrayEquals(K2, kek)
    }

    @Test
    fun deriveKek_asciiPasswordDefaultParameters_matchesKnownAnswer() {
        val kek = ByteArray(32)

        kdf.deriveKek(ASCII_PASSWORD.toCharArray(), KdfParameters.DEFAULT, S2, kek)

        assertArrayEquals(K3, kek)
    }

    @Test
    fun deriveKek_unicodePasswordNfdOrNfc_matchesNfcKnownAnswer() {
        for (password in listOf(UNICODE_PASSWORD_NFD, UNICODE_PASSWORD_NFC)) {
            val kek = ByteArray(32)

            kdf.deriveKek(password.toCharArray(), LOW_COST, S1, kek)

            assertArrayEquals("password=${password.length} chars", K4, kek)
        }
    }

    @Test
    fun deriveKek_rawSecretBytes_matchesKnownAnswer() {
        val kek = ByteArray(32)

        kdf.deriveKek(SECRET, LOW_COST, S1, kek)

        assertArrayEquals(K5, kek)
    }

    @Test
    fun deriveKek_parametersAndSaltReadFromSlot_matchesKnownAnswer() {
        val slot = ByteBuffer.allocate(101).order(ByteOrder.LITTLE_ENDIAN)
            .put(1.toByte())
            .putShort(98)
            .put(1.toByte())
            .putInt(1024)
            .putInt(2)
            .put(1.toByte())
            .put(S1)
            .array()
        val kek = ByteArray(32)

        val parameters = KdfParameters.readFromSlot(slot)
        val salt = slot.copyOfRange(VaultFormat.SLOT_SALT_OFFSET, VaultFormat.SLOT_SALT_OFFSET + VaultFormat.SALT_SIZE)
        kdf.deriveKek(ASCII_PASSWORD.toCharArray(), parameters, salt, kek)

        assertArrayEquals(K2, kek)
    }

    @Test
    fun deriveKek_wrongSaltLength_throwsIllegalArgumentException() {
        for (saltLength in listOf(15, 17)) {
            val salt = ByteArray(saltLength)
            val kek = ByteArray(32)

            assertThrows(IllegalArgumentException::class.java) {
                kdf.deriveKek(ASCII_PASSWORD.toCharArray(), LOW_COST, salt, kek)
            }
            assertThrows(IllegalArgumentException::class.java) {
                kdf.deriveKek(SECRET, LOW_COST, salt, kek)
            }
        }
    }

    @Test
    fun deriveKek_wrongKekLength_throwsIllegalArgumentException() {
        for (kekLength in listOf(31, 33)) {
            val kek = ByteArray(kekLength)

            assertThrows(IllegalArgumentException::class.java) {
                kdf.deriveKek(ASCII_PASSWORD.toCharArray(), LOW_COST, S1, kek)
            }
            assertThrows(IllegalArgumentException::class.java) {
                kdf.deriveKek(SECRET, LOW_COST, S1, kek)
            }
        }
    }

    @Test
    fun deriveKek_parametersOutOfRange_throwsIllegalArgumentException() {
        // KdfParameters(0, 0) stands in for the C# `default` case.
        val cases = listOf(
            KdfParameters(0, 0),
            KdfParameters(7, 2),
            KdfParameters(1024, 33),
        )
        for (parameters in cases) {
            val kek = ByteArray(32)

            assertThrows(IllegalArgumentException::class.java) {
                kdf.deriveKek(ASCII_PASSWORD.toCharArray(), parameters, S1, kek)
            }
            assertThrows(IllegalArgumentException::class.java) {
                kdf.deriveKek(SECRET, parameters, S1, kek)
            }
        }
    }

    @Test
    fun deriveKek_loneSurrogate_throwsIllegalArgumentException() {
        val kek = ByteArray(32)

        assertThrows(IllegalArgumentException::class.java) {
            kdf.deriveKek("a\uD800b".toCharArray(), LOW_COST, S1, kek)
        }
    }

    @Test
    fun deriveKek_charArrayPassword_isNotModified() {
        val password = UNICODE_PASSWORD_NFD.toCharArray()
        val original = UNICODE_PASSWORD_NFD.toCharArray()
        val kek = ByteArray(32)

        kdf.deriveKek(password, LOW_COST, S1, kek)

        assertArrayEquals(original, password)
        assertArrayEquals(K4, kek)
    }

    companion object {
        // Desktop libsodium: the host JVM cannot load lazysodium-android's .so.
        private val kdf = Argon2idKdf(LazySodiumJava(SodiumJava()))

        // Computed with OpenSSL Argon2id via Python cryptography 50.0.2 (independent of libsodium); NFC via Python unicodedata.
        // Copied verbatim from windows/tests/PasswordManager.Core.Tests/Crypto/Argon2idKdfTests.cs.
        private val K1 = hex("d17ea6341ca93da6079ea2f64dc4aa31dd1aaf9caa67fb42ac4afd0714706f26")
        private val K2 = hex("58782fc96f06a6d7fcec4728099f6a788ee98a04f4cd8fa0a27af6db8b7a46b8")
        private val K3 = hex("da1fe23ea05cfd9e9d3d83fe89bfc3418f0e80d8ab8a345b89b43e9d41213433")
        private val K4 = hex("b2139fcdb9dac3eaec8ab6e84eed39d012d47455f18da2de398b1a6519105991")
        private val K5 = hex("f5da83698016cd37261aea6ea81a90af036c5e135cf9185f4d06775a50b2e3a5")

        private const val ASCII_PASSWORD = "correct horse battery staple"
        private const val UNICODE_PASSWORD_NFD = "Pässwörd-ção-🔑"
        private const val UNICODE_PASSWORD_NFC = "Pässwörd-ção-🔑"

        private val S1 = ByteArray(16) { it.toByte() }
        private val S2 = ByteArray(16) { (0xF0 + it).toByte() }
        private val SECRET = ByteArray(20) { (0x01 + it).toByte() }

        private val LOW_COST = KdfParameters(1024, 2)

        private fun hex(value: String): ByteArray =
            ByteArray(value.length / 2) { value.substring(it * 2, it * 2 + 2).toInt(16).toByte() }
    }
}
