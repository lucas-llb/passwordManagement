namespace PasswordManager.Core.Vault;

/// <summary>Why a whole import file is refused.</summary>
public enum ImportFileError
{
    /// <summary>The file is over <see cref="VaultImport.MaxFileBytes"/>.</summary>
    TooLarge,

    /// <summary>The file holds more than <see cref="VaultImport.MaxEntries"/> items.</summary>
    TooManyEntries,

    /// <summary>A CSV is not strict UTF-8.</summary>
    InvalidEncoding,

    /// <summary>A CSV has no header, or a header the app cannot map.</summary>
    InvalidHeader,

    /// <summary>A CSV breaks the record structure, so nothing can be trusted about what follows.</summary>
    MalformedCsv,
}
