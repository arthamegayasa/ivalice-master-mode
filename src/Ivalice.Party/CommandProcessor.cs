using System.Globalization;
using System.Text.Json;

namespace Ivalice.Party;

/// <summary>A command file as far as it could be understood.</summary>
/// <param name="Id">The command's id when it is a GUID, so it is safe to use in a file name; otherwise null.</param>
/// <param name="Command">The command, or null when <paramref name="Refusal"/> says why there is none.</param>
public sealed record ParsedCommand(Guid? Id, ReincarnationCommand? Command, Refusal Refusal);

/// <summary>Reads, checks and carries out commands. Nothing in here throws on bad input.</summary>
public static class CommandProcessor
{
    /// <summary>A command older than this is refused: the player has long stopped waiting for it.</summary>
    public const int MaxAgeSeconds = 120;

    public static ParsedCommand Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new ParsedCommand(null, null, Refusal.Malformed);
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return new ParsedCommand(null, null, Refusal.Malformed);

            Guid? id = Text(root, "id") is { } idText && Guid.TryParse(idText, out var parsed) ? parsed : null;
            if (id is null) return new ParsedCommand(null, null, Refusal.Malformed);

            var action = Text(root, "action");
            if (action is null) return new ParsedCommand(id, null, Refusal.Malformed);
            if (action != ReincarnationCommand.Reincarnate) return new ParsedCommand(id, null, Refusal.UnknownAction);

            if (!root.TryGetProperty("slot", out var slot) || slot.ValueKind != JsonValueKind.Number || !slot.TryGetInt32(out var slotNumber))
                return new ParsedCommand(id, null, Refusal.Malformed);

            int? expectedLevel = null;
            if (root.TryGetProperty("expectedLevel", out var level) && level.ValueKind != JsonValueKind.Null)
            {
                if (level.ValueKind != JsonValueKind.Number || !level.TryGetInt32(out var levelNumber))
                    return new ParsedCommand(id, null, Refusal.Malformed);
                expectedLevel = levelNumber;
            }

            var simulate = false;
            if (root.TryGetProperty("simulate", out var simulateValue) && simulateValue.ValueKind != JsonValueKind.Null)
            {
                if (simulateValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    return new ParsedCommand(id, null, Refusal.Malformed);
                simulate = simulateValue.GetBoolean();
            }

            return new ParsedCommand(id, new ReincarnationCommand(id.Value.ToString("D"), action, slotNumber,
                Text(root, "expectedFingerprint"), expectedLevel, Text(root, "createdAt"), simulate), Refusal.None);
        }
        catch (JsonException)
        {
            return new ParsedCommand(null, null, Refusal.Malformed);
        }
    }

    /// <summary>The answer to a command that could not be carried out.</summary>
    public static CommandResult Refuse(Guid? id, Refusal refusal, bool safeMode, DateTimeOffset now, UnitEntry? unit = null) =>
        new(id?.ToString("D"), Ok: false, Applied: false, safeMode, refusal.Code(), refusal.Describe(), unit, Stamp(now));

    /// <summary>
    /// Checks a command against the live roster and, when everything holds, reincarnates the unit.
    /// The record is prepared on a copy and written back in one piece, after the journal line is safe.
    /// </summary>
    /// <param name="roster">The game's roster table. Only the one record is written, and only on success.</param>
    /// <param name="battleTable">The game's battle unit table. On success, a leftover battle copy of the unit gets the same level, experience and raw stats.</param>
    /// <param name="fileTime">When the command file was written; used when the command carries no time of its own.</param>
    /// <param name="safeMode">Check and answer, but never write.</param>
    /// <param name="journal">Stores the journal line and reports whether that worked. Not called in safe mode.</param>
    public static CommandResult Execute(ReincarnationCommand command, Span<byte> roster, Span<byte> battleTable,
        ReadOnlySpan<byte> jobTable, GameStatus game, DateTimeOffset now, DateTimeOffset fileTime, bool safeMode,
        Func<JournalEntry, bool> journal, NameResolver? names = null)
    {
        // The player confirmed either a real change or a simulation; a simulation stays one even if
        // safe mode was switched off while the command was waiting.
        safeMode |= command.Simulate;

        Guid? id = Guid.TryParse(command.Id, out var parsed) ? parsed : null;
        if (id is null) return Refuse(null, Refusal.Malformed, safeMode, now);
        if (command.Action != ReincarnationCommand.Reincarnate) return Refuse(id, Refusal.UnknownAction, safeMode, now);

        var createdAt = fileTime;
        if (command.CreatedAt is not null
            && !DateTimeOffset.TryParse(command.CreatedAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out createdAt))
            return Refuse(id, Refusal.Malformed, safeMode, now);
        if (Math.Abs((now - createdAt).TotalSeconds) > MaxAgeSeconds) return Refuse(id, Refusal.Expired, safeMode, now);

        if (roster.Length != RosterLayout.TableSize) return Refuse(id, Refusal.GameStateUnknown, safeMode, now);
        if (command.Slot < 0 || command.Slot >= RosterLayout.Capacity) return Refuse(id, Refusal.BadSlot, safeMode, now);

        var live = RosterLayout.Record(roster, command.Slot);
        if (!RosterRecord.Holds(live, command.Slot)) return Refuse(id, Refusal.EmptySlot, safeMode, now);

        // Without a fingerprint the command could land on whoever holds the slot now.
        if (command.ExpectedFingerprint is null) return Refuse(id, Refusal.Malformed, safeMode, now, RosterExport.Describe(live, command.Slot, battleTable, jobTable, game, names));
        if (!string.Equals(command.ExpectedFingerprint, StampCodec.FingerprintText(live), StringComparison.OrdinalIgnoreCase))
            return Refuse(id, Refusal.FingerprintMismatch, safeMode, now, RosterExport.Describe(live, command.Slot, battleTable, jobTable, game, names));

        var refusal = GameSafety.Evaluate(live, command.Slot, battleTable, game);
        if (refusal != Refusal.None) return Refuse(id, refusal, safeMode, now, RosterExport.Describe(live, command.Slot, battleTable, jobTable, game, names));
        if (command.ExpectedLevel is { } expected && expected != live[RosterLayout.Level])
            return Refuse(id, Refusal.LevelChanged, safeMode, now, RosterExport.Describe(live, command.Slot, battleTable, jobTable, game, names));
        if (Reincarnation.Plan(live) is not { } plan) return Refuse(id, Refusal.UnknownKind, safeMode, now, RosterExport.Describe(live, command.Slot, battleTable, jobTable, game, names));

        Span<byte> after = stackalloc byte[RosterLayout.Stride];
        live.CopyTo(after);
        Reincarnation.Apply(after, plan);

        if (safeMode)
            return new CommandResult(command.Id, Ok: true, Applied: false, SafeMode: true, Refusal.None.Code(),
                "Safe mode: nothing was changed. The unit shown is what it would have become.", RosterExport.Describe(after, command.Slot, battleTable, jobTable, game, names), Stamp(now));

        var entry = new JournalEntry(Stamp(now), command.Id, command.Slot, StampCodec.FingerprintText(live),
            plan.LevelBefore, plan.Stamp.PowerPermille - plan.GainPermille, plan.Stamp.PowerPermille,
            Convert.ToHexString(live), Convert.ToHexString(after));
        bool journaled;
        try { journaled = journal(entry); }
        catch (Exception) { journaled = false; }
        if (!journaled) return Refuse(id, Refusal.JournalFailed, safeMode, now, RosterExport.Describe(live, command.Slot, battleTable, jobTable, game, names));

        after.CopyTo(live);
        BattleBinding.MirrorAll(battleTable, command.Slot, live);
        return new CommandResult(command.Id, Ok: true, Applied: true, SafeMode: false, Refusal.None.Code(),
            $"Reincarnated: level {plan.LevelBefore} to 1, Power +{plan.GainPermille / 10.0:0.0}% (total {plan.Stamp.PowerPermille / 10.0:0.0}%).",
            RosterExport.Describe(live, command.Slot, battleTable, jobTable, game, names), Stamp(now));
    }

    public static string Stamp(DateTimeOffset time) =>
        time.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
