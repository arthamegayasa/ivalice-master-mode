namespace Ivalice.Party.Tests;

public class StatTuningTests
{
    // Squire's own values in the game: growth, multiplier for HP, MP, Speed, PA, MA.
    private static byte[] SquirePairs() => [11, 100, 15, 75, 100, 100, 60, 90, 50, 80];

    private const int Squire = 0x4A;
    private const string Someone = "0A1B2C3D";

    private static StatTuningRules Rules(string json) => StatTuningRules.Parse(json) ?? throw new InvalidOperationException("rules did not parse");

    [Fact]
    public void A_job_rule_replaces_only_the_values_it_names()
    {
        var rules = Rules("""{ "jobs": [ { "job": 74, "growth": [8, null, null, null, null], "multiplier": [null, null, null, 120, null] } ] }""");
        var pairs = SquirePairs();

        Assert.True(rules.Apply(pairs, Squire, playerOwned: true, fingerprint: null));

        Assert.Equal(new byte[] { 8, 100, 15, 75, 100, 100, 60, 120, 50, 80 }, pairs);
    }

    [Fact]
    public void Enemies_keep_the_games_values_unless_the_player_asks_otherwise()
    {
        const string job = """ "jobs": [ { "job": 74, "multiplier": [200, 200, 200, 200, 200] } ] """;
        var enemy = SquirePairs();

        Assert.False(Rules("{" + job + "}").Apply(enemy, Squire, playerOwned: false, fingerprint: null));
        Assert.Equal(SquirePairs(), enemy);

        Assert.True(Rules("""{ "applyJobsToEnemies": true, """ + job + "}").Apply(enemy, Squire, playerOwned: false, fingerprint: null));
        Assert.Equal(200, enemy[1]);
    }

    [Fact]
    public void Another_job_is_left_alone()
    {
        var rules = Rules("""{ "jobs": [ { "job": 75, "multiplier": [200, 200, 200, 200, 200] } ] }""");
        var pairs = SquirePairs();

        Assert.False(rules.Apply(pairs, Squire, playerOwned: true, fingerprint: null));
        Assert.Equal(SquirePairs(), pairs);
    }

    [Fact]
    public void A_character_rule_scales_what_the_job_gives()
    {
        var rules = Rules($$"""{ "characters": [ { "fingerprint": "{{Someone}}", "growthPercent": [200, 100, 100, 100, 50], "multiplierPercent": [150, 100, 100, 100, 100] } ] }""");
        var pairs = SquirePairs();

        Assert.True(rules.Apply(pairs, Squire, playerOwned: true, fingerprint: Someone));

        // HP grows twice as fast (11 -> 6, rounded) and shows 150%; MA grows half as fast (50 -> 100).
        Assert.Equal(new byte[] { 6, 150, 15, 75, 100, 100, 60, 90, 100, 80 }, pairs);
    }

    [Fact]
    public void A_character_rule_stacks_on_a_job_rule_and_stays_within_a_byte()
    {
        var rules = Rules($$"""
            { "jobs": [ { "job": 74, "multiplier": [200, null, null, null, null], "growth": [2, null, null, null, null] } ],
              "characters": [ { "fingerprint": "{{Someone}}", "growthPercent": [400, 100, 100, 100, 100], "multiplierPercent": [400, 100, 100, 100, 100] } ] }
            """);
        var pairs = SquirePairs();

        rules.Apply(pairs, Squire, playerOwned: true, fingerprint: Someone);

        Assert.Equal(1, pairs[0]);   // 2 / 4 rounds to 1 and never reaches 0
        Assert.Equal(255, pairs[1]); // 200 x 4 is held at the largest value a byte takes
    }

    [Fact]
    public void A_character_rule_never_applies_to_a_unit_that_is_not_the_players_or_is_someone_else()
    {
        var rules = Rules($$"""{ "characters": [ { "fingerprint": "{{Someone}}", "multiplierPercent": [300, 300, 300, 300, 300] } ] }""");
        var pairs = SquirePairs();

        Assert.False(rules.Apply(pairs, Squire, playerOwned: false, fingerprint: Someone));
        Assert.False(rules.Apply(pairs, Squire, playerOwned: true, fingerprint: "FFFFFFFF"));
        Assert.False(rules.Apply(pairs, Squire, playerOwned: true, fingerprint: null));
        Assert.Equal(SquirePairs(), pairs);
    }

    [Fact]
    public void A_stat_left_at_100_percent_keeps_the_jobs_byte_even_when_that_is_zero()
    {
        // Some jobs have a multiplier of 0 for a stat they do not use.
        byte[] pairs = [11, 100, 0, 0, 100, 100, 60, 90, 0, 0];
        var before = pairs.ToArray();
        var rules = Rules($$"""{ "characters": [ { "fingerprint": "{{Someone}}", "growthPercent": [200, 100, 100, 100, 100] } ] }""");

        rules.Apply(pairs, Squire, playerOwned: true, fingerprint: Someone);

        Assert.Equal(6, pairs[0]);
        Assert.Equal(before[1..], pairs[1..]);
    }

    [Theory]
    [InlineData("""{ "schema": 1, "jobs": {} }""")]
    [InlineData("""{ "characters": "none" }""")]
    public void A_list_of_the_wrong_shape_is_not_read_as_no_rules(string text) => Assert.Null(StatTuningRules.Parse(text));

    [Fact]
    public void Empty_lists_are_a_valid_way_to_have_no_rules()
    {
        Assert.True(Rules("""{ "schema": 1, "jobs": [], "characters": [] }""").IsEmpty);
    }

    [Fact]
    public void One_bad_entry_does_not_cost_the_good_ones()
    {
        var rules = Rules("""{ "jobs": [ { "job": 74, "growth": [8] }, { "job": "x", "growth": [1] }, { "job": 7.5, "growth": [1] } ] }""");

        Assert.Equal(74, Assert.Single(rules.Jobs).Key);
    }

    [Fact]
    public void Text_that_is_not_valid_unicode_gives_no_rules_instead_of_an_error()
    {
        Assert.Null(StatTuningRules.Parse("{ \"jobs\": [ \"" + '\uD800' + "\" ] }"));
    }

    [Fact]
    public void Fingerprints_match_whatever_their_letter_case()
    {
        var rules = Rules("""{ "characters": [ { "fingerprint": "0a1b2c3d", "multiplierPercent": [200, 100, 100, 100, 100] } ] }""");
        var pairs = SquirePairs();

        Assert.True(rules.Apply(pairs, Squire, playerOwned: true, fingerprint: "0A1B2C3D"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1, 2]")]
    [InlineData("""{ "schema": 99 }""")]
    public void Unusable_text_gives_no_rules(string? text) => Assert.Null(StatTuningRules.Parse(text));

    [Fact]
    public void Values_out_of_range_are_pulled_into_range_and_bad_entries_are_dropped()
    {
        var rules = Rules("""
            { "jobs": [ { "job": 74, "growth": [0, 999, "x"], "multiplier": [] }, { "job": 9999, "growth": [1] }, { "job": 75 }, 5 ],
              "characters": [ { "fingerprint": "XYZ", "multiplierPercent": [200] }, { "fingerprint": "0A1B2C3D", "growthPercent": [1, 9999] } ] }
            """);

        var job = Assert.Single(rules.Jobs);
        Assert.Equal(74, job.Key);
        Assert.Equal(new int?[] { 1, 255, null, null, null }, job.Value.Growth);

        var character = Assert.Single(rules.Characters);
        Assert.Equal(new[] { 25, 400, 100, 100, 100 }, character.Value.GrowthPercent);
    }

    [Fact]
    public void Rules_that_change_nothing_are_not_kept()
    {
        var rules = Rules("""{ "jobs": [ { "job": 74, "growth": [null] } ], "characters": [ { "fingerprint": "0A1B2C3D", "growthPercent": [100, 100, 100, 100, 100] } ] }""");

        Assert.True(rules.IsEmpty);
    }

    [Fact]
    public void Rules_survive_being_written_and_read_again()
    {
        var rules = Rules($$"""
            { "applyJobsToEnemies": true,
              "jobs": [ { "job": 74, "growth": [8, null, 90, null, null], "multiplier": [110, null, null, 120, null] } ],
              "characters": [ { "fingerprint": "{{Someone}}", "name": "Ramza", "growthPercent": [150, 100, 100, 100, 100], "multiplierPercent": [100, 100, 125, 100, 100] } ] }
            """);

        var again = Rules(rules.Serialize());

        Assert.True(again.ApplyJobsToEnemies);
        Assert.Equal(rules.Jobs[74].Growth, again.Jobs[74].Growth);
        Assert.Equal(rules.Jobs[74].Multiplier, again.Jobs[74].Multiplier);
        Assert.Equal("Ramza", again.Characters[Someone].Name);
        Assert.Equal(rules.Characters[Someone].GrowthPercent, again.Characters[Someone].GrowthPercent);
        Assert.Equal(rules.Characters[Someone].MultiplierPercent, again.Characters[Someone].MultiplierPercent);
    }

    // ---- which rules a battle unit gets ----

    private static byte[] BattleUnit(byte marker, byte rosterSlot, byte job, byte flags = 0)
    {
        var unit = new byte[BattleBinding.Stride];
        unit[0x00] = marker;
        unit[0x02] = rosterSlot;
        unit[0x03] = job;
        unit[0x05] = flags;
        return unit;
    }

    [Fact]
    public void A_party_member_in_battle_is_recognised_by_its_roster_record()
    {
        var record = Fake.Unit(slot: 3);
        var roster = Fake.Roster(record);
        var unit = BattleUnit(record[RosterLayout.Marker], rosterSlot: 3, job: Squire);

        var identity = TunedUnit.Identify(unit, roster);

        Assert.True(identity.PlayerOwned);
        Assert.True(TunedUnit.IsPartyMember(unit, roster));
        Assert.Equal(Squire, identity.JobRow);
        Assert.Equal(StampCodec.FingerprintText(record), identity.Fingerprint);
    }

    [Fact]
    public void An_enemy_is_not_the_players_and_has_no_fingerprint_even_when_its_slot_number_is_in_use()
    {
        var record = Fake.Unit(slot: 3);
        var unit = BattleUnit(record[RosterLayout.Marker], rosterSlot: 3, job: Squire, flags: 0x10);

        var identity = TunedUnit.Identify(unit, Fake.Roster(record));

        Assert.False(identity.PlayerOwned);
        Assert.False(TunedUnit.IsPartyMember(unit, Fake.Roster(record)));
        Assert.Null(identity.Fingerprint);
    }

    [Fact]
    public void A_guest_has_a_valid_identity_but_is_excluded_from_player_stat_limits()
    {
        var record = Fake.Unit(slot: RosterLayout.PartySlots);
        var unit = BattleUnit(record[RosterLayout.Marker], RosterLayout.PartySlots, Squire);
        var identity = TunedUnit.Identify(unit, Fake.Roster(record));
        Assert.True(identity.PlayerOwned);
        Assert.NotNull(identity.Fingerprint);
        Assert.False(TunedUnit.IsPartyMember(unit, Fake.Roster(record)));
    }

    [Fact]
    public void A_unit_whose_slot_holds_someone_else_gets_job_rules_only()
    {
        var record = Fake.Unit(slot: 3);
        var other = BattleUnit((byte)(record[RosterLayout.Marker] ^ 0x01), rosterSlot: 3, job: Squire);
        var outside = BattleUnit(record[RosterLayout.Marker], rosterSlot: 0xFF, job: Squire);

        Assert.Null(TunedUnit.Identify(other, Fake.Roster(record)).Fingerprint);
        Assert.Null(TunedUnit.Identify(outside, Fake.Roster(record)).Fingerprint);
        Assert.Null(TunedUnit.Identify(BattleUnit(record[RosterLayout.Marker], 3, Squire), ReadOnlySpan<byte>.Empty).Fingerprint);
        Assert.False(TunedUnit.IsPartyMember(other, Fake.Roster(record)));
        Assert.False(TunedUnit.IsPartyMember(outside, Fake.Roster(record)));
        Assert.False(TunedUnit.IsPartyMember(ReadOnlySpan<byte>.Empty, Fake.Roster(record)));
        Assert.False(TunedUnit.IsPartyMember(BattleUnit(record[RosterLayout.Marker], 3, Squire), ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void The_special_job_uses_its_upgraded_row_from_job_level_8()
    {
        var unit = BattleUnit(0x80, 0xFF, 0xA1);
        Assert.Equal(0xA1, TunedUnit.Identify(unit, ReadOnlySpan<byte>.Empty).JobRow);

        unit[0xEE] = 0x38;
        Assert.Equal(0xA4, TunedUnit.Identify(unit, ReadOnlySpan<byte>.Empty).JobRow);
    }

    [Fact]
    public void The_job_catalog_lists_every_row_with_its_pairs()
    {
        var table = new byte[JobTable.TableSize];
        SquirePairs().CopyTo(table, Squire * JobTable.RowSize + JobTable.GrowthOffset);

        using var document = System.Text.Json.JsonDocument.Parse(JobCatalog.Describe(table));
        var jobs = document.RootElement.GetProperty("jobs");

        Assert.Equal(JobTable.Rows, jobs.GetArrayLength());
        var squire = jobs[Squire];
        Assert.Equal(Squire, squire.GetProperty("job").GetInt32());
        Assert.Equal(new[] { 11, 15, 100, 60, 50 }, squire.GetProperty("growth").EnumerateArray().Select(v => v.GetInt32()).ToArray());
        Assert.Equal(new[] { 100, 75, 100, 90, 80 }, squire.GetProperty("multiplier").EnumerateArray().Select(v => v.GetInt32()).ToArray());
    }
}
