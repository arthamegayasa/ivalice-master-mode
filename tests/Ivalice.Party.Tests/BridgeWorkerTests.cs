using System.Text.Json;
using Ivalice.Party;
using ivalice.party.reincarnation;

namespace Ivalice.Party.Tests;

/// <summary>
/// Drives the mod's file side against a throwaway folder and a fake copy of the game's memory:
/// what the mod does once per look, without the game.
/// </summary>
public sealed class BridgeWorkerTests : IDisposable
{
    private static readonly DateTimeOffset Start = Session.Now;

    private readonly BridgePaths _paths = new(Path.Combine(Path.GetTempPath(), "ivalice-bridge-tests-" + Guid.NewGuid().ToString("N")));
    private readonly List<string> _log = [];
    private readonly BridgeWorker _worker;
    private readonly byte[] _roster = Fake.Roster(RealRecords.Ramza, Fake.Unit(slot: 1, level: 30));
    private readonly byte[] _battleTable = Fake.QuietBattleTable();
    private long _clock;

    public BridgeWorkerTests()
    {
        _worker = new BridgeWorker(_paths, "0.4.0", processId: 4242, _log.Add);
        _worker.Prepare(Start.UtcDateTime);
    }

    public void Dispose() => Directory.Delete(_paths.Root, recursive: true);

    private DateTimeOffset Now => Start.AddMilliseconds(_clock);
    private Span<byte> Record(int slot) => RosterLayout.Record(_roster.AsSpan(), slot);

    /// <summary>Lets time pass, then has the mod look at the files once.</summary>
    private void Look(int afterMilliseconds = 1000, GameStatus? game = null, bool safeMode = false, bool debugLog = false)
    {
        _clock += afterMilliseconds;
        _worker.Tick(_clock, Now, _roster, _battleTable, Fake.JobTable(), game ?? Fake.WorldMap,
            new WorkerSettings(safeMode, debugLog), key => key == 1 ? "Ramza" : null);
    }

    private RosterDocument ReadRoster() =>
        JsonSerializer.Deserialize(File.ReadAllText(_paths.RosterFile), BridgeJson.Default.RosterDocument)!;

    private CommandResult ReadResult(string id = Session.Id) =>
        JsonSerializer.Deserialize(File.ReadAllText(_paths.ResultFile(Guid.Parse(id))), BridgeJson.Default.CommandResult)!;

    /// <summary>Leaves a command file, dated as if it was written at the current fake time.</summary>
    private void LeaveCommand(string json, int ageSeconds = 0)
    {
        File.WriteAllText(_paths.CommandFile, json);
        File.SetLastWriteTimeUtc(_paths.CommandFile, Now.AddSeconds(-ageSeconds).UtcDateTime);
    }

    private void LeaveReincarnation(int slot = 1) => LeaveCommand(JsonSerializer.Serialize(
        new ReincarnationCommand(Session.Id, ReincarnationCommand.Reincarnate, slot, StampCodec.FingerprintText(Record(slot)),
            Record(slot)[RosterLayout.Level], CommandProcessor.Stamp(Now)),
        BridgeJson.Default.ReincarnationCommand));

    [Fact]
    public void The_first_look_writes_the_roster_file()
    {
        Look();

        var roster = ReadRoster();
        Assert.Equal((1, "0.4.0", 4242, false), (roster.Schema, roster.ModVersion, roster.ProcessId, roster.SafeMode));
        Assert.Equal(CommandProcessor.Stamp(Now), roster.WrittenAt);
        Assert.Equal([0, 1], roster.Units.Select(u => u.Slot));
        Assert.Equal(["Ramza", null], roster.Units.Select(u => u.Name));
        Assert.False(roster.Game.InBattle);
        Assert.Equal(FeedStatus.Ok, RosterFeed.Evaluate(File.ReadAllText(_paths.RosterFile), Now, [4242]).Status);
    }

    [Fact]
    public void The_roster_file_is_left_alone_while_nothing_changes()
    {
        Look();
        var first = File.ReadAllText(_paths.RosterFile);

        Look();
        Look();

        Assert.Equal(first, File.ReadAllText(_paths.RosterFile));
    }

    [Fact]
    public void An_unchanged_roster_is_still_rewritten_often_enough_to_show_the_game_is_alive()
    {
        Look();
        var first = ReadRoster().WrittenAt;

        for (var i = 0; i < RosterFeed.HeartbeatSeconds; i++) Look();

        Assert.NotEqual(first, ReadRoster().WrittenAt);
        Assert.Equal(CommandProcessor.Stamp(Now), ReadRoster().WrittenAt);
    }

    [Fact]
    public void A_change_in_the_game_reaches_the_roster_file_on_the_next_look()
    {
        Look();
        Record(1)[RosterLayout.Level] = 31;

        Look();

        Assert.Equal(31, ReadRoster().Units[1].Level);
    }

    [Fact]
    public void Looks_less_than_a_second_apart_do_not_rewrite_the_roster()
    {
        Look();
        Record(1)[RosterLayout.Level] = 31;

        Look(afterMilliseconds: 250);
        Assert.Equal(30, ReadRoster().Units[1].Level);

        Look(afterMilliseconds: 750);
        Assert.Equal(31, ReadRoster().Units[1].Level);
    }

    [Fact]
    public void The_roster_file_says_when_a_battle_is_running_and_how_that_was_decided()
    {
        Look(game: Fake.Battle, safeMode: true);

        var roster = ReadRoster();
        Assert.True(roster.SafeMode);
        Assert.Equal((true, "battleControlPresent"), (roster.Game.InBattle, roster.Game.DecidedBy));
        Assert.Equal(Fake.Battle.Inputs, roster.Game.Inputs);
        Assert.Equal("inBattle", roster.Units[1].Reason);
    }

    [Fact]
    public void A_command_is_carried_out_answered_and_removed()
    {
        Look();
        LeaveReincarnation(slot: 1);

        Look(afterMilliseconds: 250);

        var result = ReadResult();
        Assert.Equal((true, true, "eligible"), (result.Ok, result.Applied, result.Reason));
        Assert.Equal((1, 150), (result.Unit!.Level, result.Unit.PowerPermille));
        Assert.False(File.Exists(_paths.CommandFile));
        Assert.Equal(1, Record(1)[RosterLayout.Level]);
        // The roster file is refreshed at once, not a second later.
        Assert.Equal((1, 1), (ReadRoster().Units[1].Level, ReadRoster().Units[1].Reincarnations));
    }

    [Fact]
    public void An_applied_reincarnation_leaves_one_journal_line_that_can_restore_the_record()
    {
        var before = Record(1).ToArray();
        LeaveReincarnation(slot: 1);

        Look();

        var line = Assert.Single(File.ReadAllLines(_paths.JournalFile));
        var entry = JsonSerializer.Deserialize(line, BridgeJson.Default.JournalEntry)!;
        Assert.Equal((1, Session.Id, 30, 0, 150), (entry.Slot, entry.CommandId, entry.LevelBefore, entry.PowerBeforePermille, entry.PowerAfterPermille));
        Assert.Equal(before, Convert.FromHexString(entry.Before));
        Assert.Equal(Record(1).ToArray(), Convert.FromHexString(entry.After));
        Assert.Contains(_log, l => l.Contains("Slot 1") && l.Contains(_paths.JournalFile));
    }

    [Fact]
    public void In_safe_mode_a_command_is_answered_but_nothing_is_written()
    {
        var before = (byte[])_roster.Clone();
        LeaveReincarnation(slot: 1);

        Look(safeMode: true);

        var result = ReadResult();
        Assert.Equal((true, false, true), (result.Ok, result.Applied, result.SafeMode));
        Assert.Equal(1, result.Unit!.Level);
        Assert.Equal(before, _roster);
        Assert.False(File.Exists(_paths.JournalFile));
        Assert.False(File.Exists(_paths.CommandFile));
    }

    [Fact]
    public void A_command_during_a_battle_is_refused_with_the_reason()
    {
        LeaveReincarnation(slot: 1);

        Look(game: Fake.Battle);

        Assert.Equal((false, false, "inBattle"), (ReadResult().Ok, ReadResult().Applied, ReadResult().Reason));
        Assert.Equal(30, Record(1)[RosterLayout.Level]);
        Assert.False(File.Exists(_paths.CommandFile));
    }

    [Fact]
    public void A_command_for_an_action_the_mod_does_not_know_is_refused_under_its_own_id()
    {
        LeaveCommand($$"""{ "id": "{{Session.Id}}", "action": "erase", "slot": 1 }""");

        Look();

        Assert.Equal("unknownAction", ReadResult().Reason);
        Assert.False(File.Exists(_paths.CommandFile));
    }

    [Fact]
    public void A_command_that_cannot_be_read_is_answered_under_a_fixed_name()
    {
        LeaveCommand("this is not a command", ageSeconds: 10);

        Look();

        var result = JsonSerializer.Deserialize(File.ReadAllText(_paths.InvalidResultFile), BridgeJson.Default.CommandResult)!;
        Assert.Equal((null, false, "malformed"), (result.Id, result.Ok, result.Reason));
        Assert.False(File.Exists(_paths.CommandFile));
    }

    [Fact]
    public void A_command_file_that_may_still_be_being_written_is_given_a_moment()
    {
        LeaveCommand("{ \"id\": \"0f8f");

        Look(afterMilliseconds: 250);
        Assert.True(File.Exists(_paths.CommandFile));
        Assert.False(File.Exists(_paths.InvalidResultFile));

        Look(afterMilliseconds: 2500);
        Assert.False(File.Exists(_paths.CommandFile));
        Assert.True(File.Exists(_paths.InvalidResultFile));
    }

    [Fact]
    public void A_hand_written_command_without_a_time_is_dated_by_its_file()
    {
        var fingerprint = StampCodec.FingerprintText(Record(1));
        LeaveCommand($$"""{ "id": "{{Session.Id}}", "action": "reincarnate", "slot": 1, "expectedFingerprint": "{{fingerprint}}" }""", ageSeconds: 30);

        Look();

        Assert.True(ReadResult().Applied);
    }

    [Fact]
    public void A_command_left_lying_for_minutes_has_expired()
    {
        var fingerprint = StampCodec.FingerprintText(Record(1));
        LeaveCommand($$"""{ "id": "{{Session.Id}}", "action": "reincarnate", "slot": 1, "expectedFingerprint": "{{fingerprint}}" }""", ageSeconds: 600);

        Look();

        Assert.Equal("expired", ReadResult().Reason);
        Assert.Equal(30, Record(1)[RosterLayout.Level]);
    }

    [Fact]
    public void Nothing_changes_when_the_journal_cannot_be_written()
    {
        Directory.CreateDirectory(_paths.JournalFile); // a folder where the file should be
        LeaveReincarnation(slot: 1);

        Look();

        Assert.Equal(("journalFailed", false), (ReadResult().Reason, ReadResult().Applied));
        Assert.Equal(30, Record(1)[RosterLayout.Level]);
    }

    [Fact]
    public void A_roster_file_someone_is_holding_open_does_not_stop_the_mod()
    {
        Look();
        Record(1)[RosterLayout.Level] = 31;

        using (new FileStream(_paths.RosterFile, FileMode.Open, FileAccess.Read, FileShare.None))
            Look();
        Look();

        Assert.Equal(31, ReadRoster().Units[1].Level);
    }

    [Fact]
    public void The_diagnostic_log_lists_every_command_and_its_outcome()
    {
        LeaveReincarnation(slot: 0); // the real Ramza, level 2: refused

        Look(debugLog: true);

        Assert.Contains(_log, l => l.Contains($"Command {Session.Id}: reincarnate slot 0"));
        Assert.Contains(_log, l => l.Contains("refused (levelTooLow)"));
    }

    [Fact]
    public void Without_the_diagnostic_log_a_refusal_is_not_logged()
    {
        LeaveReincarnation(slot: 0);

        Look();

        Assert.Empty(_log);
        Assert.Equal("levelTooLow", ReadResult().Reason);
    }

    [Fact]
    public void Starting_the_game_drops_a_command_and_old_answers_from_an_earlier_session()
    {
        LeaveReincarnation(slot: 1);
        var oldAnswer = _paths.ResultFile(Guid.NewGuid());
        var freshAnswer = _paths.ResultFile(Guid.NewGuid());
        File.WriteAllText(oldAnswer, "{}");
        File.SetLastWriteTimeUtc(oldAnswer, Start.UtcDateTime.AddDays(-2));
        File.WriteAllText(freshAnswer, "{}");
        File.SetLastWriteTimeUtc(freshAnswer, Start.UtcDateTime.AddHours(-1));

        new BridgeWorker(_paths, "0.4.0", 5000, _log.Add).Prepare(Start.UtcDateTime);

        Assert.False(File.Exists(_paths.CommandFile));
        Assert.False(File.Exists(oldAnswer));
        Assert.True(File.Exists(freshAnswer));
    }
}
