using PasswordManager.Core.Vault;

namespace PasswordManager.Core.Tests.Vault;

public class VaultHeaderTests
{
    private static readonly byte[] Bn = [.. Enumerable.Range(0x60, 24).Select(i => (byte)i)];

    // W-03 test files: F1 = preamble | slotCount 1 | Slot1 (108 bytes); F2 = preamble | slotCount 2 | Slot1 | Slot2 (209 bytes).
    private static readonly byte[] Aad1 = Convert.FromHexString("504d5646010001620001000400000200000001000102030405060708090a0b0c0d0e0f");
    private static readonly byte[] Aad2 = Convert.FromHexString("504d5646010002620001000400000200000001000102030405060708090a0b0c0d0e0f");
    private static readonly byte[] WrapNonce = [.. Enumerable.Range(0x40, 24).Select(i => (byte)i)];
    private static readonly byte[] W1 = Convert.FromHexString("2e8a17f179bdf5a651b7b489c0ce899222020515738dd5df49db643b04d135cc002785a15a71b4953a359d825d34a848");
    private static readonly byte[] W2 = Convert.FromHexString("fa2d2e816d758690c33354f04cee974acc4fd17d6fcd7f7e914492971f8c52256ac12581922863d5d3e2b9e93c85a1f6");
    private static readonly byte[] Preamble = Convert.FromHexString("504d56460100");
    private static readonly byte[] Slot1 = [.. Aad1.AsSpan(6), .. WrapNonce, .. W1];
    private static readonly byte[] Slot2 = [.. Aad2.AsSpan(6), .. WrapNonce, .. W2];
    private static readonly byte[] F1 = [.. Preamble, 0x01, .. Slot1];
    private static readonly byte[] F2 = [.. Preamble, 0x02, .. Slot1, .. Slot2];

    private static readonly byte[] C0 = Convert.FromHexString("93052548f103f2a4d923af544fdcf72089107a0fb053d81aed759ce00dcb");
    private static readonly byte[] C2 = Convert.FromHexString("93052548f103f2a4d923af544fdc6c3aa10b7fa64c287cbe96a51fa2167a");

    // V0 = F1 || BN || C0 (162 bytes); V2 = F2 || BN || C2 (263 bytes).
    private static readonly byte[] V0 = [.. F1, .. Bn, .. C0];
    private static readonly byte[] V2 = [.. F2, .. Bn, .. C2];

    private static readonly byte[] Z40 = new byte[40];

    private const string Truncated = "Vault file is truncated.";
    private const string InvalidMagic = "Vault file has an invalid magic.";
    private const string UnsupportedVersion = "Vault format version is not supported.";
    private const string InvalidSlotCount = "Vault file has an invalid slot count.";
    private const string SlotTruncated = "Key slot is truncated.";
    private const string InvalidSlotType = "Key slot has an invalid type.";
    private const string DuplicatedSlotType = "Key slot type is duplicated.";
    private const string NoPasswordSlot = "Vault file has no password slot.";
    private const string BodyTruncated = "Vault body is truncated.";

    private const int Slot1Offset = 7;
    private const int Slot2Offset = 108;

    // [type, n LE16, n x 0xEE]
    private static byte[] Unknown(byte type, int n) =>
        [type, (byte)n, (byte)(n >> 8), .. Enumerable.Repeat((byte)0xEE, n)];

    private static byte[] Header(byte slotCount, params byte[][] records) =>
        [.. Preamble, slotCount, .. records.SelectMany(record => record)];

    private static byte[] With(byte[] source, int index, params byte[] bytes)
    {
        var copy = (byte[])source.Clone();
        bytes.CopyTo(copy, index);
        return copy;
    }

    private static void AssertFormatError(byte[] file, string message)
    {
        var exception = Assert.Throws<VaultFormatException>(() => VaultHeader.Parse(file));

        Assert.Equal(message, exception.Message);
    }

    private static void AssertParsesOrFormatError(byte[] file)
    {
        var exception = Record.Exception(() => VaultHeader.Parse(file));

        Assert.True(exception is null or VaultFormatException, exception?.GetType().FullName);
    }

    private static void AssertBuildArgumentError(byte[][] slots)
    {
        var exception = Assert.Throws<ArgumentException>(() => VaultHeader.Build(slots));

        Assert.Equal("slots", exception.ParamName);
    }

    // Field edits inside a KDF slot (offsets relative to the slot) and the KdfParameters message they produce.
    public static TheoryData<string, int, byte[], string> BadKdfFields() => new()
    {
        { "slotLength 97", 1, [97, 0], "Key slot has an invalid length." },
        { "slotLength 99", 1, [99, 0], "Key slot has an invalid length." },
        { "kdfAlgorithm 2", 3, [2], "Key slot uses an unsupported KDF algorithm." },
        { "parallelism 2", 12, [2], "Key slot uses an unsupported KDF parallelism." },
        { "memory 7", 4, [7, 0, 0, 0], "Key slot KDF parameters are out of range." },
        { "iterations 33", 8, [33, 0, 0, 0], "Key slot KDF parameters are out of range." },
    };

    // --- Layouts ---

    [Fact]
    public void Parse_V0_ReturnsSinglePasswordSlot()
    {
        var header = VaultHeader.Parse(V0);

        Assert.Equal(108, header.BodyOffset);
        Assert.Equal(7, header.PasswordSlotOffset);
        Assert.Null(header.RecoveryKitSlotOffset);
        Assert.Equal([new VaultSlotRange(1, 7, 101)], header.Slots);
    }

    [Fact]
    public void Parse_V2_ReturnsPasswordAndRecoveryKitSlots()
    {
        var header = VaultHeader.Parse(V2);

        Assert.Equal(209, header.BodyOffset);
        Assert.Equal(7, header.PasswordSlotOffset);
        Assert.Equal(108, header.RecoveryKitSlotOffset);
        Assert.Equal([new VaultSlotRange(1, 7, 101), new VaultSlotRange(2, 108, 101)], header.Slots);
    }

    [Fact]
    public void Parse_MinimumSizeFile_ReturnsBodyOffset()
    {
        byte[] file = [.. F1, .. Z40];

        Assert.Equal(148, file.Length);
        Assert.Equal(108, VaultHeader.Parse(file).BodyOffset);
    }

    [Fact]
    public void Parse_UnknownSlotTypes_AreSkippedAndRecorded()
    {
        var file = Header(3, Slot1, Unknown(3, 5), Unknown(255, 0), Z40);

        var header = VaultHeader.Parse(file);

        Assert.Equal(119, header.BodyOffset);
        Assert.Equal(7, header.PasswordSlotOffset);
        Assert.Null(header.RecoveryKitSlotOffset);
        Assert.Equal(
            [new VaultSlotRange(1, 7, 101), new VaultSlotRange(3, 108, 8), new VaultSlotRange(255, 116, 3)],
            header.Slots);
    }

    [Fact]
    public void Parse_SlotsOutOfOrder_AreAccepted()
    {
        var file = Header(2, Unknown(3, 5), Slot1, Z40);

        var header = VaultHeader.Parse(file);

        Assert.Equal(15, header.PasswordSlotOffset);
        Assert.Equal(116, header.BodyOffset);
    }

    [Fact]
    public void Parse_Input_IsNotModified()
    {
        var file = (byte[])V2.Clone();

        VaultHeader.Parse(file);

        Assert.Equal(V2, file);
    }

    // --- Step 1: length ---

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(147)]
    public void Parse_ShorterThanMinimum_ThrowsTruncated(int length)
    {
        AssertFormatError(V0[..length], Truncated);
    }

    [Fact]
    public void Parse_ShortFileWithBadMagic_ThrowsTruncatedFirst()
    {
        AssertFormatError(With(V0, 0, 0x00)[..147], Truncated);
    }

    // --- Step 2: magic ---

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Parse_MagicByteFlipped_ThrowsInvalidMagic(int index)
    {
        var file = (byte[])V0.Clone();
        file[index] ^= 0x01;

        AssertFormatError(file, InvalidMagic);
    }

    [Fact]
    public void Parse_BadMagicAndVersion2_ThrowsInvalidMagicFirst()
    {
        AssertFormatError(With(V0, 0, 0x00, 0x4D, 0x56, 0x46, 0x02, 0x00), InvalidMagic);
    }

    // --- Step 3: version ---

    [Theory]
    [InlineData(0x00, 0x00)]
    [InlineData(0x02, 0x00)]
    [InlineData(0x00, 0x01)]
    [InlineData(0xFF, 0xFF)]
    public void Parse_UnsupportedVersion_ThrowsNotSupported(byte low, byte high)
    {
        AssertFormatError(With(V0, 4, low, high), UnsupportedVersion);
    }

    [Fact]
    public void Parse_Version2AndSlotCount0_ThrowsNotSupportedFirst()
    {
        AssertFormatError(With(V0, 4, 0x02, 0x00, 0x00), UnsupportedVersion);
    }

    // --- Step 4: slot count ---

    [Theory]
    [InlineData(0)]
    [InlineData(9)]
    [InlineData(255)]
    public void Parse_SlotCountOutOfRange_ThrowsInvalidSlotCount(byte slotCount)
    {
        AssertFormatError(With(V0, 6, slotCount), InvalidSlotCount);
    }

    // --- Step 5: slot walk ---

    [Fact]
    public void Parse_SlotCountPastLastSlot_ThrowsSlotTruncated()
    {
        // The second slot reads bodyNonce bytes 60 61 62: slotLength 0x6261 is past the end.
        AssertFormatError(With(V0, 6, 0x02), SlotTruncated);
    }

    [Fact]
    public void Parse_FewerThanThreeBytesForSlotHeader_ThrowsSlotTruncated()
    {
        var file = Header(3, Slot1, Unknown(3, 35), [0xAA, 0xBB]);

        Assert.Equal(148, file.Length);
        AssertFormatError(file, SlotTruncated);
    }

    [Fact]
    public void Parse_SlotType0_ThrowsInvalidType()
    {
        AssertFormatError(With(V0, Slot1Offset, 0x00), InvalidSlotType);
    }

    [Theory]
    [MemberData(nameof(BadKdfFields))]
    public void Parse_BadKdfFieldInPasswordSlot_ThrowsKdfParametersMessage(string name, int fieldOffset, byte[] bytes, string message)
    {
        Assert.NotNull(name);
        AssertFormatError(With(V0, Slot1Offset + fieldOffset, bytes), message);
    }

    [Theory]
    [MemberData(nameof(BadKdfFields))]
    public void Parse_BadKdfFieldInRecoveryKitSlot_ThrowsKdfParametersMessage(string name, int fieldOffset, byte[] bytes, string message)
    {
        Assert.NotNull(name);
        AssertFormatError(With(V2, Slot2Offset + fieldOffset, bytes), message);
    }

    [Fact]
    public void Parse_TwoPasswordSlots_ThrowsDuplicated()
    {
        AssertFormatError(With(V2, Slot2Offset, 0x01), DuplicatedSlotType);
    }

    [Fact]
    public void Parse_TwoUnknownSlotsOfSameType_ThrowsDuplicated()
    {
        AssertFormatError(Header(3, Slot1, Unknown(3, 0), Unknown(3, 0), Z40), DuplicatedSlotType);
    }

    [Fact]
    public void Parse_OnlyRecoveryKitSlot_ThrowsNoPasswordSlot()
    {
        AssertFormatError(With(V0, Slot1Offset, 0x02), NoPasswordSlot);
    }

    [Fact]
    public void Parse_OnlyUnknownSlot_ThrowsNoPasswordSlot()
    {
        var file = Header(1, Unknown(3, 98), Z40);

        Assert.Equal(148, file.Length);
        AssertFormatError(file, NoPasswordSlot);
    }

    // --- Step 6: body length ---

    [Fact]
    public void Parse_BodyShorterThanNonceAndTag_ThrowsBodyTruncated()
    {
        AssertFormatError([.. F2, .. new byte[39]], BodyTruncated);
    }

    [Fact]
    public void Parse_BodyOfExactlyNonceAndTag_Parses()
    {
        Assert.Equal(209, VaultHeader.Parse([.. F2, .. Z40]).BodyOffset);
    }

    [Fact]
    public void Parse_OneSlotFileWithShortBody_ThrowsFileTruncated()
    {
        AssertFormatError([.. F1, .. new byte[39]], Truncated);
    }

    // --- Robustness ---

    [Fact]
    public void Parse_EveryPrefixOfV2_ParsesOrThrowsFormatError()
    {
        for (var length = 0; length <= V2.Length; length++)
        {
            AssertParsesOrFormatError(V2[..length]);
        }
    }

    [Theory]
    [InlineData(0x01)]
    [InlineData(0xFF)]
    public void Parse_EveryHeaderByteXored_ParsesOrThrowsFormatError(byte mask)
    {
        for (var index = 0; index < 209; index++)
        {
            var file = (byte[])V2.Clone();
            file[index] ^= mask;

            AssertParsesOrFormatError(file);
        }
    }

    // --- Build ---

    [Fact]
    public void Build_PasswordSlot_ReturnsF1()
    {
        Assert.Equal(F1, VaultHeader.Build([Slot1]));
    }

    [Fact]
    public void Build_TwoSlotsInEitherOrder_ReturnsF2()
    {
        Assert.Equal(F2, VaultHeader.Build([Slot1, Slot2]));
        Assert.Equal(F2, VaultHeader.Build([Slot2, Slot1]));
    }

    [Fact]
    public void Build_UnknownSlotFirst_SortsByType()
    {
        var unknown = Unknown(3, 5);

        Assert.Equal(Header(2, Slot1, unknown), VaultHeader.Build([unknown, Slot1]));
    }

    [Fact]
    public void Build_SlicesFromParsedV2_ReturnsV2Header()
    {
        var slots = VaultHeader.Parse(V2).Slots
            .Select(range => V2[range.Offset..(range.Offset + range.Length)])
            .ToArray();

        Assert.Equal(V2[..209], VaultHeader.Build(slots));
    }

    [Fact]
    public void Build_Inputs_AreNotModified()
    {
        var unknown = Unknown(3, 5);
        var slot1 = (byte[])Slot1.Clone();
        var slot2 = (byte[])Slot2.Clone();
        byte[][] slots = [slot2, unknown, slot1];

        VaultHeader.Build(slots);

        Assert.Same(slot2, slots[0]);
        Assert.Same(unknown, slots[1]);
        Assert.Same(slot1, slots[2]);
        Assert.Equal(Slot1, slot1);
        Assert.Equal(Slot2, slot2);
        Assert.Equal(Unknown(3, 5), unknown);
    }

    [Fact]
    public void Build_NullList_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() => VaultHeader.Build(null!));
    }

    [Fact]
    public void Build_NullElement_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() => VaultHeader.Build([Slot1, null!]));
    }

    [Fact]
    public void Build_EmptyList_ThrowsArgument()
    {
        AssertBuildArgumentError([]);
    }

    [Fact]
    public void Build_NineRecords_ThrowsArgument()
    {
        byte[][] slots = [Slot1, .. Enumerable.Range(3, 8).Select(type => Unknown((byte)type, 0))];

        Assert.Equal(9, slots.Length);
        AssertBuildArgumentError(slots);
    }

    [Fact]
    public void Build_TwoByteRecord_ThrowsArgument()
    {
        AssertBuildArgumentError([[0x01, 0x00]]);
    }

    [Fact]
    public void Build_RecordLongerThanSlotLength_ThrowsArgument()
    {
        AssertBuildArgumentError([[.. Slot1, 0x00]]);
    }

    [Fact]
    public void Build_RecordShorterThanSlotLength_ThrowsArgument()
    {
        AssertBuildArgumentError([Slot1[..^1]]);
    }

    [Fact]
    public void Build_TwoPasswordSlots_ThrowsArgument()
    {
        AssertBuildArgumentError([Slot1, Slot1]);
    }

    [Fact]
    public void Build_OnlyRecoveryKitSlot_ThrowsArgument()
    {
        AssertBuildArgumentError([Slot2]);
    }

    [Fact]
    public void Build_SlotType0_ThrowsArgument()
    {
        AssertBuildArgumentError([[0x00, 0x00, 0x00]]);
    }

    [Fact]
    public void Build_UnsupportedKdfAlgorithm_ThrowsArgumentWrappingFormatError()
    {
        byte[][] slots = [With(Slot1, 3, 0x02)];

        var exception = Assert.Throws<ArgumentException>(() => VaultHeader.Build(slots));

        Assert.Equal("slots", exception.ParamName);
        Assert.StartsWith("Slots do not form a valid v1 header.", exception.Message, StringComparison.Ordinal);
        var inner = Assert.IsType<VaultFormatException>(exception.InnerException);
        Assert.Equal("Key slot uses an unsupported KDF algorithm.", inner.Message);
    }

    // --- WithSlot ---

    // A valid type-1 record with a different salt.
    private static readonly byte[] Slot1b = With(Slot1, 13, 0xFF);

    [Fact]
    public void WithSlot_PasswordSlotInV2_ReplacesSlot1AndKeepsSlot2()
    {
        Assert.Equal(Header(2, Slot1b, Slot2), VaultHeader.Parse(V2).WithSlot(V2, Slot1b));
    }

    [Fact]
    public void WithSlot_PasswordSlotInV0_ReplacesSlot1()
    {
        Assert.Equal(Header(1, Slot1b), VaultHeader.Parse(V0).WithSlot(V0, Slot1b));
    }

    [Fact]
    public void WithSlot_RecoveryKitSlotInV0_AddsSlot()
    {
        Assert.Equal(F2, VaultHeader.Parse(V0).WithSlot(V0, Slot2));
    }

    [Fact]
    public void WithSlot_UnknownSlots_AreKeptByteForByte()
    {
        var file = Header(3, Slot1, Unknown(3, 5), Unknown(255, 0), Z40);

        var header = VaultHeader.Parse(file).WithSlot(file, Slot1b);

        Assert.Equal(Header(3, Slot1b, Unknown(3, 5), Unknown(255, 0)), header);
    }

    [Fact]
    public void WithSlot_SlotsOutOfOrder_AreSortedByType()
    {
        var file = Header(2, Unknown(3, 5), Slot1, Z40);

        var header = VaultHeader.Parse(file).WithSlot(file, Slot1b);

        Assert.Equal(Header(2, Slot1b, Unknown(3, 5)), header);
    }

    [Fact]
    public void WithSlot_NullRecord_ThrowsArgumentNull()
    {
        var header = VaultHeader.Parse(V0);

        Assert.Throws<ArgumentNullException>(() => header.WithSlot(V0, null!));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void WithSlot_RecordShorterThanSlotHeader_ThrowsArgument(int length)
    {
        var header = VaultHeader.Parse(V0);
        var record = new byte[length];
        if (length > 0)
        {
            record[0] = 0x01;
        }

        var exception = Assert.Throws<ArgumentException>(() => header.WithSlot(V0, record));

        Assert.Equal("record", exception.ParamName);
    }

    [Fact]
    public void WithSlot_SlotType0_ThrowsArgument()
    {
        var header = VaultHeader.Parse(V0);

        Assert.Throws<ArgumentException>(() => header.WithSlot(V0, [0x00, 0x00, 0x00]));
    }

    [Fact]
    public void WithSlot_UnsupportedKdfAlgorithm_ThrowsArgument()
    {
        var header = VaultHeader.Parse(V0);

        Assert.Throws<ArgumentException>(() => header.WithSlot(V0, With(Slot1, 3, 0x02)));
    }

    [Fact]
    public void WithSlot_NinthSlot_ThrowsArgument()
    {
        byte[][] records = [Slot1, .. Enumerable.Range(3, 7).Select(type => Unknown((byte)type, 0))];
        var file = Header(8, [.. records, Z40]);
        var header = VaultHeader.Parse(file);

        Assert.Equal(8, header.Slots.Count);
        Assert.Throws<ArgumentException>(() => header.WithSlot(file, Slot2));
    }

    [Fact]
    public void WithSlot_FileShorterThanHeader_ThrowsArgument()
    {
        var header = VaultHeader.Parse(V0);

        var exception = Assert.Throws<ArgumentException>(() => header.WithSlot(V0.AsSpan(0, 100), Slot1b));

        Assert.Equal("file", exception.ParamName);
    }

    [Fact]
    public void WithSlot_Inputs_AreNotModified()
    {
        var file = (byte[])V2.Clone();
        var record = (byte[])Slot1b.Clone();

        VaultHeader.Parse(file).WithSlot(file, record);

        Assert.Equal(V2, file);
        Assert.Equal(Slot1b, record);
    }
}
