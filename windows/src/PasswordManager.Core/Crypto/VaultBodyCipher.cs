using System.Security.Cryptography;
using NSec.Cryptography;
using PasswordManager.Core.Vault;

namespace PasswordManager.Core.Crypto;

/// <summary>
/// Encrypts and decrypts the vault body with the Vault Key using XChaCha20-Poly1305 (libsodium via NSec), in the
/// combined mode of <c>crypto_aead_xchacha20poly1305_ietf</c>: <c>bodyCiphertext = ciphertext || tag</c>.
/// The associated data is <c>AAD_body = file[0 .. B+24)</c> (docs/vault-format.md); building it is the caller's job.
/// </summary>
/// <remarks>
/// Ownership: the caller's input buffers are never mutated. The caller owns and must zero its own
/// <c>vaultKey</c> and plaintext buffers once it no longer needs them. The NSec key created from the Vault Key
/// lives in libsodium secure memory and is disposed (zeroed and freed) before returning.
/// </remarks>
internal static class VaultBodyCipher
{
    /// <summary>
    /// Encrypts the body plaintext under the Vault Key.
    /// </summary>
    /// <param name="vaultKey">The Vault Key, exactly <see cref="VaultFormat.KeySize"/> bytes. Not modified.</param>
    /// <param name="plaintext">The UTF-8 JSON body. Not modified.</param>
    /// <param name="aad">The associated data, any length.</param>
    /// <param name="nonce">A fresh random nonce, exactly <see cref="VaultFormat.NonceSize"/> bytes.</param>
    /// <param name="ciphertext">Receives ciphertext followed by the tag; exactly
    /// <c>plaintext.Length + </c><see cref="VaultFormat.TagSize"/> bytes.</param>
    /// <exception cref="ArgumentException">Any buffer has the wrong length.</exception>
    public static void Encrypt(
        ReadOnlySpan<byte> vaultKey,
        ReadOnlySpan<byte> plaintext,
        ReadOnlySpan<byte> aad,
        ReadOnlySpan<byte> nonce,
        Span<byte> ciphertext)
    {
        ValidateKeyAndNonce(vaultKey.Length, nonce.Length);

        if (ciphertext.Length - VaultFormat.TagSize != plaintext.Length)
        {
            throw new ArgumentException("Body ciphertext buffer has an invalid length.");
        }

        using var key = Key.Import(AeadAlgorithm.XChaCha20Poly1305, vaultKey, KeyBlobFormat.RawSymmetricKey);
        AeadAlgorithm.XChaCha20Poly1305.Encrypt(key, nonce, aad, plaintext, ciphertext);
    }

    /// <summary>
    /// Decrypts the body with the Vault Key.
    /// </summary>
    /// <param name="vaultKey">The Vault Key, exactly <see cref="VaultFormat.KeySize"/> bytes. Not modified.</param>
    /// <param name="ciphertext">Ciphertext followed by the tag; at least <see cref="VaultFormat.TagSize"/> bytes. Not modified.</param>
    /// <param name="aad">The associated data, any length.</param>
    /// <param name="nonce">The stored body nonce, exactly <see cref="VaultFormat.NonceSize"/> bytes.</param>
    /// <param name="plaintext">Receives the plaintext; exactly <c>ciphertext.Length - </c><see cref="VaultFormat.TagSize"/>
    /// bytes. The caller zeros it. It is zeroed before any exception propagates.</param>
    /// <exception cref="ArgumentException">Any buffer has the wrong length.</exception>
    /// <exception cref="VaultAuthenticationException">The tag does not verify: wrong Vault Key, or tampered AAD, nonce or ciphertext.</exception>
    public static void Decrypt(
        ReadOnlySpan<byte> vaultKey,
        ReadOnlySpan<byte> ciphertext,
        ReadOnlySpan<byte> aad,
        ReadOnlySpan<byte> nonce,
        Span<byte> plaintext)
    {
        ValidateKeyAndNonce(vaultKey.Length, nonce.Length);

        if (ciphertext.Length < VaultFormat.TagSize)
        {
            throw new ArgumentException("Body ciphertext is shorter than the tag.");
        }

        if (plaintext.Length != ciphertext.Length - VaultFormat.TagSize)
        {
            throw new ArgumentException("Body plaintext buffer has an invalid length.");
        }

        bool authenticated;
        try
        {
            using var key = Key.Import(AeadAlgorithm.XChaCha20Poly1305, vaultKey, KeyBlobFormat.RawSymmetricKey);
            authenticated = AeadAlgorithm.XChaCha20Poly1305.Decrypt(key, nonce, aad, ciphertext, plaintext);
        }
        catch
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw;
        }

        if (!authenticated)
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw new VaultAuthenticationException();
        }
    }

    private static void ValidateKeyAndNonce(int vaultKeyLength, int nonceLength)
    {
        if (vaultKeyLength != VaultFormat.KeySize)
        {
            throw new ArgumentException("Vault Key has an invalid length.");
        }

        if (nonceLength != VaultFormat.NonceSize)
        {
            throw new ArgumentException("Body nonce has an invalid length.");
        }
    }
}
