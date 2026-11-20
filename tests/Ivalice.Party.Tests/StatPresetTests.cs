namespace Ivalice.Party.Tests;

public class StatPresetTests
{
    private static JobDefaults Job(int id, int hp = 11) => new(id, [hp, 0, 1, 250, 12], [75, 0, 2, 250, 200]);
    private static readonly JobDefaults[] Jobs = [Job(1), Job(0x49), Job(0x4A), Job(0x5D, 21), Job(0x5E), Job(0xA1)];
    private static readonly StatTuningRules Draft = StatTuningRules.Parse("""
        { "applyJobsToEnemies":true, "jobs":[{"job":74,"growth":[99,14,12,null,6],"multiplier":[33,17,14,null,7]},
          {"job":94,"growth":[1]}], "characters":[{"fingerprint":"AABBCCDD","growthPercent":[120]}] }
        """)!;

    [Theory]
    [InlineData(PresetScope.Generic, new int[] { 74, 93 })]
    [InlineData(PresetScope.Special, new int[] { 1, 73 })]
    [InlineData(PresetScope.Both, new int[] { 1, 73, 74, 93 })]
    public void Only_the_requested_human_job_groups_get_rules(PresetScope scope, int[] expected)
    {
        var result = new StatPreset(scope, [0], 200, 100).Apply(StatTuningRules.Empty, Jobs);
        Assert.Equal(expected, result.Jobs.Keys.Order());
    }

    [Fact]
    public void A_preset_scales_each_jobs_own_values_and_preserves_everything_outside_the_selection()
    {
        var preset = new StatPreset(PresetScope.Generic, [0, 1, 2, 3], 200, 150);
        var result = preset.Apply(Draft, Jobs);
        Assert.Equal(new int?[] { 6, null, null, 125, 6 }, result.Jobs[74].Growth);
        Assert.Equal(new int?[] { 113, null, 3, 255, 7 }, result.Jobs[74].Multiplier);
        Assert.Equal(11, result.Jobs[93].Growth[0]);
        Assert.Equal(99, Draft.Jobs[74].Growth[0]);
        Assert.Equal(result.Serialize(), preset.Apply(result, Jobs).Serialize());
        Assert.Same(Draft.Jobs[94], result.Jobs[94]);
        Assert.Same(Draft.Characters, result.Characters);
        Assert.True(result.ApplyJobsToEnemies);
    }

    [Fact]
    public void A_neutral_percentage_preserves_edits_in_that_column()
    {
        var result = new StatPreset(PresetScope.Generic, [0], 100, 25).Apply(Draft, Jobs);
        Assert.Equal(99, result.Jobs[74].Growth[0]);
        Assert.Equal(19, result.Jobs[74].Multiplier[0]);
    }

    [Fact]
    public void A_rule_disappears_when_the_rounded_or_clamped_value_equals_the_game_byte()
    {
        var rules = StatTuningRules.Parse("""{"jobs":[{"job":74,"growth":[null,null,6],"multiplier":[null,null,8]}]}""")!;
        Assert.Empty(new StatPreset(PresetScope.Generic, [2], 400, 101).Apply(rules, [Job(74)]).Jobs);
    }

    [Fact]
    public void Slow_growth_and_small_multipliers_stay_within_one_byte()
    {
        var result = new StatPreset(PresetScope.Generic, [2, 3], 25, 25).Apply(Draft, Jobs);
        Assert.Equal(255, result.Jobs[74].Growth[3]);
        Assert.Equal(1, result.Jobs[74].Multiplier[2]);
    }

    [Theory]
    [InlineData(-1, 100, 110)]
    [InlineData(0, 24, 100)]
    [InlineData(0, 100, 401)]
    public void Invalid_custom_values_cannot_replace_draft_rules(int stat, int growth, int multiplier)
    {
        var preset = new StatPreset(PresetScope.Generic, [stat], growth, multiplier);
        Assert.Throws<ArgumentException>(() => preset.Apply(Draft, Jobs));
    }

    private static RosterDocument Roster(params byte[][] records) => new(1, "test", 1, "", false, Fake.WorldMap,
        RosterExport.DescribeAll(Fake.Roster(records), Fake.QuietBattleTable(), Fake.JobTable(), Fake.WorldMap));

    [Fact]
    public void Both_stat_blocks_use_their_own_raw_stats_and_the_characters_current_job()
    {
        var roster = Roster(Fake.Unit(), Fake.Unit(slot: 2, nameKey: 900), Fake.Monster(slot: 3));
        var unit = roster.Units[0];
        var rules = StatTuningRules.Parse($$"""
            {"jobs":[{"job":74,"multiplier":[120]},{"job":94,"multiplier":[200]}],
             "characters":[{"fingerprint":"{{unit.Fingerprint}}","multiplierPercent":[150]}]}
            """)!;
        var jobs = Jobs.ToDictionary(j => j.Job);
        var result = TunedRoster.Apply(roster, rules, jobs);
        var first = result.Units[0];
        Assert.Equal(55, first.Shown!.Value.Hp); // 31 x 180%
        Assert.Equal(64, first.Next!.Shown!.Value.Hp); // 31 x 115% Power x 180%
        Assert.Equal(37, result.Units[1].Shown!.Value.Hp); // Same job, different fingerprint.
        Assert.Equal(42, result.Units[1].Next!.Shown!.Value.Hp);
        Assert.Null(result.Units[2].Next);
        Assert.Equal(unit, first with { Shown = unit.Shown, Next = first.Next with { Shown = unit.Next!.Shown } });
    }

    [Fact]
    public void Absent_rules_unknown_jobs_and_job_A1_keep_the_exact_feed()
    {
        var roster = Roster(Fake.Unit(job: 0xA1), Fake.Unit(slot: 2, job: 0x77));
        var rules = StatTuningRules.Parse("""{"jobs":[{"job":161,"multiplier":[200]},{"job":119,"multiplier":[200]}]}""")!;
        Assert.Same(roster, TunedRoster.Apply(roster, rules, Jobs.ToDictionary(j => j.Job)));
        var known = Roster(Fake.Unit());
        Assert.Same(known, TunedRoster.Apply(known, null, Jobs.ToDictionary(j => j.Job)));
        Assert.Same(known, TunedRoster.Apply(known, StatTuningRules.Empty, Jobs.ToDictionary(j => j.Job)));
        Assert.Same(known, TunedRoster.Apply(known, Draft, new Dictionary<int, JobDefaults>()));
        Assert.Same(known, TunedRoster.Apply(known, rules, Jobs.ToDictionary(j => j.Job)));
    }
}
