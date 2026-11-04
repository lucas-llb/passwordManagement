namespace PasswordManager.ViewModels;

/// <summary>Why the last import step failed. The view maps each value to its own text.</summary>
public enum ImportError
{
    /// <summary>Nothing to show: no attempt yet, the attempt worked or was canceled, or the vault locked.</summary>
    None,

    /// <summary>The file could not be read (it is missing, denied, locked, and so on).</summary>
    ReadFailed,

    /// <summary>The file is over the size limit.</summary>
    TooLarge,

    /// <summary>The file holds more items than the limit.</summary>
    TooManyEntries,

    /// <summary>The file is neither a <c>.pmvault</c> nor a <c>.csv</c>, by its extension.</summary>
    UnsupportedFile,

    /// <summary>The file is not a vault, or its content could not be opened for a reason other than the password.</summary>
    InvalidVaultFile,

    /// <summary>The password is wrong or the file was altered; the two are deliberately the same message.</summary>
    WrongPassword,

    /// <summary>The CSV is not UTF-8.</summary>
    InvalidEncoding,

    /// <summary>The CSV has no header, or a header the app cannot map.</summary>
    InvalidHeader,

    /// <summary>The CSV breaks the record structure, near <see cref="ImportViewModel.ErrorLine"/>.</summary>
    MalformedCsv,

    /// <summary>The vault could not be written; it and the list are unchanged, and the preview stays for a retry.</summary>
    SaveFailed,
}
