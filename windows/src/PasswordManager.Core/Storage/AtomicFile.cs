using PasswordManager.Core.Vault;

namespace PasswordManager.Core.Storage;

/// <summary>
/// Atomic whole-file replacement shared by the vault file and <c>profiles.json</c>.
/// </summary>
/// <remarks>
/// <para>Write procedure: write the bytes to <c>&lt;path&gt;.tmp</c> and flush to disk; when
/// <paramref name="keepBackup"/> is true and the target exists, copy the existing file over <c>&lt;path&gt;.bak</c>;
/// rename the temp file over the target. The target is never absent or torn: on failure it stays as it was.</para>
/// <para>There is no directory fsync, so a power cut right after <see cref="Write"/> returns may roll back to the
/// previous version, never to a torn file. The <c>.bak</c> copy is not flushed and is best-effort.</para>
/// <para>The temp name is fixed, so callers must serialize writes to one file. The directory must exist.</para>
/// </remarks>
internal static class AtomicFile
{
    /// <summary>
    /// Replaces <paramref name="fullPath"/> with <paramref name="contents"/> atomically.
    /// </summary>
    /// <param name="fullPath">Full path of the target file. Its directory must exist.</param>
    /// <param name="contents">The whole file bytes. Not modified.</param>
    /// <param name="keepBackup">When true, the previous version is copied to <c>&lt;fullPath&gt;.bak</c> first.</param>
    /// <exception cref="IOException">The write failed; the target is unchanged.</exception>
    /// <exception cref="UnauthorizedAccessException">The write was denied; the target is unchanged.</exception>
    internal static void Write(string fullPath, ReadOnlySpan<byte> contents, bool keepBackup)
    {
        var tempPath = fullPath + VaultFile.TempSuffix;
        var backupPath = fullPath + VaultFile.BackupSuffix;
        try
        {
            using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(contents);
                stream.Flush(flushToDisk: true);
            }

            if (keepBackup && File.Exists(fullPath))
            {
                File.Copy(fullPath, backupPath, overwrite: true);
            }

            File.Move(tempPath, fullPath, overwrite: true);
        }
        finally
        {
            DeleteQuietly(tempPath);
        }
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Best effort: a leftover temp is overwritten by the next write.
        }
        catch (UnauthorizedAccessException)
        {
            // Best effort, as above.
        }
    }
}
