namespace Ivalice.Party;

/// <summary>A job row from the catalog written by the game mod.</summary>
public sealed record JobDefaults(int Job, int[] Growth, int[] Multiplier)
{
    public string Group => Job < 0x4A ? "special" : Job <= 0x5D ? "generic" : "monster";
    public string? Name => JobNames.Find(Job) ?? (Job is 0xA1 or 0xA4 ? "Job tanpa nama" : null);
}

public enum PresetScope { Generic, Special, Both }

public sealed record StatPreset(PresetScope Scope, int[] Stats, int GrowthPercent, int MultiplierPercent)
{
    public StatTuningRules Apply(StatTuningRules draft, IEnumerable<JobDefaults> catalog)
    {
        if (!Enum.IsDefined(Scope) || Stats is not { Length: > 0 } || Stats.Any(i => i < 0 || i >= StatBlock.Count))
            throw new ArgumentException("Pilihan job atau stat tidak sah.");
        if (GrowthPercent is < StatTuningRules.MinPercent or > StatTuningRules.MaxPercent ||
            MultiplierPercent is < StatTuningRules.MinPercent or > StatTuningRules.MaxPercent)
            throw new ArgumentException("Persentase harus antara 25 dan 400.");

        var jobs = draft.Jobs.ToDictionary();
        foreach (var job in catalog)
        {
            if (job.Group == "monster" || (Scope == PresetScope.Generic && job.Group != "generic")
                || (Scope == PresetScope.Special && job.Group != "special")) continue;

            jobs.TryGetValue(job.Job, out var previous);
            var growth = previous?.Growth.ToArray() ?? new int?[StatBlock.Count];
            var multiplier = previous?.Multiplier.ToArray() ?? new int?[StatBlock.Count];
            foreach (var i in Stats)
            {
                if (GrowthPercent != 100)
                    growth[i] = Override(job.Growth[i], StatTuningRules.ScaleGrowth(job.Growth[i], GrowthPercent));
                if (MultiplierPercent != 100)
                    multiplier[i] = Override(job.Multiplier[i], StatTuningRules.ScaleMultiplier(job.Multiplier[i], MultiplierPercent));
            }
            var rule = new JobTuning(growth, multiplier);
            if (rule.IsEmpty) jobs.Remove(job.Job);
            else jobs[job.Job] = rule;
        }
        return new(draft.ApplyJobsToEnemies, jobs, draft.Characters);
    }

    private static int? Override(int original, int scaled) => original == 0 || original == scaled ? null : scaled;
}
