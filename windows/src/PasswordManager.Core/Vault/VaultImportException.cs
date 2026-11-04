namespace PasswordManager.Core.Vault;

/// <summary>
/// Thrown when a whole import file is refused. It carries the reason and a line number, and never file content.
/// </summary>
public sealed class VaultImportException : Exception
{
    public VaultImportException()
    {
    }

    public VaultImportException(string message)
        : base(message)
    {
    }

    public VaultImportException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <param name="error">The reason.</param>
    /// <param name="line">The 1-based line, or item position, of the problem; 0 when it has none.</param>
    public VaultImportException(ImportFileError error, int line = 0)
        : base("Import file was refused.")
    {
        Error = error;
        Line = line;
    }

    /// <summary>The reason the file was refused.</summary>
    public ImportFileError Error { get; }

    /// <summary>The 1-based line, or item position, of the problem; 0 when it has none.</summary>
    public int Line { get; }
}
