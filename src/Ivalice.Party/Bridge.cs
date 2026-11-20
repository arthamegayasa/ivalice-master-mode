using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ivalice.Party;

/// <summary>
/// The files through which the mod inside the game and Ivalice Control Center talk to each other.
/// The mod writes roster.json and result files; the Control Center writes command.json.
/// </summary>
public sealed record BridgePaths(string Root)
{
    public static BridgePaths Default { get; } = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IvaliceControlCenter"));

    public string BridgeDirectory => Path.Combine(Root, "bridge");
    public string RosterFile => Path.Combine(BridgeDirectory, "roster.json");
    public string CommandFile => Path.Combine(BridgeDirectory, "command.json");
    public string ResultFile(Guid commandId) => Path.Combine(BridgeDirectory, $"result-{commandId:D}.json");
    /// <summary>Where the answer to a command without a usable id goes.</summary>
    public string InvalidResultFile => Path.Combine(BridgeDirectory, "result-invalid.json");
    public string JournalFile => Path.Combine(Root, "reincarnation-journal.jsonl");
    public string SaveBackupsDirectory => Path.Combine(Root, "save-backups");
}

/// <summary>roster.json: what the mod saw in the game the last time it looked.</summary>
/// <param name="WrittenAt">UTC time in ISO 8601 form.</param>
/// <param name="SafeMode">The mod is set to never change game memory.</param>
public sealed record RosterDocument(
    int Schema,
    string ModVersion,
    int ProcessId,
    string WrittenAt,
    bool SafeMode,
    GameStatus Game,
    IReadOnlyList<UnitEntry> Units)
{
    public const int CurrentSchema = 1;

    /// <summary>The minimum level the mod is set to require.</summary>
    public int MinimumLevel { get; init; } = ReincarnationRules.DefaultMinimumLevel;
}

/// <summary>command.json: a request from the Control Center.</summary>
/// <param name="ExpectedFingerprint">Fingerprint of the unit the player confirmed; the command is refused if the slot holds another.</param>
/// <param name="ExpectedLevel">Level the player saw when confirming; optional.</param>
/// <param name="CreatedAt">UTC time in ISO 8601 form; optional, the file's own time is used without it.</param>
public sealed record ReincarnationCommand(
    string Id,
    string Action,
    int Slot,
    string? ExpectedFingerprint = null,
    int? ExpectedLevel = null,
    string? CreatedAt = null,
    bool Simulate = false)
{
    public const string Reincarnate = "reincarnate";
}

/// <summary>result-&lt;id&gt;.json: the mod's answer to one command.</summary>
/// <param name="Ok">The command passed every check.</param>
/// <param name="Applied">Game memory was changed. False in safe mode even when <paramref name="Ok"/> is true.</param>
/// <param name="Reason">"eligible", or the code of the refusal.</param>
/// <param name="Unit">The unit afterwards; in safe mode, what it would have become.</param>
public sealed record CommandResult(
    string? Id,
    bool Ok,
    bool Applied,
    bool SafeMode,
    string Reason,
    string Message,
    UnitEntry? Unit,
    string FinishedAt);

/// <summary>One line of the journal: enough to put a record back by hand.</summary>
public sealed record JournalEntry(
    string Time,
    string CommandId,
    int Slot,
    string Fingerprint,
    int LevelBefore,
    int PowerBeforePermille,
    int PowerAfterPermille,
    string Before,
    string After);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(RosterDocument))]
[JsonSerializable(typeof(ReincarnationCommand))]
[JsonSerializable(typeof(CommandResult))]
[JsonSerializable(typeof(JournalEntry))]
public sealed partial class BridgeJson : JsonSerializerContext;

/// <summary>How fresh and how trustworthy the roster.json on disk is.</summary>
public enum FeedStatus
{
    Ok,
    /// <summary>There is no roster.json: the mod has not run yet.</summary>
    NoData,
    /// <summary>The file is not something this version understands.</summary>
    Unreadable,
    /// <summary>The file was written by a game process that is no longer running.</summary>
    OtherSession,
    /// <summary>The game process is running but has not refreshed the file for a while.</summary>
    Stale,
}

public sealed record FeedReading(FeedStatus Status, RosterDocument? Roster, double? AgeSeconds);

public static class RosterFeed
{
    /// <summary>The mod rewrites roster.json at least this often, even when nothing changed.</summary>
    public const int HeartbeatSeconds = 5;
    public const int StaleAfterSeconds = 15;

    public static string Serialize(RosterDocument document) => JsonSerializer.Serialize(document, BridgeJson.Default.RosterDocument);

    /// <param name="json">Contents of roster.json, or null when the file does not exist.</param>
    /// <param name="gameProcessIds">Ids of the game processes running now.</param>
    public static FeedReading Evaluate(string? json, DateTimeOffset now, IReadOnlyCollection<int> gameProcessIds)
    {
        if (json is null) return new FeedReading(FeedStatus.NoData, null, null);

        RosterDocument? document;
        try
        {
            document = JsonSerializer.Deserialize(json, BridgeJson.Default.RosterDocument);
        }
        catch (JsonException)
        {
            return new FeedReading(FeedStatus.Unreadable, null, null);
        }

        if (document is not { Schema: RosterDocument.CurrentSchema, Game.Inputs: not null, Units: not null }
            || !DateTimeOffset.TryParse(document.WrittenAt, null, System.Globalization.DateTimeStyles.AssumeUniversal, out var writtenAt))
            return new FeedReading(FeedStatus.Unreadable, null, null);

        var age = Math.Max(0, (now - writtenAt).TotalSeconds);
        if (!gameProcessIds.Contains(document.ProcessId)) return new FeedReading(FeedStatus.OtherSession, document, age);
        return new FeedReading(age > StaleAfterSeconds ? FeedStatus.Stale : FeedStatus.Ok, document, age);
    }
}
