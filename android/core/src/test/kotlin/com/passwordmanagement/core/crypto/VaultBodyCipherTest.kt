package com.passwordmanagement.core.crypto

import com.goterl.lazysodium.LazySodiumJava
import com.goterl.lazysodium.SodiumJava
import com.passwordmanagement.core.vault.VaultAuthenticationException
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertThrows
import org.junit.Test

class VaultBodyCipherTest {
    // ---- Known answers ----

    @Test
    fun encrypt_emptyEntriesWithF1Aad_matchesKnownAnswerC0() {
        val ciphertext = ByteArray(P0.size + 16)

        cipher.encrypt(VK, P0, AAD_F1, BN, ciphertext)

        assertArrayEquals(C0, ciphertext)
    }

    @Test
    fun encrypt_emptyEntriesWithF2Aad_matchesKnownAnswerC2() {
        val ciphertext = ByteArray(P0.size + 16)

        cipher.encrypt(VK, P0, AAD_F2, BN, ciphertext)

        assertArrayEquals(C2, ciphertext)
    }

    @Test
    fun encrypt_oneEntryWithF1Aad_matchesKnownAnswerC1() {
        val ciphertext = ByteArray(P1.size + 16)

        cipher.encrypt(VK, P1, AAD_F1, BN, ciphertext)

        assertArrayEquals(C1, ciphertext)
    }

    @Test
    fun decrypt_knownAnswers_returnPlaintexts() {
        val cases = listOf(
            Triple("C0", C0 to AAD_F1, P0),
            Triple("C2", C2 to AAD_F2, P0),
            Triple("C1", C1 to AAD_F1, P1),
        )

        for ((name, input, expected) in cases) {
            val plaintext = filled(input.first.size - 16)

            cipher.decrypt(VK, input.first, input.second, BN, plaintext)

            assertArrayEquals(name, expected, plaintext)
        }
    }

    @Test
    fun decrypt_inputs_areNotModified() {
        val key = VK.copyOf()
        val ciphertext = C1.copyOf()
        val aad = AAD_F1.copyOf()
        val nonce = BN.copyOf()

        cipher.decrypt(key, ciphertext, aad, nonce, ByteArray(P1.size))

        assertArrayEquals(VK, key)
        assertArrayEquals(C1, ciphertext)
        assertArrayEquals(AAD_F1, aad)
        assertArrayEquals(BN, nonce)
    }

    // ---- Authentication failures ----

    @Test
    fun decrypt_wrongKey_throwsVaultAuthenticationExceptionWithNoReasonAndZeroesOutput() {
        val plaintext = filled(P0.size)

        val e = assertThrows(VaultAuthenticationException::class.java) {
            cipher.decrypt(flip(VK, 0), C0, AAD_F1, BN, plaintext)
        }

        assertAuthenticationFailure(e)
        assertAllZero(plaintext)
    }

    @Test
    fun decrypt_eachAadByteFlipped_throwsVaultAuthenticationExceptionAndZeroesOutput() {
        assertEquals(132, AAD_F1.size)
        for (index in AAD_F1.indices) {
            val plaintext = filled(P0.size)

            val e = assertThrows("aad[$index]", VaultAuthenticationException::class.java) {
                cipher.decrypt(VK, C0, flip(AAD_F1, index), BN, plaintext)
            }

            assertAuthenticationFailure(e, "aad[$index]")
            assertAllZero(plaintext, "aad[$index]")
        }
    }

    @Test
    fun decrypt_eachNonceByteFlipped_throwsVaultAuthenticationExceptionAndZeroesOutput() {
        for (index in BN.indices) {
            val plaintext = filled(P0.size)

            val e = assertThrows("nonce[$index]", VaultAuthenticationException::class.java) {
                cipher.decrypt(VK, C0, AAD_F1, flip(BN, index), plaintext)
            }

            assertAuthenticationFailure(e, "nonce[$index]")
            assertAllZero(plaintext, "nonce[$index]")
        }
    }

    @Test
    fun decrypt_eachCiphertextByteFlipped_throwsVaultAuthenticationExceptionAndZeroesOutput() {
        assertEquals(30, C0.size)
        for (index in C0.indices) {
            val plaintext = filled(P0.size)

            val e = assertThrows("ciphertext[$index]", VaultAuthenticationException::class.java) {
                cipher.decrypt(VK, flip(C0, index), AAD_F1, BN, plaintext)
            }

            assertAuthenticationFailure(e, "ciphertext[$index]")
            assertAllZero(plaintext, "ciphertext[$index]")
        }
    }

    // ---- Lengths ----

    @Test
    fun encryptAndDecrypt_wrongVaultKeyLength_throwIllegalArgumentException() {
        for (length in listOf(31, 33)) {
            val key = ByteArray(length)

            assertThrows("encrypt key=$length", IllegalArgumentException::class.java) {
                cipher.encrypt(key, P0, AAD_F1, BN, ByteArray(P0.size + 16))
            }
            assertThrows("decrypt key=$length", IllegalArgumentException::class.java) {
                cipher.decrypt(key, C0, AAD_F1, BN, ByteArray(P0.size))
            }
        }
    }

    @Test
    fun encryptAndDecrypt_wrongNonceLength_throwIllegalArgumentException() {
        for (length in listOf(23, 25)) {
            val nonce = ByteArray(length)

            assertThrows("encrypt nonce=$length", IllegalArgumentException::class.java) {
                cipher.encrypt(VK, P0, AAD_F1, nonce, ByteArray(P0.size + 16))
            }
            assertThrows("decrypt nonce=$length", IllegalArgumentException::class.java) {
                cipher.decrypt(VK, C0, AAD_F1, nonce, ByteArray(P0.size))
            }
        }
    }

    @Test
    fun encrypt_wrongCiphertextBufferLength_throwsIllegalArgumentException() {
        for (length in listOf(P0.size + 15, P0.size + 17)) {
            assertThrows("ciphertext=$length", IllegalArgumentException::class.java) {
                cipher.encrypt(VK, P0, AAD_F1, BN, ByteArray(length))
            }
        }
    }

    @Test
    fun decrypt_ciphertextShorterThanTag_throwsIllegalArgumentException() {
        assertThrows(IllegalArgumentException::class.java) {
            cipher.decrypt(VK, ByteArray(15), AAD_F1, BN, ByteArray(0))
        }
    }

    @Test
    fun decrypt_wrongPlaintextBufferLength_throwsIllegalArgumentException() {
        for (length in listOf(P0.size - 1, P0.size + 1)) {
            assertThrows("plaintext=$length", IllegalArgumentException::class.java) {
                cipher.decrypt(VK, C0, AAD_F1, BN, ByteArray(length))
            }
        }
    }

    // ---- Helpers ----

    private fun assertAuthenticationFailure(e: VaultAuthenticationException, message: String = "") {
        assertEquals(message, "Vault authentication failed.", e.message)
        assertNull(message, e.cause)
    }

    private fun flip(bytes: ByteArray, index: Int): ByteArray =
        bytes.copyOf().also { it[index] = (it[index].toInt() xor 1).toByte() }

    private fun filled(size: Int): ByteArray = ByteArray(size) { 0xAA.toByte() }

    private fun assertAllZero(bytes: ByteArray, message: String = "") {
        assertArrayEquals(message, ByteArray(bytes.size), bytes)
    }

    companion object {
        // Desktop libsodium: the host JVM cannot load lazysodium-android's .so.
        private val cipher = VaultBodyCipher(LazySodiumJava(SodiumJava()))

        // Known answers (W-04 plan): HChaCha20 in pure Python plus OpenSSL ChaCha20-Poly1305 via Python
        // cryptography 50.0.2, independent of libsodium. Copied verbatim; never computed with lazysodium here.
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

        private val AAD_F1 = F1 + BN
        private val AAD_F2 = F2 + BN

        private val P0 = hex("7b22656e7472696573223a5b5d7d")
        private val P1 = hex(
            "7b22656e7472696573223a5b7b226964223a2230623666376331652d326635642d346136622d396138652d336331643265346635613662222c227469746c6522" +
                "3a22436166c3a920f09f9491222c22757365726e616d65223a2275736572406578616d706c652e636f6d222c2270617373776f7264223a226e6f742d612d7265" +
                "616c2d70617373776f7264222c2275726c223a2268747470733a2f2f6578616d706c652e636f6d222c226e6f746573223a224361665c7530304539205c754438" +
                "33445c7544443131222c22666f6c646572223a22506572736f6e616c222c226661766f72697465223a747275652c22637265617465644174223a22323032362d" +
                "30312d30315431323a30303a30302e3030305a222c22757064617465644174223a22323032362d30312d30325431323a30303a30302e3030305a227d5d2c2266" +
                "75747572654669656c64223a317d",
        )

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

        private fun hex(value: String): ByteArray =
            ByteArray(value.length / 2) { value.substring(it * 2, it * 2 + 2).toInt(16).toByte() }
    }
}
