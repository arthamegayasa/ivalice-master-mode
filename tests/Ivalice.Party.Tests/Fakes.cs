using Ivalice.Party;

namespace Ivalice.Party.Tests;

/// <summary>Builds fake roster records and tables.</summary>
internal static class Fake
{
    public const byte Squire = 0x4A;
    public const byte Chocobo = 0x5E;

    /// <summary>A generic party member with the stats of an average new recruit.</summary>
    public static byte[] Unit(int slot = 1, int level = 30, bool female = false, byte job = Squire, int nameKey = 346,
        bool onErrand = false, byte brave = 60, byte faith = 55, int experience = 42)
    {
        var record = new byte[RosterLayout.Stride];
        record[RosterLayout.Marker] = (byte)(female ? 0x81 : 0x80);
        record[RosterLayout.SlotId] = (byte)slot;
        record[RosterLayout.Job] = job;
        record[RosterLayout.SexFlags] = (byte)((female ? RosterLayout.FemaleBit : RosterLayout.MaleBit) | 0x10);
        record[RosterLayout.Birth] = 0x9D;
        record[RosterLayout.Birth + 1] = 0x30;
        record[RosterLayout.Experience] = (byte)experience;
        record[RosterLayout.Level] = (byte)level;
        record[RosterLayout.Brave] = brave;
        record[RosterLayout.Faith] = faith;
        RosterRecord.WriteRawStats(record, Reincarnation.Baseline(female ? UnitKind.Female : UnitKind.Male));
        BitConverter.TryWriteBytes(record.AsSpan(RosterLayout.NameKey), nameKey);
        if (onErrand) record[RosterLayout.OnErrand] = 1;
        // Something in every other region, so a test notices a stray write.
        for (var i = 0x32; i < 0xDC; i++) record[i] = (byte)(i * 7);
        return record;
    }

    public static byte[] Monster(int slot = 2, int level = 30)
    {
        var record = Unit(slot, level, job: Chocobo);
        record[RosterLayout.Marker] = RosterLayout.MarkerMonster;
        record[RosterLayout.SexFlags] = RosterLayout.MonsterBit;
        return record;
    }

    /// <summary>A roster with every slot empty except the records given, each placed in its own slot.</summary>
    public static byte[] Roster(params byte[][] records)
    {
        var table = new byte[RosterLayout.TableSize];
        for (var slot = 0; slot < RosterLayout.Capacity; slot++)
            table[slot * RosterLayout.Stride + RosterLayout.SlotId] = RosterLayout.EmptySlot;
        foreach (var record in records)
            record.CopyTo(table, record[RosterLayout.SlotId] * RosterLayout.Stride);
        return table;
    }

    /// <summary>The battle unit table as it is when the game starts: all zero.</summary>
    public static byte[] QuietBattleTable() => new byte[BattleBinding.TableSize];

    /// <summary>A battle unit table with one unit built from a roster slot.</summary>
    public static byte[] BattleTableWith(int battleSlot, int rosterSlot, byte inUse = 0, byte onField = 1, byte unitId = 0x21)
    {
        var table = QuietBattleTable();
        var unit = table.AsSpan(battleSlot * BattleBinding.Stride, BattleBinding.Stride);
        unit[0x01] = inUse;
        unit[0x02] = (byte)rosterSlot;
        unit[0x191] = unitId;
        unit[0x1B5] = onField;
        return table;
    }

    /// <summary>A job table holding the growth rows of the jobs the tests use, as they are in the game.</summary>
    public static byte[] JobTable()
    {
        var table = new byte[Party.JobTable.TableSize];
        Row(0x01, 11, 120, 11, 105, 95, 100, 50, 110, 48, 100); // Ramza's Squire
        Row(0x04, 10, 120, 11, 100, 100, 100, 50, 110, 50, 100);
        Row(0x4A, 11, 100, 15, 75, 100, 100, 60, 90, 50, 80);   // Squire
        Row(0x4B, 12, 80, 16, 75, 100, 100, 75, 75, 50, 80);    // Chemist
        Row(0x5E, 8, 108, 30, 100, 75, 119, 35, 98, 7, 94);     // Chocobo
        Row(0xA1, 13, 50, 13, 50, 100, 100, 80, 50, 50, 50);
        Row(0xA4, 6, 150, 8, 140, 80, 120, 35, 130, 40, 120);
        return table;

        void Row(int job, params byte[] pairs) =>
            pairs.CopyTo(table, job * Party.JobTable.RowSize + Party.JobTable.GrowthOffset);
    }

    public static GameStatus WorldMap => GameSafety.Decide(new SafetyInputs(false, false, 0, 0, 0, null));
    public static GameStatus Battle => GameSafety.Decide(new SafetyInputs(true, true, 8, 5, 4, 12));
}
