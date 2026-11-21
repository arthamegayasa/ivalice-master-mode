using System.Buffers.Binary;
using Ivalice.SkillCost;

namespace Ivalice.Core.Tests;

public class AbilityPricesTests
{
    private const int Index = 48;
    private const int Rows = Index + 512 * 8;
    private const int Stride = 68;

    [Fact]
    public void Only_the_price_bytes_of_paid_job_skills_change()
    {
        var table = Table();
        foreach (int id in new[] { 4, 75, 154, 216, 217, 218, 368, 440, 483, 497, 505, 508 })
            WritePrice(table, id, 800);
        WritePrice(table, 1, 50);
        WritePrice(table, 55, 30);
        WritePrice(table, 23, 9999);
        WritePrice(table, 184, 500);
        WriteNameOffset(table, 184, table.Length - 1); // Empty name
        var before = (byte[])table.Clone();
        var expected = (byte[])before.Clone();
        foreach (int id in new[] { 4, 55, 368 })
            WritePrice(expected, id, 50);

        var actual = AbilityPrices.WithFlatPrices(table);

        Assert.Equal(expected, actual);
        Assert.Equal(before, table);
        Assert.Equal(actual, AbilityPrices.WithFlatPrices(actual));
    }

    [Fact]
    public void Invalid_index_or_layout_is_rejected_before_any_price_changes_escape()
    {
        var table = Table();
        WritePrice(table, 1, 800);
        BinaryPrimitives.WriteInt32LittleEndian(table.AsSpan(Index + 511 * 8 + 4), int.MaxValue);
        var before = (byte[])table.Clone();
        Assert.Throws<InvalidDataException>(() => AbilityPrices.WithFlatPrices(table));
        Assert.Equal(before, table);
        Assert.Throws<InvalidDataException>(() => AbilityPrices.WithFlatPrices(new byte[4]));
    }

    private static byte[] Table()
    {
        var data = new byte[Rows + Stride * 512 + 2];
        "NXDF"u8.CopyTo(data);
        data[4] = 1;
        data[8] = 1;
        data[9] = 2;
        data[10] = 1;
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(32), Index);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(36), 512);
        data[^2] = (byte)'A';
        for (int id = 0; id < 512; id++)
        {
            int entry = Index + 8 * id;
            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(entry), id);
            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(entry + 4), Rows + Stride * id - entry);
            WriteNameOffset(data, id, data.Length - 2);
        }
        return data;
    }

    private static void WriteNameOffset(byte[] table, int id, int name)
    {
        int field = Rows + Stride * id + 8;
        BinaryPrimitives.WriteInt32LittleEndian(table.AsSpan(field), name - field);
    }

    private static void WritePrice(byte[] table, int id, ushort price) =>
        BinaryPrimitives.WriteUInt16LittleEndian(table.AsSpan(Rows + Stride * id + 64), price);
}
