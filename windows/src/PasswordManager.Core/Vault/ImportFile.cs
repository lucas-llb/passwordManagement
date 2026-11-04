namespace PasswordManager.Core.Vault;

/// <summary>What an import file holds after validation. It exists only in memory, and only until the import ends.</summary>
/// <remarks>A class and not a record, so no generated member can print the entries.</remarks>
public sealed class ImportFile
{
    internal ImportFile(IReadOnlyList<VaultEntry> entries, IReadOnlyList<int> rejectedLines, IReadOnlyList<string> ignoredColumns)
    {
        Entries = entries;
        RejectedLines = rejectedLines;
        IgnoredColumns = ignoredColumns;
    }

    /// <summary>The accepted entries, in file order.</summary>
    public IReadOnlyList<VaultEntry> Entries { get; }

    /// <summary>
    /// The items left out, in file order: the 1-based physical line where a CSV record starts, or the 1-based position
    /// of a <c>.pmvault</c> entry.
    /// </summary>
    public IReadOnlyList<int> RejectedLines { get; }

    /// <summary>CSV columns with a value in an accepted record that the app does not keep. Names only, never values.</summary>
    public IReadOnlyList<string> IgnoredColumns { get; }
}
