using System.Text.Json;
using Ivalice.Party;

namespace Ivalice.Party.Tests;

/// <summary>A game with a party of three, one command away from a reincarnation.</summary>
internal sealed class Session
{
    public static readonly DateTimeOffset Now = new(2026, 11, 20, 15, 0, 0, TimeSpan.Zero);
    public const string Id = "0f8fad5b-d9cb-469f-a165-70867728950e";

    public byte[] Roster { get; } = Fake.Roster(
        RealRecords.Ramza, Fake.Unit(slot: 1, level: 30), Fake.Unit(slot: 2, level: 12, female: true, nameKey: 600));
    public byte[] BattleTable { get; set; } = Fake.QuietBattleTable();
    public GameStatus Game { get; set; } = Fake.WorldMap;
    public bool SafeMode { get; set; }
    public List<JournalEntry> Journal { get; } = [];
    public Func<JournalEntry, bool>? JournalOverride { get; set; }
    /// <summary>The level the unit had at the moment the journal was asked to store its line.</summary>
    public int? LevelSeenByJournal { get; private set; }

    public Span<byte> Record(int slot) => RosterLayout.Record(Roster.AsSpan(), slot);
    public string Fingerprint(int slot) => StampCodec.FingerprintText(Record(slot));

    public ReincarnationCommand Command(int slot = 1, string? fingerprint = null, int? level = null, string? createdAt = null) =>
        new(Id, ReincarnationCommand.Reincarnate, slot, fingerprint ?? Fingerprint(slot), level, createdAt);

    public CommandResult Run(ReincarnationCommand command, DateTimeOffset? fileTime = null) =>
        CommandProcessor.Execute(command, Roster, BattleTable, Fake.JobTable(), Game, Now, fileTime ?? Now, SafeMode,
            entry =>
            {
                LevelSeenByJournal = Record(entry.Slot)[RosterLayout.Level];
                if (JournalOverride is not null) return JournalOverride(entry);
                Journal.Add(entry);
                return true;
            });
}

public class CommandProcessorTests
{
    [Fact]
    public void A_command_from_the_control_center_is_understood()
    {
        var parsed = CommandProcessor.Parse(JsonSerializer.Serialize(
            new ReincarnationCommand(Session.Id, "reincarnate", 7, "1A2B3C4D", 31, "2026-11-20T15:00:00.000Z"),
            BridgeJson.Default.ReincarnationCommand));

        Assert.Equal(Refusal.None, parsed.Refusal);
        Assert.Equal(Guid.Parse(Session.Id), parsed.Id);
        Assert.Equal(new ReincarnationCommand(Session.Id, "reincarnate", 7, "1A2B3C4D", 31, "2026-11-20T15:00:00.000Z"), parsed.Command);
    }

    [Fact]
    public void A_hand_written_command_with_only_the_four_basic_fields_is_understood()
    {
        var parsed = CommandProcessor.Parse(
            """{ "id": "0F8FAD5B-D9CB-469F-A165-70867728950E", "action": "reincarnate", "slot": 3, "expectedFingerprint": "DEADBEEF" }""");

        Assert.Equal(Refusal.None, parsed.Refusal);
        Assert.Equal(new ReincarnationCommand(Session.Id, "reincarnate", 3, "DEADBEEF"), parsed.Command);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("[1, 2, 3]")]
    [InlineData("{ \"action\": \"reincarnate\", \"slot\": 1 }")]
    [InlineData("{ \"id\": \"../../evil\", \"action\": \"reincarnate\", \"slot\": 1 }")]
    [InlineData("{ \"id\": 5, \"action\": \"reincarnate\", \"slot\": 1 }")]
    public void A_command_without_a_usable_id_is_malformed_and_has_no_id(string? json)
    {
        var parsed = CommandProcessor.Parse(json);

        Assert.Equal(Refusal.Malformed, parsed.Refusal);
        Assert.Null(parsed.Id);
        Assert.Null(parsed.Command);
    }

    [Theory]
    [InlineData("{ \"id\": \"0f8fad5b-d9cb-469f-a165-70867728950e\" }")]
    [InlineData("{ \"id\": \"0f8fad5b-d9cb-469f-a165-70867728950e\", \"action\": \"reincarnate\" }")]
    [InlineData("{ \"id\": \"0f8fad5b-d9cb-469f-a165-70867728950e\", \"action\": \"reincarnate\", \"slot\": \"3\" }")]
    [InlineData("{ \"id\": \"0f8fad5b-d9cb-469f-a165-70867728950e\", \"action\": \"reincarnate\", \"slot\": 3.5 }")]
    [InlineData("{ \"id\": \"0f8fad5b-d9cb-469f-a165-70867728950e\", \"action\": \"reincarnate\", \"slot\": 3, \"expectedLevel\": \"x\" }")]
    public void A_command_with_missing_or_wrong_fields_is_malformed_but_keeps_its_id(string json)
    {
        var parsed = CommandProcessor.Parse(json);

        Assert.Equal(Refusal.Malformed, parsed.Refusal);
        Assert.Equal(Guid.Parse(Session.Id), parsed.Id);
    }

    [Fact]
    public void A_command_for_another_action_is_refused_as_unknown()
    {
        var parsed = CommandProcessor.Parse(
            """{ "id": "0f8fad5b-d9cb-469f-a165-70867728950e", "action": "setLevel", "slot": 1 }""");

        Assert.Equal(Refusal.UnknownAction, parsed.Refusal);
        Assert.NotNull(parsed.Id);
    }

    [Fact]
    public void A_valid_command_reincarnates_the_unit()
    {
        var session = new Session();

        var result = session.Run(session.Command(slot: 1, level: 30));

        Assert.True(result.Ok);
        Assert.True(result.Applied);
        Assert.False(result.SafeMode);
        Assert.Equal("eligible", result.Reason);
        Assert.Equal(Session.Id, result.Id);
        Assert.Equal(1, session.Record(1)[RosterLayout.Level]);
        Assert.Equal(new ReincarnationStamp(1, 150), StampCodec.Read(session.Record(1)));
        Assert.Equal((1, 1, 150, "levelTooLow"), (result.Unit!.Level, result.Unit.Reincarnations, result.Unit.PowerPermille, result.Unit.Reason));
    }

    [Fact]
    public void Only_the_one_record_changes()
    {
        var session = new Session();
        var before = (byte[])session.Roster.Clone();

        session.Run(session.Command(slot: 1));

        var changedSlots = Enumerable.Range(0, before.Length).Where(i => before[i] != session.Roster[i])
            .Select(i => i / RosterLayout.Stride).Distinct();
        Assert.Equal([1], changedSlots);
    }

    [Fact]
    public void The_journal_gets_the_record_before_and_after_and_is_written_before_the_game_is_touched()
    {
        var session = new Session();
        var before = Convert.ToHexString(session.Record(1));

        session.Run(session.Command(slot: 1));

        var entry = Assert.Single(session.Journal);
        Assert.Equal(30, session.LevelSeenByJournal);
        Assert.Equal(before, entry.Before);
        Assert.Equal(Convert.ToHexString(session.Record(1)), entry.After);
        Assert.Equal((1, Session.Id, 30, 0, 150), (entry.Slot, entry.CommandId, entry.LevelBefore, entry.PowerBeforePermille, entry.PowerAfterPermille));
        Assert.Equal(1200, entry.Before.Length);
        Assert.Equal("2026-11-20T15:00:00.000Z", entry.Time);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Nothing_is_changed_when_the_journal_cannot_be_written(bool journalThrows)
    {
        var session = new Session
        {
            JournalOverride = _ => journalThrows ? throw new IOException("disk full") : false,
        };
        var before = (byte[])session.Roster.Clone();

        var result = session.Run(session.Command(slot: 1));

        Assert.Equal(("journalFailed", false, false), (result.Reason, result.Ok, result.Applied));
        Assert.Equal(before, session.Roster);
    }

    [Fact]
    public void Safe_mode_answers_with_what_would_happen_and_changes_nothing()
    {
        var session = new Session { SafeMode = true };
        var before = (byte[])session.Roster.Clone();

        var result = session.Run(session.Command(slot: 1));

        Assert.Equal((true, false, true), (result.Ok, result.Applied, result.SafeMode));
        Assert.Equal((1, 1, 150), (result.Unit!.Level, result.Unit.Reincarnations, result.Unit.PowerPermille));
        Assert.Equal(before, session.Roster);
        Assert.Empty(session.Journal);
    }

    [Fact]
    public void Safe_mode_still_reports_a_refusal()
    {
        var session = new Session { SafeMode = true, Game = Fake.Battle };

        var result = session.Run(session.Command(slot: 1));

        Assert.Equal(("inBattle", false, true), (result.Reason, result.Ok, result.SafeMode));
    }

    [Fact]
    public void A_command_meant_for_another_unit_is_refused()
    {
        var session = new Session();
        var command = session.Command(slot: 1, fingerprint: session.Fingerprint(2));
        var before = (byte[])session.Roster.Clone();

        var result = session.Run(command);

        Assert.Equal("fingerprintMismatch", result.Reason);
        Assert.Equal(30, result.Unit!.Level);
        Assert.Equal(before, session.Roster);
    }

    [Fact]
    public void A_fingerprint_matches_whatever_its_letter_case()
    {
        var session = new Session();

        Assert.True(session.Run(session.Command(slot: 1, fingerprint: session.Fingerprint(1).ToLowerInvariant())).Applied);
    }

    [Fact]
    public void A_command_without_a_fingerprint_is_refused()
    {
        var session = new Session();

        var result = session.Run(new ReincarnationCommand(Session.Id, "reincarnate", 1));

        Assert.Equal("malformed", result.Reason);
        Assert.Equal(30, session.Record(1)[RosterLayout.Level]);
    }

    [Fact]
    public void A_command_is_refused_when_the_unit_levelled_since_the_player_confirmed()
    {
        var session = new Session();

        var result = session.Run(session.Command(slot: 1, level: 29));

        Assert.Equal("levelChanged", result.Reason);
        Assert.Equal(30, session.Record(1)[RosterLayout.Level]);
    }

    [Fact]
    public void A_command_is_refused_during_a_battle()
    {
        var session = new Session { Game = Fake.Battle };

        var result = session.Run(session.Command(slot: 1));

        Assert.Equal(("inBattle", false), (result.Reason, result.Applied));
        Assert.Equal(30, session.Record(1)[RosterLayout.Level]);
        Assert.Empty(session.Journal);
    }

    [Fact]
    public void A_leftover_battle_copy_of_the_unit_does_not_block_it_and_receives_the_same_change()
    {
        var session = new Session { BattleTable = Fake.BattleTableWith(battleSlot: 3, rosterSlot: 1, inUse: 0xFF, onField: 0) };
        var copy = 3 * BattleBinding.Stride;
        session.BattleTable[copy + 0x29] = 30; // the level the unit fought at
        session.BattleTable[copy + 0x28] = 77;

        Assert.True(session.Run(session.Command(slot: 1)).Applied);

        var record = session.Record(1);
        Assert.Equal(1, session.BattleTable[copy + 0x29]);
        Assert.Equal(0, session.BattleTable[copy + 0x28]);
        Assert.Equal(record.Slice(RosterLayout.RawStats, 15).ToArray(), session.BattleTable.AsSpan(copy + 0x7B, 15).ToArray());
    }

    [Fact]
    public void Every_battle_copy_of_the_unit_receives_the_change_not_just_the_first()
    {
        var session = new Session { BattleTable = Fake.BattleTableWith(battleSlot: 3, rosterSlot: 1, inUse: 0xFF, onField: 0) };
        // A second record for the same roster slot, as left by a later battle: active this time.
        var second = Fake.BattleTableWith(battleSlot: 9, rosterSlot: 1, inUse: 0, onField: 1);
        second.AsSpan(9 * BattleBinding.Stride, BattleBinding.Stride).CopyTo(session.BattleTable.AsSpan(9 * BattleBinding.Stride));
        foreach (var index in new[] { 3, 9 }) session.BattleTable[index * BattleBinding.Stride + 0x29] = 30;

        Assert.True(session.Run(session.Command(slot: 1)).Applied);

        Assert.Equal(1, session.BattleTable[3 * BattleBinding.Stride + 0x29]);
        Assert.Equal(1, session.BattleTable[9 * BattleBinding.Stride + 0x29]);
    }

    [Fact]
    public void A_command_marked_as_a_simulation_never_changes_the_game_even_with_safe_mode_off()
    {
        var session = new Session { SafeMode = false };
        var before = session.Record(1).ToArray();

        var result = session.Run(session.Command(slot: 1) with { Simulate = true });

        Assert.Equal((true, false, true), (result.Ok, result.Applied, result.SafeMode));
        Assert.Equal(before, session.Record(1).ToArray());
        Assert.Empty(session.Journal);
    }

    [Fact]
    public void The_simulate_flag_is_read_from_the_command_file_and_must_be_a_boolean()
    {
        var id = Guid.NewGuid();
        var text = $$"""{ "id": "{{id}}", "action": "reincarnate", "slot": 1, "simulate": true }""";

        Assert.True(CommandProcessor.Parse(text).Command!.Simulate);
        Assert.False(CommandProcessor.Parse(text.Replace(", \"simulate\": true", "")).Command!.Simulate);
        Assert.Equal(Refusal.Malformed, CommandProcessor.Parse(text.Replace("true", "\"yes\"")).Refusal);
    }

    [Fact]
    public void Safe_mode_leaves_a_leftover_battle_copy_alone()
    {
        var session = new Session { SafeMode = true, BattleTable = Fake.BattleTableWith(battleSlot: 3, rosterSlot: 1, inUse: 0xFF, onField: 0) };
        var before = session.BattleTable.ToArray();

        session.Run(session.Command(slot: 1));

        Assert.Equal(before, session.BattleTable);
    }

    [Fact]
    public void A_unit_that_is_not_eligible_is_refused_with_its_reason()
    {
        var session = new Session();

        Assert.Equal("levelTooLow", session.Run(session.Command(slot: 0)).Reason); // the real Ramza, level 2
    }

    [Theory]
    [InlineData(-1, "badSlot")]
    [InlineData(54, "badSlot")]
    [InlineData(9, "emptySlot")]
    public void A_command_for_a_slot_without_a_unit_is_refused(int slot, string reason)
    {
        var session = new Session();

        var result = session.Run(new ReincarnationCommand(Session.Id, "reincarnate", slot, "00000000"));

        Assert.Equal(reason, result.Reason);
        Assert.Null(result.Unit);
    }

    [Theory]
    [InlineData(-121)]
    [InlineData(121)]
    public void A_command_more_than_two_minutes_off_is_refused(int secondsFromNow)
    {
        var session = new Session();
        var createdAt = CommandProcessor.Stamp(Session.Now.AddSeconds(secondsFromNow));

        Assert.Equal("expired", session.Run(session.Command(slot: 1, createdAt: createdAt)).Reason);
        Assert.Equal(30, session.Record(1)[RosterLayout.Level]);
    }

    [Fact]
    public void A_command_without_a_time_is_dated_by_its_file()
    {
        var fresh = new Session();
        var old = new Session();

        Assert.True(fresh.Run(fresh.Command(slot: 1), fileTime: Session.Now.AddSeconds(-30)).Applied);
        Assert.Equal("expired", old.Run(old.Command(slot: 1), fileTime: Session.Now.AddMinutes(-10)).Reason);
    }

    [Fact]
    public void A_command_with_an_unreadable_time_is_malformed()
    {
        var session = new Session();

        Assert.Equal("malformed", session.Run(session.Command(slot: 1, createdAt: "yesterday-ish")).Reason);
    }

    [Fact]
    public void A_command_object_with_a_bad_id_or_action_is_refused_without_touching_anything()
    {
        var session = new Session();

        var badId = session.Run(session.Command(slot: 1) with { Id = "result" });
        var badAction = session.Run(session.Command(slot: 1) with { Action = "erase" });

        Assert.Equal(("malformed", null), (badId.Reason, badId.Id));
        Assert.Equal("unknownAction", badAction.Reason);
        Assert.Equal(30, session.Record(1)[RosterLayout.Level]);
    }

    [Fact]
    public void A_roster_of_the_wrong_size_is_never_written()
    {
        var result = CommandProcessor.Execute(new ReincarnationCommand(Session.Id, "reincarnate", 1, "00000000"),
            new byte[64], Fake.QuietBattleTable(), Fake.JobTable(), Fake.WorldMap, Session.Now, Session.Now, false, _ => true);

        Assert.Equal("gameStateUnknown", result.Reason);
    }

    [Fact]
    public void A_second_reincarnation_builds_on_the_first()
    {
        var session = new Session();
        session.Run(session.Command(slot: 1));
        session.Record(1)[RosterLayout.Level] = 20;

        var result = session.Run(session.Command(slot: 1));

        Assert.Equal((2, 250), (result.Unit!.Reincarnations, result.Unit.PowerPermille));
        Assert.Equal(2, session.Journal.Count);
    }
}

public class BridgeTests
{
    private static readonly DateTimeOffset Now = Session.Now;

    private static RosterDocument Document(int processId = 4242, DateTimeOffset? writtenAt = null, int schema = RosterDocument.CurrentSchema) =>
        new(schema, "0.4.0", processId, CommandProcessor.Stamp(writtenAt ?? Now), SafeMode: false, Fake.WorldMap,
            RosterExport.DescribeAll(Fake.Roster(RealRecords.Ramza, Fake.Unit(slot: 1, level: 30)),
                Fake.QuietBattleTable(), Fake.JobTable(), Fake.WorldMap, key => key == 1 ? "Ramza" : null));

    [Fact]
    public void The_roster_file_reads_back_as_it_was_written()
    {
        var document = Document();

        var reading = RosterFeed.Evaluate(RosterFeed.Serialize(document), Now, [4242]);

        Assert.Equal(FeedStatus.Ok, reading.Status);
        Assert.Equal(document.Units, reading.Roster!.Units);
        Assert.Equal(document.Game.Inputs, reading.Roster.Game.Inputs);
        Assert.Equal("Ramza", reading.Roster.Units[0].Name);
    }

    [Fact]
    public void The_roster_file_uses_the_field_names_the_interface_reads()
    {
        var json = RosterFeed.Serialize(Document());

        using var parsed = JsonDocument.Parse(json);
        var root = parsed.RootElement;
        Assert.Equal(1, root.GetProperty("schema").GetInt32());
        Assert.Equal(4242, root.GetProperty("processId").GetInt32());
        Assert.False(root.GetProperty("game").GetProperty("inBattle").GetBoolean());
        Assert.Equal("battleControlAbsent", root.GetProperty("game").GetProperty("decidedBy").GetString());
        Assert.False(root.GetProperty("game").GetProperty("inputs").GetProperty("battleControlPresent").GetBoolean());
        var unit = root.GetProperty("units")[1];
        Assert.Equal(30, unit.GetProperty("level").GetInt32());
        Assert.Equal(150, unit.GetProperty("next").GetProperty("gainPermille").GetInt32());
        Assert.Equal(31, unit.GetProperty("shown").GetProperty("hp").GetInt32());
        Assert.Equal(4, unit.GetProperty("shown").GetProperty("physicalAttack").GetInt32());
        Assert.Equal(JsonValueKind.Null, unit.GetProperty("name").ValueKind);
        Assert.True(unit.GetProperty("eligible").GetBoolean());
        Assert.Matches("^[0-9A-F]{8}$", unit.GetProperty("fingerprint").GetString()!);
    }

    [Fact]
    public void Without_a_roster_file_there_is_no_data()
    {
        Assert.Equal(FeedStatus.NoData, RosterFeed.Evaluate(null, Now, [4242]).Status);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{ broken")]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("{ \"schema\": 1, \"writtenAt\": \"2026-11-20T15:00:00Z\", \"processId\": 1 }")]
    public void A_roster_file_that_is_not_one_is_unreadable(string json)
    {
        Assert.Equal(FeedStatus.Unreadable, RosterFeed.Evaluate(json, Now, [1]).Status);
    }

    [Fact]
    public void A_roster_file_of_another_schema_is_unreadable()
    {
        Assert.Equal(FeedStatus.Unreadable, RosterFeed.Evaluate(RosterFeed.Serialize(Document(schema: 2)), Now, [4242]).Status);
    }

    [Fact]
    public void A_roster_file_from_a_game_that_has_closed_belongs_to_another_session()
    {
        var reading = RosterFeed.Evaluate(RosterFeed.Serialize(Document(processId: 4242)), Now, [9000]);

        Assert.Equal(FeedStatus.OtherSession, reading.Status);
        Assert.Equal(FeedStatus.OtherSession, RosterFeed.Evaluate(RosterFeed.Serialize(Document()), Now, []).Status);
    }

    [Fact]
    public void A_roster_file_the_running_game_stopped_refreshing_is_stale()
    {
        var json = RosterFeed.Serialize(Document(writtenAt: Now.AddSeconds(-16)));

        var reading = RosterFeed.Evaluate(json, Now, [4242]);

        Assert.Equal(FeedStatus.Stale, reading.Status);
        Assert.Equal(16, reading.AgeSeconds!.Value, 3);
        Assert.NotNull(reading.Roster);
    }

    [Fact]
    public void A_roster_file_refreshed_within_the_heartbeat_is_fresh()
    {
        var json = RosterFeed.Serialize(Document(writtenAt: Now.AddSeconds(-RosterFeed.HeartbeatSeconds * 2)));

        Assert.Equal(FeedStatus.Ok, RosterFeed.Evaluate(json, Now, [4242]).Status);
    }

    [Fact]
    public void A_result_file_carries_the_refusal_code_and_a_sentence()
    {
        var result = CommandProcessor.Refuse(Guid.Parse(Session.Id), Refusal.InBattle, safeMode: false, Now);

        var json = JsonSerializer.Serialize(result, BridgeJson.Default.CommandResult);

        using var parsed = JsonDocument.Parse(json);
        Assert.Equal(Session.Id, parsed.RootElement.GetProperty("id").GetString());
        Assert.False(parsed.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("inBattle", parsed.RootElement.GetProperty("reason").GetString());
        Assert.Equal(Refusal.InBattle.Describe(), parsed.RootElement.GetProperty("message").GetString());
        Assert.Equal(result, JsonSerializer.Deserialize(json, BridgeJson.Default.CommandResult));
    }

    [Fact]
    public void Bridge_files_live_under_one_folder()
    {
        var paths = new BridgePaths(@"C:\Data\IvaliceControlCenter");

        Assert.Equal(@"C:\Data\IvaliceControlCenter\bridge\roster.json", paths.RosterFile);
        Assert.Equal(@"C:\Data\IvaliceControlCenter\bridge\command.json", paths.CommandFile);
        Assert.Equal(@"C:\Data\IvaliceControlCenter\bridge\result-0f8fad5b-d9cb-469f-a165-70867728950e.json",
            paths.ResultFile(Guid.Parse(Session.Id)));
        Assert.Equal(@"C:\Data\IvaliceControlCenter\reincarnation-journal.jsonl", paths.JournalFile);
        Assert.EndsWith(@"IvaliceControlCenter", BridgePaths.Default.Root);
    }
}
