namespace PasswordManager.Core.Vault;

/// <summary>The whole entry list after an import, ready to save, and what happened to the imported entries.</summary>
/// <remarks>A class and not a record, so no generated member can print the entries.</remarks>
public sealed class ImportMergeResult
{
    internal ImportMergeResult(VaultEntry[] entries, int added, int replaced, int skipped)
    {
        Entries = entries;
        Added = added;
        Replaced = replaced;
        Skipped = skipped;
    }

    /// <summary>The merged entries: the vault's in their order, then the appended ones in file order.</summary>
    public VaultEntry[] Entries { get; }

    /// <summary>Imported entries appended as new ones.</summary>
    public int Added { get; }

    /// <summary>Vault entries updated in place.</summary>
    public int Replaced { get; }

    /// <summary>Duplicates dropped.</summary>
    public int Skipped { get; }
}
