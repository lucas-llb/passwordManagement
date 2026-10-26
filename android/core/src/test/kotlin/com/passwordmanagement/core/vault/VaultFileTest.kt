package com.passwordmanagement.core.vault

import com.goterl.lazysodium.LazySodiumJava
import com.goterl.lazysodium.SodiumJava
import com.passwordmanagement.core.crypto.Argon2idKdf
import com.passwordmanagement.core.crypto.VaultBodyCipher
import com.passwordmanagement.core.crypto.VaultKeyWrap
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertThrows
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder
import java.io.File
import java.io.IOException
import java.util.UUID

class VaultFileTest {
    @get:Rule
    val temp = TemporaryFolder()

    private val dir: File by lazy { temp.newFolder("pm-vaultfile") }
    private val path: File by lazy { File(dir, "${UUID.randomUUID()}.pmvault") }
    private val tmp: File get() = File(path.path + VaultFile.TEMP_SUFFIX)
    private val bak: File get() = File(path.path + VaultFile.BACKUP_SUFFIX)

    // ---- Successful writes ----

    @Test
    fun write_firstWrite_createsOnlyTarget() {
        VaultFile.write(path, V0)

        assertArrayEquals(V0, path.readBytes())
        assertFalse(bak.exists())
        assertFalse(tmp.exists())
        assertEquals(1, dir.listFiles()!!.size)
    }

    @Test
    fun write_secondWrite_keepsPreviousAsBackup() {
        VaultFile.write(path, V0)
        VaultFile.write(path, V2)

        assertArrayEquals(V2, path.readBytes())
        assertArrayEquals(V0, bak.readBytes())
        assertFalse(tmp.exists())
    }

    @Test
    fun write_thirdWrite_overwritesBackup() {
        VaultFile.write(path, V0)
        VaultFile.write(path, V2)
        VaultFile.write(path, V1)

        assertArrayEquals(V1, path.readBytes())
        assertArrayEquals(V2, bak.readBytes())
        assertFalse(tmp.exists())
    }

    @Test
    fun write_staleTempFile_isOverwrittenAndRemoved() {
        tmp.writeBytes(ByteArray(10) { 0x5A })

        VaultFile.write(path, V0)

        assertArrayEquals(V0, path.readBytes())
        assertFalse(tmp.exists())
    }

    // ---- Failed writes ----

    @Test
    fun write_invalidContentsWithNoTarget_throwsAndCreatesNothing() {
        for ((contents, message) in INVALID) {
            val e = assertThrows(message, VaultFormatException::class.java) { VaultFile.write(path, contents) }

            assertEquals(message, e.message)
            assertFalse(path.exists())
            assertFalse(bak.exists())
            assertFalse(tmp.exists())
            assertEquals(0, dir.listFiles()!!.size)
        }
    }

    @Test
    fun write_invalidContentsWithExistingTarget_leavesFilesUntouched() {
        VaultFile.write(path, V0)
        VaultFile.write(path, V2)

        for ((contents, message) in INVALID) {
            val e = assertThrows(message, VaultFormatException::class.java) { VaultFile.write(path, contents) }

            assertEquals(message, e.message)
            assertArrayEquals(V2, path.readBytes())
            assertArrayEquals(V0, bak.readBytes())
            assertFalse(tmp.exists())
        }
    }

    @Test
    fun write_tempPathBlockedByDirectory_throwsIOExceptionAndKeepsFiles() {
        VaultFile.write(path, V0)
        VaultFile.write(path, V2)
        assertTrue(tmp.mkdir())
        File(tmp, "keep").writeBytes(byteArrayOf(1))

        assertThrows(IOException::class.java) { VaultFile.write(path, V1) }

        assertArrayEquals(V2, path.readBytes())
        assertArrayEquals(V0, bak.readBytes())
    }

    @Test
    fun write_missingDirectory_throwsIOExceptionAndCreatesNothing() {
        val missing = File(dir, "missing")
        val target = File(missing, "${UUID.randomUUID()}.pmvault")

        assertThrows(IOException::class.java) { VaultFile.write(target, V0) }

        assertFalse(missing.exists())
        assertEquals(0, dir.listFiles()!!.size)
    }

    @Test
    fun readAndWrite_emptyPath_throwIllegalArgumentException() {
        assertThrows(IllegalArgumentException::class.java) { VaultFile.read(File("")) }
        assertThrows(IllegalArgumentException::class.java) { VaultFile.write(File(""), V0) }
    }

    @Test
    fun write_contents_isNotModified() {
        val contents = V2.copyOf()

        VaultFile.write(path, contents)

        assertArrayEquals(V2, contents)
    }

    // ---- Read ----

    @Test
    fun read_afterWrite_returnsWrittenBytes() {
        VaultFile.write(path, V2)

        assertArrayEquals(V2, VaultFile.read(path))
    }

    @Test
    fun read_missingFile_throwsIOException() {
        assertThrows(IOException::class.java) { VaultFile.read(path) }
    }

    @Test
    fun read_invalidFilesWrittenRaw_returnsBytesThatFailToParse() {
        val version2 = V0.copyOf().also { it[4] = 2 }
        val cases = INVALID + (version2 to "Vault format version is not supported.")

        for ((contents, message) in cases) {
            path.writeBytes(contents)

            val bytes = VaultFile.read(path)

            assertArrayEquals(message, contents, bytes)
            val e = assertThrows(message, VaultFormatException::class.java) { VaultHeader.parse(bytes) }
            assertEquals(message, e.message)
        }
    }

    // ---- End to end ----

    @Test
    fun writeReadParseUnwrapOpen_roundTripsAndBackupStillOpens() {
        val slot = keySlot.createPasswordSlot(PASSWORD.toCharArray(), LOW_COST, VK)
        val first = body.seal(VaultHeader.build(listOf(slot)), VK, SAMPLE)
        VaultFile.write(path, first)

        val bytes = VaultFile.read(path)
        val header = VaultHeader.parse(bytes)
        val vaultKey = ByteArray(32)
        keySlot.unwrapPasswordSlot(bytes, header.passwordSlotOffset, PASSWORD.toCharArray(), vaultKey)
        assertArrayEquals(VK, vaultKey)
        assertEquals(SAMPLE.entries, body.open(bytes, header.bodyOffset, vaultKey).entries)

        val second = body.seal(bytes.copyOf(header.bodyOffset), vaultKey, VaultContent())
        VaultFile.write(path, second)

        assertArrayEquals(second, VaultFile.read(path))
        val backup = VaultFile.read(bak)
        assertArrayEquals(first, backup)
        val backupHeader = VaultHeader.parse(backup)
        assertEquals(SAMPLE.entries, body.open(backup, backupHeader.bodyOffset, vaultKey).entries)
        assertTrue(body.open(second, header.bodyOffset, vaultKey).entries.isEmpty())
    }

    companion object {
        // Desktop libsodium: the host JVM cannot load lazysodium-android's .so.
        private val sodium = LazySodiumJava(SodiumJava())
        private val keySlot = KeySlot(Argon2idKdf(sodium), VaultKeyWrap(sodium))
        private val body = VaultBody(VaultBodyCipher(sodium))

        private const val PASSWORD = "correct horse battery staple"
        private val LOW_COST = KdfParameters(1024, 2)

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

        // Known answers (W-03/W-04 plans), independent of libsodium. Copied verbatim; never computed here.
        private val VK = ByteArray(32) { (0x20 + it).toByte() }
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

        private val INVALID = listOf(
            V0.copyOf(147) to "Vault file is truncated.",
            V0.copyOf().also { it[0] = 'X'.code.toByte() } to "Vault file has an invalid magic.",
        )

        private fun hex(value: String): ByteArray =
            ByteArray(value.length / 2) { value.substring(it * 2, it * 2 + 2).toInt(16).toByte() }
    }
}
