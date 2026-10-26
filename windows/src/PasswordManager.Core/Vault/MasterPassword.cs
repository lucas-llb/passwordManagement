using System.Security.Cryptography;

namespace PasswordManager.Core.Vault;

/// <summary>
/// Changes the master password of a vault file: only the type-1 slot is replaced (fresh salt and wrap nonce, same
/// Vault Key), every other slot stays byte for byte, and the body is re-sealed with the same Vault Key under a fresh
/// body nonce, keeping its plaintext bytes (docs/vault-format.md, "Regras de escrita").
/// </summary>
/// <remarks>
/// This is a pure byte transform: it does no disk I/O, and the caller passes the result to the
/// <see cref="VaultFile"/> writer. The caller's input buffers are never mutated; the caller owns and must zero its own
/// password and <c>vaultKey</c> buffers. The format version does not change.
/// </remarks>
public static class MasterPassword
{
    /// <summary>
    /// Unwraps the Vault Key with the current master password and returns a new file protected by
    /// <paramref name="newPassword"/>.
    /// </summary>
    /// <remarks>
    /// Costs two Argon2id runs (unwrap and new slot), so callers run it off the UI thread.
    /// </remarks>
    /// <param name="file">The whole current file bytes. Not modified.</param>
    /// <param name="currentPassword">The current master password. Not modified; the caller zeros it.</param>
    /// <param name="newPassword">The new master password. Not modified; the caller zeros it.</param>
    /// <param name="parameters">Argon2id cost parameters for the new password slot.</param>
    /// <returns>The whole new file bytes.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="parameters"/> is outside the format bounds.</exception>
    /// <exception cref="ArgumentException"><paramref name="newPassword"/> contains an invalid surrogate.</exception>
    /// <exception cref="VaultFormatException"><paramref name="file"/> is not a valid v1 vault file.</exception>
    /// <exception cref="VaultAuthenticationException">The current password is wrong, or the slot, header or body
    /// was tampered with.</exception>
    public static byte[] Change(
        ReadOnlySpan<byte> file,
        ReadOnlySpan<char> currentPassword,
        ReadOnlySpan<char> newPassword,
        KdfParameters parameters)
    {
        ValidateParameters(parameters);

        var header = VaultHeader.Parse(file);

        Span<byte> vaultKey = stackalloc byte[VaultFormat.KeySize];
        try
        {
            KeySlot.UnwrapPasswordSlot(file, header.PasswordSlotOffset, currentPassword, vaultKey);
            return ResetCore(file, header, vaultKey, newPassword, parameters);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(vaultKey);
        }
    }

    /// <summary>
    /// Sets a new master password from a Vault Key that is already unwrapped (by an open session, or by the recovery
    /// kit). The current body is decrypted with <paramref name="vaultKey"/> first, so a key that does not belong to
    /// this file is rejected instead of being wrapped into the new slot.
    /// </summary>
    /// <remarks>
    /// Costs one Argon2id run, so callers run it off the UI thread.
    /// </remarks>
    /// <param name="file">The whole current file bytes. Not modified.</param>
    /// <param name="vaultKey">The Vault Key, exactly <see cref="VaultFormat.KeySize"/> bytes. Not modified; the caller
    /// owns and zeros it.</param>
    /// <param name="newPassword">The new master password. Not modified; the caller zeros it.</param>
    /// <param name="parameters">Argon2id cost parameters for the new password slot.</param>
    /// <returns>The whole new file bytes.</returns>
    /// <exception cref="ArgumentException">The Vault Key has the wrong length, or <paramref name="newPassword"/>
    /// contains an invalid surrogate.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="parameters"/> is outside the format bounds.</exception>
    /// <exception cref="VaultFormatException"><paramref name="file"/> is not a valid v1 vault file.</exception>
    /// <exception cref="VaultAuthenticationException">The Vault Key does not open this file, or the header or body
    /// was tampered with.</exception>
    public static byte[] Reset(
        ReadOnlySpan<byte> file,
        ReadOnlySpan<byte> vaultKey,
        ReadOnlySpan<char> newPassword,
        KdfParameters parameters)
    {
        if (vaultKey.Length != VaultFormat.KeySize)
        {
            throw new ArgumentException("Vault Key has an invalid length.", nameof(vaultKey));
        }

        ValidateParameters(parameters);

        var header = VaultHeader.Parse(file);
        return ResetCore(file, header, vaultKey, newPassword, parameters);
    }

    private static void ValidateParameters(KdfParameters parameters)
    {
        if (!parameters.IsInRange)
        {
            throw new ArgumentOutOfRangeException(nameof(parameters), "KDF parameters are outside the format bounds.");
        }
    }

    private static byte[] ResetCore(
        ReadOnlySpan<byte> file,
        VaultHeader header,
        ReadOnlySpan<byte> vaultKey,
        ReadOnlySpan<char> newPassword,
        KdfParameters parameters)
    {
        var slot = KeySlot.CreatePasswordSlot(newPassword, parameters, vaultKey);
        var newHeader = header.WithSlot(file, slot);

        // Decrypts the current body under its old AAD: never seal from a cached model, or a wrong key goes unnoticed.
        return VaultBody.Reseal(file, header.BodyOffset, newHeader, vaultKey);
    }
}
