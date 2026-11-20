namespace Ivalice.Party;

/// <summary>
/// Layout of the game's party roster in FFT_enhanced.exe, as far as this project needs it.
/// Every offset was read from the game's own code; see docs/reincarnation.md for the evidence.
/// </summary>
public static class RosterLayout
{
    public const int Capacity = 54;
    /// <summary>Slots 0 to 49 are the party; 50 to 53 hold guests.</summary>
    public const int PartySlots = 50;
    public const int Stride = 0x258;
    public const int TableSize = Capacity * Stride;

    /// <summary>1 to 3 for Ramza (it changes with the chapter), 0x80 / 0x81 for a generic man / woman, 0x82 for a monster.</summary>
    public const int Marker = 0x00;
    public const byte MarkerMonster = 0x82;
    /// <summary>The record's own slot number, or <see cref="EmptySlot"/> when the slot is free.</summary>
    public const int SlotId = 0x01;
    public const byte EmptySlot = 0xFF;
    public const int Job = 0x02;

    public const int SexFlags = 0x04;
    public const byte MaleBit = 0x80;
    public const byte FemaleBit = 0x40;
    public const byte MonsterBit = 0x20;

    /// <summary>Two bytes that hold the birthday and zodiac sign; they never change after the unit is created.</summary>
    public const int Birth = 0x05;

    public const int Experience = 0x1C;
    public const int Level = 0x1D;
    public const int Brave = 0x1E;
    public const int Faith = 0x1F;
    /// <summary>HP, MP, Speed, Physical Attack, Magick Attack: five 24-bit little-endian values, 16384 = 1.0.</summary>
    public const int RawStats = 0x20;

    /// <summary>Twelve bytes of job levels, one nibble per job index.</summary>
    public const int JobLevels = 0x74;

    /// <summary>The unit's own name as a zero-terminated string; empty for units that use a name from the game's table.</summary>
    public const int OwnName = 0xDC;
    public const int OwnNameLength = 64;

    /// <summary>Non-zero while the unit is away on an errand.</summary>
    public const int OnErrand = 0x11E;
    /// <summary>Key of the unit's name in the game's name table (32-bit).</summary>
    public const int NameKey = 0x230;

    /// <summary>Bytes the game copies with the record but never reads or writes on their own.</summary>
    public const int Spare = 0x234;
    public const int SpareLength = 0x24;

    public static ReadOnlySpan<byte> Record(ReadOnlySpan<byte> table, int slot) => table.Slice(slot * Stride, Stride);
    public static Span<byte> Record(Span<byte> table, int slot) => table.Slice(slot * Stride, Stride);
}

/// <summary>Which row of the game's starting-stat table a unit was rolled from.</summary>
public enum UnitKind
{
    Unknown,
    Male,
    Female,
    Ramza,
    Monster,
}

/// <summary>One value per stat, in the order the game stores them.</summary>
public readonly record struct StatBlock(int Hp, int Mp, int Speed, int PhysicalAttack, int MagickAttack)
{
    public const int Count = 5;

    public int this[int index] => index switch
    {
        0 => Hp,
        1 => Mp,
        2 => Speed,
        3 => PhysicalAttack,
        4 => MagickAttack,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    public static StatBlock From(Func<int, int> value) => new(value(0), value(1), value(2), value(3), value(4));
}

/// <summary>Reads single fields of one roster record.</summary>
public static class RosterRecord
{
    /// <summary>
    /// Whether this record holds the unit of the given slot. A record in use carries its own slot number;
    /// a free one carries 0xFF, and memory the game has not set up yet carries neither.
    /// </summary>
    public static bool Holds(ReadOnlySpan<byte> record, int slot) =>
        record[RosterLayout.SlotId] != RosterLayout.EmptySlot && record[RosterLayout.SlotId] == slot;

    public static UnitKind Kind(ReadOnlySpan<byte> record)
    {
        var marker = record[RosterLayout.Marker];
        var sex = record[RosterLayout.SexFlags];
        if (marker is >= 1 and <= 3) return UnitKind.Ramza;
        if (marker == RosterLayout.MarkerMonster || (sex & RosterLayout.MonsterBit) != 0) return UnitKind.Monster;
        if ((sex & RosterLayout.FemaleBit) != 0) return UnitKind.Female;
        if ((sex & RosterLayout.MaleBit) != 0) return UnitKind.Male;
        return UnitKind.Unknown;
    }

    public static StatBlock RawStats(ReadOnlySpan<byte> record)
    {
        var stats = record.Slice(RosterLayout.RawStats, StatBlock.Count * 3);
        return new StatBlock(Read24(stats, 0), Read24(stats, 3), Read24(stats, 6), Read24(stats, 9), Read24(stats, 12));
    }

    public static void WriteRawStats(Span<byte> record, StatBlock raw)
    {
        var stats = record.Slice(RosterLayout.RawStats, StatBlock.Count * 3);
        for (var i = 0; i < StatBlock.Count; i++)
        {
            var value = Math.Clamp(raw[i], 0, 0xFFFFFF);
            stats[i * 3] = (byte)value;
            stats[i * 3 + 1] = (byte)(value >> 8);
            stats[i * 3 + 2] = (byte)(value >> 16);
        }
    }

    public static int NameKey(ReadOnlySpan<byte> record) =>
        BitConverter.ToInt32(record.Slice(RosterLayout.NameKey, 4));

    /// <summary>The unit's own stored name, or null when the record has none or it is not valid UTF-8 text.</summary>
    public static string? OwnName(ReadOnlySpan<byte> record)
    {
        var bytes = record.Slice(RosterLayout.OwnName, RosterLayout.OwnNameLength);
        var end = bytes.IndexOf((byte)0);
        if (end >= 0) bytes = bytes[..end];
        return NameText.Decode(bytes);
    }

    private static int Read24(ReadOnlySpan<byte> bytes, int offset) =>
        bytes[offset] | (bytes[offset + 1] << 8) | (bytes[offset + 2] << 16);
}

public static class NameText
{
    private static readonly System.Text.UTF8Encoding StrictUtf8 = new(false, throwOnInvalidBytes: true);

    /// <summary>Decodes a name read from game memory; null when it is empty, not UTF-8 or holds control characters.</summary>
    public static string? Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty) return null;
        try
        {
            var text = StrictUtf8.GetString(bytes);
            foreach (var c in text)
                if (char.IsControl(c)) return null;
            text = text.Trim();
            return text.Length == 0 ? null : text;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
