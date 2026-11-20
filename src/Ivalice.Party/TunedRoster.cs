namespace Ivalice.Party;

public static class TunedRoster
{
    /// <summary>Projects saved rules onto the current and restarted raw stats, returning the original
    /// document when no unit is tuned. The feed has no job level to disambiguate row 0xA1.</summary>
    public static RosterDocument Apply(RosterDocument roster, StatTuningRules? rules, IReadOnlyDictionary<int, JobDefaults> jobs)
    {
        if (rules is null || rules.IsEmpty || jobs.Count == 0) return roster;
        UnitEntry[]? units = null;
        for (var index = 0; index < roster.Units.Count; index++)
        {
            var unit = roster.Units[index];
            if (unit.JobId == 0xA1 || !jobs.TryGetValue(unit.JobId, out var job)) continue;

            var pairs = new byte[StatTuningRules.PairBytes];
            for (var i = 0; i < StatBlock.Count; i++)
            {
                pairs[i * 2] = (byte)job.Growth[i];
                pairs[i * 2 + 1] = (byte)job.Multiplier[i];
            }
            if (!rules.Apply(pairs, unit.JobId, playerOwned: true, unit.Fingerprint)) continue;
            var tuned = new JobGrowth(StatBlock.From(i => pairs[i * 2]), StatBlock.From(i => pairs[i * 2 + 1]));
            units ??= roster.Units.ToArray();
            units[index] = unit with
            {
                Shown = tuned.Shown(unit.Raw),
                Next = unit.Next is { } next ? next with { Shown = tuned.Shown(next.Raw) } : null,
            };
        }
        return units is null ? roster : roster with { Units = units };
    }
}
