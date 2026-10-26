package com.passwordmanagement.core.vault

import java.io.File
import java.io.FileOutputStream
import java.io.IOException
import java.nio.file.Files
import java.nio.file.StandardCopyOption

/**
 * Reads and atomically writes `<id>.pmvault` files (docs/vault-format.md, "Gravação atômica").
 *
 * Write procedure, the same as on Windows (`tmp = path + ".tmp"`, `bak = path + ".bak"`, both in the
 * same directory so the rename never crosses volumes):
 * 1. validate the contents with [VaultHeader.parse], before the disk is touched;
 * 2. create or truncate `tmp` (a stale temp from a crash is overwritten), write, flush and `fd.sync()`;
 * 3. if the target exists, copy it over `bak` (on the first write no `.bak` is made and an existing one
 *    is left untouched);
 * 4. rename `tmp` over the target with `ATOMIC_MOVE` (`rename(2)` on the device);
 * 5. finally, delete `tmp` on a best-effort basis.
 *
 * The target is never absent or torn: on failure it stays as it was. Directories are never created.
 *
 * Limits: there is no directory fsync (Java cannot do one portably), so a power cut right after
 * [write] returns may roll back to the previous version, but never to a torn file. The `.bak` copy is
 * not synced either. The temp name is fixed and no lock is taken, so callers must serialize writes to
 * one profile.
 */
object VaultFile {
    const val TEMP_SUFFIX: String = ".tmp"
    const val BACKUP_SUFFIX: String = ".bak"

    /**
     * Reads the whole file. It does not parse (use [VaultHeader.parse]) and does not fall back to `.bak`.
     *
     * @throws IllegalArgumentException [file] has an empty path.
     * @throws IOException the file is missing or cannot be read.
     */
    fun read(file: File): ByteArray {
        require(file.path.isNotEmpty()) { "Path must not be empty." }
        return file.readBytes()
    }

    /**
     * Writes [contents] to [file] atomically, keeping the previous version as `<file>.bak`.
     *
     * @param contents the whole vault bytes. Not modified.
     * @throws IllegalArgumentException [file] has an empty path.
     * @throws VaultFormatException [contents] is not a valid v1 vault file; nothing is written.
     * @throws IOException the directory is missing or a file cannot be written, copied or renamed.
     */
    fun write(file: File, contents: ByteArray) {
        // An empty path would resolve to the working directory and put the temp file next to it.
        require(file.path.isNotEmpty()) { "Path must not be empty." }
        VaultHeader.parse(contents)

        val target = file.absoluteFile.toPath()
        val temp = File(file.absoluteFile.path + TEMP_SUFFIX).toPath()
        val backup = File(file.absoluteFile.path + BACKUP_SUFFIX).toPath()
        try {
            FileOutputStream(temp.toFile()).use { stream ->
                stream.write(contents)
                stream.flush()
                stream.fd.sync()
            }
            if (Files.exists(target)) {
                Files.copy(target, backup, StandardCopyOption.REPLACE_EXISTING)
            }
            Files.move(temp, target, StandardCopyOption.ATOMIC_MOVE)
        } finally {
            try {
                Files.deleteIfExists(temp)
            } catch (_: IOException) {
                // Best effort: a leftover temp is overwritten by the next write.
            }
        }
    }
}
