using System.Security.Cryptography;
using NSec.Cryptography;
using PasswordManager.Core.Vault;

namespace PasswordManager.Core.Crypto;

/// <summary>
/// Wraps and unwraps the 32-byte Vault Key of a key slot with XChaCha20-Poly1305 (libsodium via NSec), in the
/// combined mode of <c>crypto_aead_xchacha20poly1305_ietf</c>: <c>wrappedVaultKey = ciphertext || tag</c>.
/// The associated data is <c>AAD_wrap</c> (docs/vault-format.md); building it is the caller's job.
/// </summary>
/// <remarks>
/// Ownership: the caller's input buffers are never mutated. The caller owns and must zero its own
/// <c>kek</c> and <c>vaultKey</c> buffers once it no longer needs them. The NSec key created from the KEK
/// lives in libsodium secure memory and is disposed (zeroed and freed) before returning.
/// </remarks>
internal static class VaultKeyWrap
{
    /// <summary>
    /// Encrypts the Vault Key under the KEK.
    /// </summary>
    /// <param name="kek">The key-encryption key, exactly <see cref="VaultFormat.KeySize"/> bytes. Not modified.</param>
    /// <param name="vaultKey">The Vault Key, exactly <see cref="VaultFormat.KeySize"/> bytes. Not modified.</param>
    /// <param name="aad">The associated data, exactly <see cref="VaultFormat.WrapAadSize"/> bytes.</param>
    /// <param name="nonce">A fresh random nonce, exactly <see cref="VaultFormat.NonceSize"/> bytes.</param>
    /// <param name="wrappedVaultKey">Receives ciphertext followed by the tag; exactly <see cref="VaultFormat.WrappedKeySize"/> bytes.</param>
    /// <exception cref="ArgumentException">Any buffer has the wrong length.</exception>
    public static void Wrap(
        ReadOnlySpan<byte> kek,
        ReadOnlySpan<byte> vaultKey,
        ReadOnlySpan<byte> aad,
        ReadOnlySpan<byte> nonce,
        Span<byte> wrappedVaultKey)
    {
        ValidateLengths(kek.Length, vaultKey.Length, aad.Length, nonce.Length, wrappedVaultKey.Length);

        using var key = Key.Import(AeadAlgorithm.XChaCha20Poly1305, kek, KeyBlobFormat.RawSymmetricKey);
        AeadAlgorithm.XChaCha20Poly1305.Encrypt(key, nonce, aad, vaultKey, wrappedVaultKey);
    }

    /// <summary>
    /// Decrypts the Vault Key with the KEK.
    /// </summary>
    /// <param name="kek">The key-encryption key, exactly <see cref="VaultFormat.KeySize"/> bytes. Not modified.</param>
    /// <param name="wrappedVaultKey">Ciphertext followed by the tag; exactly <see cref="VaultFormat.WrappedKeySize"/> bytes. Not modified.</param>
    /// <param name="aad">The associated data, exactly <see cref="VaultFormat.WrapAadSize"/> bytes.</param>
    /// <param name="nonce">The nonce stored in the slot, exactly <see cref="VaultFormat.NonceSize"/> bytes.</param>
    /// <param name="vaultKey">Receives the Vault Key; exactly <see cref="VaultFormat.KeySize"/> bytes. The caller zeros it.
    /// It is zeroed before any exception propagates.</param>
    /// <exception cref="ArgumentException">Any buffer has the wrong length.</exception>
    /// <exception cref="VaultAuthenticationException">The tag does not verify: wrong KEK, or tampered AAD, nonce or ciphertext.</exception>
    public static void Unwrap(
        ReadOnlySpan<byte> kek,
        ReadOnlySpan<byte> wrappedVaultKey,
        ReadOnlySpan<byte> aad,
        ReadOnlySpan<byte> nonce,
        Span<byte> vaultKey)
    {
        ValidateLengths(kek.Length, vaultKey.Length, aad.Length, nonce.Length, wrappedVaultKey.Length);

        bool authenticated;
        try
        {
            using var key = Key.Import(AeadAlgorithm.XChaCha20Poly1305, kek, KeyBlobFormat.RawSymmetricKey);
            authenticated = AeadAlgorithm.XChaCha20Poly1305.Decrypt(key, nonce, aad, wrappedVaultKey, vaultKey);
        }
        catch
        {
            CryptographicOperations.ZeroMemory(vaultKey);
            throw;
        }

        if (!authenticated)
        {
            CryptographicOperations.ZeroMemory(vaultKey);
            throw new VaultAuthenticationException();
        }
    }

    private static void ValidateLengths(int kekLength, int vaultKeyLength, int aadLength, int nonceLength, int wrappedLength)
    {
        if (kekLength != VaultFormat.KeySize)
        {
            throw new ArgumentException("KEK has an invalid length.");
        }

        if (vaultKeyLength != VaultFormat.KeySize)
        {
            throw new ArgumentException("Vault Key has an invalid length.");
        }

        if (aadLength != VaultFormat.WrapAadSize)
        {
            throw new ArgumentException("Wrap AAD has an invalid length.");
        }

        if (nonceLength != VaultFormat.NonceSize)
        {
            throw new ArgumentException("Wrap nonce has an invalid length.");
        }

        if (wrappedLength != VaultFormat.WrappedKeySize)
        {
            throw new ArgumentException("Wrapped Vault Key has an invalid length.");
        }
    }
}
