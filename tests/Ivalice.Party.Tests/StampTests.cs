using Ivalice.Party;

namespace Ivalice.Party.Tests;

public class StampTests
{
    [Fact]
    public void A_unit_from_a_real_save_has_never_reincarnated()
    {
        Assert.Equal(StampState.Absent, StampCodec.Inspect(RealRecords.GenericMan));
        Assert.Equal(ReincarnationStamp.None, StampCodec.Read(RealRecords.GenericMan));
    }

    [Fact]
    public void A_stamp_is_read_back_as_it_was_written()
    {
        var record = RealRecords.GenericWoman;

        StampCodec.Write(record, new ReincarnationStamp(Count: 3, PowerPermille: 615));

        Assert.Equal(StampState.Valid, StampCodec.Inspect(record));
        Assert.Equal(new ReincarnationStamp(3, 615), StampCodec.Read(record));
    }

    [Fact]
    public void Writing_a_stamp_changes_only_its_own_twenty_bytes()
    {
        var before = Fake.Unit();
        before.AsSpan(RosterLayout.Spare).Fill(0);
        var after = (byte[])before.Clone();

        StampCodec.Write(after, new ReincarnationStamp(1, 50));

        for (var i = 0; i < after.Length; i++)
        {
            var inStamp = i >= StampCodec.Offset && i < StampCodec.Offset + StampCodec.Length;
            if (!inStamp) Assert.True(before[i] == after[i], $"Byte 0x{i:X} outside the stamp changed.");
        }
        Assert.Equal(RosterLayout.Spare, StampCodec.Offset);
        Assert.True(StampCodec.Offset + StampCodec.Length <= RosterLayout.Spare + RosterLayout.SpareLength);
    }

    [Fact]
    public void A_stamp_left_behind_by_an_earlier_unit_in_the_slot_is_ignored()
    {
        var record = Fake.Unit(nameKey: 346);
        StampCodec.Write(record, new ReincarnationStamp(2, 400));

        // The game reuses the slot for a new recruit without clearing the spare bytes.
        BitConverter.TryWriteBytes(record.AsSpan(RosterLayout.NameKey), 418);

        Assert.Equal(StampState.Stale, StampCodec.Inspect(record));
        Assert.Equal(ReincarnationStamp.None, StampCodec.Read(record));
    }

    [Theory]
    [InlineData(RosterLayout.Marker)]
    [InlineData(RosterLayout.Birth)]
    [InlineData(RosterLayout.Birth + 1)]
    public void A_stamp_does_not_carry_over_to_a_unit_with_a_different_identity(int identityByte)
    {
        var record = Fake.Unit();
        StampCodec.Write(record, new ReincarnationStamp(1, 100));

        record[identityByte] ^= 0x01;

        Assert.Equal(ReincarnationStamp.None, StampCodec.Read(record));
    }

    [Theory]
    [InlineData(6)]  // count
    [InlineData(8)]  // power
    [InlineData(12)] // fingerprint
    [InlineData(16)] // checksum
    public void A_damaged_stamp_is_ignored(int offsetInStamp)
    {
        var record = Fake.Unit();
        StampCodec.Write(record, new ReincarnationStamp(1, 100));

        record[StampCodec.Offset + offsetInStamp] ^= 0x40;

        Assert.Equal(StampState.Stale, StampCodec.Inspect(record));
        Assert.Equal(ReincarnationStamp.None, StampCodec.Read(record));
    }

    [Fact]
    public void Spare_bytes_written_by_something_else_are_foreign()
    {
        var record = Fake.Unit();
        record[StampCodec.Offset + 3] = 0x7F;

        Assert.Equal(StampState.Foreign, StampCodec.Inspect(record));
        Assert.Equal(ReincarnationStamp.None, StampCodec.Read(record));
    }

    [Fact]
    public void A_stamp_of_a_version_this_code_does_not_know_is_foreign()
    {
        var record = Fake.Unit();
        StampCodec.Write(record, new ReincarnationStamp(1, 100));

        record[StampCodec.Offset + 4] = StampCodec.Version + 1;

        Assert.Equal(StampState.Foreign, StampCodec.Inspect(record));
    }

    [Fact]
    public void A_stamp_stays_with_the_unit_through_job_changes_and_level_ups()
    {
        var record = Fake.Unit(slot: 4, level: 12);
        StampCodec.Write(record, new ReincarnationStamp(1, 60));

        record[RosterLayout.Job] = 0x4B;
        record[RosterLayout.Level] = 57;
        record[RosterLayout.Experience] = 99;
        record[RosterLayout.Brave] = 97;
        record[RosterLayout.SlotId] = 9;
        record[RosterLayout.SexFlags] |= 0x05; // bits the game's write-back toggles
        RosterRecord.WriteRawStats(record, new StatBlock(900000, 400000, 150000, 120000, 110000));

        Assert.Equal(new ReincarnationStamp(1, 60), StampCodec.Read(record));
    }

    [Fact]
    public void Ramza_keeps_his_stamp_when_his_marker_changes_with_the_chapter()
    {
        var ramza = RealRecords.Ramza;
        ramza[RosterLayout.Level] = 40;
        StampCodec.Write(ramza, new ReincarnationStamp(1, 200));

        ramza[RosterLayout.Marker] = 3;
        BitConverter.TryWriteBytes(ramza.AsSpan(RosterLayout.NameKey), 3);

        Assert.Equal(new ReincarnationStamp(1, 200), StampCodec.Read(ramza));
    }

    [Fact]
    public void Two_real_units_have_different_fingerprints()
    {
        var prints = new[] { RealRecords.Ramza, RealRecords.GenericMan, RealRecords.GenericWoman, RealRecords.Chemist, RealRecords.Guest }
            .Select(r => StampCodec.FingerprintText(r)).ToList();

        Assert.Equal(prints.Count, prints.Distinct().Count());
        Assert.All(prints, p => Assert.Matches("^[0-9A-F]{8}$", p));
    }

    [Fact]
    public void Count_and_power_are_kept_within_what_the_stamp_can_hold()
    {
        var record = Fake.Unit();

        StampCodec.Write(record, new ReincarnationStamp(Count: 70000, PowerPermille: 5000));

        Assert.Equal(new ReincarnationStamp(65535, 1000), StampCodec.Read(record));
    }
}
