using Ivalice.Party;

namespace Ivalice.Party.Tests;

public class ReincarnationTests
{
    [Fact]
    public void A_new_recruit_starts_from_the_middle_of_the_games_starting_roll()
    {
        // Men roll HP 30 to 32 and MP 14 to 15, in units of 16384.
        Assert.Equal(new StatBlock(31 * 16384, 14 * 16384 + 8192, 6 * 16384, 5 * 16384, 4 * 16384), Reincarnation.Baseline(UnitKind.Male));
        Assert.Equal(new StatBlock(29 * 16384, 15 * 16384 + 8192, 6 * 16384, 4 * 16384, 5 * 16384), Reincarnation.Baseline(UnitKind.Female));
        Assert.Equal(new StatBlock(31 * 16384, 15 * 16384 + 8192, 6 * 16384, 5 * 16384, 5 * 16384), Reincarnation.Baseline(UnitKind.Ramza));
    }

    [Fact]
    public void Real_level_one_recruits_fall_inside_the_starting_roll_the_baseline_is_the_middle_of()
    {
        var chemist = RosterRecord.RawStats(RealRecords.Chemist); // a level 1 woman from a real save
        var middle = Reincarnation.Baseline(UnitKind.Female);

        Assert.InRange(chemist.Hp, 28 * 16384, 30 * 16384);
        Assert.InRange(chemist.Mp, 15 * 16384, 16 * 16384);
        Assert.Equal(middle.Speed, chemist.Speed);
        Assert.Equal(middle.PhysicalAttack, chemist.PhysicalAttack);
        Assert.Equal(middle.MagickAttack, chemist.MagickAttack);
    }

    [Theory]
    [InlineData(10, 50)]
    [InlineData(37, 185)]
    [InlineData(99, 495)]
    public void Each_level_given_up_is_worth_half_a_percent_of_power(int level, int permille)
    {
        var plan = Reincarnation.Plan(Fake.Unit(level: level))!.Value;

        Assert.Equal(level, plan.LevelBefore);
        Assert.Equal(permille, plan.GainPermille);
        Assert.Equal(new ReincarnationStamp(1, permille), plan.Stamp);
    }

    [Fact]
    public void Power_adds_up_over_reincarnations()
    {
        var record = Fake.Unit(level: 50);
        Reincarnation.Apply(record, Reincarnation.Plan(record)!.Value);
        record[RosterLayout.Level] = 20;

        var second = Reincarnation.Plan(record)!.Value;

        Assert.Equal(100, second.GainPermille);
        Assert.Equal(new ReincarnationStamp(2, 350), second.Stamp);
    }

    [Fact]
    public void Power_stops_at_one_hundred_percent()
    {
        var record = Fake.Unit(level: 99);
        StampCodec.Write(record, new ReincarnationStamp(2, 990));

        var plan = Reincarnation.Plan(record)!.Value;

        Assert.Equal(10, plan.GainPermille);
        Assert.Equal(new ReincarnationStamp(3, 1000), plan.Stamp);
        Assert.Equal(Reincarnation.Baseline(UnitKind.Male).Hp * 2, plan.Raw.Hp);
    }

    [Fact]
    public void A_unit_restarts_with_the_baseline_raised_by_its_power()
    {
        var plan = Reincarnation.Plan(Fake.Unit(level: 10, female: true))!.Value;

        var baseline = Reincarnation.Baseline(UnitKind.Female);
        Assert.Equal(StatBlock.From(i => (int)((long)baseline[i] * 1050 / 1000)), plan.Raw);
        Assert.Equal(498892, plan.Raw.Hp); // 29 x 16384 x 1.05, rounded down
    }

    [Fact]
    public void Ramza_restarts_from_his_own_starting_row()
    {
        var ramza = RealRecords.Ramza;
        ramza[RosterLayout.Level] = 20;

        var plan = Reincarnation.Plan(ramza)!.Value;

        Assert.Equal((int)((long)Reincarnation.Baseline(UnitKind.Ramza).MagickAttack * 1100 / 1000), plan.Raw.MagickAttack);
    }

    [Fact]
    public void Reincarnating_resets_level_and_experience()
    {
        var record = Fake.Unit(level: 45, experience: 77);

        Reincarnation.Apply(record, Reincarnation.Plan(record)!.Value);

        Assert.Equal(1, record[RosterLayout.Level]);
        Assert.Equal(0, record[RosterLayout.Experience]);
        Assert.Equal(new ReincarnationStamp(1, 225), StampCodec.Read(record));
    }

    [Fact]
    public void Reincarnating_a_real_record_leaves_job_abilities_equipment_brave_and_faith_alone()
    {
        var before = RealRecords.GenericWoman;
        before[RosterLayout.Level] = 60;
        var after = (byte[])before.Clone();

        Reincarnation.Apply(after, Reincarnation.Plan(after)!.Value);

        var changed = Enumerable.Range(0, after.Length).Where(i => before[i] != after[i]).ToList();
        Assert.All(changed, i => Assert.True(
            i is RosterLayout.Level or RosterLayout.Experience
            || (i >= RosterLayout.RawStats && i < RosterLayout.RawStats + 15)
            || (i >= StampCodec.Offset && i < StampCodec.Offset + StampCodec.Length),
            $"Byte 0x{i:X} must not change."));
        Assert.Contains(RosterLayout.Level, changed);
        Assert.Equal(before[RosterLayout.Brave], after[RosterLayout.Brave]);
        Assert.Equal(before[RosterLayout.Job], after[RosterLayout.Job]);
    }

    [Fact]
    public void A_party_member_at_level_ten_may_reincarnate()
    {
        Assert.Equal(Refusal.None, Reincarnation.Check(Fake.Unit(slot: 7, level: 10), 7));
    }

    [Fact]
    public void A_unit_below_level_ten_may_not()
    {
        Assert.Equal(Refusal.LevelTooLow, Reincarnation.Check(Fake.Unit(slot: 7, level: 9), 7));
    }

    [Fact]
    public void Every_real_unit_of_the_chapter_one_save_is_still_too_low_or_a_guest()
    {
        Assert.Equal(Refusal.LevelTooLow, Reincarnation.Check(RealRecords.Ramza, 0));
        Assert.Equal(Refusal.LevelTooLow, Reincarnation.Check(RealRecords.GenericMan, 1));
        Assert.Equal(Refusal.Guest, Reincarnation.Check(RealRecords.Guest, 50));
    }

    [Fact]
    public void An_empty_slot_holds_nobody_to_reincarnate()
    {
        var record = Fake.Unit(level: 50);
        record[RosterLayout.SlotId] = RosterLayout.EmptySlot;

        Assert.Equal(Refusal.EmptySlot, Reincarnation.Check(record, 3));
    }

    [Theory]
    [InlineData(50)]
    [InlineData(53)]
    public void Guests_may_not_reincarnate(int slot)
    {
        Assert.Equal(Refusal.Guest, Reincarnation.Check(Fake.Unit(slot: slot, level: 50), slot));
    }

    [Fact]
    public void Monsters_may_not_reincarnate()
    {
        Assert.Equal(Refusal.Monster, Reincarnation.Check(Fake.Monster(slot: 2, level: 50), 2));
    }

    [Fact]
    public void A_unit_away_on_an_errand_may_not_reincarnate()
    {
        Assert.Equal(Refusal.OnErrand, Reincarnation.Check(Fake.Unit(level: 50, onErrand: true), 1));
    }

    [Fact]
    public void A_unit_of_unknown_kind_is_refused_and_has_no_plan()
    {
        var odd = Fake.Unit(level: 50);
        odd[RosterLayout.SexFlags] = 0;

        Assert.Equal(Refusal.UnknownKind, Reincarnation.Check(odd, 1));
        Assert.Null(Reincarnation.Plan(odd));
    }

    [Fact]
    public void Spare_bytes_someone_else_wrote_are_not_overwritten()
    {
        var record = Fake.Unit(level: 50);
        record[StampCodec.Offset] = 0x01;

        Assert.Equal(Refusal.SpareBytesInUse, Reincarnation.Check(record, 1));
    }

    [Fact]
    public void A_stale_stamp_of_this_project_does_not_block_a_new_recruit()
    {
        var record = Fake.Unit(level: 50);
        StampCodec.Write(record, new ReincarnationStamp(4, 800));
        BitConverter.TryWriteBytes(record.AsSpan(RosterLayout.NameKey), 999);

        Assert.Equal(Refusal.None, Reincarnation.Check(record, 1));
        Assert.Equal(new ReincarnationStamp(1, 250), Reincarnation.Plan(record)!.Value.Stamp);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(54)]
    public void A_slot_outside_the_roster_is_refused(int slot)
    {
        Assert.Equal(Refusal.BadSlot, Reincarnation.Check(Fake.Unit(level: 50), slot));
    }

    [Fact]
    public void Every_refusal_has_a_code_and_a_sentence()
    {
        foreach (var refusal in Enum.GetValues<Refusal>())
        {
            Assert.Matches("^[a-z][A-Za-z]+$", refusal.Code());
            Assert.EndsWith(".", refusal.Describe());
        }
        Assert.Equal("eligible", Refusal.None.Code());
        Assert.Equal("levelTooLow", Refusal.LevelTooLow.Code());
    }
}
