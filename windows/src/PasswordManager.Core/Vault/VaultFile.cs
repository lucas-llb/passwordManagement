using PasswordManager.Core.Storage;

namespace PasswordManager.Core.Vault;

/// <summary>
/// Reads and atomically writes a <c>&lt;id&gt;.pmvault</c> file (docs/vault-format.md, "Gravação atômica").
/// </summary>
/// <remarks>
/// <para>Write procedure: validate the bytes with <see cref="VaultHeader.Parse"/>; write them to
/// <c>&lt;path&gt;.tmp</c> and flush to disk; copy the existing file over <c>&lt;path&gt;.bak</c>; rename the temp file
/// over the original. The original is never absent or torn: on failure it stays as it was.</para>
/// <para>There is no directory fsync, so a power cut right after <see cref="Write"/> returns may roll back to the
/// previous version, never to a torn file. The <c>.bak</c> copy is not flushed and is best-effort.</para>
/// <para>After a change of credentials, the <c>.bak</c> receives the new file instead (<see cref="WriteCredentialChange"/>).</para>
/// <para>The temp name is fixed, so callers must serialize writes to one profile. Directories are never created.</para>
/// </remarks>
public static class VaultFile
{
    /// <summary>Suffix appended to the vault path for the temporary file of an atomic write.</summary>
    public const string TempSuffix = ".tmp";

    /// <summary>Suffix appended to the vault path for the copy of the previous version.</summary>
    public const string BackupSuffix = ".bak";

    /// <summary>
    /// Reads the whole file. It does not parse the bytes (use <see cref="VaultHeader.Parse"/>) and does not fall
    /// back to the <c>.bak</c> copy.
    /// </summary>
    /// <param name="path">Path of the vault file.</param>
    /// <returns>The file bytes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="path"/> is empty.</exception>
    /// <exception cref="IOException">The file cannot be read (for example, <see cref="FileNotFoundException"/>).</exception>
    public static byte[] Read(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        return File.ReadAllBytes(path);
    }

    /// <summary>
    /// Validates <paramref name="contents"/> and writes it atomically over <paramref name="path"/>, keeping the
    /// previous version as <c>&lt;path&gt;.bak</c>.
    /// </summary>
    /// <param name="path">Path of the vault file. Its directory must exist.</param>
    /// <param name="contents">The whole file bytes. Not modified.</param>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="path"/> is empty.</exception>
    /// <exception cref="VaultFormatException"><paramref name="contents"/> is not a valid v1 vault file; the disk is not touched.</exception>
    /// <exception cref="IOException">The write failed; the original file is unchanged.</exception>
    /// <exception cref="UnauthorizedAccessException">The write was denied; the original file is unchanged.</exception>
    public static void Write(string path, ReadOnlySpan<byte> contents)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        var fullPath = Path.GetFullPath(path);
        VaultHeader.Parse(contents);

        AtomicFile.Write(fullPath, contents, keepBackup: true);
    }

    /// <summary>
    /// Validates <paramref name="contents"/>, writes it atomically over <paramref name="path"/> and then copies the new
    /// file over <c>&lt;path&gt;.bak</c> (<see cref="RefreshBackup"/>), for a change of credentials: a copy of the
    /// previous version would keep opening with the credential that was just replaced.
    /// </summary>
    /// <remarks>
    /// The previous version is not copied first, as <see cref="Write"/> does, because a locked <c>.bak</c> would then
    /// fail the write. A failed write leaves the file and the <c>.bak</c> as they were; a crash between the write and the
    /// copy leaves the old <c>.bak</c> until the next write.
    /// </remarks>
    /// <param name="path">Path of the vault file. Its directory must exist.</param>
    /// <param name="contents">The whole file bytes. Not modified.</param>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="path"/> is empty.</exception>
    /// <exception cref="VaultFormatException"><paramref name="contents"/> is not a valid v1 vault file; the disk is not touched.</exception>
    /// <exception cref="IOException">The write failed; the original file is unchanged.</exception>
    /// <exception cref="UnauthorizedAccessException">The write was denied; the original file is unchanged.</exception>
    internal static void WriteCredentialChange(string path, ReadOnlySpan<byte> contents)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        var fullPath = Path.GetFullPath(path);
        VaultHeader.Parse(contents);

        AtomicFile.Write(fullPath, contents, keepBackup: false);
        RefreshBackup(fullPath);
    }

    /// <summary>
    /// Copies the current file over <c>&lt;path&gt;.bak</c>. Best effort, like the <c>.bak</c> itself: a failure is
    /// swallowed, because the file is already written, and the next write replaces the copy.
    /// </summary>
    /// <param name="path">Path of the vault file.</param>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="path"/> is empty.</exception>
    internal static void RefreshBackup(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        try
        {
            File.Copy(path, path + BackupSuffix, overwrite: true);
        }
        catch (IOException)
        {
            // Best effort: the next write replaces the copy.
        }
        catch (UnauthorizedAccessException)
        {
            // Best effort, as above.
        }
    }

    /// <summary>
    /// Validates an export and writes it atomically to <paramref name="path"/>, the place the user chose. Unlike
    /// <see cref="Write"/>, it keeps no <c>.bak</c> copy and creates no folder.
    /// </summary>
    /// <remarks>
    /// The export must hold exactly one slot, of type 1 (the export password). The temp file is <c>&lt;path&gt;.tmp</c>
    /// in the same folder as <paramref name="path"/>, never in a temp or cache folder.
    /// </remarks>
    /// <param name="path">Full or relative path chosen by the user. Its directory must exist.</param>
    /// <param name="contents">The whole export bytes. Not modified.</param>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="path"/> is empty, or <paramref name="contents"/> does not hold
    /// exactly one type-1 slot; the disk is not touched.</exception>
    /// <exception cref="VaultFormatException"><paramref name="contents"/> is not a valid v1 vault file; the disk is not touched.</exception>
    /// <exception cref="IOException">The write failed (for example, the folder does not exist or the target is locked);
    /// the original file is unchanged.</exception>
    /// <exception cref="UnauthorizedAccessException">The write was denied; the original file is unchanged.</exception>
    public static void WriteExport(string path, ReadOnlySpan<byte> contents)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        var fullPath = Path.GetFullPath(path);
        var header = VaultHeader.Parse(contents);

        if (header.Slots.Count != 1 || header.RecoveryKitSlotOffset is not null)
        {
            throw new ArgumentException("An export must hold exactly one password slot.", nameof(contents));
        }

        AtomicFile.Write(fullPath, contents, keepBackup: false);
    }
}
