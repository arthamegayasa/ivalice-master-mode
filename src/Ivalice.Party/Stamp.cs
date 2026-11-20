using System.Buffers.Binary;

namespace Ivalice.Party;

/// <summary>What a unit has gained from reincarnating so far.</summary>
/// <param name="Count">How many times the unit has reincarnated.</param>
/// <param name="PowerPermille">The bonus on its starting stats, in tenths of a percent.</param>
public readonly record struct ReincarnationStamp(int Count, int PowerPermille)
{
    public static readonly ReincarnationStamp None = new(0, 0);
}

public enum StampState
{
    /// <summary>The stamp bytes are all zero: the unit never reincarnated.</summary>
    Absent,
    /// <summary>A stamp written by this project for this very unit.</summary>
    Valid,
    /// <summary>A stamp of this project that is damaged or belongs to a unit that used the slot before.</summary>
    Stale,
    /// <summary>Bytes this project did not write. They are left alone.</summary>
    Foreign,
}

/// <summary>
/// The reincarnation stamp kept in the first 20 spare bytes of a roster record (+0x234 to +0x247).
/// The game saves and loads those bytes with the record and does nothing else with them. It also does
/// not clear them when a slot is freed, so the stamp carries a fingerprint of its unit: a new recruit
/// in a reused slot has a different fingerprint and so starts without a stamp.
/// </summary>
/// <remarks>
/// Layout, little-endian: magic "IVRC" (4), version (1), zero (1), count (2), power in permille (2),
/// zero (2), fingerprint (4), FNV-1a checksum of the 16 bytes before it (4).
/// </remarks>
public static class StampCodec
{
    public const int Offset = RosterLayout.Spare;
    public const int Length = 20;
    public const byte Version = 1;
    public const int MaxCount = ushort.MaxValue;

    private static ReadOnlySpan<byte> Magic => "IVRC"u8;
    private const int ChecksumAt = 16;

    public static StampState Inspect(ReadOnlySpan<byte> record) => Decode(record, out _);

    /// <summary>The unit's stamp, or <see cref="ReincarnationStamp.None"/> when it has no valid one.</summary>
    public static ReincarnationStamp Read(ReadOnlySpan<byte> record) =>
        Decode(record, out var stamp) == StampState.Valid ? stamp : ReincarnationStamp.None;

    public static void Write(Span<byte> record, ReincarnationStamp stamp)
    {
        var bytes = record.Slice(Offset, Length);
        bytes.Clear();
        Magic.CopyTo(bytes);
        bytes[4] = Version;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes[6..], (ushort)Math.Clamp(stamp.Count, 0, MaxCount));
        BinaryPrimitives.WriteUInt16LittleEndian(bytes[8..],
            (ushort)Math.Clamp(stamp.PowerPermille, 0, ReincarnationRules.MaxPowerPermille));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[12..], Fingerprint(record));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[ChecksumAt..], Fnv1a(bytes[..ChecksumAt]));
    }

    /// <summary>
    /// A number that identifies the unit and does not change while it is in the party: its marker
    /// (Ramza's changes with the chapter, so all three count as one), sex, birthday and name key.
    /// Job, level, stats, equipment and the slot number are left out on purpose.
    /// </summary>
    public static uint Fingerprint(ReadOnlySpan<byte> record)
    {
        Span<byte> identity = stackalloc byte[8];
        var isRamza = RosterRecord.Kind(record) == UnitKind.Ramza;
        identity[0] = isRamza ? (byte)1 : record[RosterLayout.Marker];
        identity[1] = (byte)(record[RosterLayout.SexFlags] & (RosterLayout.MaleBit | RosterLayout.FemaleBit | RosterLayout.MonsterBit));
        identity[2] = record[RosterLayout.Birth];
        identity[3] = record[RosterLayout.Birth + 1];
        // Ramza's name key is left out as well, in case it changes with his marker.
        if (!isRamza) record.Slice(RosterLayout.NameKey, 4).CopyTo(identity[4..]);
        return Fnv1a(identity);
    }

    public static string FingerprintText(ReadOnlySpan<byte> record) => Fingerprint(record).ToString("X8");

    private static StampState Decode(ReadOnlySpan<byte> record, out ReincarnationStamp stamp)
    {
        stamp = ReincarnationStamp.None;
        var bytes = record.Slice(Offset, Length);
        if (bytes.IndexOfAnyExcept((byte)0) < 0) return StampState.Absent;
        if (!bytes[..4].SequenceEqual(Magic) || bytes[4] != Version) return StampState.Foreign;

        if (BinaryPrimitives.ReadUInt32LittleEndian(bytes[ChecksumAt..]) != Fnv1a(bytes[..ChecksumAt])) return StampState.Stale;
        if (BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]) != Fingerprint(record)) return StampState.Stale;

        var power = BinaryPrimitives.ReadUInt16LittleEndian(bytes[8..]);
        if (power > ReincarnationRules.MaxPowerPermille) return StampState.Stale;

        stamp = new ReincarnationStamp(BinaryPrimitives.ReadUInt16LittleEndian(bytes[6..]), power);
        return StampState.Valid;
    }

    private static uint Fnv1a(ReadOnlySpan<byte> bytes)
    {
        var hash = 2166136261u;
        foreach (var b in bytes) hash = (hash ^ b) * 16777619u;
        return hash;
    }
}
