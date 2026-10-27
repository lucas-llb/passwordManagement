using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using PasswordManager.Core.Crypto;
using PasswordManager.Core.Vault;

namespace PasswordManager.Core.Tests.TestVectors;

/// <summary>
/// Locates and reads the shared test vectors committed in <c>spec/test-vectors/</c> (one <c>&lt;name&gt;.pmvault</c>
/// plus one <c>&lt;name&gt;.json</c> manifest each). The files are read in place, never copied to the output folder.
/// </summary>
internal static class TestVectorFiles
{
    private const string VectorsFolder = "spec/test-vectors";
    private const string MarkerFile = "README.md";

    private static readonly Lazy<string> LazyDirectory = new(Locate);

    /// <summary>
    /// Options of the manifest JSON: camelCase, indented with LF, optional fields omitted when null. Non-ASCII text
    /// is written as is (UTF-8, no BOM), except what the encoder always escapes.
    /// </summary>
    internal static readonly JsonSerializerOptions ManifestOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        NewLine = "\n",
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        RespectNullableAnnotations = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Full path of <c>spec/test-vectors/</c>, found by walking up from the test binaries.</summary>
    /// <exception cref="InvalidOperationException">No ancestor folder contains <c>spec/test-vectors/README.md</c>.</exception>
    internal static string Directory => LazyDirectory.Value;

    /// <summary>The base names of the committed manifests (<c>*.json</c>), in ordinal order.</summary>
    internal static IReadOnlyList<string> Names() =>
    [
        .. System.IO.Directory.EnumerateFiles(Directory, "*.json")
            .Select(path => Path.GetFileNameWithoutExtension(path))
            .Order(StringComparer.Ordinal),
    ];

    /// <summary>Full path of a file inside <c>spec/test-vectors/</c>.</summary>
    internal static string PathOf(string fileName) => Path.Combine(Directory, fileName);

    /// <summary>The bytes of <c>&lt;name&gt;.pmvault</c>.</summary>
    internal static byte[] ReadFile(string name) => File.ReadAllBytes(PathOf(name + VaultFormat.FileExtension));

    /// <summary>The parsed <c>&lt;name&gt;.json</c> manifest.</summary>
    /// <exception cref="JsonException">The manifest is not valid or misses a required field.</exception>
    internal static TestVectorManifest ReadManifest(string name) =>
        JsonSerializer.Deserialize<TestVectorManifest>(File.ReadAllBytes(PathOf(name + ".json")), ManifestOptions)
        ?? throw new JsonException($"Manifest {name}.json is null.");

    /// <summary>
    /// Decrypts the body of a vault file and parses it as plain JSON, without the Core model, so unknown fields and the
    /// exact strings stay visible.
    /// </summary>
    /// <param name="file">The whole file bytes.</param>
    /// <param name="vaultKey">The Vault Key of the file.</param>
    internal static JsonNode DecryptBodyJson(ReadOnlySpan<byte> file, ReadOnlySpan<byte> vaultKey)
    {
        var bodyOffset = VaultHeader.Parse(file).BodyOffset;
        var aad = file[..(bodyOffset + VaultFormat.NonceSize)];
        var nonce = file.Slice(bodyOffset, VaultFormat.NonceSize);
        var ciphertext = file[(bodyOffset + VaultFormat.NonceSize)..];
        var plaintext = new byte[ciphertext.Length - VaultFormat.TagSize];
        try
        {
            VaultBodyCipher.Decrypt(vaultKey, ciphertext, aad, nonce, plaintext);
            return JsonNode.Parse(plaintext) ?? throw new JsonException("Vault body is JSON null.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static string Locate()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            var candidate = Path.Combine(folder.FullName, VectorsFolder);
            if (File.Exists(Path.Combine(candidate, MarkerFile)))
            {
                return Path.GetFullPath(candidate);
            }
        }

        throw new InvalidOperationException(
            $"Could not find {VectorsFolder}/{MarkerFile} in any folder above {AppContext.BaseDirectory}.");
    }
}

/// <summary>
/// The <c>&lt;name&gt;.json</c> manifest of a test vector (schema in <c>spec/test-vectors/README.md</c>). Hex is
/// lowercase; optional fields are omitted when they do not apply.
/// </summary>
internal sealed record TestVectorManifest
{
    /// <summary>Short pt-BR description.</summary>
    public required string Description { get; init; }

    /// <summary>The vault file name, <c>&lt;name&gt;.pmvault</c>.</summary>
    public required string File { get; init; }

    /// <summary>The format version in the file preamble.</summary>
    public required int FormatVersion { get; init; }

    /// <summary>The secret of the type-1 slot: the master password, or the export password.</summary>
    public required string Password { get; init; }

    /// <summary>The 32-byte Vault Key the type-1 slot unwraps to.</summary>
    public required string VaultKeyHex { get; init; }

    /// <summary>The Recovery Kit text; present only when the file has a type-2 slot.</summary>
    public string? RecoveryKit { get; init; }

    /// <summary>The 20 bytes <see cref="RecoveryKit"/> decodes to; present only with it.</summary>
    public string? RecoveryKeyHex { get; init; }

    /// <summary>The vector this export was made from; present only on exports.</summary>
    public string? SourceVector { get; init; }

    /// <summary>The slot records, in file order.</summary>
    public required IReadOnlyList<TestVectorSlot> Slots { get; init; }

    /// <summary>The body offset <c>B</c>.</summary>
    public required int BodyOffset { get; init; }

    /// <summary>The decrypted body JSON object, <c>{"entries":[…]}</c>.</summary>
    public required JsonObject Content { get; init; }
}

/// <summary>One slot record of a test vector manifest.</summary>
internal sealed record TestVectorSlot(int Type, uint KdfMemoryKiB, uint KdfIterations, int KdfParallelism);
