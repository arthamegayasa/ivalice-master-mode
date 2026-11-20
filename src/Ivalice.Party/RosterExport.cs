namespace Ivalice.Party;

/// <summary>What the next reincarnation would give a unit.</summary>
/// <param name="GainPermille">Power it adds, in tenths of a percent.</param>
/// <param name="TotalPermille">The unit's Power afterwards.</param>
/// <param name="Raw">Raw stats at level 1 afterwards.</param>
/// <param name="Shown">Those stats as the game shows them in the unit's current job, before equipment.</param>
public sealed record NextReincarnation(int GainPermille, int TotalPermille, StatBlock Raw, StatBlock? Shown);

/// <summary>Everything the interface shows about one unit of the roster.</summary>
/// <param name="Shown">Stats as the game shows them, before equipment; null when the job's data is unknown.</param>
/// <param name="Reason">"eligible", or the code of what stands in the way.</param>
/// <param name="BattleSlot">The battle unit record still tied to this unit, if any.</param>
/// <param name="Next">Null for units that can never reincarnate (guests, monsters).</param>
public sealed record UnitEntry(
    int Slot,
    string? Name,
    int JobId,
    string? JobName,
    string Kind,
    int Level,
    int Experience,
    int Brave,
    int Faith,
    StatBlock Raw,
    StatBlock? Shown,
    int Reincarnations,
    int PowerPermille,
    bool Eligible,
    string Reason,
    string Fingerprint,
    bool OnErrand,
    int? BattleSlot,
    NextReincarnation? Next);

/// <summary>Looks up a unit's display name from its name key; returns null when it cannot.</summary>
public delegate string? NameResolver(int nameKey);

/// <summary>Turns roster records into the entries written to roster.json.</summary>
public static class RosterExport
{
    public static UnitEntry Describe(ReadOnlySpan<byte> record, int slot, ReadOnlySpan<byte> battleTable,
        ReadOnlySpan<byte> jobTable, GameStatus game, NameResolver? names = null)
    {
        var growth = JobTable.GrowthOf(jobTable, record);
        var raw = RosterRecord.RawStats(record);
        var stamp = StampCodec.Read(record);
        var refusal = GameSafety.Evaluate(record, slot, battleTable, game);
        var kind = RosterRecord.Kind(record);
        var bound = BattleBinding.Find(battleTable, slot);

        NextReincarnation? next = null;
        var canEverReincarnate = slot < RosterLayout.PartySlots && kind is UnitKind.Male or UnitKind.Female or UnitKind.Ramza;
        if (canEverReincarnate && Reincarnation.Plan(record) is { } plan)
            next = new NextReincarnation(plan.GainPermille, plan.Stamp.PowerPermille, plan.Raw, growth?.Shown(plan.Raw));

        return new UnitEntry(
            Slot: slot,
            Name: RosterRecord.OwnName(record) ?? names?.Invoke(RosterRecord.NameKey(record)),
            JobId: record[RosterLayout.Job],
            JobName: JobNames.Find(record[RosterLayout.Job]),
            Kind: kind.ToString().ToLowerInvariant(),
            Level: record[RosterLayout.Level],
            Experience: record[RosterLayout.Experience],
            Brave: record[RosterLayout.Brave],
            Faith: record[RosterLayout.Faith],
            Raw: raw,
            Shown: growth?.Shown(raw),
            Reincarnations: stamp.Count,
            PowerPermille: stamp.PowerPermille,
            Eligible: refusal == Refusal.None,
            Reason: refusal.Code(),
            Fingerprint: StampCodec.FingerprintText(record),
            OnErrand: record[RosterLayout.OnErrand] != 0,
            BattleSlot: bound >= 0 ? bound : null,
            Next: next);
    }

    /// <summary>One entry per slot that holds a unit, in slot order.</summary>
    public static List<UnitEntry> DescribeAll(ReadOnlySpan<byte> roster, ReadOnlySpan<byte> battleTable,
        ReadOnlySpan<byte> jobTable, GameStatus game, NameResolver? names = null)
    {
        var units = new List<UnitEntry>();
        if (roster.Length != RosterLayout.TableSize) return units;

        for (var slot = 0; slot < RosterLayout.Capacity; slot++)
        {
            var record = RosterLayout.Record(roster, slot);
            if (RosterRecord.Holds(record, slot)) units.Add(Describe(record, slot, battleTable, jobTable, game, names));
        }
        return units;
    }
}
