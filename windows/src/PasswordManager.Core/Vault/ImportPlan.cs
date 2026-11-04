namespace PasswordManager.Core.Vault;

/// <summary>Which imported entries are duplicates of vault entries. The counts do not depend on the policy.</summary>
public sealed class ImportPlan
{
    internal ImportPlan(bool[] isDuplicate, int duplicateCount)
    {
        IsDuplicate = isDuplicate;
        DuplicateCount = duplicateCount;
    }

    /// <summary>One flag per imported entry, in file order.</summary>
    public bool[] IsDuplicate { get; }

    /// <summary>The imported entries that match no vault entry.</summary>
    public int NewCount => IsDuplicate.Length - DuplicateCount;

    /// <summary>The imported entries that match a vault entry.</summary>
    public int DuplicateCount { get; }
}
