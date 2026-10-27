using System.Security.Cryptography;

namespace PasswordManager.Core.Vault;

/// <summary>
/// Text form of the 20-byte Recovery Kit key: Crockford base32 (alphabet <see cref="VaultFormat.RecoveryKeyAlphabet"/>,
/// bits read MSB-first as in RFC 4648, no padding) shown as 8 groups of 4 symbols joined by
/// <see cref="VaultFormat.RecoveryKeySeparator"/> (docs/vault-format.md, "Kit de Recuperação").
/// </summary>
/// <remarks>
/// This is encoding, not crypto. It works on caller-owned spans only: the kit text and the key never become a managed
/// string, and decoding is ordinal ASCII arithmetic so the result does not depend on the current culture. 160 bits are
/// exactly 32 symbols, so every 32-symbol text decodes to exactly one key.
/// </remarks>
internal static class RecoveryKitText
{
    /// <summary>Length of the canonical text: 32 symbols plus 7 separators.</summary>
    internal const int Length =
        (VaultFormat.RecoveryKeyGroupCount * VaultFormat.RecoveryKeyGroupLength) + VaultFormat.RecoveryKeyGroupCount - 1;

    private const int BitsPerSymbol = 5;
    private const int SymbolMask = (1 << BitsPerSymbol) - 1;
    private const int BytesPerBlock = 5;
    private const int SymbolsPerBlock = 8;
    private const int SymbolCount = VaultFormat.RecoveryKeyGroupCount * VaultFormat.RecoveryKeyGroupLength;

    /// <summary>
    /// Writes the canonical text of <paramref name="recoveryKey"/> into <paramref name="text"/>.
    /// </summary>
    /// <param name="recoveryKey">The key, exactly <see cref="VaultFormat.RecoveryKeySize"/> bytes. Not modified.</param>
    /// <param name="text">The destination, exactly <see cref="Length"/> chars. The caller zeros it afterwards.</param>
    /// <exception cref="ArgumentException">A buffer has the wrong length.</exception>
    internal static void Encode(ReadOnlySpan<byte> recoveryKey, Span<char> text)
    {
        if (recoveryKey.Length != VaultFormat.RecoveryKeySize)
        {
            throw new ArgumentException("Recovery key has an invalid length.", nameof(recoveryKey));
        }

        if (text.Length != Length)
        {
            throw new ArgumentException("Recovery kit text buffer has an invalid length.", nameof(text));
        }

        for (var block = 0; block < VaultFormat.RecoveryKeySize / BytesPerBlock; block++)
        {
            var bytes = recoveryKey.Slice(block * BytesPerBlock, BytesPerBlock);
            var value = ((ulong)bytes[0] << 32) | ((ulong)bytes[1] << 24) | ((ulong)bytes[2] << 16) | ((ulong)bytes[3] << 8) | bytes[4];

            for (var i = 0; i < SymbolsPerBlock; i++)
            {
                var symbol = (block * SymbolsPerBlock) + i;
                var index = (int)(value >> (BitsPerSymbol * (SymbolsPerBlock - 1 - i))) & SymbolMask;
                text[symbol + (symbol / VaultFormat.RecoveryKeyGroupLength)] = VaultFormat.RecoveryKeyAlphabet[index];
            }
        }

        for (var group = 0; group < VaultFormat.RecoveryKeyGroupCount - 1; group++)
        {
            text[(group * (VaultFormat.RecoveryKeyGroupLength + 1)) + VaultFormat.RecoveryKeyGroupLength] =
                VaultFormat.RecoveryKeySeparator;
        }
    }

    /// <summary>
    /// Decodes a kit text typed or pasted by the user into the 20-byte key. Hyphens and spaces are ignored, letters are
    /// case-insensitive, and <c>O</c>, <c>I</c>, <c>L</c> are read as <c>0</c>, <c>1</c>, <c>1</c>.
    /// </summary>
    /// <param name="text">The kit text. Not modified.</param>
    /// <param name="recoveryKey">The destination, exactly <see cref="VaultFormat.RecoveryKeySize"/> bytes. Zeroed when
    /// this returns <see langword="false"/>.</param>
    /// <returns><see langword="true"/> when the text holds exactly 32 valid symbols.</returns>
    /// <exception cref="ArgumentException"><paramref name="recoveryKey"/> has the wrong length.</exception>
    internal static bool TryDecode(ReadOnlySpan<char> text, Span<byte> recoveryKey)
    {
        if (recoveryKey.Length != VaultFormat.RecoveryKeySize)
        {
            throw new ArgumentException("Recovery key buffer has an invalid length.", nameof(recoveryKey));
        }

        if (DecodeCore(text, recoveryKey))
        {
            return true;
        }

        CryptographicOperations.ZeroMemory(recoveryKey);
        return false;
    }

    private static bool DecodeCore(ReadOnlySpan<char> text, Span<byte> recoveryKey)
    {
        var count = 0;
        ulong block = 0;

        foreach (var raw in text)
        {
            if (raw is VaultFormat.RecoveryKeySeparator or ' ')
            {
                continue;
            }

            // ASCII-only folding: culture-aware upper-casing is wrong here (in tr-TR, 'i' becomes U+0130).
            var c = raw is >= 'a' and <= 'z' ? (char)(raw - ('a' - 'A')) : raw;
            c = c switch
            {
                'O' => '0',
                'I' or 'L' => '1',
                _ => c,
            };

            var value = VaultFormat.RecoveryKeyAlphabet.IndexOf(c);
            if (value < 0 || count == SymbolCount)
            {
                return false;
            }

            block = (block << BitsPerSymbol) | (uint)value;
            count++;

            if (count % SymbolsPerBlock == 0)
            {
                var bytes = recoveryKey.Slice((count / SymbolsPerBlock - 1) * BytesPerBlock, BytesPerBlock);
                bytes[0] = (byte)(block >> 32);
                bytes[1] = (byte)(block >> 24);
                bytes[2] = (byte)(block >> 16);
                bytes[3] = (byte)(block >> 8);
                bytes[4] = (byte)block;
                block = 0;
            }
        }

        return count == SymbolCount;
    }
}
