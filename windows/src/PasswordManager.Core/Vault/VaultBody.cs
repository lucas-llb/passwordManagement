using System.Security.Cryptography;
using System.Text.Json;
using PasswordManager.Core.Crypto;

namespace PasswordManager.Core.Vault;

/// <summary>
/// Seals a <see cref="VaultContent"/> into <c>header || bodyNonce || ciphertext || tag</c> with the Vault Key, and
/// opens such bytes back. <c>AAD_body = file[0 .. B+24)</c>: the whole header, every slot (unknown ones too) and the
/// body nonce (docs/vault-format.md). Header parsing and computing <c>B</c> are the caller's job.
/// </summary>
/// <remarks>
/// Only the UTF-8 JSON byte buffers are zeroed after use. The <see cref="VaultContent"/> model and its strings are
/// managed objects that cannot be zeroed (AGENTS.md §3.5); the caller discards them on lock.
/// </remarks>
public static class VaultBody
{
    private const string InvalidJsonMessage = "Vault body is not valid JSON.";

    /// <summary>
    /// Serializes <paramref name="content"/> and encrypts it under a fresh random body nonce.
    /// </summary>
    /// <param name="header">The file bytes before the body nonce (<c>file[0..B)</c>). Not modified.</param>
    /// <param name="vaultKey">The Vault Key, exactly <see cref="VaultFormat.KeySize"/> bytes. Not modified.</param>
    /// <param name="content">The plaintext vault content.</param>
    /// <returns>The whole file bytes: <c>header || bodyNonce || ciphertext || tag</c>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="content"/> is null.</exception>
    /// <exception cref="ArgumentException">The header is too short or the Vault Key has the wrong length.</exception>
    public static byte[] Seal(ReadOnlySpan<byte> header, ReadOnlySpan<byte> vaultKey, VaultContent content)
    {
        ArgumentNullException.ThrowIfNull(content);

        if (header.Length < VaultFormat.FirstSlotOffset)
        {
            throw new ArgumentException("Header is too short.", nameof(header));
        }

        if (vaultKey.Length != VaultFormat.KeySize)
        {
            throw new ArgumentException("Vault Key has an invalid length.", nameof(vaultKey));
        }

        var plaintext = JsonSerializer.SerializeToUtf8Bytes(content, VaultJsonContext.Default.VaultContent);
        try
        {
            var bodyOffset = header.Length;
            var file = new byte[bodyOffset + VaultFormat.NonceSize + plaintext.Length + VaultFormat.TagSize];

            header.CopyTo(file);
            RandomNumberGenerator.Fill(file.AsSpan(bodyOffset, VaultFormat.NonceSize));

            VaultBodyCipher.Encrypt(
                vaultKey,
                plaintext,
                aad: file.AsSpan(0, bodyOffset + VaultFormat.NonceSize),
                nonce: file.AsSpan(bodyOffset, VaultFormat.NonceSize),
                ciphertext: file.AsSpan(bodyOffset + VaultFormat.NonceSize));

            return file;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    /// <summary>
    /// Decrypts and parses the body of a vault file.
    /// </summary>
    /// <param name="file">The whole file bytes. Not modified.</param>
    /// <param name="bodyOffset">The offset <c>B</c> of the body nonce, computed by the caller from the header.</param>
    /// <param name="vaultKey">The Vault Key, exactly <see cref="VaultFormat.KeySize"/> bytes. Not modified.</param>
    /// <returns>The decrypted content.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="bodyOffset"/> is before the first slot.</exception>
    /// <exception cref="ArgumentException">The Vault Key has the wrong length.</exception>
    /// <exception cref="VaultFormatException">The body is truncated, or the authenticated plaintext is not valid vault JSON.</exception>
    /// <exception cref="VaultAuthenticationException">The tag does not verify: wrong Vault Key, wrong offset, or tampered bytes.</exception>
    public static VaultContent Open(ReadOnlySpan<byte> file, int bodyOffset, ReadOnlySpan<byte> vaultKey)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(bodyOffset, VaultFormat.FirstSlotOffset);

        // bodyOffset >= 7, so the subtraction cannot overflow.
        if (file.Length - bodyOffset < VaultFormat.NonceSize + VaultFormat.TagSize)
        {
            throw new VaultFormatException("Vault body is truncated.");
        }

        if (vaultKey.Length != VaultFormat.KeySize)
        {
            throw new ArgumentException("Vault Key has an invalid length.", nameof(vaultKey));
        }

        var aad = file[..(bodyOffset + VaultFormat.NonceSize)];
        var nonce = file.Slice(bodyOffset, VaultFormat.NonceSize);
        var ciphertext = file[(bodyOffset + VaultFormat.NonceSize)..];
        var plaintext = new byte[ciphertext.Length - VaultFormat.TagSize];
        try
        {
            VaultBodyCipher.Decrypt(vaultKey, ciphertext, aad, nonce, plaintext);
            return Parse(plaintext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static VaultContent Parse(byte[] plaintext)
    {
        VaultContent? content;
        try
        {
            content = JsonSerializer.Deserialize(plaintext, VaultJsonContext.Default.VaultContent);
        }
        catch (JsonException)
        {
            // No inner exception: its message may quote decrypted secrets.
            throw new VaultFormatException(InvalidJsonMessage);
        }

        if (content?.Entries is null || content.Entries.Any(entry => entry is null))
        {
            throw new VaultFormatException(InvalidJsonMessage);
        }

        return content;
    }
}
