using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using PasswordManager.Core.Storage;

namespace PasswordManager.Core.Vault;

/// <summary>
/// The plaintext CSV export (AGENTS.md §5.2): one record per entry with the columns
/// <c>name,url,username,password,notes,folder</c>, compatible with the Bitwarden and KeePass importers. It also reads CSV
/// files back (AGENTS.md §5.3): its own export and the Bitwarden, KeePassXC and KeePass 1.x layouts.
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
/// <para><see cref="ParseAndZero"/> accepts strict UTF-8 with an optional BOM, and records ending in CRLF, LF or a lone
/// CR. A field is quoted only if a double quote is its first character, and values stay verbatim: no trim, and a formula
/// prefix stays. The KeePass 1.x layout is the one dialect: its quoted fields escape with a backslash (<c>\"</c> and
/// <c>\\</c>), so reading it as RFC 4180 would silently corrupt a password. The only field strings created are the mapped
/// fields of an accepted record; rejected records and ignored columns never leave the pinned buffer.</para>
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
    private const char Backslash = '\\';
    private const char NullChar = '\0';
    private const int MaxColumns = 64;

    private static readonly SearchValues<char> NeedsQuoting = SearchValues.Create(",\"\r\n");

    // Where an unquoted field stops: its end, or a character it may not hold.
    private static readonly SearchValues<char> UnquotedStop = SearchValues.Create(",\"\r\n\0");

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static readonly Dictionary<string, CsvField> HeaderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["name"] = CsvField.Title,
        ["title"] = CsvField.Title,
        ["account"] = CsvField.Title,
        ["password"] = CsvField.Password,
        ["login_password"] = CsvField.Password,
        ["url"] = CsvField.Url,
        ["login_uri"] = CsvField.Url,
        ["web site"] = CsvField.Url,
        ["username"] = CsvField.Username,
        ["login_username"] = CsvField.Username,
        ["login name"] = CsvField.Username,
        ["notes"] = CsvField.Notes,
        ["comments"] = CsvField.Notes,
        ["folder"] = CsvField.Folder,
        ["group"] = CsvField.Folder,
        ["collections"] = CsvField.Folder,
        ["favorite"] = CsvField.Favorite,
    };

    // Columns the preview does not mention even when they have a value.
    private static readonly HashSet<string> SilentlyIgnoredNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "type",
        "reprompt",
        "icon",
        "last modified",
        "created",
    };

    private static readonly string[] KeePass1xNames = ["account", "login name", "password", "web site", "comments"];

    private enum CsvField
    {
        Title,
        Password,
        Url,
        Username,
        Notes,
        Folder,
        Favorite,
    }

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

    /// <summary>
    /// Parses a CSV file into import entries, and zeroes <paramref name="file"/> and its decoded text before returning,
    /// whatever the outcome. Each entry gets a new <see cref="VaultEntry.Id"/> and <paramref name="now"/>, truncated to
    /// milliseconds, as both dates; dates in the file are ignored.
    /// </summary>
    /// <remarks>
    /// A record with the wrong field count, a whitespace-only title or a field over
    /// <see cref="VaultImport.MaxFieldChars"/> is left out and listed in <see cref="ImportFile.RejectedLines"/>. A record
    /// with every field empty is skipped silently.
    /// </remarks>
    /// <param name="file">The whole file bytes, from <see cref="VaultImport.ReadFile"/>. Zeroed by this call.</param>
    /// <param name="now">The import time.</param>
    /// <returns>The accepted entries, the rejected lines and the columns whose values stay out of the vault.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="file"/> is null.</exception>
    /// <exception cref="VaultImportException">The whole file is refused: <see cref="ImportFileError.InvalidEncoding"/>,
    /// <see cref="ImportFileError.InvalidHeader"/>, <see cref="ImportFileError.MalformedCsv"/> or
    /// <see cref="ImportFileError.TooManyEntries"/>. It carries a line number, never content.</exception>
    public static ImportFile ParseAndZero(byte[] file, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(file);

        char[]? chars = null;
        try
        {
            chars = Decode(file);
            return Parse(chars, VaultImport.TruncateToMilliseconds(now));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(file);
            if (chars is not null)
            {
                CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(chars.AsSpan()));
            }
        }
    }

    private static char[] Decode(byte[] file)
    {
        var bytes = file.AsSpan();
        if (bytes.StartsWith(Encoding.UTF8.Preamble))
        {
            bytes = bytes[Encoding.UTF8.Preamble.Length..];
        }

        try
        {
            // The count already validates every byte, so nothing is decoded from an invalid file.
            var chars = GC.AllocateArray<char>(Utf8.GetCharCount(bytes), pinned: true);
            Utf8.GetChars(bytes, chars);
            return chars;
        }
        catch (DecoderFallbackException)
        {
            // Not chained: this exception type carries the raw bytes it could not decode.
            throw new VaultImportException(ImportFileError.InvalidEncoding);
        }
    }

    private static ImportFile Parse(char[] chars, DateTimeOffset now)
    {
        var starts = new int[MaxColumns + 1];
        var lengths = new int[MaxColumns + 1];
        var reader = new RecordReader(chars, starts, lengths);

        var columns = ReadHeader(ref reader, chars);
        var hasValue = new bool[columns.Count];
        var entries = new List<VaultEntry>();
        var rejected = new List<int>();

        while (reader.MoveNext(columns.BackslashEscapes))
        {
            if (!reader.HasValue)
            {
                continue;
            }

            // Rejected records count too, or a hostile file could grow the rejected list up to its size limit.
            if (entries.Count + rejected.Count == VaultImport.MaxEntries)
            {
                throw new VaultImportException(ImportFileError.TooManyEntries, reader.RecordLine);
            }

            if (reader.FieldCount != columns.Count ||
                reader.HasOversizedField ||
                chars.AsSpan(starts[columns.Of(CsvField.Title)], lengths[columns.Of(CsvField.Title)]).IsWhiteSpace())
            {
                rejected.Add(reader.RecordLine);
                continue;
            }

            entries.Add(BuildEntry(columns, chars, starts, lengths, now));
            foreach (var column in columns.Reported)
            {
                hasValue[column] |= lengths[column] > 0;
            }
        }

        return new ImportFile(entries, rejected, columns.IgnoredNames(hasValue));
    }

    private static CsvColumns ReadHeader(ref RecordReader reader, char[] chars)
    {
        // The dialect is only known once the names are read, so the header itself is always RFC 4180.
        do
        {
            if (!reader.MoveNext(backslashEscapes: false))
            {
                throw new VaultImportException(ImportFileError.InvalidHeader);
            }
        }
        while (!reader.HasValue);

        var line = reader.RecordLine;
        if (reader.FieldCount > MaxColumns || reader.HasOversizedField)
        {
            throw new VaultImportException(ImportFileError.InvalidHeader, line);
        }

        var names = new string[reader.FieldCount];
        for (var i = 0; i < names.Length; i++)
        {
            names[i] = chars.AsSpan(reader.FieldStart(i), reader.FieldLength(i)).Trim().ToString();
        }

        return CsvColumns.FromNames(names) ?? throw new VaultImportException(ImportFileError.InvalidHeader, line);
    }

    private static VaultEntry BuildEntry(CsvColumns columns, char[] chars, int[] starts, int[] lengths, DateTimeOffset now)
    {
        string Text(CsvField field)
        {
            var column = columns.Of(field);
            return column < 0 ? "" : new string(chars, starts[column], lengths[column]);
        }

        var favoriteColumn = columns.Of(CsvField.Favorite);

        return new VaultEntry
        {
            Id = Guid.NewGuid(),
            Title = Text(CsvField.Title),
            Username = Text(CsvField.Username),
            Password = Text(CsvField.Password),
            Url = Text(CsvField.Url),
            Notes = Text(CsvField.Notes),
            Folder = Text(CsvField.Folder),
            Favorite = favoriteColumn >= 0 && IsFavoriteValue(chars.AsSpan(starts[favoriteColumn], lengths[favoriteColumn])),
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    private static bool IsFavoriteValue(ReadOnlySpan<char> value) =>
        value.Equals("1", StringComparison.Ordinal) || value.Equals("true", StringComparison.OrdinalIgnoreCase);

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

    /// <summary>What a header says: which column holds each field, the dialect, and the columns to report.</summary>
    private sealed class CsvColumns
    {
        private readonly int[] _fields;
        private readonly string[] _names;

        private CsvColumns(int[] fields, string[] names, int[] reported, bool backslashEscapes)
        {
            _fields = fields;
            _names = names;
            Reported = reported;
            BackslashEscapes = backslashEscapes;
        }

        public int Count => _names.Length;

        public int[] Reported { get; }

        public bool BackslashEscapes { get; }

        /// <summary>Maps <paramref name="names"/>; null when a required field is missing or a field has two columns.</summary>
        public static CsvColumns? FromNames(string[] names)
        {
            var fields = new int[Enum.GetValues<CsvField>().Length];
            Array.Fill(fields, -1);
            var reported = new List<int>();

            for (var column = 0; column < names.Length; column++)
            {
                if (HeaderNames.TryGetValue(names[column], out var field))
                {
                    if (fields[(int)field] >= 0)
                    {
                        return null;
                    }

                    fields[(int)field] = column;
                }
                else if (!SilentlyIgnoredNames.Contains(names[column]))
                {
                    reported.Add(column);
                }
            }

            if (fields[(int)CsvField.Title] < 0 || fields[(int)CsvField.Password] < 0)
            {
                return null;
            }

            var backslashEscapes = names.SequenceEqual(KeePass1xNames, StringComparer.OrdinalIgnoreCase);
            return new CsvColumns(fields, names, [.. reported], backslashEscapes);
        }

        public int Of(CsvField field) => _fields[(int)field];

        /// <summary>The names of the reported columns that had a value, in header order and without repeats.</summary>
        public List<string> IgnoredNames(bool[] hasValue)
        {
            var names = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var column in Reported)
            {
                // A column with no name can still lose data, so it is reported by its position.
                var name = _names[column].Length == 0
                    ? string.Create(CultureInfo.InvariantCulture, $"#{column + 1}")
                    : _names[column];
                if (hasValue[column] && seen.Add(name))
                {
                    names.Add(name);
                }
            }

            return names;
        }
    }

    /// <summary>
    /// Walks the decoded text one record at a time. An unescaped value is written back over its own quoted source, which
    /// is never shorter, so a field needs no second buffer and only offsets leave this type.
    /// </summary>
    private ref struct RecordReader
    {
        private readonly Span<char> _chars;
        private readonly int[] _starts;
        private readonly int[] _lengths;
        private int _position;
        private int _line = 1;

        public RecordReader(Span<char> chars, int[] starts, int[] lengths)
        {
            _chars = chars;
            _starts = starts;
            _lengths = lengths;
        }

        /// <summary>The 1-based physical line where the current record starts.</summary>
        public int RecordLine { get; private set; }

        /// <summary>The number of fields in the current record, including any past the end of the offset arrays.</summary>
        public int FieldCount { get; private set; }

        /// <summary>True when some field of the current record is not empty.</summary>
        public bool HasValue { get; private set; }

        /// <summary>True when some field of the current record is over <see cref="VaultImport.MaxFieldChars"/>.</summary>
        public bool HasOversizedField { get; private set; }

        public readonly int FieldStart(int index) => _starts[index];

        public readonly int FieldLength(int index) => _lengths[index];

        /// <summary>Reads the next record; false at the end of the text.</summary>
        /// <param name="backslashEscapes">True for the KeePass 1.x rules inside quotes, false for RFC 4180.</param>
        public bool MoveNext(bool backslashEscapes)
        {
            if (_position >= _chars.Length)
            {
                return false;
            }

            RecordLine = _line;
            FieldCount = 0;
            HasValue = false;
            HasOversizedField = false;

            while (true)
            {
                var start = _position;
                var isQuoted = _position < _chars.Length && _chars[_position] == Quote;
                AddField(start, isQuoted ? ReadQuoted(backslashEscapes) : ReadUnquoted());

                if (_position == _chars.Length)
                {
                    return true;
                }

                if (_chars[_position] != Separator)
                {
                    ConsumeLineEnd();
                    return true;
                }

                _position++;
            }
        }

        private void AddField(int start, int length)
        {
            if (FieldCount < _starts.Length)
            {
                _starts[FieldCount] = start;
                _lengths[FieldCount] = length;
            }

            FieldCount++;
            HasValue |= length > 0;
            HasOversizedField |= length > VaultImport.MaxFieldChars;
        }

        // Leaves _position on a separator, on a line end or at the end of the text.
        private int ReadUnquoted()
        {
            var rest = _chars[_position..];
            var length = rest.IndexOfAny(UnquotedStop);
            if (length < 0)
            {
                length = rest.Length;
            }

            _position += length;
            if (_position < _chars.Length && _chars[_position] is Quote or NullChar)
            {
                throw new VaultImportException(ImportFileError.MalformedCsv, _line);
            }

            return length;
        }

        // Leaves _position after the closing quote, on a separator, on a line end or at the end of the text.
        private int ReadQuoted(bool backslashEscapes)
        {
            var openingLine = _line;
            var start = _position;
            var write = start;
            _position++;

            while (_position < _chars.Length)
            {
                var c = _chars[_position];
                var next = _position + 1 < _chars.Length ? _chars[_position + 1] : NullChar;

                switch (c)
                {
                    case NullChar:
                        throw new VaultImportException(ImportFileError.MalformedCsv, _line);

                    case Quote when !backslashEscapes && next == Quote:
                    case Backslash when backslashEscapes && next is Quote or Backslash:
                        _chars[write++] = next;
                        _position += 2;
                        continue;

                    case Quote:
                        _position++;
                        if (_position < _chars.Length && _chars[_position] is not (Separator or '\r' or '\n'))
                        {
                            throw new VaultImportException(ImportFileError.MalformedCsv, _line);
                        }

                        return write - start;

                    case '\r' when next != '\n':
                    case '\n':
                        _line++;
                        break;
                }

                _chars[write++] = c;
                _position++;
            }

            throw new VaultImportException(ImportFileError.MalformedCsv, openingLine);
        }

        private void ConsumeLineEnd()
        {
            _position += _chars[_position] == '\r' && _position + 1 < _chars.Length && _chars[_position + 1] == '\n' ? 2 : 1;
            _line++;
        }
    }
}
