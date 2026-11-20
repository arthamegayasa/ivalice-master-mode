using Ivalice.Party;

namespace Ivalice.Party.Tests;

public class GameSafetyTests
{
    private static SafetyInputs Inputs(bool battleControl = false, bool? activeActor = false) =>
        new(battleControl, activeActor, BattleUnitsInUse: 0, BattleUnitsBound: 0, BattleModeFlag: 0, BattleState: null);

    [Fact]
    public void A_battle_is_in_progress_while_the_battle_controller_exists()
    {
        var status = GameSafety.Decide(Inputs(battleControl: true));

        Assert.True(status.InBattle);
        Assert.Equal("battleControlPresent", status.DecidedBy);
    }

    [Fact]
    public void No_battle_is_in_progress_once_the_battle_controller_is_gone()
    {
        var status = GameSafety.Decide(Inputs());

        Assert.False(status.InBattle);
        Assert.Equal("battleControlAbsent", status.DecidedBy);
    }

    [Fact]
    public void A_unit_taking_its_turn_counts_as_a_battle_even_without_the_controller()
    {
        var status = GameSafety.Decide(Inputs(activeActor: true));

        Assert.True(status.InBattle);
        Assert.Equal("activeActorPresent", status.DecidedBy);
    }

    [Fact]
    public void Not_knowing_whether_a_unit_is_acting_does_not_by_itself_mean_a_battle()
    {
        Assert.False(GameSafety.Decide(Inputs(activeActor: null)).InBattle);
    }

    [Fact]
    public void Stale_battle_records_do_not_by_themselves_mean_a_battle()
    {
        // The game does not clear its battle unit table when a battle ends.
        var status = GameSafety.Decide(new SafetyInputs(false, false, BattleUnitsInUse: 12, BattleUnitsBound: 0, 0, 3));

        Assert.False(status.InBattle);
    }

    [Fact]
    public void A_freshly_started_game_ties_no_roster_slot_to_a_battle_unit()
    {
        // Every record is zero, so every record names roster slot 0; none has a unit id.
        Assert.Equal(-1, BattleBinding.Find(Fake.QuietBattleTable(), 0));
        Assert.Equal(0, BattleBinding.CountBound(Fake.QuietBattleTable()));
    }

    [Fact]
    public void A_unit_on_the_field_is_tied_to_the_roster_slot_it_was_built_from()
    {
        var table = Fake.BattleTableWith(battleSlot: 3, rosterSlot: 5);

        Assert.Equal(3, BattleBinding.Find(table, 5));
        Assert.Equal(-1, BattleBinding.Find(table, 4));
        Assert.Equal(1, BattleBinding.CountBound(table));
    }

    [Fact]
    public void A_record_the_next_battle_has_reset_but_not_emptied_is_still_tied()
    {
        // The battle routine starts by marking each record unused (+1 = 0xFF) and benched (+0x1B5 = 0),
        // which the game's write-back still accepts.
        var table = Fake.BattleTableWith(battleSlot: 0, rosterSlot: 5, inUse: 0xFF, onField: 0);

        Assert.Equal(0, BattleBinding.Find(table, 5));
    }

    [Fact]
    public void A_record_marked_empty_is_not_tied_to_anyone()
    {
        var table = Fake.BattleTableWith(battleSlot: 0, rosterSlot: 5, inUse: 0xFF, onField: 0xFF);

        Assert.Equal(-1, BattleBinding.Find(table, 5));
    }

    [Fact]
    public void An_enemy_built_without_a_roster_slot_is_tied_to_nobody()
    {
        var table = Fake.BattleTableWith(battleSlot: 9, rosterSlot: 0xFF);

        Assert.Equal(0, BattleBinding.CountBound(table));
    }

    [Fact]
    public void Records_count_as_in_use_until_the_game_marks_them_empty()
    {
        var table = Fake.QuietBattleTable();
        for (var index = 4; index < BattleBinding.Capacity; index++) table[index * BattleBinding.Stride + 1] = 0xFF;

        Assert.Equal(4, BattleBinding.CountInUse(table));
        Assert.Equal(0, BattleBinding.CountInUse(new byte[10]));
    }

    [Fact]
    public void A_unit_with_its_own_problem_is_told_that_first()
    {
        var record = Fake.Unit(slot: 5, level: 3);

        Assert.Equal(Refusal.LevelTooLow, GameSafety.Evaluate(record, 5, Fake.BattleTableWith(0, 5), Fake.Battle));
    }

    [Fact]
    public void An_eligible_unit_is_refused_during_a_battle()
    {
        Assert.Equal(Refusal.InBattle, GameSafety.Evaluate(Fake.Unit(slot: 5), 5, Fake.QuietBattleTable(), Fake.Battle));
    }

    [Fact]
    public void A_leftover_battle_copy_does_not_make_an_eligible_unit_ineligible_outside_battle()
    {
        Assert.Equal(Refusal.None,
            GameSafety.Evaluate(Fake.Unit(slot: 5), 5, Fake.BattleTableWith(2, 5, inUse: 0xFF, onField: 0), Fake.WorldMap));
    }

    [Fact]
    public void The_minimum_level_is_a_setting_kept_between_2_and_99()
    {
        try
        {
            ReincarnationRules.UseMinimumLevel(3);
            Assert.Equal(Refusal.None, Reincarnation.Check(Fake.Unit(slot: 5, level: 3), 5));
            Assert.Equal(Refusal.LevelTooLow, Reincarnation.Check(Fake.Unit(slot: 5, level: 2), 5));

            ReincarnationRules.UseMinimumLevel(0);
            Assert.Equal(2, ReincarnationRules.MinimumLevel);
            ReincarnationRules.UseMinimumLevel(500);
            Assert.Equal(99, ReincarnationRules.MinimumLevel);
        }
        finally
        {
            ReincarnationRules.UseMinimumLevel(ReincarnationRules.DefaultMinimumLevel);
        }
    }

    [Fact]
    public void An_eligible_unit_is_refused_when_the_battle_table_could_not_be_read()
    {
        Assert.Equal(Refusal.GameStateUnknown, GameSafety.Evaluate(Fake.Unit(slot: 5), 5, [], Fake.WorldMap));
    }

    [Fact]
    public void An_eligible_unit_may_reincarnate_on_the_world_map()
    {
        Assert.Equal(Refusal.None,
            GameSafety.Evaluate(Fake.Unit(slot: 5), 5, Fake.BattleTableWith(2, rosterSlot: 6), Fake.WorldMap));
    }
}

public class RosterExportTests
{
    private static readonly byte[] Jobs = Fake.JobTable();

    private static byte[] RealRoster() => Fake.Roster(
        RealRecords.Ramza, RealRecords.GenericMan, RealRecords.GenericWoman, RealRecords.Chemist, RealRecords.Guest);

    [Fact]
    public void The_roster_of_a_real_save_lists_its_units_in_slot_order()
    {
        var units = RosterExport.DescribeAll(RealRoster(), Fake.QuietBattleTable(), Jobs, Fake.WorldMap);

        Assert.Equal([0, 1, 4, 6, 50], units.Select(u => u.Slot));
        Assert.Equal(["ramza", "male", "female", "female", "male"], units.Select(u => u.Kind));
        Assert.Equal([2, 3, 4, 1, 2], units.Select(u => u.Level));
        Assert.Equal(["Squire", "Squire", "Squire", "Chemist", "Squire"], units.Select(u => u.JobName));
    }

    [Fact]
    public void A_real_unit_is_described_with_what_the_save_holds()
    {
        var woman = RosterExport.Describe(RealRecords.GenericWoman, 4, Fake.QuietBattleTable(), Jobs, Fake.WorldMap);

        Assert.Equal(0x4A, woman.JobId);
        Assert.Equal(56, woman.Brave);
        Assert.Equal(47, woman.Faith);
        Assert.Equal(new StatBlock(581538, 297397, 101223, 68758, 86738), woman.Raw);
        Assert.Equal(new StatBlock(35, 13, 6, 3, 4), woman.Shown);
        Assert.Equal(0, woman.Reincarnations);
        Assert.False(woman.Eligible);
        Assert.Equal("levelTooLow", woman.Reason);
        Assert.False(woman.OnErrand);
        Assert.Null(woman.BattleSlot);
    }

    [Fact]
    public void A_table_of_the_wrong_size_lists_nobody()
    {
        Assert.Empty(RosterExport.DescribeAll(new byte[100], Fake.QuietBattleTable(), Jobs, Fake.WorldMap));
    }

    [Fact]
    public void An_eligible_unit_shows_what_the_next_reincarnation_gives()
    {
        var unit = RosterExport.Describe(Fake.Unit(slot: 3, level: 40), 3, Fake.QuietBattleTable(), Jobs, Fake.WorldMap);

        Assert.True(unit.Eligible);
        Assert.Equal("eligible", unit.Reason);
        Assert.Equal(200, unit.Next!.GainPermille);
        Assert.Equal(200, unit.Next.TotalPermille);
        Assert.Equal(31 * 16384 * 12 / 10, unit.Next.Raw.Hp);
        // 37.2 HP x Squire's 100%, 17.4 MP x 75%, 7.2 Speed, 6 PA x 90%, 4.8 MA x 80%.
        Assert.Equal(new StatBlock(37, 13, 7, 5, 3), unit.Next.Shown);
    }

    [Fact]
    public void A_unit_too_low_to_reincarnate_still_shows_what_it_would_give_now()
    {
        var unit = RosterExport.Describe(Fake.Unit(slot: 3, level: 6), 3, Fake.QuietBattleTable(), Jobs, Fake.WorldMap);

        Assert.False(unit.Eligible);
        Assert.Equal(30, unit.Next!.GainPermille);
    }

    [Fact]
    public void Guests_and_monsters_are_listed_with_the_reason_and_no_preview()
    {
        var guest = RosterExport.Describe(RealRecords.Guest, 50, Fake.QuietBattleTable(), Jobs, Fake.WorldMap);
        var monster = RosterExport.Describe(Fake.Monster(slot: 2), 2, Fake.QuietBattleTable(), Jobs, Fake.WorldMap);

        Assert.Equal(("guest", false), (guest.Reason, guest.Eligible));
        Assert.Null(guest.Next);
        Assert.Equal(("monster", "monster"), (monster.Reason, monster.Kind));
        Assert.Equal("Chocobo", monster.JobName);
        Assert.Null(monster.Next);
    }

    [Fact]
    public void A_reincarnated_unit_shows_its_count_and_power()
    {
        var record = Fake.Unit(slot: 3, level: 50);
        Reincarnation.Apply(record, Reincarnation.Plan(record)!.Value);

        var unit = RosterExport.Describe(record, 3, Fake.QuietBattleTable(), Jobs, Fake.WorldMap);

        Assert.Equal((1, 250, 1), (unit.Reincarnations, unit.PowerPermille, unit.Level));
        Assert.Equal("levelTooLow", unit.Reason);
    }

    [Fact]
    public void During_a_battle_every_otherwise_eligible_unit_reports_the_battle()
    {
        var unit = RosterExport.Describe(Fake.Unit(slot: 3), 3, Fake.BattleTableWith(1, 3), Jobs, Fake.Battle);

        Assert.Equal(("inBattle", false, 1), (unit.Reason, unit.Eligible, unit.BattleSlot));
    }

    [Fact]
    public void A_unit_is_named_by_the_games_table_when_its_record_has_no_name()
    {
        var unit = RosterExport.Describe(RealRecords.GenericMan, 1, Fake.QuietBattleTable(), Jobs, Fake.WorldMap,
            key => key == 346 ? "Rad" : null);

        Assert.Equal("Rad", unit.Name);
    }

    [Fact]
    public void A_name_stored_in_the_record_wins_over_the_games_table()
    {
        var record = Fake.Unit();
        "Kiara\0"u8.CopyTo(record.AsSpan(RosterLayout.OwnName));

        Assert.Equal("Kiara", RosterExport.Describe(record, 1, Fake.QuietBattleTable(), Jobs, Fake.WorldMap, _ => "Rad").Name);
    }

    [Fact]
    public void A_unit_stays_nameless_when_no_name_can_be_found()
    {
        Assert.Null(RosterExport.Describe(RealRecords.GenericMan, 1, Fake.QuietBattleTable(), Jobs, Fake.WorldMap).Name);
        Assert.Null(RosterExport.Describe(RealRecords.GenericMan, 1, Fake.QuietBattleTable(), Jobs, Fake.WorldMap, _ => null).Name);
    }

    [Fact]
    public void A_job_the_table_does_not_cover_leaves_the_shown_stats_out()
    {
        var unit = RosterExport.Describe(Fake.Unit(job: 0xB0), 1, Fake.QuietBattleTable(), Jobs, Fake.WorldMap);

        Assert.Null(unit.Shown);
        Assert.Null(unit.JobName);
        Assert.Null(unit.Next!.Shown);
    }
}
