using System.Security.Cryptography;

namespace PasswordManager.Core.Vault;

/// <summary>
/// Recovery Kit operations on a vault file: generate (or replace) the kit, unlock with the kit text, and set a new
/// master password from the kit (docs/vault-format.md, "Kit de Recuperação"). The kit is a 20-byte CSPRNG key shown as
/// <see cref="TextLength"/> chars of Crockford base32 and stored in the type-2 slot, which is the only slot it tries.
/// </summary>
/// <remarks>
/// Like <see cref="MasterPassword"/>, this is a pure byte transform: it does no disk I/O, and the caller passes the
/// result to the <see cref="VaultFile"/> writer. The caller's input buffers are never mutated unless documented; the
/// caller owns and must zero its own kit text, password and <c>vaultKey</c> buffers. The raw 20-byte key only lives on
/// the stack and is zeroed before returning. The format version does not change.
/// </remarks>
public static class RecoveryKit
{
    /// <summary>
    /// Length of the kit text: 8 groups of 4 symbols joined by hyphens. The caller allocates
    /// <c>new char[RecoveryKit.TextLength]</c> for <see cref="Create"/> and zeroes it after showing it.
    /// </summary>
    public const int TextLength = RecoveryKitText.Length;

    private const string InvalidKitMessage = "Recovery kit is invalid.";
    private const string NoKitMessage = "Vault has no recovery kit.";

    /// <summary>
    /// Generates a new Recovery Kit and returns a new file whose type-2 slot wraps the Vault Key with it. An existing
    /// kit slot is replaced, so the old kit stops opening the returned file; every other slot stays byte for byte, and
    /// the body is re-sealed under a fresh nonce.
    /// </summary>
    /// <remarks>
    /// Costs one Argon2id run, so callers run it off the UI thread. A wrong Vault Key is detected only after that run,
    /// when the current body is decrypted.
    /// </remarks>
    /// <param name="file">The whole current file bytes. Not modified.</param>
    /// <param name="vaultKey">The Vault Key, exactly <see cref="VaultFormat.KeySize"/> bytes. Not modified; the caller
    /// owns and zeros it.</param>
    /// <param name="parameters">Argon2id cost parameters for the kit slot.</param>
    /// <param name="kitText">Receives the kit text, exactly <see cref="TextLength"/> chars. Written only on success;
    /// the caller zeros it after showing it.</param>
    /// <returns>The whole new file bytes.</returns>
    /// <exception cref="ArgumentException">The Vault Key or <paramref name="kitText"/> has the wrong length.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="parameters"/> is outside the format bounds.</exception>
    /// <exception cref="VaultFormatException"><paramref name="file"/> is not a valid v1 vault file.</exception>
    /// <exception cref="VaultAuthenticationException">The Vault Key does not open this file, or the header or body
    /// was tampered with.</exception>
    public static byte[] Create(
        ReadOnlySpan<byte> file,
        ReadOnlySpan<byte> vaultKey,
        KdfParameters parameters,
        Span<char> kitText)
    {
        if (vaultKey.Length != VaultFormat.KeySize)
        {
            throw new ArgumentException("Vault Key has an invalid length.", nameof(vaultKey));
        }

        if (kitText.Length != TextLength)
        {
            throw new ArgumentException("Recovery kit text buffer has an invalid length.", nameof(kitText));
        }

        ValidateParameters(parameters);

        var header = VaultHeader.Parse(file);

        Span<byte> recoveryKey = stackalloc byte[VaultFormat.RecoveryKeySize];
        RandomNumberGenerator.Fill(recoveryKey);
        try
        {
            var slot = KeySlot.CreateRecoveryKitSlot(recoveryKey, parameters, vaultKey);
            var newHeader = header.WithSlot(file, slot);

            // Decrypts the current body under its old AAD: never seal from a cached model, or a wrong key goes unnoticed.
            var result = VaultBody.Reseal(file, header.BodyOffset, newHeader, vaultKey);

            // Only now, so the caller never sees a kit that is not in a valid file.
            RecoveryKitText.Encode(recoveryKey, kitText);
            return result;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(recoveryKey);
        }
    }

    /// <summary>
    /// Unwraps the Vault Key with the Recovery Kit text, without the master password.
    /// </summary>
    /// <remarks>
    /// Costs one Argon2id run, so callers run it off the UI thread. The slot and text checks run before it.
    /// </remarks>
    /// <param name="file">The whole file bytes. Not modified.</param>
    /// <param name="kitText">The kit text as typed or pasted (hyphens and spaces ignored, case-insensitive, <c>O</c>,
    /// <c>I</c>, <c>L</c> read as <c>0</c>, <c>1</c>, <c>1</c>). Not modified; the caller zeros it.</param>
    /// <param name="vaultKey">Receives the Vault Key, exactly <see cref="VaultFormat.KeySize"/> bytes. Zeroed on an
    /// authentication failure; the caller owns and zeros it.</param>
    /// <exception cref="ArgumentException"><paramref name="vaultKey"/> has the wrong length.</exception>
    /// <exception cref="VaultFormatException"><paramref name="file"/> is not a valid v1 vault file.</exception>
    /// <exception cref="InvalidOperationException">The vault has no recovery kit slot.</exception>
    /// <exception cref="FormatException"><paramref name="kitText"/> is not a valid kit text.</exception>
    /// <exception cref="VaultAuthenticationException">The kit is wrong (or was revoked), or the slot was tampered
    /// with.</exception>
    public static void Unlock(ReadOnlySpan<byte> file, ReadOnlySpan<char> kitText, Span<byte> vaultKey)
    {
        if (vaultKey.Length != VaultFormat.KeySize)
        {
            throw new ArgumentException("Vault Key has an invalid length.", nameof(vaultKey));
        }

        var header = VaultHeader.Parse(file);
        if (header.RecoveryKitSlotOffset is not { } slotOffset)
        {
            throw new InvalidOperationException(NoKitMessage);
        }

        Span<byte> recoveryKey = stackalloc byte[VaultFormat.RecoveryKeySize];
        try
        {
            if (!RecoveryKitText.TryDecode(kitText, recoveryKey))
            {
                throw new FormatException(InvalidKitMessage);
            }

            KeySlot.UnwrapRecoveryKitSlot(file, slotOffset, recoveryKey, vaultKey);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(recoveryKey);
        }
    }

    /// <summary>
    /// Unwraps the Vault Key with the Recovery Kit text and returns a new file protected by
    /// <paramref name="newPassword"/>. The kit slot stays byte for byte, so the same kit keeps working.
    /// </summary>
    /// <remarks>
    /// Costs two Argon2id runs (kit unwrap and new password slot), so callers run it off the UI thread.
    /// </remarks>
    /// <param name="file">The whole current file bytes. Not modified.</param>
    /// <param name="kitText">The kit text. Not modified; the caller zeros it.</param>
    /// <param name="newPassword">The new master password. Not modified; the caller zeros it.</param>
    /// <param name="parameters">Argon2id cost parameters for the new password slot.</param>
    /// <returns>The whole new file bytes.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="parameters"/> is outside the format bounds.</exception>
    /// <exception cref="ArgumentException"><paramref name="newPassword"/> contains an invalid surrogate.</exception>
    /// <exception cref="VaultFormatException"><paramref name="file"/> is not a valid v1 vault file.</exception>
    /// <exception cref="InvalidOperationException">The vault has no recovery kit slot.</exception>
    /// <exception cref="FormatException"><paramref name="kitText"/> is not a valid kit text.</exception>
    /// <exception cref="VaultAuthenticationException">The kit is wrong, or the slot, header or body was tampered
    /// with.</exception>
    public static byte[] ResetPassword(
        ReadOnlySpan<byte> file,
        ReadOnlySpan<char> kitText,
        ReadOnlySpan<char> newPassword,
        KdfParameters parameters)
    {
        ValidateParameters(parameters);

        Span<byte> vaultKey = stackalloc byte[VaultFormat.KeySize];
        try
        {
            Unlock(file, kitText, vaultKey);
            return MasterPassword.Reset(file, vaultKey, newPassword, parameters);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(vaultKey);
        }
    }

    private static void ValidateParameters(KdfParameters parameters)
    {
        if (!parameters.IsInRange)
        {
            throw new ArgumentOutOfRangeException(nameof(parameters), "KDF parameters are outside the format bounds.");
        }
    }
}
