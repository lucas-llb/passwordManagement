namespace PasswordManager.Core.Vault;

/// <summary>
/// A new Recovery Kit that is ready but not yet written: the result of <see cref="VaultSession.PrepareRecoveryKit"/>,
/// passed to <see cref="VaultSession.CommitRecoveryKit"/> once the user has written the kit down.
/// </summary>
/// <remarks>
/// It holds only ciphertext (the file it was built from and the new file bytes), never the kit text or the Vault Key.
/// Dropping it writes nothing, and the old kit keeps working.
/// </remarks>
public sealed class RecoveryKitReplacement
{
    internal RecoveryKitReplacement(byte[] baseFile, byte[] newFile)
    {
        BaseFile = baseFile;
        NewFile = newFile;
    }

    /// <summary>The session's file bytes when this was prepared. Compared by reference to detect a later write.</summary>
    internal byte[] BaseFile { get; }

    /// <summary>The whole new file bytes, with the new kit slot.</summary>
    internal byte[] NewFile { get; }
}
