using System.Security.Cryptography;
using System.Text;

namespace PasswordManager.Core.Vault;

/// <summary>
/// The rules of an import, the same for a <c>.pmvault</c> and a CSV (AGENTS.md §5.3): the file and record limits, how an
/// imported entry is matched to a vault entry, and how the two lists are merged. Nothing here touches the vault: the
/// caller saves the merged entries through <see cref="VaultSession.Save"/>.
/// </summary>
/// <remarks>
/// <para>An imported entry is a duplicate when its <see cref="VaultEntry.Id"/> equals a vault entry's, or else when
/// <see cref="VaultEntry.Title"/>, <see cref="VaultEntry.Username"/> and <see cref="VaultEntry.Url"/> are all equal.
/// Text compares ordinally after NFC, with no trim and no case folding: a missed match only adds an entry, while a false
/// match could lose data.</para>
/// <para>Each vault entry matches at most one imported entry, so a later row with the same key counts as new. That keeps
/// the counts independent of the policy, loses no row to another row of the file, and makes a second import of the same
/// file stable.</para>
/// </remarks>
public static class VaultImport
{
    /// <summary>The largest import file, in bytes (32 MiB).</summary>
    public const int MaxFileBytes = 32 * 1024 * 1024;

    /// <summary>The most items an import file can hold.</summary>
    public const int MaxEntries = 20_000;

    /// <summary>The longest field of an imported entry, in UTF-16 chars.</summary>
    public const int MaxFieldChars = 65_536;

    /// <summary>
    /// Reads a whole import file, refusing it by its length before allocating. Writers are shut out while reading, and
    /// the file stream keeps no buffer of its own.
    /// </summary>
    /// <param name="path">The file the user chose.</param>
    /// <returns>The file bytes, in a pinned array the caller zeroes after use.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="path"/> is empty or not a valid path.</exception>
    /// <exception cref="VaultImportException">The file is <see cref="ImportFileError.TooLarge"/>.</exception>
    /// <exception cref="IOException">The file cannot be read (for example, <see cref="FileNotFoundException"/>).</exception>
    /// <exception cref="UnauthorizedAccessException">Reading the file was denied.</exception>
    public static byte[] ReadFile(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 0);
        if (stream.Length > MaxFileBytes)
        {
            throw new VaultImportException(ImportFileError.TooLarge);
        }

        var bytes = GC.AllocateArray<byte>((int)stream.Length, pinned: true);
        try
        {
            stream.ReadExactly(bytes);
            return bytes;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(bytes);
            throw;
        }
    }

    /// <summary>
    /// Applies the record rules and the limit to the decrypted content of a <c>.pmvault</c>. Accepted entries keep their
    /// <see cref="VaultEntry.Id"/>, dates and favorite. An entry with every field empty is skipped silently; one with a
    /// whitespace-only title or a field over <see cref="MaxFieldChars"/> is listed by its 1-based position.
    /// </summary>
    /// <param name="content">The decrypted content.</param>
    /// <returns>The accepted entries and the rejected positions.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="content"/> is null.</exception>
    /// <exception cref="VaultImportException">The content holds more than <see cref="MaxEntries"/> entries.</exception>
    public static ImportFile FromContent(VaultContent content)
    {
        ArgumentNullException.ThrowIfNull(content);

        if (content.Entries.Count > MaxEntries)
        {
            throw new VaultImportException(ImportFileError.TooManyEntries);
        }

        var entries = new List<VaultEntry>(content.Entries.Count);
        var rejected = new List<int>();
        for (var i = 0; i < content.Entries.Count; i++)
        {
            var entry = content.Entries[i];
            if (HasNoText(entry))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(entry.Title) || HasOversizedField(entry))
            {
                rejected.Add(i + 1);
                continue;
            }

            entries.Add(entry);
        }

        return new ImportFile(entries, rejected, []);
    }

    /// <summary>Finds which of <paramref name="incoming"/> are duplicates of <paramref name="existing"/>.</summary>
    /// <param name="existing">The vault entries.</param>
    /// <param name="incoming">The imported entries, in file order.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static ImportPlan Plan(IReadOnlyList<VaultEntry> existing, IReadOnlyList<VaultEntry> incoming)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(incoming);

        var matches = Match(existing, incoming);
        var isDuplicate = new bool[matches.Length];
        var duplicates = 0;
        for (var i = 0; i < matches.Length; i++)
        {
            if (matches[i] >= 0)
            {
                isDuplicate[i] = true;
                duplicates++;
            }
        }

        return new ImportPlan(isDuplicate, duplicates);
    }

    /// <summary>
    /// Builds the whole entry list after an import. Vault entries keep their order, new entries are appended in file
    /// order, and the result always has unique ids: an appended entry whose id is taken gets a new one.
    /// </summary>
    /// <remarks>
    /// <see cref="DuplicatePolicy.Replace"/> keeps the vault entry's id and creation date, takes each text field only
    /// when the imported one is not empty (so a column a CSV lacks erases nothing), ORs the favorite flags and sets the
    /// update date to <paramref name="now"/> truncated to milliseconds.
    /// </remarks>
    /// <param name="existing">The vault entries. Not modified.</param>
    /// <param name="incoming">The imported entries, in file order. Not modified.</param>
    /// <param name="policy">What to do with a duplicate.</param>
    /// <param name="now">The import time.</param>
    /// <exception cref="ArgumentNullException"><paramref name="existing"/> or <paramref name="incoming"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="policy"/> is not a defined value.</exception>
    public static ImportMergeResult Merge(
        IReadOnlyList<VaultEntry> existing,
        IReadOnlyList<VaultEntry> incoming,
        DuplicatePolicy policy,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(incoming);
        if (!Enum.IsDefined(policy))
        {
            throw new ArgumentOutOfRangeException(nameof(policy));
        }

        var stamp = TruncateToMilliseconds(now);
        var matches = Match(existing, incoming);
        var merged = new List<VaultEntry>(existing.Count + incoming.Count);
        merged.AddRange(existing);
        var ids = new HashSet<Guid>(existing.Select(entry => entry.Id));
        int added = 0, replaced = 0, skipped = 0;

        for (var i = 0; i < incoming.Count; i++)
        {
            var match = matches[i];
            if (match >= 0 && policy == DuplicatePolicy.Skip)
            {
                skipped++;
            }
            else if (match >= 0 && policy == DuplicatePolicy.Replace)
            {
                merged[match] = ReplaceWith(existing[match], incoming[i], stamp);
                replaced++;
            }
            else
            {
                merged.Add(WithUniqueId(incoming[i], ids));
                added++;
            }
        }

        return new ImportMergeResult([.. merged], added, replaced, skipped);
    }

    internal static DateTimeOffset TruncateToMilliseconds(DateTimeOffset value) =>
        new(value.Ticks - (value.Ticks % TimeSpan.TicksPerMillisecond), value.Offset);

    // For each incoming entry, the index of the vault entry it matches, or -1. Ids go first over the whole file, so a
    // key match in an earlier row cannot take the vault entry a later row names by its Id.
    private static int[] Match(IReadOnlyList<VaultEntry> existing, IReadOnlyList<VaultEntry> incoming)
    {
        var matches = new int[incoming.Count];
        Array.Fill(matches, -1);
        var taken = new bool[existing.Count];

        var byId = new Dictionary<Guid, int>(existing.Count);
        var byKey = new Dictionary<EntryKey, Queue<int>>(existing.Count);
        for (var i = 0; i < existing.Count; i++)
        {
            byId.TryAdd(existing[i].Id, i);

            var key = EntryKey.Of(existing[i]);
            if (!byKey.TryGetValue(key, out var queue))
            {
                queue = new Queue<int>(1);
                byKey.Add(key, queue);
            }

            queue.Enqueue(i);
        }

        for (var i = 0; i < incoming.Count; i++)
        {
            if (byId.TryGetValue(incoming[i].Id, out var match) && !taken[match])
            {
                taken[match] = true;
                matches[i] = match;
            }
        }

        for (var i = 0; i < incoming.Count; i++)
        {
            if (matches[i] >= 0 || !byKey.TryGetValue(EntryKey.Of(incoming[i]), out var queue))
            {
                continue;
            }

            while (queue.TryDequeue(out var candidate))
            {
                if (!taken[candidate])
                {
                    taken[candidate] = true;
                    matches[i] = candidate;
                    break;
                }
            }
        }

        return matches;
    }

    private static VaultEntry ReplaceWith(VaultEntry saved, VaultEntry imported, DateTimeOffset now) =>
        saved with
        {
            Title = Pick(imported.Title, saved.Title),
            Username = Pick(imported.Username, saved.Username),
            Password = Pick(imported.Password, saved.Password),
            Url = Pick(imported.Url, saved.Url),
            Notes = Pick(imported.Notes, saved.Notes),
            Folder = Pick(imported.Folder, saved.Folder),
            Favorite = saved.Favorite || imported.Favorite,
            UpdatedAt = now,
        };

    private static string Pick(string imported, string saved) => imported.Length > 0 ? imported : saved;

    private static VaultEntry WithUniqueId(VaultEntry entry, HashSet<Guid> ids)
    {
        if (ids.Add(entry.Id))
        {
            return entry;
        }

        Guid id;
        do
        {
            id = Guid.NewGuid();
        }
        while (!ids.Add(id));

        return entry with { Id = id };
    }

    private static bool HasNoText(VaultEntry entry) =>
        entry.Title.Length == 0 && entry.Username.Length == 0 && entry.Password.Length == 0 &&
        entry.Url.Length == 0 && entry.Notes.Length == 0 && entry.Folder.Length == 0;

    private static bool HasOversizedField(VaultEntry entry) =>
        entry.Title.Length > MaxFieldChars || entry.Username.Length > MaxFieldChars ||
        entry.Password.Length > MaxFieldChars || entry.Url.Length > MaxFieldChars ||
        entry.Notes.Length > MaxFieldChars || entry.Folder.Length > MaxFieldChars;

    private static string Normalized(string text)
    {
        try
        {
            return text.Normalize(NormalizationForm.FormC);
        }
        catch (ArgumentException)
        {
            // Not valid UTF-16 (a lone surrogate): it compares raw, as in the list search.
            return text;
        }
    }

    // The generated equality compares strings ordinally.
    private readonly record struct EntryKey(string Title, string Username, string Url)
    {
        public static EntryKey Of(VaultEntry entry) =>
            new(Normalized(entry.Title), Normalized(entry.Username), Normalized(entry.Url));
    }
}
