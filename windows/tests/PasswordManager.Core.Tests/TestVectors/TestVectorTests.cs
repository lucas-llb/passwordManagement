using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PasswordManager.Core.Vault;

namespace PasswordManager.Core.Tests.TestVectors;

/// <summary>
/// Opens every vector committed in <c>spec/test-vectors/</c> and checks it against its manifest. The theories list the
/// folder, so a vector added later (for example, by the Android project) is tested without changing this class.
/// Nothing is opened in place: each test works on a copy in a temp folder.
/// </summary>
public class TestVectorTests
{
    private static readonly string[] RequiredVectorNames = ["empty", "entries-unicode", "recovery-kit", "export"];

    // Valid symbols, never a real kit: used only where the kit text must parse but nothing may unwrap.
    private const string FictitiousKitText = "0000-0000-0000-0000-0000-0000-0000-0000";

    public static TheoryData<string> VectorNames() => [.. TestVectorFiles.Names()];

    // --- Folder ---

    [Fact]
    public void RequiredVectors_ArePresentAndPaired()
    {
        var names = TestVectorFiles.Names();
        foreach (var required in RequiredVectorNames)
        {
            Assert.Contains(required, names);
        }

        var vaultNames = System.IO.Directory.EnumerateFiles(TestVectorFiles.Directory, "*" + VaultFormat.FileExtension)
            .Select(path => Path.GetFileNameWithoutExtension(path))
            .Order(StringComparer.Ordinal);
        Assert.Equal(names, vaultNames);

        foreach (var name in names)
        {
            var manifest = TestVectorFiles.ReadManifest(name);
            Assert.Equal(name + VaultFormat.FileExtension, manifest.File);
            Assert.True(File.Exists(TestVectorFiles.PathOf(manifest.File)), $"{name}: {manifest.File} is missing.");
        }

        var unicode = TestVectorFiles.ReadManifest("entries-unicode");
        Assert.Contains(unicode.Password, c => c > 0x7F);
        Assert.True(HasSurrogatePair(unicode.Password), "The entries-unicode password has no non-BMP character.");

        var strings = EnumerateStrings(unicode.Content).ToList();
        Assert.Contains(strings, HasSurrogatePair);
        Assert.Contains(strings, text => !text.IsNormalized(NormalizationForm.FormC));
    }

    // --- Header ---

    [Theory]
    [MemberData(nameof(VectorNames))]
    public void Header_MatchesManifest(string name)
    {
        var file = TestVectorFiles.ReadFile(name);
        var manifest = TestVectorFiles.ReadManifest(name);

        var header = VaultHeader.Parse(file);

        Assert.True(file.AsSpan(0, VaultFormat.MagicSize).SequenceEqual(VaultFormat.Magic));
        Assert.Equal(manifest.FormatVersion, BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(VaultFormat.VersionOffset)));
        Assert.Equal(manifest.Slots.Count, file[VaultFormat.SlotCountOffset]);
        Assert.Equal(manifest.Slots.Count, header.Slots.Count);

        for (var i = 0; i < manifest.Slots.Count; i++)
        {
            var expected = manifest.Slots[i];
            var range = header.Slots[i];
            var record = file.AsSpan(range.Offset, range.Length);
            var parameters = KdfParameters.ReadFromSlot(record);

            Assert.Equal(expected.Type, range.Type);
            Assert.Equal(expected.KdfMemoryKiB, parameters.MemoryKiB);
            Assert.Equal(expected.KdfIterations, parameters.Iterations);
            Assert.Equal(expected.KdfParallelism, record[VaultFormat.SlotKdfParallelismOffset]);
        }

        // B = 7 + 101 * n for n slots of types 1 and 2: 108 with one slot, 209 with two.
        Assert.Equal(manifest.BodyOffset, header.BodyOffset);
        Assert.Equal(7 + (101 * manifest.Slots.Count), header.BodyOffset);
    }

    // --- Unlock ---

    [Theory]
    [MemberData(nameof(VectorNames))]
    public void Unlock_WithPassword_MatchesManifest(string name)
    {
        var file = TestVectorFiles.ReadFile(name);
        var manifest = TestVectorFiles.ReadManifest(name);

        WithVectorCopy(name, path =>
        {
            using var session = new VaultSession(path);
            session.Unlock(manifest.Password);

            Assert.True(session.IsUnlocked);
            Assert.Equal(manifest.VaultKeyHex, Convert.ToHexStringLower(session.VaultKeyBufferForTesting));
            Assert.Equal(ManifestEntries(manifest), session.Content.Entries);
            Assert.True(JsonNode.DeepEquals(
                TestVectorFiles.DecryptBodyJson(file, session.VaultKeyBufferForTesting),
                manifest.Content));

            session.Lock();

            Assert.False(session.IsUnlocked);
            Assert.Equal(file, File.ReadAllBytes(path));
            Assert.Equal([path], System.IO.Directory.GetFileSystemEntries(Path.GetDirectoryName(path)!));
        });
    }

    [Theory]
    [MemberData(nameof(VectorNames))]
    public void Unlock_WithWrongPassword_Fails(string name)
    {
        var manifest = TestVectorFiles.ReadManifest(name);

        WithVectorCopy(name, path =>
        {
            using var session = new VaultSession(path);

            AssertAuthenticationFailure(() => session.Unlock(manifest.Password + " (wrong)"));

            Assert.False(session.IsUnlocked);
            Assert.All(session.VaultKeyBufferForTesting, b => Assert.Equal(0, b));
        });
    }

    [Fact]
    public void Unlock_EntriesUnicode_AcceptsNfdPassword()
    {
        var manifest = TestVectorFiles.ReadManifest("entries-unicode");
        var nfd = manifest.Password.Normalize(NormalizationForm.FormD);

        // The manifest stores the NFC form, and the NFD form is a different string.
        Assert.True(manifest.Password.IsNormalized(NormalizationForm.FormC));
        Assert.NotEqual(manifest.Password, nfd);

        WithVectorCopy("entries-unicode", path =>
        {
            using var session = new VaultSession(path);
            session.Unlock(nfd);

            Assert.True(session.IsUnlocked);
            Assert.Equal(manifest.VaultKeyHex, Convert.ToHexStringLower(session.VaultKeyBufferForTesting));
            Assert.Equal(ManifestEntries(manifest), session.Content.Entries);
        });
    }

    // --- Recovery Kit ---

    [Theory]
    [MemberData(nameof(VectorNames))]
    public void RecoveryKit_MatchesManifest(string name)
    {
        var file = TestVectorFiles.ReadFile(name);
        var manifest = TestVectorFiles.ReadManifest(name);
        var header = VaultHeader.Parse(file);
        var vaultKey = new byte[VaultFormat.KeySize];

        var hasKitSlot = manifest.Slots.Any(slot => slot.Type == VaultFormat.SlotTypeRecoveryKit);
        Assert.Equal(hasKitSlot, header.RecoveryKitSlotOffset is not null);
        Assert.Equal(hasKitSlot, manifest.RecoveryKit is not null);
        Assert.Equal(hasKitSlot, manifest.RecoveryKeyHex is not null);

        if (manifest.RecoveryKit is not { } kitText)
        {
            // Rejected by the slot check, before any Argon2id run.
            Assert.Throws<InvalidOperationException>(() => RecoveryKit.Unlock(file, FictitiousKitText, vaultKey));
            Assert.All(vaultKey, b => Assert.Equal(0, b));
            return;
        }

        var recoveryKey = new byte[VaultFormat.RecoveryKeySize];
        Assert.True(RecoveryKitText.TryDecode(kitText, recoveryKey));
        Assert.Equal(manifest.RecoveryKeyHex, Convert.ToHexStringLower(recoveryKey));

        RecoveryKit.Unlock(file, kitText, vaultKey);

        Assert.Equal(manifest.VaultKeyHex, Convert.ToHexStringLower(vaultKey));
        Assert.Equal(ManifestEntries(manifest), VaultBody.Open(file, header.BodyOffset, vaultKey).Entries);
        Assert.True(JsonNode.DeepEquals(TestVectorFiles.DecryptBodyJson(file, vaultKey), manifest.Content));
    }

    // --- Export ---

    [Fact]
    public void Export_HasNewKeyOneSlotAndSourceContent()
    {
        var exportFile = TestVectorFiles.ReadFile("export");
        var export = TestVectorFiles.ReadManifest("export");
        var sourceName = export.SourceVector;
        Assert.NotNull(sourceName);
        var source = TestVectorFiles.ReadManifest(sourceName);

        var slot = Assert.Single(VaultHeader.Parse(exportFile).Slots);
        Assert.Equal(VaultFormat.SlotTypePassword, slot.Type);
        Assert.Equal(VaultFormat.SlotTypePassword, Assert.Single(export.Slots).Type);
        Assert.Null(export.RecoveryKit);

        Assert.NotEqual(source.VaultKeyHex, export.VaultKeyHex);
        Assert.NotEqual(source.Password, export.Password);
        Assert.True(JsonNode.DeepEquals(source.Content, export.Content));

        // The vault the export is imported into is another one: the empty vector.
        var empty = TestVectorFiles.ReadManifest("empty");
        WithVectorCopy("empty", path =>
        {
            using var session = new VaultSession(path);
            session.Unlock(empty.Password);

            var imported = session.Import(exportFile, export.Password);

            Assert.Equal(ManifestEntries(source), imported.Entries);
            Assert.NotEmpty(imported.Entries);
            Assert.Empty(session.Content.Entries);
        });
    }

    // --- Helpers ---

    // Copies the vector to a fresh temp folder, runs the test with the copy's path and deletes the folder.
    private static void WithVectorCopy(string name, Action<string> test)
    {
        var directory = Directory.CreateTempSubdirectory("pm-vectors-");
        try
        {
            var path = Path.Combine(directory.FullName, name + VaultFormat.FileExtension);
            File.Copy(TestVectorFiles.PathOf(name + VaultFormat.FileExtension), path);
            test(path);
        }
        finally
        {
            if (Directory.Exists(directory.FullName))
            {
                directory.Delete(recursive: true);
            }
        }
    }

    private static IReadOnlyList<VaultEntry> ManifestEntries(TestVectorManifest manifest) =>
        manifest.Content.Deserialize(VaultJsonContext.Default.VaultContent)?.Entries
        ?? throw new JsonException($"Manifest {manifest.File} has no content.");

    private static IEnumerable<string> EnumerateStrings(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject jsonObject:
                foreach (var property in jsonObject)
                {
                    foreach (var text in EnumerateStrings(property.Value))
                    {
                        yield return text;
                    }
                }

                break;
            case JsonArray jsonArray:
                foreach (var item in jsonArray)
                {
                    foreach (var text in EnumerateStrings(item))
                    {
                        yield return text;
                    }
                }

                break;
            case JsonValue value when value.GetValueKind() == JsonValueKind.String:
                yield return value.GetValue<string>();
                break;
        }
    }

    private static bool HasSurrogatePair(string text)
    {
        for (var i = 0; i + 1 < text.Length; i++)
        {
            if (char.IsSurrogatePair(text[i], text[i + 1]))
            {
                return true;
            }
        }

        return false;
    }

    private static void AssertAuthenticationFailure(Action action)
    {
        var exception = Assert.Throws<VaultAuthenticationException>(action);

        Assert.Equal("Vault authentication failed.", exception.Message);
        Assert.Null(exception.InnerException);
    }
}
