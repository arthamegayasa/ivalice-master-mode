namespace Ivalice.Party;

public static class ReincarnationRules
{
    public const int DefaultMinimumLevel = 10;

    /// <summary>The lowest level a unit may reincarnate at. A setting of the mod; the default is 10.</summary>
    public static int MinimumLevel { get; private set; } = DefaultMinimumLevel;

    /// <summary>Sets the minimum level, kept within 2 to 99 (a level-1 unit has nothing to give up).</summary>
    public static void UseMinimumLevel(int level) => MinimumLevel = Math.Clamp(level, 2, 99);
    /// <summary>Each level the unit had adds 0.5% to its Power.</summary>
    public const int PermillePerLevel = 5;
    /// <summary>Power stops growing at +100%.</summary>
    public const int MaxPowerPermille = 1000;
}

/// <summary>Why a reincarnation was not carried out. <see cref="None"/> means nothing stands in the way.</summary>
public enum Refusal
{
    None,
    // About the unit itself.
    BadSlot,
    EmptySlot,
    Guest,
    Monster,
    UnknownKind,
    OnErrand,
    LevelTooLow,
    SpareBytesInUse,
    // About the moment.
    InBattle,
    BoundToBattleUnit,
    GameStateUnknown,
    // About the command.
    FingerprintMismatch,
    LevelChanged,
    Expired,
    Malformed,
    UnknownAction,
    JournalFailed,
}

public static class RefusalText
{
    /// <summary>The code written to the bridge files, for example "levelTooLow".</summary>
    public static string Code(this Refusal refusal)
    {
        if (refusal == Refusal.None) return "eligible";
        var name = refusal.ToString();
        return char.ToLowerInvariant(name[0]) + name[1..];
    }

    /// <summary>A plain English sentence for logs and for whoever reads the result file by hand.</summary>
    public static string Describe(this Refusal refusal) => refusal switch
    {
        Refusal.None => "Nothing stands in the way.",
        Refusal.BadSlot => "The slot number is outside the roster.",
        Refusal.EmptySlot => "The slot is empty.",
        Refusal.Guest => "Guests cannot reincarnate.",
        Refusal.Monster => "Monsters cannot reincarnate.",
        Refusal.UnknownKind => "The unit is neither man, woman nor monster, so its starting stats are unknown.",
        Refusal.OnErrand => "The unit is away on an errand.",
        Refusal.LevelTooLow => $"The unit must be at least level {ReincarnationRules.MinimumLevel}.",
        Refusal.SpareBytesInUse => "The record's spare bytes hold data this mod did not write, so it will not overwrite them.",
        Refusal.InBattle => "A battle or story scene is in progress.",
        Refusal.BoundToBattleUnit => "The game still holds a battle copy of this unit that it would write back over the change.",
        Refusal.GameStateUnknown => "The mod could not read the game's battle state, so it does not know whether a change is safe.",
        Refusal.FingerprintMismatch => "The slot no longer holds the unit the command was meant for.",
        Refusal.LevelChanged => "The unit's level changed since the command was made.",
        Refusal.Expired => "The command is too old.",
        Refusal.Malformed => "The command could not be read.",
        Refusal.UnknownAction => "The command asks for an action this mod does not know.",
        Refusal.JournalFailed => "The journal could not be written, so nothing was changed.",
        _ => refusal.ToString(),
    };
}

/// <summary>What a reincarnation does to a unit.</summary>
/// <param name="LevelBefore">The level the unit gives up.</param>
/// <param name="GainPermille">Power added by this reincarnation, after the cap.</param>
/// <param name="Stamp">The unit's count and total Power afterwards.</param>
/// <param name="Raw">The raw stats the unit restarts with at level 1.</param>
public readonly record struct ReincarnationPlan(int LevelBefore, int GainPermille, ReincarnationStamp Stamp, StatBlock Raw);

/// <summary>The reincarnation rules, as pure functions over one roster record.</summary>
public static class Reincarnation
{
    // Base and span of the game's starting-stat rows (RVA 0x67F7D0 and 0x67F800). A new unit rolls
    // base + a random share of the span; reincarnation uses the middle of that roll.
    private static readonly StatBlock MaleBase = new(30, 14, 6, 5, 4), MaleSpan = new(2, 1, 0, 0, 0);
    private static readonly StatBlock FemaleBase = new(28, 15, 6, 4, 5), FemaleSpan = new(2, 1, 0, 0, 0);
    private static readonly StatBlock RamzaBase = new(30, 15, 6, 5, 5), RamzaSpan = new(2, 1, 0, 0, 0);
    private static readonly StatBlock MonsterBase = new(35, 8, 5, 5, 5), MonsterSpan = new(3, 1, 0, 1, 1);

    /// <summary>The raw stats of an average newly created unit of this kind.</summary>
    public static StatBlock Baseline(UnitKind kind)
    {
        var (start, span) = kind switch
        {
            UnitKind.Male => (MaleBase, MaleSpan),
            UnitKind.Female => (FemaleBase, FemaleSpan),
            UnitKind.Ramza => (RamzaBase, RamzaSpan),
            UnitKind.Monster => (MonsterBase, MonsterSpan),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "No starting stats for this kind."),
        };
        return StatBlock.From(i => (start[i] << 14) + (span[i] << 13));
    }

    /// <summary>Whether the unit in this slot may reincarnate, judged from its record alone.</summary>
    public static Refusal Check(ReadOnlySpan<byte> record, int slot)
    {
        if (slot < 0 || slot >= RosterLayout.Capacity || record.Length != RosterLayout.Stride) return Refusal.BadSlot;
        if (!RosterRecord.Holds(record, slot)) return Refusal.EmptySlot;
        if (slot >= RosterLayout.PartySlots) return Refusal.Guest;

        switch (RosterRecord.Kind(record))
        {
            case UnitKind.Monster: return Refusal.Monster;
            case UnitKind.Unknown: return Refusal.UnknownKind;
        }

        if (record[RosterLayout.OnErrand] != 0) return Refusal.OnErrand;
        if (record[RosterLayout.Level] < ReincarnationRules.MinimumLevel) return Refusal.LevelTooLow;
        if (StampCodec.Inspect(record) == StampState.Foreign) return Refusal.SpareBytesInUse;
        return Refusal.None;
    }

    /// <summary>
    /// What reincarnating would give the unit at its current level. It answers for any unit with known
    /// starting stats, so the interface can preview it before the unit is eligible.
    /// </summary>
    public static ReincarnationPlan? Plan(ReadOnlySpan<byte> record)
    {
        var kind = RosterRecord.Kind(record);
        if (kind is UnitKind.Unknown) return null;

        var level = record[RosterLayout.Level];
        var before = StampCodec.Read(record);
        var power = Math.Min(before.PowerPermille + level * ReincarnationRules.PermillePerLevel, ReincarnationRules.MaxPowerPermille);
        var baseline = Baseline(kind);
        var raw = StatBlock.From(i => (int)Math.Min((long)baseline[i] * (1000 + power) / 1000, 0xFFFFFF));
        var stamp = new ReincarnationStamp(Math.Min(before.Count + 1, StampCodec.MaxCount), power);
        return new ReincarnationPlan(level, power - before.PowerPermille, stamp, raw);
    }

    /// <summary>
    /// Carries out a plan on a record: level 1, no experience, the new raw stats and the new stamp.
    /// Job, JP, abilities, equipment, Brave, Faith and every other byte stay as they are.
    /// </summary>
    public static void Apply(Span<byte> record, ReincarnationPlan plan)
    {
        record[RosterLayout.Level] = 1;
        record[RosterLayout.Experience] = 0;
        RosterRecord.WriteRawStats(record, plan.Raw);
        StampCodec.Write(record, plan.Stamp);
    }
}
