using Ivalice.Party;

namespace Ivalice.Party.Tests;

public class RosterRecordTests
{
    [Fact]
    public void A_real_record_gives_the_level_and_stats_the_save_holds()
    {
        var ramza = RealRecords.Ramza;

        Assert.Equal(2, ramza[RosterLayout.Level]);
        Assert.Equal(72, ramza[RosterLayout.Brave]);
        Assert.Equal(70, ramza[RosterLayout.Faith]);
        Assert.Equal(new StatBlock(543839, 278018, 99328, 83526, 83591), RosterRecord.RawStats(ramza));
        Assert.Equal(1, RosterRecord.NameKey(ramza));
    }

    [Fact]
    public void Real_records_are_told_apart_by_kind()
    {
        Assert.Equal(UnitKind.Ramza, RosterRecord.Kind(RealRecords.Ramza));
        Assert.Equal(UnitKind.Male, RosterRecord.Kind(RealRecords.GenericMan));
        Assert.Equal(UnitKind.Female, RosterRecord.Kind(RealRecords.GenericWoman));
        Assert.Equal(UnitKind.Male, RosterRecord.Kind(RealRecords.Guest));
    }

    [Fact]
    public void A_monster_is_recognised_by_its_marker_or_its_sex_flag()
    {
        var byMarker = Fake.Unit();
        byMarker[RosterLayout.Marker] = RosterLayout.MarkerMonster;
        var byFlag = Fake.Unit();
        byFlag[RosterLayout.SexFlags] = RosterLayout.MonsterBit;

        Assert.Equal(UnitKind.Monster, RosterRecord.Kind(byMarker));
        Assert.Equal(UnitKind.Monster, RosterRecord.Kind(byFlag));
    }

    [Fact]
    public void A_unit_with_no_sex_flag_is_of_unknown_kind()
    {
        var odd = Fake.Unit();
        odd[RosterLayout.SexFlags] = 0x10;

        Assert.Equal(UnitKind.Unknown, RosterRecord.Kind(odd));
    }

    [Fact]
    public void Raw_stats_survive_being_written_and_read_back()
    {
        var record = Fake.Unit();
        var stats = new StatBlock(0xFFFFFF, 1, 98304, 81920, 65536);

        RosterRecord.WriteRawStats(record, stats);

        Assert.Equal(stats, RosterRecord.RawStats(record));
    }

    [Fact]
    public void A_raw_stat_beyond_three_bytes_is_capped()
    {
        var record = Fake.Unit();

        RosterRecord.WriteRawStats(record, new StatBlock(0x1000000, 0, 0, 0, 0));

        Assert.Equal(0xFFFFFF, RosterRecord.RawStats(record).Hp);
    }

    [Fact]
    public void A_record_holds_a_unit_only_when_it_carries_its_own_slot_number()
    {
        var record = Fake.Unit(slot: 6);
        var free = Fake.Unit(slot: 6);
        free[RosterLayout.SlotId] = RosterLayout.EmptySlot;

        Assert.True(RosterRecord.Holds(record, 6));
        Assert.False(RosterRecord.Holds(record, 7));
        Assert.False(RosterRecord.Holds(free, 6));
        Assert.True(RosterRecord.Holds(RealRecords.Guest, 50));
    }

    [Fact]
    public void Memory_the_game_has_not_set_up_yet_lists_at_most_a_blank_first_slot()
    {
        // Before the game clears the roster at start-up every byte is zero.
        var units = RosterExport.DescribeAll(new byte[RosterLayout.TableSize], Fake.QuietBattleTable(), Fake.JobTable(), Fake.WorldMap);

        Assert.Equal([0], units.Select(u => u.Slot));
        Assert.Equal(("unknown", "unknownKind", false), (units[0].Kind, units[0].Reason, units[0].Eligible));
    }

    [Fact]
    public void Real_records_carry_no_name_of_their_own()
    {
        Assert.Null(RosterRecord.OwnName(RealRecords.GenericMan));
    }

    [Fact]
    public void A_name_stored_in_the_record_is_read_up_to_its_terminator()
    {
        var record = Fake.Unit();
        "Agrias\0junk"u8.CopyTo(record.AsSpan(RosterLayout.OwnName));

        Assert.Equal("Agrias", RosterRecord.OwnName(record));
    }

    [Fact]
    public void Bytes_that_are_not_text_do_not_become_a_name()
    {
        var record = Fake.Unit();
        record[RosterLayout.OwnName] = 0xFF;
        record[RosterLayout.OwnName + 1] = 0xFE;

        Assert.Null(RosterRecord.OwnName(record));
    }
}

public class JobTableTests
{
    [Fact]
    public void A_real_unit_is_shown_with_its_raw_stats_scaled_by_its_job()
    {
        // Squire multipliers: HP 100, MP 75, Speed 100, PA 90, MA 80.
        var growth = JobTable.GrowthOf(Fake.JobTable(), RealRecords.GenericMan)!.Value;

        var shown = growth.Shown(RosterRecord.RawStats(RealRecords.GenericMan));

        Assert.Equal(new StatBlock(36, 12, 6, 4, 3), shown);
    }

    [Fact]
    public void Growth_constants_and_multipliers_come_in_pairs_from_the_job_row()
    {
        var growth = JobTable.GrowthOf(Fake.JobTable(), Fake.Unit(job: 0x4B))!.Value;

        Assert.Equal(new StatBlock(12, 16, 100, 75, 50), growth.Growth);
        Assert.Equal(new StatBlock(80, 75, 100, 75, 80), growth.Multiplier);
    }

    [Fact]
    public void Shown_stats_stop_at_the_caps_the_game_uses()
    {
        var growth = new JobGrowth(new StatBlock(1, 1, 1, 1, 1), new StatBlock(255, 255, 255, 255, 255));

        var shown = growth.Shown(new StatBlock(0xFFFFFF, 0xFFFFFF, 0xFFFFFF, 0xFFFFFF, 0xFFFFFF));

        Assert.Equal(new StatBlock(999, 999, 50, 99, 99), shown);
    }

    [Fact]
    public void A_shown_stat_is_never_below_one()
    {
        var growth = new JobGrowth(new StatBlock(1, 1, 1, 1, 1), new StatBlock(0, 100, 100, 100, 100));

        Assert.Equal(new StatBlock(1, 1, 1, 1, 1), growth.Shown(new StatBlock(500000, 0, 100, 16383, 1)));
    }

    [Fact]
    public void A_job_outside_the_table_has_no_growth_data()
    {
        Assert.Null(JobTable.GrowthOf(Fake.JobTable(), Fake.Unit(job: 0xB0)));
    }

    [Fact]
    public void A_missing_job_table_gives_no_growth_data()
    {
        Assert.Null(JobTable.GrowthOf([], Fake.Unit()));
    }

    [Fact]
    public void Job_A1_uses_row_A4_once_its_own_job_level_reaches_eight()
    {
        var novice = Fake.Unit(job: 0xA1);
        novice[RosterLayout.JobLevels + 10] = 0x07;
        var master = Fake.Unit(job: 0xA1);
        master[RosterLayout.JobLevels + 10] = 0x08;

        Assert.Equal(0xA1, JobTable.RowOf(novice));
        Assert.Equal(0xA4, JobTable.RowOf(master));
        Assert.Equal(150, JobTable.GrowthOf(Fake.JobTable(), master)!.Value.Multiplier.Hp);
    }

    [Theory]
    [InlineData(74, "Squire")]
    [InlineData(75, "Chemist")]
    [InlineData(94, "Chocobo")]
    public void Jobs_have_english_names(int jobId, string name)
    {
        Assert.Equal(name, JobNames.Find(jobId));
    }

    [Fact]
    public void An_unused_job_id_has_no_name()
    {
        Assert.Null(JobNames.Find(0));
        Assert.Null(JobNames.Find(250));
    }
}
