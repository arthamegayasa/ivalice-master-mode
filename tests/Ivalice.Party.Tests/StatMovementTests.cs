using StatTuningStub = ivalice.party.stattuning.HookStub;

namespace Ivalice.Party.Tests;

/// <summary>Move and Jump: a job rule replaces the job's base value, a character rule adds to it.</summary>
public class StatMovementTests
{
    private const int Squire = 0x4A;
    private const string Someone = "0A1B2C3D";

    private static StatTuningRules Rules(string json) => StatTuningRules.Parse(json) ?? throw new InvalidOperationException("rules did not parse");

    private static (byte Move, byte Jump, bool Changed) Apply(StatTuningRules rules, byte move = 4, byte jump = 3,
        int job = Squire, bool playerOwned = true, string? fingerprint = null)
    {
        var changed = rules.ApplyMovement(ref move, ref jump, job, playerOwned, fingerprint);
        return (move, jump, changed);
    }

    [Fact]
    public void A_job_rule_replaces_the_base_move_and_jump_it_names()
    {
        Assert.Equal((6, 3, true), Apply(Rules("""{ "jobs": [ { "job": 74, "move": 6 } ] }""")));
        Assert.Equal((4, 5, true), Apply(Rules("""{ "jobs": [ { "job": 74, "jump": 5 } ] }""")));
    }

    [Fact]
    public void A_rule_that_only_sets_move_is_kept_and_leaves_growth_and_multipliers_alone()
    {
        var rules = Rules("""{ "jobs": [ { "job": 74, "move": 6 } ] }""");
        byte[] pairs = [11, 100, 15, 75, 100, 100, 60, 90, 50, 80];

        Assert.False(rules.IsEmpty);
        Assert.False(rules.Apply(pairs, Squire, playerOwned: true, fingerprint: null));
    }

    [Fact]
    public void Enemies_keep_the_games_move_and_jump_unless_the_player_asks_otherwise()
    {
        const string job = """ "jobs": [ { "job": 74, "move": 9, "jump": 6 } ] """;

        Assert.Equal((4, 3, false), Apply(Rules("{" + job + "}"), playerOwned: false));
        Assert.Equal((9, 6, true), Apply(Rules("""{ "applyJobsToEnemies": true, """ + job + "}"), playerOwned: false));
    }

    [Fact]
    public void A_character_bonus_is_added_to_what_the_job_gives_including_a_job_rule()
    {
        var rules = Rules($$"""
            { "jobs": [ { "job": 74, "move": 5 } ],
              "characters": [ { "fingerprint": "{{Someone}}", "moveBonus": 2, "jumpBonus": -1 } ] }
            """);

        Assert.Equal((7, 2, true), Apply(rules, fingerprint: Someone));
        Assert.Equal((5, 3, true), Apply(rules, fingerprint: "FFFFFFFF"));
        // In another job the bonus still applies, on that job's own values.
        Assert.Equal((5, 3, true), Apply(rules, move: 3, jump: 4, job: 0x4C, fingerprint: Someone));
    }

    [Fact]
    public void A_character_bonus_never_reaches_a_unit_that_is_not_the_players()
    {
        var rules = Rules($$"""{ "characters": [ { "fingerprint": "{{Someone}}", "moveBonus": 3 } ] }""");

        Assert.Equal((4, 3, false), Apply(rules, playerOwned: false, fingerprint: Someone));
    }

    [Fact]
    public void A_bonus_is_held_within_the_allowed_range_and_a_bonus_of_zero_changes_nothing()
    {
        var up = Rules($$"""{ "characters": [ { "fingerprint": "{{Someone}}", "moveBonus": 5, "jumpBonus": 5 } ] }""");
        var down = Rules($$"""{ "characters": [ { "fingerprint": "{{Someone}}", "moveBonus": -3, "jumpBonus": -3 } ] }""");
        var onlyJump = Rules($$"""{ "characters": [ { "fingerprint": "{{Someone}}", "jumpBonus": 1 } ] }""");

        Assert.Equal((StatTuningRules.MaxMove, StatTuningRules.MaxJump, true), Apply(up, move: 10, jump: 6, fingerprint: Someone));
        Assert.Equal((StatTuningRules.MinMove, StatTuningRules.MinJump, true), Apply(down, move: 2, jump: 2, fingerprint: Someone));
        // A base value outside the range is not pulled into it by a bonus of 0.
        Assert.Equal((0, 4, true), Apply(onlyJump, move: 0, jump: 3, fingerprint: Someone));
    }

    [Fact]
    public void Values_out_of_range_are_pulled_into_range_and_non_numbers_are_ignored()
    {
        var rules = Rules($$"""
            { "jobs": [ { "job": 74, "move": 99, "jump": 0 }, { "job": 75, "move": "fast", "jump": null }, { "job": 76, "move": 4.5 } ],
              "characters": [ { "fingerprint": "{{Someone}}", "moveBonus": 40, "jumpBonus": -40 } ] }
            """);

        var job = Assert.Single(rules.Jobs);
        Assert.Equal((StatTuningRules.MaxMove, StatTuningRules.MinJump), (job.Value.Move, job.Value.Jump));
        Assert.Equal((StatTuningRules.MaxBonus, StatTuningRules.MinBonus), (rules.Characters[Someone].MoveBonus, rules.Characters[Someone].JumpBonus));
    }

    [Fact]
    public void Move_and_jump_survive_being_written_and_read_again()
    {
        var rules = Rules($$"""
            { "jobs": [ { "job": 74, "growth": [8, null, null, null, null], "move": 5, "jump": 4 } ],
              "characters": [ { "fingerprint": "{{Someone}}", "name": "Ramza", "moveBonus": 1, "jumpBonus": -2 } ] }
            """);

        var again = Rules(rules.Serialize());

        Assert.Equal((5, 4, 8), (again.Jobs[74].Move, again.Jobs[74].Jump, again.Jobs[74].Growth[0]));
        Assert.Equal((1, -2), (again.Characters[Someone].MoveBonus, again.Characters[Someone].JumpBonus));
    }

    [Fact]
    public void Rules_written_before_move_and_jump_existed_still_read_the_same()
    {
        var rules = Rules("""{ "schema": 1, "jobs": [ { "job": 74, "growth": [8, null, null, null, null], "multiplier": [null, null, null, null, null] } ], "characters": [] }""");

        Assert.Null(rules.Jobs[74].Move);
        Assert.Null(rules.Jobs[74].Jump);
        Assert.Equal((4, 3, false), Apply(rules));
    }

    [Fact]
    public void The_job_catalog_lists_move_and_jump_without_the_jump_bytes_flag_bit()
    {
        var table = new byte[JobTable.TableSize];
        table[Squire * JobTable.RowSize + JobTable.MoveOffset] = 4;
        table[Squire * JobTable.RowSize + JobTable.JumpOffset] = 0x83;

        using var document = System.Text.Json.JsonDocument.Parse(JobCatalog.Describe(table));
        var squire = document.RootElement.GetProperty("jobs")[Squire];

        Assert.Equal(4, squire.GetProperty("move").GetInt32());
        Assert.Equal(3, squire.GetProperty("jump").GetInt32());
    }

    // ---- presets ----

    private static readonly JobDefaults[] Catalog =
    [
        new(0x4A, [11, 15, 100, 60, 50], [100, 75, 100, 90, 80]) { Move = 4, Jump = 3 },
        new(0x4C, [10, 15, 100, 40, 50], [120, 80, 100, 120, 80]) { Move = 3, Jump = 3 },
        new(0x05, [10, 11, 100, 40, 50], [140, 100, 100, 100, 100]) { Move = 4, Jump = 3 },
        new(0x5E, [8, 30, 75, 35, 7], [108, 100, 119, 98, 94]) { Move = 6, Jump = 5 },
        new(0x4D, [11, 16, 100, 45, 50], [100, 65, 100, 110, 80]),
    ];

    [Fact]
    public void A_preset_adds_to_each_jobs_own_move_within_its_scope_and_leaves_monsters_alone()
    {
        var preset = new StatPreset(PresetScope.Generic, [], 100, 100) { MoveBonus = 1 };

        var rules = preset.Apply(StatTuningRules.Empty, Catalog);

        Assert.Equal(5, rules.Jobs[0x4A].Move);
        Assert.Equal(4, rules.Jobs[0x4C].Move);
        Assert.Null(rules.Jobs[0x4A].Jump);
        Assert.False(rules.Jobs.ContainsKey(0x05));
        Assert.False(rules.Jobs.ContainsKey(0x5E));
        // A job whose Move the catalog does not know is left alone.
        Assert.False(rules.Jobs.ContainsKey(0x4D));
    }

    [Fact]
    public void A_preset_keeps_what_the_draft_already_says_about_things_it_does_not_touch()
    {
        var draft = Rules("""{ "jobs": [ { "job": 74, "growth": [8, null, null, null, null], "move": 7, "jump": 5 } ] }""");

        var onlyStats = new StatPreset(PresetScope.Generic, [1], 100, 120).Apply(draft, Catalog);
        var onlyJump = new StatPreset(PresetScope.Generic, [], 100, 100) { JumpBonus = 1 }.Apply(draft, Catalog);

        Assert.Equal((7, 5, 8, 90), (onlyStats.Jobs[74].Move, onlyStats.Jobs[74].Jump, onlyStats.Jobs[74].Growth[0], onlyStats.Jobs[74].Multiplier[1]));
        Assert.Equal((7, 4, 8), (onlyJump.Jobs[74].Move, onlyJump.Jobs[74].Jump, onlyJump.Jobs[74].Growth[0]));
    }

    [Fact]
    public void A_preset_with_no_stats_and_no_bonus_or_a_bonus_out_of_range_is_refused()
    {
        Assert.Throws<ArgumentException>(() => new StatPreset(PresetScope.Generic, [], 100, 100).Apply(StatTuningRules.Empty, Catalog));
        Assert.Throws<ArgumentException>(() => new StatPreset(PresetScope.Generic, [], 100, 100) { MoveBonus = 9 }.Apply(StatTuningRules.Empty, Catalog));
    }

    // ---- hook stub ----

    [Theory]
    [InlineData("rbx")]
    [InlineData("rdi")]
    public void The_stub_passes_the_register_that_holds_the_unit(string register)
    {
        var lines = StatTuningStub.Build("call managed", atFunctionEntry: false, register);

        Assert.Equal($"mov rcx, {register}", lines[Array.IndexOf(lines, "call managed") - 1]);
    }
}
