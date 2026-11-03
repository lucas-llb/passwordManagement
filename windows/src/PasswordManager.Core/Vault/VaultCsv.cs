using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using PasswordManager.Core.Storage;

namespace PasswordManager.Core.Vault;

/// <summary>
/// The plaintext CSV export (AGENTS.md §5.2): one record per entry with the columns
/// <c>name,url,username,password,notes,folder</c>, compatible with the Bitwarden and KeePass importers.
/// </summary>
/// <remarks>
/// <para>The format follows RFC 4180: UTF-8 without BOM, every record (the header too) ends with CRLF, and a field is
/// quoted only when it contains a comma, a double quote, CR or LF, with each inner double quote doubled. Every other
/// character is written verbatim: tabs, leading or trailing spaces, line breaks inside quotes, and text in any Unicode
/// normalization form. A leading <c>=</c>, <c>+</c>, <c>-</c> or <c>@</c> is not neutralized, so the file imports
/// losslessly; the UI warns the user not to open it in a spreadsheet.</para>
/// <para>The output is unencrypted by design. <see cref="Serialize"/> builds no whole-document string: it fills a
/// pinned char buffer that is zeroed before returning, and returns a pinned byte array that the caller zeroes after
/// writing it.</para>
/// </remarks>
public static class VaultCsv
{
    /// <summary>The file extension of a CSV export, with the leading dot.</summary>
    public const string FileExtension = ".csv";

    /// <summary>The header record, without its line break.</summary>
    public const string HeaderLine = "name,url,username,password,notes,folder";

    private const string RecordEnd = "\r\n";
    private const char Separator = ',';
    private const char Quote = '"';

    private static readonly SearchValues<char> NeedsQuoting = SearchValues.Create(",\"\r\n");

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// Serializes <paramref name="entries"/>, in order, to the bytes of a CSV file: the header record, then one record
    /// per entry with its title, URL, username, password, notes and folder. Favorite and dates are not exported.
    /// </summary>
    /// <param name="entries">The entries to export. Not modified.</param>
    /// <returns>The whole file bytes, in a pinned array the caller zeroes after use.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="entries"/> is null.</exception>
    /// <exception cref="ArgumentException">A field contains an invalid surrogate; nothing is returned.</exception>
    public static byte[] Serialize(IReadOnlyList<VaultEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var length = HeaderLine.Length + RecordEnd.Length;
        foreach (var entry in entries)
        {
            length = checked(length + RecordLength(entry));
        }

        var chars = GC.AllocateArray<char>(length, pinned: true);
        try
        {
            var position = 0;
            Append(chars, ref position, HeaderLine);
            Append(chars, ref position, RecordEnd);
            foreach (var entry in entries)
            {
                AppendRecord(chars, ref position, entry);
            }

            Debug.Assert(position == length, "The sizing pass and the filling pass disagree.");

            var bytes = GC.AllocateArray<byte>(Utf8.GetByteCount(chars), pinned: true);
            Utf8.GetBytes(chars, bytes);
            return bytes;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(chars.AsSpan()));
        }
    }

    /// <summary>
    /// Writes a CSV export atomically to <paramref name="path"/>, replacing an existing file. No <c>.bak</c> copy is
    /// kept, and no folder is created.
    /// </summary>
    /// <param name="path">The file the user chose. Its folder must exist.</param>
    /// <param name="contents">The bytes from <see cref="Serialize"/>. Not modified; the caller zeroes them.</param>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="path"/> is empty or not a valid path.</exception>
    /// <exception cref="IOException">The write failed (for example, <see cref="DirectoryNotFoundException"/>); an
    /// existing file is unchanged.</exception>
    /// <exception cref="UnauthorizedAccessException">The write was denied; an existing file is unchanged.</exception>
    public static void WriteFile(string path, ReadOnlySpan<byte> contents)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        AtomicFile.Write(Path.GetFullPath(path), contents, keepBackup: false);
    }

    private static int RecordLength(VaultEntry entry) =>
        checked(
            FieldLength(entry.Title) + FieldLength(entry.Url) + FieldLength(entry.Username) +
            FieldLength(entry.Password) + FieldLength(entry.Notes) + FieldLength(entry.Folder) +
            5 + RecordEnd.Length);

    private static int FieldLength(string field)
    {
        var span = field.AsSpan();
        return span.ContainsAny(NeedsQuoting) ? checked(span.Length + span.Count(Quote) + 2) : span.Length;
    }

    private static void AppendRecord(char[] chars, ref int position, VaultEntry entry)
    {
        AppendField(chars, ref position, entry.Title);
        chars[position++] = Separator;
        AppendField(chars, ref position, entry.Url);
        chars[position++] = Separator;
        AppendField(chars, ref position, entry.Username);
        chars[position++] = Separator;
        AppendField(chars, ref position, entry.Password);
        chars[position++] = Separator;
        AppendField(chars, ref position, entry.Notes);
        chars[position++] = Separator;
        AppendField(chars, ref position, entry.Folder);
        Append(chars, ref position, RecordEnd);
    }

    private static void AppendField(char[] chars, ref int position, string field)
    {
        var span = field.AsSpan();
        if (!span.ContainsAny(NeedsQuoting))
        {
            Append(chars, ref position, span);
            return;
        }

        chars[position++] = Quote;
        foreach (var c in span)
        {
            if (c == Quote)
            {
                chars[position++] = Quote;
            }

            chars[position++] = c;
        }

        chars[position++] = Quote;
    }

    private static void Append(char[] chars, ref int position, ReadOnlySpan<char> text)
    {
        text.CopyTo(chars.AsSpan(position));
        position += text.Length;
    }
}
