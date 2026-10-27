using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PasswordManager.Core.Vault;

namespace PasswordManager.Core.Tests.TestVectors;

/// <summary>
/// Generates the shared test vectors in <c>spec/test-vectors/</c>. It is an explicit test: normal runs never execute
/// it, and the committed files are the truth. Salts, nonces and Vault Keys come from the CSPRNG, so every run changes
/// every byte. It refuses to overwrite: delete the 8 files first (allowed only before the v1 freeze). Run from
/// <c>windows/</c>:
/// <code>
/// dotnet test --project tests/PasswordManager.Core.Tests/PasswordManager.Core.Tests.csproj --filter-class "PasswordManager.Core.Tests.TestVectors.TestVectorGenerator" --explicit only
/// </code>
/// Every value here is fictitious.
/// </summary>
public class TestVectorGenerator
{
    private const string EmptyPassword = "fictitious empty vault passphrase";

    // NFC; í, ç, ã and ñ decompose under NFD, and 🔐 is outside the BMP.
    private const string UnicodePassword = "Senha fictícia: açaí, maçã e piñata 🔐";

    private const string RecoveryKitPassword = "fictitious recovery kit passphrase";
    private const string ExportPassword = "fictitious export passphrase";

    private static readonly KdfParameters LowCost = new(1024, 2);

    [Fact(Explicit = true)]
    public void Generate()
    {
        string[] names = ["empty", "entries-unicode", "recovery-kit", "export"];
        var existing = names
            .SelectMany(name => new[] { name + VaultFormat.FileExtension, name + ".json" })
            .Where(fileName => File.Exists(TestVectorFiles.PathOf(fileName)))
            .ToList();
        if (existing.Count > 0)
        {
            throw new InvalidOperationException(
                $"Test vector files already exist ({string.Join(", ", existing)}). Delete them first to regenerate.");
        }

        Assert.True(UnicodePassword.IsNormalized(NormalizationForm.FormC));
        Assert.NotEqual(UnicodePassword, UnicodePassword.Normalize(NormalizationForm.FormD));

        var vectors = new List<(string Name, byte[] File, TestVectorManifest Manifest)>
        {
            BuildEmpty(),
            BuildEntriesUnicode(),
        };
        vectors.AddRange(BuildRecoveryKitAndExport());

        Assert.Equal(names, vectors.Select(vector => vector.Name));
        foreach (var (name, file, manifest) in vectors)
        {
            WriteNew(name + VaultFormat.FileExtension, file);
            WriteNew(name + ".json", [.. JsonSerializer.SerializeToUtf8Bytes(manifest, TestVectorFiles.ManifestOptions), (byte)'\n']);
        }
    }

    private static (string, byte[], TestVectorManifest) BuildEmpty()
    {
        var file = SealWithPasswordSlot(EmptyPassword, new VaultContent());
        return ("empty", file, Describe(
            "empty",
            "Cofre vazio, sem entradas, com só o slot de senha (senha ASCII, KDF de teste 1024 KiB/2/1).",
            file,
            EmptyPassword));
    }

    private static (string, byte[], TestVectorManifest) BuildEntriesUnicode()
    {
        var content = new VaultContent
        {
            Entries =
            [
                new VaultEntry
                {
                    Id = Guid.Parse("3f2a8c1d-5b6e-4f70-8a91-b2c3d4e5f601"),
                    Title = "Conta de luz — São João",
                    Username = "joão.conceição@example.com",
                    Password = "Pão-de-açúcar-ÁÉÍÓÚ-ãõ-ç",
                    Url = "https://example.com/serviços/ação",
                    Notes = "Vencimento todo dia 10; é fictício.",
                    Folder = "Finanças",
                    Favorite = true,
                    CreatedAt = Utc(2026, 1, 15, 8, 30, 15, 123),
                    UpdatedAt = Utc(2026, 3, 2, 19, 45, 0, 987),
                },
                new VaultEntry
                {
                    Id = Guid.Parse("3f2a8c1d-5b6e-4f70-8a91-b2c3d4e5f602"),
                    // 👨 ZWJ 👩 ZWJ 👧 (family emoji), with the joiners escaped so they stay visible here.
                    Title = "Família \U0001F468‍\U0001F469‍\U0001F467 🏠",
                    Username = "familia@example.com",
                    Password = "senha-\U0001F468‍\U0001F469‍\U0001F467-🔑-fictícia",
                    Url = "https://example.org/família",
                    Notes = "",
                    Folder = "Casa 🏡",
                    Favorite = false,
                    CreatedAt = Utc(2026, 2, 1, 0, 0, 0, 1),
                    UpdatedAt = Utc(2026, 2, 28, 23, 59, 59, 999),
                },
                new VaultEntry
                {
                    Id = Guid.Parse("3f2a8c1d-5b6e-4f70-8a91-b2c3d4e5f603"),
                    Title = "東京アカウント",
                    Username = "用户名",
                    Password = "密码-סיסמה-كلمة",
                    Url = "https://example.jp/ログイン",
                    Notes = "שלום עולם — مرحبا بالعالم — 你好，世界",
                    Folder = "国际",
                    Favorite = true,
                    CreatedAt = Utc(2026, 4, 10, 12, 0, 0, 250),
                    UpdatedAt = Utc(2026, 4, 11, 13, 14, 15, 500),
                },
                new VaultEntry
                {
                    Id = Guid.Parse("3f2a8c1d-5b6e-4f70-8a91-b2c3d4e5f604"),
                    Title = "Nota segura",
                    // "Cafe" + U+0301 is NFD on purpose: body strings are never normalized.
                    Notes = "linha 1\nlinha 2\tcom tab\n\"aspas\" e barra \\ invertida\nCafé em NFD",
                    Favorite = false,
                    CreatedAt = Utc(2026, 5, 5, 5, 5, 5, 5),
                    UpdatedAt = Utc(2026, 6, 6, 6, 6, 6, 66),
                },
            ],
        };

        var file = SealWithPasswordSlot(UnicodePassword, content);
        return ("entries-unicode", file, Describe(
            "entries-unicode",
            "Quatro entradas com acentos, emoji (inclusive sequência ZWJ), CJK, hebraico, árabe, escapes JSON e uma "
            + "string NFD nas notas; senha NFC não ASCII (KDF de teste 1024 KiB/2/1).",
            file,
            UnicodePassword));
    }

    // recovery-kit is the real new-profile path (default KDF); export comes from the same unlocked session.
    private static (string, byte[], TestVectorManifest)[] BuildRecoveryKitAndExport()
    {
        var kitText = new char[RecoveryKit.TextLength];
        var created = VaultSession.Create(RecoveryKitPassword, kitText, KdfParameters.Default);

        var directory = Directory.CreateTempSubdirectory("pm-testvectors-");
        try
        {
            var path = Path.Combine(directory.FullName, $"{Guid.NewGuid()}.pmvault");
            VaultFile.Write(path, created);

            using var session = new VaultSession(path, LowCost);
            session.Unlock(RecoveryKitPassword);
            session.Save(new VaultContent
            {
                Entries =
                [
                    new VaultEntry
                    {
                        Id = Guid.Parse("8e4b2a90-1c3d-4e5f-9a0b-c1d2e3f4a501"),
                        Title = "E-mail pessoal",
                        Username = "pessoa@example.com",
                        Password = "not-a-real-password",
                        Url = "https://mail.example.com",
                        Notes = "Conta fictícia.",
                        Folder = "Pessoal",
                        Favorite = true,
                        CreatedAt = Utc(2026, 7, 1, 9, 0, 0, 0),
                        UpdatedAt = Utc(2026, 7, 2, 10, 30, 0, 450),
                    },
                    new VaultEntry
                    {
                        Id = Guid.Parse("8e4b2a90-1c3d-4e5f-9a0b-c1d2e3f4a502"),
                        Title = "Banco 🏦",
                        Username = "cliente-0001",
                        Password = "fake-🔒-password",
                        Url = "https://bank.example.net",
                        Folder = "Finanças",
                        Favorite = false,
                        CreatedAt = Utc(2026, 7, 3, 14, 15, 16, 170),
                        UpdatedAt = Utc(2026, 7, 3, 14, 15, 16, 170),
                    },
                ],
            });

            var file = File.ReadAllBytes(path);
            var recoveryKey = new byte[VaultFormat.RecoveryKeySize];
            Assert.True(RecoveryKitText.TryDecode(kitText, recoveryKey));

            var recoveryKit = Describe(
                "recovery-kit",
                "Cofre criado pelo caminho real de novo perfil: slot de senha e Kit de Recuperação com o KDF padrão "
                + "(64 MiB/3/1) e duas entradas, uma com emoji.",
                file,
                RecoveryKitPassword) with
            {
                RecoveryKit = new string(kitText),
                RecoveryKeyHex = Convert.ToHexStringLower(recoveryKey),
            };

            var exported = session.Export(ExportPassword);
            var export = Describe(
                "export",
                "Exportação cifrada do vetor recovery-kit: Vault Key nova, só um slot de senha (senha de exportação, "
                + "KDF de teste 1024 KiB/2/1) e o mesmo conteúdo.",
                exported,
                ExportPassword) with
            {
                SourceVector = "recovery-kit",
            };

            return [("recovery-kit", file, recoveryKit), ("export", exported, export)];
        }
        finally
        {
            if (Directory.Exists(directory.FullName))
            {
                directory.Delete(recursive: true);
            }
        }
    }

    private static byte[] SealWithPasswordSlot(string password, VaultContent content)
    {
        var vaultKey = RandomNumberGenerator.GetBytes(VaultFormat.KeySize);
        return VaultBody.Seal(VaultHeader.Build([KeySlot.CreatePasswordSlot(password, LowCost, vaultKey)]), vaultKey, content);
    }

    // Builds the manifest from the final bytes: the key is unwrapped with the password, the content is decrypted.
    private static TestVectorManifest Describe(string name, string description, byte[] file, string password)
    {
        var header = VaultHeader.Parse(file);
        var vaultKey = new byte[VaultFormat.KeySize];
        KeySlot.UnwrapPasswordSlot(file, header.PasswordSlotOffset, password, vaultKey);

        return new TestVectorManifest
        {
            Description = description,
            File = name + VaultFormat.FileExtension,
            FormatVersion = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(VaultFormat.VersionOffset)),
            Password = password,
            VaultKeyHex = Convert.ToHexStringLower(vaultKey),
            Slots =
            [
                .. header.Slots.Select(slot =>
                {
                    var record = file.AsSpan(slot.Offset, slot.Length);
                    var parameters = KdfParameters.ReadFromSlot(record);
                    return new TestVectorSlot(
                        slot.Type,
                        parameters.MemoryKiB,
                        parameters.Iterations,
                        record[VaultFormat.SlotKdfParallelismOffset]);
                }),
            ],
            BodyOffset = header.BodyOffset,
            Content = TestVectorFiles.DecryptBodyJson(file, vaultKey).AsObject(),
        };
    }

    private static void WriteNew(string fileName, byte[] bytes)
    {
        using var stream = new FileStream(TestVectorFiles.PathOf(fileName), FileMode.CreateNew, FileAccess.Write);
        stream.Write(bytes);
    }

    private static DateTimeOffset Utc(int year, int month, int day, int hour, int minute, int second, int millisecond) =>
        new(year, month, day, hour, minute, second, millisecond, TimeSpan.Zero);
}
