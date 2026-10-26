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
}
