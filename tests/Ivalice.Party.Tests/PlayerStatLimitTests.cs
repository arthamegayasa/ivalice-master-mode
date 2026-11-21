using System.Text.Json;

namespace Ivalice.Party.Tests;

public class PlayerStatLimitTests
{
    [Fact]
    public void The_preview_does_not_report_adjusted_stats_when_every_value_is_below_the_original_limit()
    {
        var units = RosterExport.DescribeAll(Fake.Roster(Fake.Unit()), Fake.QuietBattleTable(), Fake.JobTable(), Fake.WorldMap);
        var roster = new RosterDocument(1, "test", 1, "", false, Fake.WorldMap, units);
        var job = new JobDefaults(Fake.Squire, [11, 15, 100, 60, 50], [100, 75, 100, 90, 80]);
        var rules = StatTuningRules.Parse("""{"playerLimits":{"hp":true,"mp":true,"pa":true,"ma":true,"speed":true}}""");
        Assert.Same(roster, TunedRoster.Apply(roster, rules, new Dictionary<int, JobDefaults> { [job.Job] = job }, playerLimitsReady: true));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"schema":1,"jobs":[],"characters":[]}""")]
    [InlineData("""{"playerLimits":{}}""")]
    public void Existing_rules_keep_the_vanilla_limits(string json)
    {
        var rules = StatTuningRules.Parse(json)!;
        Assert.Equal(PlayerStatLimits.Default, rules.PlayerLimits);
        Assert.True(rules.IsEmpty);
    }

    [Theory]
    [InlineData("""{"playerLimits":[]}""")]
    [InlineData("""{"playerLimits":{"hp":"true"}}""")]
    public void Invalid_limit_settings_are_refused_as_a_whole(string json)
    {
        Assert.Null(StatTuningRules.Parse(json));
    }

    [Fact]
    public void Only_values_above_enabled_caps_change_and_guests_keep_their_original_stats()
    {
        var player = Fake.Unit();
        var guest = Fake.Unit(slot: RosterLayout.PartySlots);
        var raw = new StatBlock(0xFFFFFF, 0xFFFFFF, 0xFFFFFF, 0xFFFFFF, 0xFFFFFF);
        RosterRecord.WriteRawStats(player, raw);
        RosterRecord.WriteRawStats(guest, raw);
        var units = RosterExport.DescribeAll(Fake.Roster(player, guest), Fake.QuietBattleTable(), Fake.JobTable(), Fake.WorldMap);
        var roster = new RosterDocument(1, "test", 1, "", false, Fake.WorldMap, units);
        var job = new JobDefaults(Fake.Squire, [11, 15, 100, 60, 50], [100, 75, 100, 90, 80]);
        var rules = StatTuningRules.Parse("""{"playerLimits":{"hp":true,"pa":true,"ma":true,"speed":true}}""");

        var jobs = new Dictionary<int, JobDefaults> { [Fake.Squire] = job };
        Assert.Same(roster, TunedRoster.Apply(roster, rules, jobs));
        var adjusted = TunedRoster.Apply(roster, rules, jobs, playerLimitsReady: true);
        Assert.Equal(new StatBlock(1023, 767, 99, 255, 255), adjusted.Units[0].Shown);
        Assert.Equal(units[0].Next!.Shown, adjusted.Units[0].Next!.Shown);
        Assert.Equal(raw, adjusted.Units[0].Raw);
        Assert.Equal(units[0].Next!.Raw, adjusted.Units[0].Next!.Raw);
        Assert.Same(units[1], adjusted.Units[1]);
    }

    [Fact]
    public void A_saved_player_limit_is_active_without_job_rules_and_survives_a_job_preset()
    {
        var rules = StatTuningRules.Parse("""{"playerLimits":{"hp":true,"speed":true}}""")!;
        Assert.False(rules.IsEmpty);

        var preset = new StatPreset(PresetScope.Generic, [0], 150, 100);
        var saved = preset.Apply(rules, []).Serialize();
        using var json = JsonDocument.Parse(saved);
        var limits = json.RootElement.GetProperty("playerLimits");
        Assert.True(limits.GetProperty("hp").GetBoolean());
        Assert.False(limits.GetProperty("mp").GetBoolean());
        Assert.True(limits.GetProperty("speed").GetBoolean());
        Assert.Equal(5, limits.EnumerateObject().Count());
        Assert.False(StatTuningRules.Parse(saved)!.IsEmpty);
    }

    [Fact]
    public void Raising_caps_adds_no_power_to_existing_growth_or_multiplier_values()
    {
        var limits = new PlayerStatLimits(Hp: true, Mp: true, Pa: true, Ma: true, Speed: true);
        // At the highest legal raw stat and multiplier byte, natural HP and MP stay below 9999.
        var job = new JobGrowth(default, new(255, 255, 100, 100, 100));
        var raw = new StatBlock(0xFFFFFF, 0xFFFFFF, 0, 0, 0);
        Assert.Equal(new StatBlock(2611, 2611, 1, 1, 1), job.Shown(raw, limits));
        Assert.Equal(new StatBlock(999, 999, 1, 1, 1), job.Shown(raw));
    }

    [Theory]
    [InlineData("hp", 0, 9999)]
    [InlineData("mp", 1, 9999)]
    [InlineData("speed", 2, 99)]
    [InlineData("pa", 3, 255)]
    [InlineData("ma", 4, 255)]
    public void Each_switch_changes_only_its_own_ceiling(string field, int statIndex, int expected)
    {
        var limits = StatTuningRules.Parse($$$"""{"playerLimits":{"{{{field}}}":true}}""")!.PlayerLimits;
        for (var i = 0; i < StatBlock.Count; i++)
        {
            Assert.Equal(i == statIndex ? expected : 42, limits.CapFor(i, 42));
        }
    }
}
