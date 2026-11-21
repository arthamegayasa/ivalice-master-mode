using System.Buffers.Binary;

namespace Ivalice.SkillCost;

internal static class AbilityPrices
{
    private const int HeaderSize = 48;
    private const int RowCount = 512;
    private const int IndexEntrySize = 8;
    private const int PriceOffset = 64;
    private const int NameOffset = 8;

    // Enhanced stores a single-key localized Ability table. Offsets in the index are relative
    // to the index entry; the name string offset is relative to the name field itself.
    internal static byte[] WithFlatPrices(byte[] original)
    {
        var data = original.AsSpan();
        int indexEnd = HeaderSize + RowCount * IndexEntrySize;
        if (data.Length < indexEnd || !data[..4].SequenceEqual("NXDF"u8)
            || ReadInt(data, 4) != 1 || ReadInt(data, 8) != 0x10201
            || ReadInt(data, 32) != HeaderSize || ReadInt(data, 36) != RowCount)
            throw new InvalidDataException("Unrecognized Enhanced Ability table.");

        byte[] output = (byte[])original.Clone();
        int nextRowMinimum = indexEnd;
        for (int id = 0; id < RowCount; id++)
        {
            int entry = HeaderSize + id * IndexEntrySize;
            long start = (long)entry + ReadInt(data, entry + 4);
            if (ReadInt(data, entry) != id || start < nextRowMinimum || start > data.Length - PriceOffset - 2)
                throw new InvalidDataException($"Invalid Ability row {id}.");
            int row = (int)start;
            nextRowMinimum = row + PriceOffset + 2;
            long name = (long)row + NameOffset + ReadInt(data, row + NameOffset);
            if (name < indexEnd || name >= data.Length)
                throw new InvalidDataException($"Invalid name in Ability row {id}.");

            ushort cost = BinaryPrimitives.ReadUInt16LittleEndian(data[(row + PriceOffset)..]);
            // Among the 9999 JP entries, only Zodiark and Ultima are player-learnable.
            // Boss commands and priced placeholders outside job command lists keep their costs.
            bool learnablePrice = cost > 0 && (cost < 9999 || id is 75 or 154);
            bool bossOrUnused = id is (>= 216 and <= 218) or 440 or 483 or 497 or 505 or 508;
            if (learnablePrice && !bossOrUnused && data[(int)name] != 0)
                BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(row + PriceOffset), 50);
        }
        return output;
    }

    private static int ReadInt(ReadOnlySpan<byte> data, int offset) =>
        BinaryPrimitives.ReadInt32LittleEndian(data[offset..]);
}
