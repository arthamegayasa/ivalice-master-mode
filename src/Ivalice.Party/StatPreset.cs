namespace Ivalice.Party;

/// <summary>A job row from the catalog written by the game mod.</summary>
public sealed record JobDefaults(int Job, int[] Growth, int[] Multiplier)
{
    /// <summary>The job's base Move and Jump; 0 when the catalog was written by a mod version that did not record them.</summary>
    public int Move { get; init; }
    public int Jump { get; init; }

    public string Group => Job < 0x4A ? "special" : Job <= 0x5D ? "generic" : "monster";
    public string? Name => JobNames.Find(Job) ?? (Job is 0xA1 or 0xA4 ? "Job tanpa nama" : null);
}

public enum PresetScope { Generic, Special, Both }

/// <summary>A missing percentage leaves that column alone. An explicit percentage scales the job default.</summary>
public sealed record StatPreset(PresetScope Scope, int[] Stats, int? GrowthPercent, int? MultiplierPercent)
{
    /// <summary>Added to each job's own base Move and Jump; 0 leaves the draft's value alone.</summary>
    public int MoveBonus { get; init; }
    public int JumpBonus { get; init; }

    public StatTuningRules Apply(StatTuningRules draft, IEnumerable<JobDefaults> catalog)
    {
        if (!Enum.IsDefined(Scope) || Stats is null || Stats.Any(i => i < 0 || i >= StatBlock.Count)
            || (Stats.Length == 0 && MoveBonus == 0 && JumpBonus == 0))
            throw new ArgumentException("Pilihan job atau stat tidak sah.");
        if (MoveBonus is < StatTuningRules.MinBonus or > StatTuningRules.MaxBonus ||
            JumpBonus is < StatTuningRules.MinBonus or > StatTuningRules.MaxBonus)
            throw new ArgumentException("Tambahan Move dan Jump harus antara -3 dan 5.");
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
                if (GrowthPercent is int growthPercent)
                    growth[i] = Override(job.Growth[i], StatTuningRules.ScaleGrowth(job.Growth[i], growthPercent));
                if (MultiplierPercent is int multiplierPercent)
                    multiplier[i] = Override(job.Multiplier[i], StatTuningRules.ScaleMultiplier(job.Multiplier[i], multiplierPercent));
            }
            // A job whose own Move or Jump is 0 (or unknown) is left alone, like a growth or multiplier of 0.
            var move = MoveBonus != 0 && job.Move > 0
                ? Override(job.Move, Math.Clamp(job.Move + MoveBonus, StatTuningRules.MinMove, StatTuningRules.MaxMove))
                : previous?.Move;
            var jump = JumpBonus != 0 && job.Jump > 0
                ? Override(job.Jump, Math.Clamp(job.Jump + JumpBonus, StatTuningRules.MinJump, StatTuningRules.MaxJump))
                : previous?.Jump;
            var rule = new JobTuning(growth, multiplier) { Move = move, Jump = jump };
            if (rule.IsEmpty) jobs.Remove(job.Job);
            else jobs[job.Job] = rule;
        }
        return new(draft.ApplyJobsToEnemies, jobs, draft.Characters, draft.PlayerLimits);
    }

    private static int? Override(int original, int scaled) => original == 0 || original == scaled ? null : scaled;
}
