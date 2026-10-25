package com.passwordmanagement.core.crypto

import com.goterl.lazysodium.LazySodiumJava
import com.goterl.lazysodium.SodiumJava
import com.passwordmanagement.core.vault.VaultAuthenticationException
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertThrows
import org.junit.Test

class VaultKeyWrapTest {
    @Test
    fun wrap_password1KekKnownInputs_matchesKnownAnswerW1() {
        val wrapped = ByteArray(48)

        wrap.wrap(K2, VK, AAD1, N, wrapped)

        assertArrayEquals(W1, wrapped)
    }

    @Test
    fun wrap_recoveryKitKekKnownInputs_matchesKnownAnswerW2() {
        val wrapped = ByteArray(48)

        wrap.wrap(K5, VK, AAD2, N, wrapped)

        assertArrayEquals(W2, wrapped)
    }

    @Test
    fun unwrap_knownAnswerW1_returnsVaultKey() {
        val vaultKey = filled()

        wrap.unwrap(K2, W1, AAD1, N, vaultKey)

        assertArrayEquals(VK, vaultKey)
    }

    @Test
    fun unwrap_knownAnswerW2_returnsVaultKey() {
        val vaultKey = filled()

        wrap.unwrap(K5, W2, AAD2, N, vaultKey)

        assertArrayEquals(VK, vaultKey)
    }

    @Test
    fun unwrap_inputs_areNotModified() {
        val kek = K2.copyOf()
        val wrapped = W1.copyOf()
        val aad = AAD1.copyOf()
        val nonce = N.copyOf()

        wrap.unwrap(kek, wrapped, aad, nonce, ByteArray(32))

        assertArrayEquals(K2, kek)
        assertArrayEquals(W1, wrapped)
        assertArrayEquals(AAD1, aad)
        assertArrayEquals(N, nonce)
    }

    @Test
    fun unwrap_wrongKek_throwsVaultAuthenticationExceptionWithNoReasonAndZeroesOutput() {
        val vaultKey = filled()

        val e = assertThrows(VaultAuthenticationException::class.java) {
            wrap.unwrap(K5, W1, AAD1, N, vaultKey)
        }

        assertEquals("Vault authentication failed.", e.message)
        assertNull(e.cause)
        assertAllZero(vaultKey)
    }

    @Test
    fun unwrap_eachAadByteFlipped_throwsVaultAuthenticationExceptionAndZeroesOutput() {
        // Includes byte 6 (slotType): the slot type is bound by the AEAD.
        for (index in AAD1.indices) {
            val aad = AAD1.copyOf().also { it[index] = (it[index].toInt() xor 1).toByte() }
            val vaultKey = filled()

            assertThrows("aad[$index]", VaultAuthenticationException::class.java) {
                wrap.unwrap(K2, W1, aad, N, vaultKey)
            }

            assertAllZero(vaultKey, "aad[$index]")
        }
    }

    @Test
    fun unwrap_eachNonceByteFlipped_throwsVaultAuthenticationExceptionAndZeroesOutput() {
        for (index in N.indices) {
            val nonce = N.copyOf().also { it[index] = (it[index].toInt() xor 1).toByte() }
            val vaultKey = filled()

            assertThrows("nonce[$index]", VaultAuthenticationException::class.java) {
                wrap.unwrap(K2, W1, AAD1, nonce, vaultKey)
            }

            assertAllZero(vaultKey, "nonce[$index]")
        }
    }

    @Test
    fun unwrap_eachWrappedByteFlipped_throwsVaultAuthenticationExceptionAndZeroesOutput() {
        for (index in W1.indices) {
            val wrapped = W1.copyOf().also { it[index] = (it[index].toInt() xor 1).toByte() }
            val vaultKey = filled()

            assertThrows("wrapped[$index]", VaultAuthenticationException::class.java) {
                wrap.unwrap(K2, wrapped, AAD1, N, vaultKey)
            }

            assertAllZero(vaultKey, "wrapped[$index]")
        }
    }

    @Test
    fun wrapAndUnwrap_wrongKekLength_throwIllegalArgumentException() {
        for (length in listOf(31, 33)) {
            val kek = ByteArray(length)

            assertThrows("wrap kek=$length", IllegalArgumentException::class.java) {
                wrap.wrap(kek, VK, AAD1, N, ByteArray(48))
            }
            assertThrows("unwrap kek=$length", IllegalArgumentException::class.java) {
                wrap.unwrap(kek, W1, AAD1, N, ByteArray(32))
            }
        }
    }

    @Test
    fun wrapAndUnwrap_wrongVaultKeyLength_throwIllegalArgumentException() {
        for (length in listOf(31, 33)) {
            val vaultKey = ByteArray(length)

            assertThrows("wrap vaultKey=$length", IllegalArgumentException::class.java) {
                wrap.wrap(K2, vaultKey, AAD1, N, ByteArray(48))
            }
            assertThrows("unwrap vaultKey=$length", IllegalArgumentException::class.java) {
                wrap.unwrap(K2, W1, AAD1, N, vaultKey)
            }
        }
    }

    @Test
    fun wrapAndUnwrap_wrongAadLength_throwIllegalArgumentException() {
        for (length in listOf(34, 36)) {
            val aad = ByteArray(length)

            assertThrows("wrap aad=$length", IllegalArgumentException::class.java) {
                wrap.wrap(K2, VK, aad, N, ByteArray(48))
            }
            assertThrows("unwrap aad=$length", IllegalArgumentException::class.java) {
                wrap.unwrap(K2, W1, aad, N, ByteArray(32))
            }
        }
    }

    @Test
    fun wrapAndUnwrap_wrongNonceLength_throwIllegalArgumentException() {
        for (length in listOf(23, 25)) {
            val nonce = ByteArray(length)

            assertThrows("wrap nonce=$length", IllegalArgumentException::class.java) {
                wrap.wrap(K2, VK, AAD1, nonce, ByteArray(48))
            }
            assertThrows("unwrap nonce=$length", IllegalArgumentException::class.java) {
                wrap.unwrap(K2, W1, AAD1, nonce, ByteArray(32))
            }
        }
    }

    @Test
    fun wrapAndUnwrap_wrongWrappedLength_throwIllegalArgumentException() {
        for (length in listOf(47, 49)) {
            val wrapped = ByteArray(length)

            assertThrows("wrap wrapped=$length", IllegalArgumentException::class.java) {
                wrap.wrap(K2, VK, AAD1, N, wrapped)
            }
            assertThrows("unwrap wrapped=$length", IllegalArgumentException::class.java) {
                wrap.unwrap(K2, wrapped, AAD1, N, ByteArray(32))
            }
        }
    }

    private fun filled(): ByteArray = ByteArray(32) { 0xAA.toByte() }

    private fun assertAllZero(bytes: ByteArray, message: String = "") {
        assertArrayEquals(message, ByteArray(bytes.size), bytes)
    }

    companion object {
        // Desktop libsodium: the host JVM cannot load lazysodium-android's .so.
        private val wrap = VaultKeyWrap(LazySodiumJava(SodiumJava()))

        // Known answers (W-03 plan): HChaCha20 in pure Python plus OpenSSL ChaCha20-Poly1305 via Python
        // cryptography 50.0.2, independent of libsodium. Copied verbatim from the Windows tests; never
        // computed with lazysodium here.
        private val K2 = hex("58782fc96f06a6d7fcec4728099f6a788ee98a04f4cd8fa0a27af6db8b7a46b8")
        private val K5 = hex("f5da83698016cd37261aea6ea81a90af036c5e135cf9185f4d06775a50b2e3a5")

        private val N = ByteArray(24) { (0x40 + it).toByte() }
        private val VK = ByteArray(32) { (0x20 + it).toByte() }

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

        private fun hex(value: String): ByteArray =
            ByteArray(value.length / 2) { value.substring(it * 2, it * 2 + 2).toInt(16).toByte() }
    }
}
