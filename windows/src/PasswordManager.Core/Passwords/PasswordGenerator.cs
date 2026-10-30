using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace PasswordManager.Core.Passwords;

/// <summary>
/// Generates a random password from the character sets the user turns on. Lowercase letters are always used.
/// </summary>
/// <remarks>
/// <para>
/// The alphabet is the enabled sets joined in this order: lowercase, uppercase, digits, symbols. Each character is
/// <c>alphabet[index]</c>, where the index comes from <see cref="RandomNumberGenerator.GetInt32(int)"/>, a CSPRNG with no
/// modulo bias. There is no <c>Random</c>, no <c>%</c> on random bytes and no shuffle (AGENTS §3.2).
/// </para>
/// <para>
/// Every enabled set must appear at least once. A draw that misses one is zeroed and the whole password is drawn again
/// (rejection sampling), so the result is uniform over all valid passwords. After <c>1,000</c> rejected draws the call
/// throws <see cref="InvalidOperationException"/>; the CSPRNG never gets there, only a broken seam does.
/// </para>
/// <para>
/// The result is written straight into a <c>char[]</c>, so the password never becomes a managed string here. The caller
/// owns the array and zeroes it after use. Nothing secret goes into an exception message.
/// </para>
/// </remarks>
public static class PasswordGenerator
{
    /// <summary>Length of a generated password when the caller does not choose one.</summary>
    public const int DefaultLength = 20;

    /// <summary>Shortest password accepted by <see cref="Generate(int, bool, bool, bool)"/>.</summary>
    public const int MinLength = 8;

    /// <summary>Longest password accepted by <see cref="Generate(int, bool, bool, bool)"/>.</summary>
    public const int MaxLength = 128;

    /// <summary>Lowercase letters, always part of the alphabet.</summary>
    public const string LowercaseLetters = "abcdefghijklmnopqrstuvwxyz";

    /// <summary>Uppercase letters.</summary>
    public const string UppercaseLetters = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";

    /// <summary>Decimal digits.</summary>
    public const string DigitCharacters = "0123456789";

    /// <summary>
    /// The 28 symbols: printable ASCII without space, <c>"</c>, <c>'</c>, <c>`</c> and <c>\</c>, so nothing needs quoting
    /// in JSON, CSV or a shell.
    /// </summary>
    public const string SymbolCharacters = "!#$%&()*+,-./:;<=>?@[]^_{|}~";

    // Rejected draws before Generate gives up.
    private const int MaxAttempts = 1000;

    private const int LowercaseClass = 1;
    private const int UppercaseClass = 2;
    private const int DigitClass = 4;
    private const int SymbolClass = 8;

    /// <summary>
    /// Generates a password of <paramref name="length"/> characters with at least one character of every enabled set.
    /// </summary>
    /// <param name="length">Number of characters, from <see cref="MinLength"/> to <see cref="MaxLength"/>.</param>
    /// <param name="uppercase">Include <see cref="UppercaseLetters"/>.</param>
    /// <param name="digits">Include <see cref="DigitCharacters"/>.</param>
    /// <param name="symbols">Include <see cref="SymbolCharacters"/>.</param>
    /// <returns>The password in a new array of the exact length. The caller zeroes it with
    /// <see cref="System.Security.Cryptography.CryptographicOperations.ZeroMemory(Span{byte})"/> after use.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="length"/> is outside the allowed range.</exception>
    public static char[] Generate(
        int length = DefaultLength,
        bool uppercase = true,
        bool digits = true,
        bool symbols = true) =>
        Generate(length, uppercase, digits, symbols, static exclusiveMax => RandomNumberGenerator.GetInt32(exclusiveMax));

    /// <summary>
    /// Builds a password from the indices that <paramref name="pickIndex"/> returns. This is the seam the tests use; the
    /// public overload passes the CSPRNG.
    /// </summary>
    internal static char[] Generate(int length, bool uppercase, bool digits, bool symbols, Func<int, int> pickIndex)
    {
        if (length < MinLength || length > MaxLength)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        ArgumentNullException.ThrowIfNull(pickIndex);

        // The alphabet is public data, so it needs no zeroing.
        int capacity =
            LowercaseLetters.Length + UppercaseLetters.Length + DigitCharacters.Length + SymbolCharacters.Length;
        Span<char> buffer = stackalloc char[capacity];
        int size = 0;
        int required = LowercaseClass;
        size += Append(buffer, size, LowercaseLetters);
        if (uppercase)
        {
            size += Append(buffer, size, UppercaseLetters);
            required |= UppercaseClass;
        }

        if (digits)
        {
            size += Append(buffer, size, DigitCharacters);
            required |= DigitClass;
        }

        if (symbols)
        {
            size += Append(buffer, size, SymbolCharacters);
            required |= SymbolClass;
        }

        ReadOnlySpan<char> alphabet = buffer[..size];
        char[] password = new char[length];
        bool accepted = false;
        try
        {
            for (int attempt = 0; attempt < MaxAttempts; attempt++)
            {
                int seen = 0;
                for (int i = 0; i < length; i++)
                {
                    char c = alphabet[pickIndex(alphabet.Length)];
                    password[i] = c;
                    seen |= ClassOf(c);
                }

                if (seen == required)
                {
                    accepted = true;
                    return password;
                }

                Zero(password);
            }

            throw new InvalidOperationException("No password with every enabled character class was drawn.");
        }
        finally
        {
            if (!accepted)
            {
                Zero(password);
            }
        }
    }

    private static int Append(Span<char> destination, int offset, string set)
    {
        set.CopyTo(destination[offset..]);
        return set.Length;
    }

    private static int ClassOf(char c) =>
        c is >= 'a' and <= 'z' ? LowercaseClass :
        c is >= 'A' and <= 'Z' ? UppercaseClass :
        c is >= '0' and <= '9' ? DigitClass :
        SymbolClass;

    private static void Zero(char[] password) =>
        CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(password.AsSpan()));
}
