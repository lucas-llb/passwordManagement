using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using NSec.Cryptography;
using PasswordManager.Core.Vault;

namespace PasswordManager.Core.Crypto;

/// <summary>
/// Derives the 32-byte key-encryption key (KEK) of a key slot with Argon2id (libsodium via NSec).
/// Parallelism is fixed at 1 by the vault format v1 (docs/vault-format.md).
/// </summary>
/// <remarks>
/// Ownership: the caller's input buffers are never mutated. The caller owns and must zero its own
/// password/secret buffer and the <c>kek</c> buffer once it no longer needs them. Every intermediate
/// copy made here is pinned and zeroed before returning.
/// </remarks>
public static class Argon2idKdf
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// Derives the KEK from a master password. The Argon2id input is <c>UTF-8(NFC(password))</c>,
    /// with no trimming and no terminator.
    /// </summary>
    /// <param name="password">The master password. Not modified; the caller zeros it.</param>
    /// <param name="parameters">Argon2id cost parameters read from the key slot.</param>
    /// <param name="salt">The slot salt, exactly <see cref="VaultFormat.SaltSize"/> bytes.</param>
    /// <param name="kek">Receives the KEK; exactly <see cref="VaultFormat.KeySize"/> bytes. The caller zeros it.
    /// On failure during derivation it is zeroed before the exception propagates.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="parameters"/> is outside the format bounds.</exception>
    /// <exception cref="ArgumentException">The salt or KEK length is wrong, or the password contains an invalid surrogate.</exception>
    public static void DeriveKek(ReadOnlySpan<char> password, KdfParameters parameters, ReadOnlySpan<byte> salt, Span<byte> kek)
    {
        ValidateArguments(parameters, salt, kek);

        // Throws ArgumentException on a lone or invalid surrogate (never replaced with U+FFFD).
        var isNormalized = password.IsNormalized(NormalizationForm.FormC);

        char[]? normalized = null;
        byte[]? encoded = null;
        try
        {
            var source = password;
            if (!isNormalized)
            {
                normalized = GC.AllocateArray<char>(password.GetNormalizedLength(NormalizationForm.FormC), pinned: true);
                if (!password.TryNormalize(normalized, out var charsWritten, NormalizationForm.FormC))
                {
                    throw new ArgumentException("Password could not be normalized.", nameof(password));
                }

                source = normalized.AsSpan(0, charsWritten);
            }

            encoded = GC.AllocateArray<byte>(StrictUtf8.GetByteCount(source), pinned: true);
            var bytesWritten = StrictUtf8.GetBytes(source, encoded);

            Derive(encoded.AsSpan(0, bytesWritten), parameters, salt, kek);
        }
        finally
        {
            if (normalized is not null)
            {
                CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(normalized.AsSpan()));
            }

            if (encoded is not null)
            {
                CryptographicOperations.ZeroMemory(encoded);
            }
        }
    }

    /// <summary>
    /// Derives the KEK from raw secret bytes (for example the recovery-kit key), with no normalization.
    /// </summary>
    /// <param name="secret">The secret bytes. Not modified; the caller zeros it.</param>
    /// <param name="parameters">Argon2id cost parameters read from the key slot.</param>
    /// <param name="salt">The slot salt, exactly <see cref="VaultFormat.SaltSize"/> bytes.</param>
    /// <param name="kek">Receives the KEK; exactly <see cref="VaultFormat.KeySize"/> bytes. The caller zeros it.
    /// On failure during derivation it is zeroed before the exception propagates.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="parameters"/> is outside the format bounds.</exception>
    /// <exception cref="ArgumentException">The salt or KEK length is wrong.</exception>
    public static void DeriveKek(ReadOnlySpan<byte> secret, KdfParameters parameters, ReadOnlySpan<byte> salt, Span<byte> kek)
    {
        ValidateArguments(parameters, salt, kek);
        Derive(secret, parameters, salt, kek);
    }

    private static void ValidateArguments(KdfParameters parameters, ReadOnlySpan<byte> salt, Span<byte> kek)
    {
        if (!parameters.IsInRange)
        {
            throw new ArgumentOutOfRangeException(nameof(parameters), "KDF parameters are outside the format bounds.");
        }

        if (salt.Length != VaultFormat.SaltSize)
        {
            throw new ArgumentException("Salt has an invalid length.", nameof(salt));
        }

        if (kek.Length != VaultFormat.KeySize)
        {
            throw new ArgumentException("KEK buffer has an invalid length.", nameof(kek));
        }
    }

    private static void Derive(ReadOnlySpan<byte> input, KdfParameters parameters, ReadOnlySpan<byte> salt, Span<byte> kek)
    {
        var argon2Parameters = new Argon2Parameters
        {
            MemorySize = parameters.MemoryKiB,
            NumberOfPasses = parameters.Iterations,
            DegreeOfParallelism = VaultFormat.DefaultKdfParallelism,
        };

        try
        {
            PasswordBasedKeyDerivationAlgorithm.Argon2id(in argon2Parameters).DeriveBytes(input, salt, kek);
        }
        catch
        {
            CryptographicOperations.ZeroMemory(kek);
            throw;
        }
    }
}
