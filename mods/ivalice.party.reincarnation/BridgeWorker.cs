using System.Text.Json;
using Ivalice.Party;

namespace ivalice.party.reincarnation;

/// <param name="SafeMode">Check and answer commands, but never change the roster.</param>
/// <param name="DebugLog">Log every command and its outcome, not only applied reincarnations.</param>
internal readonly record struct WorkerSettings(bool SafeMode, bool DebugLog);

/// <summary>
/// The file side of the mod: writes the roster out for Ivalice Control Center and answers the commands
/// it leaves. It works on whatever memory it is handed, so it runs the same inside the game and in tests.
/// Every method is meant for the game's main thread; nothing here throws for a file that is missing,
/// locked or malformed.
/// </summary>
internal sealed class BridgeWorker(BridgePaths paths, string modVersion, int processId, Action<string> log)
{
    /// <summary>How often the roster is compared with what was last written out.</summary>
    public const int ExportMilliseconds = 1000;

    private long _lastExportAt = long.MinValue / 2;
    private string? _lastContent;
    private DateTimeOffset _lastWriteAt;
    private string? _lastHandledFile;
    private bool _reportedExportFailure;

    /// <summary>
    /// Creates the bridge folder and removes what an earlier game session left behind: a command never
    /// outlives the session it was meant for, and answers nobody read are dropped after a day.
    /// </summary>
    public void Prepare(DateTime utcNow)
    {
        Directory.CreateDirectory(paths.BridgeDirectory);
        File.Delete(paths.CommandFile);
        foreach (var old in Directory.EnumerateFiles(paths.BridgeDirectory, "result-*.json"))
            if (File.GetLastWriteTimeUtc(old) < utcNow.AddDays(-1)) File.Delete(old);
    }

    /// <summary>One look at the files: answer a waiting command, then refresh the roster file when due.</summary>
    /// <param name="tickCount">A millisecond counter that never runs backwards.</param>
    public void Tick(long tickCount, DateTimeOffset now, Span<byte> roster, ReadOnlySpan<byte> battleTable,
        ReadOnlySpan<byte> jobTable, GameStatus game, WorkerSettings settings, NameResolver? names)
    {
        var answered = HandleCommand(now, roster, battleTable, jobTable, game, settings, names);
        if (!answered && tickCount - _lastExportAt < ExportMilliseconds) return;
        _lastExportAt = tickCount;
        Export(now, roster, battleTable, jobTable, game, settings, names);
    }

    /// <returns>True when a command was answered, so the roster file should be refreshed at once.</returns>
    private bool HandleCommand(DateTimeOffset now, Span<byte> roster, ReadOnlySpan<byte> battleTable,
        ReadOnlySpan<byte> jobTable, GameStatus game, WorkerSettings settings, NameResolver? names)
    {
        if (!File.Exists(paths.CommandFile)) return false;

        string text;
        DateTimeOffset fileTime;
        try
        {
            text = File.ReadAllText(paths.CommandFile);
            fileTime = new DateTimeOffset(File.GetLastWriteTimeUtc(paths.CommandFile), TimeSpan.Zero);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false; // Still being written; look again on the next tick.
        }

        var parsed = CommandProcessor.Parse(text);
        // A file caught half-written reads as garbage; give its writer a moment before answering.
        if (parsed is { Id: null, Refusal: Refusal.Malformed } && (now - fileTime).TotalSeconds < 2) return false;

        // The same file seen twice was already answered; only removing it failed last time.
        var fileKey = $"{parsed.Id}@{fileTime.UtcTicks}";
        if (fileKey == _lastHandledFile)
        {
            TryDelete(paths.CommandFile);
            return false;
        }
        _lastHandledFile = fileKey;

        CommandResult result;
        if (parsed.Command is not { } command)
            result = CommandProcessor.Refuse(parsed.Id, parsed.Refusal, settings.SafeMode, now);
        else
        {
            if (settings.DebugLog)
                log($"Command {command.Id}: reincarnate slot {command.Slot}, fingerprint {command.ExpectedFingerprint ?? "none"}, level {command.ExpectedLevel?.ToString() ?? "any"}.");
            result = CommandProcessor.Execute(command, roster, battleTable, jobTable, game, now, fileTime, settings.SafeMode,
                WriteJournal, names);
        }

        if (result.Applied)
            log($"Slot {result.Unit?.Slot}: {result.Message} Journal: {paths.JournalFile}");
        else if (settings.DebugLog)
            log($"Command {result.Id ?? "without id"}: {(result.Ok ? "accepted" : "refused")} ({result.Reason}). {result.Message}");

        try
        {
            var path = parsed.Id is { } id ? paths.ResultFile(id) : paths.InvalidResultFile;
            WriteAtomically(path, JsonSerializer.Serialize(result, BridgeJson.Default.CommandResult));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log($"Could not write the answer to command {result.Id ?? "without id"} ({e.Message}). Outcome: {result.Reason}, applied: {result.Applied}.");
        }
        TryDelete(paths.CommandFile);
        return true;
    }

    private bool WriteJournal(JournalEntry entry)
    {
        File.AppendAllText(paths.JournalFile, JsonSerializer.Serialize(entry, BridgeJson.Default.JournalEntry) + "\n");
        return true;
    }

    private void Export(DateTimeOffset now, ReadOnlySpan<byte> roster, ReadOnlySpan<byte> battleTable,
        ReadOnlySpan<byte> jobTable, GameStatus game, WorkerSettings settings, NameResolver? names)
    {
        var units = RosterExport.DescribeAll(roster, battleTable, jobTable, game, names);
        var document = new RosterDocument(RosterDocument.CurrentSchema, modVersion, processId, WrittenAt: "",
            settings.SafeMode, game, units);

        // Written when something changed, and otherwise often enough to show the game is still alive.
        var content = RosterFeed.Serialize(document);
        if (content == _lastContent && (now - _lastWriteAt).TotalSeconds < RosterFeed.HeartbeatSeconds) return;

        try
        {
            WriteAtomically(paths.RosterFile, RosterFeed.Serialize(document with { WrittenAt = CommandProcessor.Stamp(now) }));
            _lastContent = content;
            _lastWriteAt = now;
            _reportedExportFailure = false;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The Control Center may be reading the file; the next look tries again.
            if (!_reportedExportFailure && settings.DebugLog) log($"Could not write the roster file ({e.Message}); will retry.");
            _reportedExportFailure = true;
        }
    }

    private static void WriteAtomically(string path, string content)
    {
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, content);
        File.Move(temporary, path, overwrite: true);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
