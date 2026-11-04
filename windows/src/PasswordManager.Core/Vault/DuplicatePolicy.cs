namespace PasswordManager.Core.Vault;

/// <summary>What an import does with an entry that <see cref="VaultImport.Plan"/> found to be a duplicate.</summary>
public enum DuplicatePolicy
{
    /// <summary>The duplicate is dropped; the vault entry stays as it is.</summary>
    Skip,

    /// <summary>The duplicate is appended as another entry.</summary>
    Duplicate,

    /// <summary>The vault entry is updated in place; an empty imported field does not erase a saved value.</summary>
    Replace,
}
