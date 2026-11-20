namespace Ivalice.Party;

/// <summary>
/// A job's growth constants and multipliers, one pair per stat. A level-up adds raw / (growth + level)
/// to each raw stat; the number the game shows is raw x multiplier / 100, in units of 16384.
/// </summary>
public readonly record struct JobGrowth(StatBlock Growth, StatBlock Multiplier)
{
    private static readonly StatBlock Caps = new(999, 999, 50, 99, 99);

    /// <summary>The stat values the game shows for these raw stats, before equipment.</summary>
    public StatBlock Shown(StatBlock raw)
    {
        var multiplier = Multiplier;
        return StatBlock.From(i =>
        {
            var shown = (int)(((long)raw[i] * multiplier[i] / 100) >> 14);
            return Math.Clamp(shown, 1, Caps[i]);
        });
    }
}

/// <summary>The game's job table: 176 rows of 0x31 bytes, with the growth and multiplier pairs at +0x0E.</summary>
public static class JobTable
{
    public const int Rows = 176;
    public const int RowSize = 0x31;
    public const int TableSize = Rows * RowSize;
    public const int GrowthOffset = 0x0E;

    private const byte SpecialJob = 0xA1;
    private const byte SpecialJobUpgradedRow = 0xA4;
    /// <summary>Roster byte whose low nibble is the job level of job index 21.</summary>
    private const int SpecialJobLevelByte = RosterLayout.JobLevels + 10;

    /// <summary>
    /// The table row the game takes a unit's growth from. It is the job id, except that job 0xA1 uses
    /// row 0xA4 once its own job level reaches 8 (the game's level-up routine at RVA 0x278B39).
    /// </summary>
    public static int RowOf(ReadOnlySpan<byte> record)
    {
        var job = record[RosterLayout.Job];
        return job == SpecialJob && (record[SpecialJobLevelByte] & 0x0F) >= 8 ? SpecialJobUpgradedRow : job;
    }

    /// <returns>The growth data of the unit's job, or null when the table is missing or the job is outside it.</returns>
    public static JobGrowth? GrowthOf(ReadOnlySpan<byte> jobTable, ReadOnlySpan<byte> record)
    {
        var row = RowOf(record);
        var start = row * RowSize + GrowthOffset;
        if (row >= Rows || jobTable.Length < start + StatBlock.Count * 2) return null;

        var pairs = jobTable.Slice(start, StatBlock.Count * 2);
        return new JobGrowth(
            new StatBlock(pairs[0], pairs[2], pairs[4], pairs[6], pairs[8]),
            new StatBlock(pairs[1], pairs[3], pairs[5], pairs[7], pairs[9]));
    }
}
