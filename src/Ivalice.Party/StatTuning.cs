using System.Text;
using System.Text.Json;

namespace Ivalice.Party;

/// <summary>
/// Replacement values for one job row. Growth is the game's growth constant (a level-up adds
/// raw / (growth + level), so a lower number grows faster); multiplier is the percentage of the raw stat
/// the game shows. A null entry leaves the game's own value alone.
/// </summary>
public sealed record JobTuning(IReadOnlyList<int?> Growth, IReadOnlyList<int?> Multiplier)
{
    public bool IsEmpty => Growth.All(v => v is null) && Multiplier.All(v => v is null);
}

/// <summary>
/// Percentages applied to one character on top of whatever its current job gives. 100 changes nothing;
/// a growth of 200 makes the stat grow about twice as fast, a multiplier of 150 shows half as much again.
/// </summary>
public sealed record CharacterTuning(string? Name, IReadOnlyList<int> GrowthPercent, IReadOnlyList<int> MultiplierPercent)
{
    public bool IsEmpty => GrowthPercent.All(v => v == 100) && MultiplierPercent.All(v => v == 100);
}

/// <summary>
/// The rules of the Stat Tuning mod: what the player set per job and per character, and how those turn
/// the ten growth and multiplier bytes the game copies from a job row into a unit.
/// </summary>
public sealed class StatTuningRules
{
    public const int CurrentSchema = 1;
    public const int MinByte = 1;
    public const int MaxByte = 255;
    public const int MinPercent = 25;
    public const int MaxPercent = 400;
    /// <summary>Growth and multiplier pairs for HP, MP, Speed, Physical Attack and Magick Attack.</summary>
    public const int PairBytes = StatBlock.Count * 2;

    public static StatTuningRules Empty { get; } = new(false, new Dictionary<int, JobTuning>(), new Dictionary<string, CharacterTuning>());

    /// <summary>Job rules also change units that are not the player's. Off by default: enemies keep the game's values.</summary>
    public bool ApplyJobsToEnemies { get; }
    public IReadOnlyDictionary<int, JobTuning> Jobs { get; }
    /// <summary>Keyed by the unit fingerprint the roster feed shows (eight hex digits, upper case).</summary>
    public IReadOnlyDictionary<string, CharacterTuning> Characters { get; }

    public bool IsEmpty => Jobs.Count == 0 && Characters.Count == 0;

    public StatTuningRules(bool applyJobsToEnemies, IReadOnlyDictionary<int, JobTuning> jobs,
        IReadOnlyDictionary<string, CharacterTuning> characters)
    {
        ApplyJobsToEnemies = applyJobsToEnemies;
        Jobs = jobs;
        Characters = characters;
    }

    /// <summary>
    /// Rewrites the pairs the game just copied from a job row, in place.
    /// </summary>
    /// <param name="pairs">Ten bytes: growth, multiplier for each of the five stats.</param>
    /// <param name="fingerprint">The roster unit behind this battle unit, or null when it is not a party member.</param>
    /// <returns>True when a byte changed.</returns>
    public bool Apply(Span<byte> pairs, int jobRow, bool playerOwned, string? fingerprint)
    {
        if (pairs.Length != PairBytes) return false;
        var changed = false;

        if ((playerOwned || ApplyJobsToEnemies) && Jobs.TryGetValue(jobRow, out var job))
        {
            for (var stat = 0; stat < StatBlock.Count; stat++)
            {
                changed |= Set(pairs, stat * 2, job.Growth[stat]);
                changed |= Set(pairs, stat * 2 + 1, job.Multiplier[stat]);
            }
        }

        if (playerOwned && fingerprint is not null && Characters.TryGetValue(fingerprint, out var character))
        {
            for (var stat = 0; stat < StatBlock.Count; stat++)
            {
                // A stat left at 100% keeps the very byte the job gave, also where that is 0.
                if (character.GrowthPercent[stat] != 100)
                {
                    // The game treats a growth of 0 as 1.
                    var growth = Math.Max(1, (int)pairs[stat * 2]);
                    changed |= Set(pairs, stat * 2, ScaleGrowth(growth, character.GrowthPercent[stat]));
                }
                if (character.MultiplierPercent[stat] != 100)
                    changed |= Set(pairs, stat * 2 + 1, ScaleMultiplier(pairs[stat * 2 + 1], character.MultiplierPercent[stat]));
            }
        }

        return changed;
    }

    internal static int ScaleGrowth(int value, int percent) => Scale(value * 100, percent);
    internal static int ScaleMultiplier(int value, int percent) => Scale(value * percent, 100);

    private static int Scale(int numerator, int denominator) =>
        Math.Clamp((numerator + denominator / 2) / denominator, MinByte, MaxByte);

    private static bool Set(Span<byte> pairs, int index, int? value)
    {
        if (value is not { } wanted || pairs[index] == wanted) return false;
        pairs[index] = (byte)wanted;
        return true;
    }

    /// <summary>Reads rules written by <see cref="Serialize"/> or by hand.</summary>
    /// <returns>Null when the text is not usable; values out of range are pulled into range.</returns>
    public static StatTuningRules? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (root.TryGetProperty("schema", out var schema) && (schema.ValueKind != JsonValueKind.Number || schema.GetInt32() != CurrentSchema)) return null;

            var toEnemies = root.TryGetProperty("applyJobsToEnemies", out var flag) && flag.ValueKind == JsonValueKind.True;

            // A list of the wrong shape is a damaged document, not a request to drop every rule.
            var hasJobs = root.TryGetProperty("jobs", out var jobList);
            var hasCharacters = root.TryGetProperty("characters", out var characterList);
            if (hasJobs && jobList.ValueKind != JsonValueKind.Array) return null;
            if (hasCharacters && characterList.ValueKind != JsonValueKind.Array) return null;

            var jobs = new Dictionary<int, JobTuning>();
            if (hasJobs)
            {
                foreach (var entry in jobList.EnumerateArray())
                {
                    if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty("job", out var id)
                        || id.ValueKind != JsonValueKind.Number || !id.TryGetInt32(out var row)) continue;
                    if (row < 0 || row >= JobTable.Rows) continue;
                    var tuning = new JobTuning(Bytes(entry, "growth"), Bytes(entry, "multiplier"));
                    if (!tuning.IsEmpty) jobs[row] = tuning;
                }
            }

            var characters = new Dictionary<string, CharacterTuning>(StringComparer.OrdinalIgnoreCase);
            if (hasCharacters)
            {
                foreach (var entry in characterList.EnumerateArray())
                {
                    if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty("fingerprint", out var key) || key.ValueKind != JsonValueKind.String) continue;
                    var fingerprint = key.GetString()!.Trim().ToUpperInvariant();
                    if (fingerprint.Length != 8 || !fingerprint.All(Uri.IsHexDigit)) continue;
                    var name = entry.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
                    var tuning = new CharacterTuning(name, Percents(entry, "growthPercent"), Percents(entry, "multiplierPercent"));
                    if (!tuning.IsEmpty) characters[fingerprint] = tuning;
                }
            }

            return new StatTuningRules(toEnemies, jobs, characters);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException or OverflowException or ArgumentException)
        {
            return null;
        }
    }

    private static int?[] Bytes(JsonElement entry, string property)
    {
        var values = new int?[StatBlock.Count];
        if (!entry.TryGetProperty(property, out var list) || list.ValueKind != JsonValueKind.Array) return values;
        var index = 0;
        foreach (var item in list.EnumerateArray())
        {
            if (index >= values.Length) break;
            if (item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out var value)) values[index] = Math.Clamp(value, MinByte, MaxByte);
            index++;
        }
        return values;
    }

    private static int[] Percents(JsonElement entry, string property)
    {
        var values = Enumerable.Repeat(100, StatBlock.Count).ToArray();
        if (!entry.TryGetProperty(property, out var list) || list.ValueKind != JsonValueKind.Array) return values;
        var index = 0;
        foreach (var item in list.EnumerateArray())
        {
            if (index >= values.Length) break;
            if (item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out var value)) values[index] = Math.Clamp(value, MinPercent, MaxPercent);
            index++;
        }
        return values;
    }

    public string Serialize()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schema", CurrentSchema);
            writer.WriteBoolean("applyJobsToEnemies", ApplyJobsToEnemies);

            writer.WriteStartArray("jobs");
            foreach (var (row, job) in Jobs.OrderBy(j => j.Key))
            {
                writer.WriteStartObject();
                writer.WriteNumber("job", row);
                if (JobNames.Find(row) is { } name) writer.WriteString("name", name);
                WriteList(writer, "growth", job.Growth);
                WriteList(writer, "multiplier", job.Multiplier);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            writer.WriteStartArray("characters");
            foreach (var (fingerprint, character) in Characters.OrderBy(c => c.Key, StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("fingerprint", fingerprint);
                if (character.Name is not null) writer.WriteString("name", character.Name);
                WriteList(writer, "growthPercent", character.GrowthPercent.Select(v => (int?)v).ToArray());
                WriteList(writer, "multiplierPercent", character.MultiplierPercent.Select(v => (int?)v).ToArray());
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteList(Utf8JsonWriter writer, string name, IReadOnlyList<int?> values)
    {
        writer.WriteStartArray(name);
        foreach (var value in values)
        {
            if (value is { } number) writer.WriteNumberValue(number);
            else writer.WriteNullValue();
        }
        writer.WriteEndArray();
    }
}

/// <summary>What the Stat Tuning mod reads from a battle unit record to decide which rules apply.</summary>
public readonly record struct TunedUnit(int JobRow, bool PlayerOwned, string? Fingerprint)
{
    private const int Marker = 0x00;
    private const int RosterSlot = 0x02;
    private const int Job = 0x03;
    private const int Flags = 0x05;
    private const byte TeamMask = 0x30;
    private const int SpecialJobLevel = 0xEE;
    private const byte SpecialJob = 0xA1;
    private const byte SpecialJobUpgradedRow = 0xA4;
    /// <summary>Where the game keeps the pairs it copied from the job row.</summary>
    public const int Pairs = 0x8A;

    /// <param name="unit">A battle unit record (0x200 bytes), also the form the game builds for its menus.</param>
    /// <param name="roster">The party roster, or empty when it is not known.</param>
    public static TunedUnit Identify(ReadOnlySpan<byte> unit, ReadOnlySpan<byte> roster)
    {
        var job = unit[Job];
        // Same exception as the game's own code: job 0xA1 uses row 0xA4 once its job level reaches 8.
        var row = job == SpecialJob && (unit[SpecialJobLevel] & 0x0F) >= 8 ? SpecialJobUpgradedRow : job;
        var playerOwned = (unit[Flags] & TeamMask) == 0;

        string? fingerprint = null;
        var slot = unit[RosterSlot];
        if (playerOwned && slot < RosterLayout.Capacity && roster.Length == RosterLayout.TableSize)
        {
            var record = RosterLayout.Record(roster, slot);
            if (RosterRecord.Holds(record, slot) && SameCharacter(record[RosterLayout.Marker], unit[Marker]))
                fingerprint = StampCodec.FingerprintText(record);
        }

        return new TunedUnit(row, playerOwned, fingerprint);
    }

    /// <summary>Ramza's marker changes with the chapter (1 to 3); every other unit keeps its own.</summary>
    private static bool SameCharacter(byte rosterMarker, byte unitMarker) =>
        rosterMarker == unitMarker || (rosterMarker is >= 1 and <= 3 && unitMarker is >= 1 and <= 3);
}

/// <summary>One row of jobs.json: the values the game itself holds for a job, for the Control Center to show.</summary>
public static class JobCatalog
{
    /// <summary>Writes the growth and multiplier pairs of every job row as JSON.</summary>
    public static string Describe(ReadOnlySpan<byte> jobTable)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schema", 1);
            writer.WriteStartArray("jobs");
            for (var row = 0; row < JobTable.Rows && jobTable.Length >= (row + 1) * JobTable.RowSize; row++)
            {
                var pairs = jobTable.Slice(row * JobTable.RowSize + JobTable.GrowthOffset, StatTuningRules.PairBytes);
                writer.WriteStartObject();
                writer.WriteNumber("job", row);
                writer.WriteStartArray("growth");
                for (var stat = 0; stat < StatBlock.Count; stat++) writer.WriteNumberValue(pairs[stat * 2]);
                writer.WriteEndArray();
                writer.WriteStartArray("multiplier");
                for (var stat = 0; stat < StatBlock.Count; stat++) writer.WriteNumberValue(pairs[stat * 2 + 1]);
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }
}

/// <summary>Where the Stat Tuning mod and the Control Center exchange files.</summary>
public sealed record StatTuningPaths(string Root)
{
    public static StatTuningPaths Default { get; } = new(Path.Combine(BridgePaths.Default.Root, "stat-tuning"));

    /// <summary>Written by the Control Center, read by the mod (again whenever it changes).</summary>
    public string RulesFile => Path.Combine(Root, "rules.json");
    /// <summary>Written by the mod: the game's own job values.</summary>
    public string JobsFile => Path.Combine(Root, "jobs.json");
}
