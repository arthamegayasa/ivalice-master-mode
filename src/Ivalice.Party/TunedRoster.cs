namespace Ivalice.Party;

public static class TunedRoster
{
    /// <summary>Projects saved rules onto the current and restarted raw stats, returning the original
    /// document when no unit is tuned. A legacy feed lacks the effective row of job 0xA1.</summary>
    public static RosterDocument Apply(RosterDocument roster, StatTuningRules? rules, IReadOnlyDictionary<int, JobDefaults> jobs,
        bool playerLimitsReady = false)
    {
        if (rules is null || rules.IsEmpty || jobs.Count == 0) return roster;
        UnitEntry[]? units = null;
        for (var index = 0; index < roster.Units.Count; index++)
        {
            var unit = roster.Units[index];
            if (unit.EffectiveJobRow is null && unit.JobId == 0xA1) continue;
            var row = unit.EffectiveJobRow ?? unit.JobId;
            if (!jobs.TryGetValue(row, out var job)) continue;

            var pairs = new byte[StatTuningRules.PairBytes];
            for (var i = 0; i < StatBlock.Count; i++)
            {
                pairs[i * 2] = (byte)job.Growth[i];
                pairs[i * 2 + 1] = (byte)job.Multiplier[i];
            }
            var tunedPairs = rules.Apply(pairs, row, playerOwned: true, unit.Fingerprint);
            var limits = playerLimitsReady && unit.Slot is >= 0 and < RosterLayout.PartySlots ? rules.PlayerLimits : PlayerStatLimits.Default;
            if (!tunedPairs && !limits.AnyEnabled) continue;
            var tuned = new JobGrowth(StatBlock.From(i => pairs[i * 2]), StatBlock.From(i => pairs[i * 2 + 1]));
            var shown = tuned.Shown(unit.Raw, limits);
            var next = unit.Next is { } restart ? restart with { Shown = tuned.Shown(restart.Raw, limits) } : null;
            if (!tunedPairs && shown == unit.Shown && next == unit.Next) continue;
            units ??= roster.Units.ToArray();
            units[index] = unit with { Shown = shown, Next = next };
        }
        return units is null ? roster : roster with { Units = units };
    }
}
