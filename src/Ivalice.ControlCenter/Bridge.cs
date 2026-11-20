using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Ivalice.Core;
using Ivalice.Party;

namespace Ivalice.ControlCenter;

/// <summary>
/// Answers requests from the UI. Each request is <c>{ id, method, args }</c> and each reply is
/// <c>{ id, ok, result }</c> or <c>{ id, ok: false, error }</c>, where error is text meant for the player.
/// </summary>
public sealed class Bridge
{
    private const string GameProcess = "FFT_enhanced";
    private const string ReincarnationMod = "ivalice.party.reincarnation";
    private const string StatTuningMod = "ivalice.party.stattuning";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly Catalog _catalog = Catalog.Load(Path.Combine(AppContext.BaseDirectory, "catalog", "mods.json"));
    private readonly BridgePaths _paths = BridgePaths.Default;
    private readonly StatTuningPaths _statPaths = StatTuningPaths.Default;

    public string Handle(string requestJson)
    {
        JsonNode? id = null;
        try
        {
            var request = JsonNode.Parse(requestJson)!.AsObject();
            id = request["id"]?.DeepClone();
            var method = request["method"]!.GetValue<string>();
            var args = request["args"] as JsonObject ?? [];
            var result = Dispatch(method, args);
            return new JsonObject { ["id"] = id, ["ok"] = true, ["result"] = JsonSerializer.SerializeToNode(result, Json) }
                .ToJsonString();
        }
        catch (ModOperationException ex)
        {
            return Error(id, ex.Message);
        }
        catch (Exception ex)
        {
            return Error(id, "Terjadi kesalahan tak terduga: " + ex.Message);
        }
    }

    private static string Error(JsonNode? id, string message) =>
        new JsonObject { ["id"] = id?.DeepClone(), ["ok"] = false, ["error"] = message }.ToJsonString();

    private object? Dispatch(string method, JsonObject args)
    {
        switch (method)
        {
            case "getState":
                return Snapshot();

            case "setEnabled":
                Manager().SetEnabled(args["modId"]!.GetValue<string>(), args["enabled"]!.GetValue<bool>());
                return Snapshot();

            case "setSetting":
                Manager().SetSetting(args["modId"]!.GetValue<string>(), args["key"]!.GetValue<string>(),
                    args["value"]?.DeepClone());
                return Snapshot();

            case "launch":
                Launch();
                return Snapshot();

            case "getLog":
                return ReadLog();

            case "getRoster":
                return ReadRoster();

            case "reincarnate":
                return SendReincarnation(args["slot"]!.GetValue<int>(), args["fingerprint"]!.GetValue<string>(),
                    args["level"]!.GetValue<int>(), args["simulate"]?.GetValue<bool>() ?? false);

            case "getReincarnationResult":
                return ReadReincarnationResult(args["commandId"]!.GetValue<string>());

            case "cancelReincarnation":
                return CancelReincarnation(args["commandId"]!.GetValue<string>());

            case "getStatTuning":
                return ReadStatTuning();

            case "applyStatPreset":
                return ApplyStatPreset(args);

            case "saveStatTuning":
                return SaveStatTuning(args["rules"]);

            case "open":
                Open(args["target"]!.GetValue<string>(), args["value"]?.GetValue<string>());
                return null;

            default:
                throw new ModOperationException($"Perintah '{method}' tidak dikenal.");
        }
    }

    private static ReloadedLayout Layout() =>
        ReloadedLayout.TryLoad()
        ?? throw new ModOperationException("Reloaded-II belum terpasang di komputer ini. Jalankan skrip instalasi lebih dulu.");

    private ModManager Manager() => new(Layout(), _catalog);

    private object Snapshot()
    {
        var layout = ReloadedLayout.TryLoad();
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
        if (layout is null)
            return new { version, reloadedFound = false, gameRunning = false, state = (AppState?)null, layout = (object?)null };

        return new
        {
            version,
            reloadedFound = true,
            gameRunning = IsGameRunning(),
            state = (AppState?)new ModManager(layout, _catalog).GetState(),
            layout = (object?)new { layout.ModsDirectory, layout.LogsDirectory, layout.LauncherPath },
        };
    }

    private static bool IsGameRunning() => GameProcessIds().Count > 0;

    private static List<int> GameProcessIds()
    {
        var found = Process.GetProcessesByName(GameProcess);
        var ids = found.Select(p => p.Id).ToList();
        foreach (var p in found) p.Dispose();
        return ids;
    }

    // ---- Reincarnation: talks to the mod inside the game through the files in BridgePaths ----

    /// <summary>
    /// The party as the mod last wrote it out, with a status saying whether it can be trusted:
    /// modNotInstalled, modDisabled, gameNotRunning, noData, unreadable, stale or ok.
    /// </summary>
    private object ReadRoster()
    {
        var layout = ReloadedLayout.TryLoad();
        var mods = layout is null ? null : new ModManager(layout, _catalog).GetState().Mods;
        var mod = mods?.FirstOrDefault(m => m.Id == ReincarnationMod);
        var processIds = GameProcessIds();

        string status;
        FeedReading? reading = null;
        if (mod is null || !mod.Installed) status = "modNotInstalled";
        else if (!mod.Enabled) status = "modDisabled";
        else if (processIds.Count == 0) status = "gameNotRunning";
        else
        {
            reading = RosterFeed.Evaluate(ReadTextOrNull(_paths.RosterFile), DateTimeOffset.UtcNow, processIds);
            status = reading.Status switch
            {
                FeedStatus.Ok => "ok",
                FeedStatus.Stale => "stale",
                FeedStatus.Unreadable => "unreadable",
                // A file from an earlier game session says nothing about this one.
                _ => "noData",
            };
        }

        var usable = reading?.Status is FeedStatus.Ok or FeedStatus.Stale;
        var roster = usable ? reading!.Roster : null;
        if (roster is not null && mods!.Any(m => m.Id == StatTuningMod && m.Installed && m.Enabled)
            && StatTuningRules.Parse(ReadTextOrNull(_statPaths.RulesFile)) is { IsEmpty: false } tuning)
            roster = TunedRoster.Apply(roster, tuning, ReadJobs());

        return new
        {
            status,
            ageSeconds = usable ? reading!.AgeSeconds : null,
            roster,
            statTuningApplied = usable && !ReferenceEquals(roster, reading!.Roster),
            commandPending = File.Exists(_paths.CommandFile),
            rules = new
            {
                minimumLevel = usable ? reading!.Roster!.MinimumLevel : ReincarnationRules.DefaultMinimumLevel,
                permillePerLevel = ReincarnationRules.PermillePerLevel,
                maxPowerPermille = ReincarnationRules.MaxPowerPermille,
                commandMaxAgeSeconds = CommandProcessor.MaxAgeSeconds,
            },
            journalFile = _paths.JournalFile,
            saveBackupsDirectory = _paths.SaveBackupsDirectory,
        };
    }

    /// <summary>Backs the save files up, then leaves a command for the mod to pick up.</summary>
    /// <param name="simulate">What the player was shown: true when the dialog promised that nothing would change.</param>
    private object SendReincarnation(int slot, string fingerprint, int level, bool simulate)
    {
        if (!IsGameRunning()) throw new ModOperationException("Game tidak sedang berjalan.");
        if (File.Exists(_paths.CommandFile) || File.Exists(_paths.CommandFile + ".taken"))
            throw new ModOperationException("Masih ada perintah yang menunggu game. Tunggu sampai selesai atau batalkan dulu.");

        var backup = SaveBackup.Create(SaveBackup.DefaultSaveDirectory, _paths.SaveBackupsDirectory, DateTime.Now);

        var command = new ReincarnationCommand(Guid.NewGuid().ToString("D"), ReincarnationCommand.Reincarnate, slot,
            fingerprint, level, CommandProcessor.Stamp(DateTimeOffset.UtcNow), simulate);
        try
        {
            Directory.CreateDirectory(_paths.BridgeDirectory);
            // Written under another name first, so the mod never reads half a file.
            var temporary = _paths.CommandFile + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(command, BridgeJson.Default.ReincarnationCommand));
            File.Move(temporary, _paths.CommandFile, overwrite: false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new ModOperationException("Perintah tidak bisa dikirim ke game: " + e.Message);
        }

        return new { commandId = command.Id, backupDirectory = backup.Directory, backupFiles = backup.Files };
    }

    /// <summary>The mod's answer to a command, once it has one. The answer file is removed after reading.</summary>
    private object ReadReincarnationResult(string commandId)
    {
        var path = _paths.ResultFile(ParseCommandId(commandId));
        var text = ReadTextOrNull(path);
        if (text is null) return new { done = false, result = (CommandResult?)null };

        CommandResult? result;
        try { result = JsonSerializer.Deserialize(text, BridgeJson.Default.CommandResult); }
        catch (JsonException) { result = null; }
        if (result is null) throw new ModOperationException("Jawaban dari game tidak bisa dibaca.");

        try { File.Delete(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return new { done = true, result = (CommandResult?)result };
    }

    /// <summary>Takes a command back, if the game has not picked it up yet.</summary>
    private object CancelReincarnation(string commandId)
    {
        var id = ParseCommandId(commandId);
        var pending = CommandProcessor.Parse(ReadTextOrNull(_paths.CommandFile));
        if (pending.Id != id) return new { cancelled = false };
        try
        {
            File.Delete(_paths.CommandFile);
            return new { cancelled = true };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new { cancelled = false };
        }
    }

    // ---- Stat Tuning: rules.json is written here and read by the mod; jobs.json comes from the mod ----

    private object ReadStatTuning()
    {
        var layout = ReloadedLayout.TryLoad();
        var mod = layout is null ? null
            : new ModManager(layout, _catalog).GetState().Mods.FirstOrDefault(m => m.Id == StatTuningMod);
        var status = mod is null || !mod.Installed ? "modNotInstalled" : !mod.Enabled ? "modDisabled" : "ok";

        // Three different things: no file yet, a file that could not be opened just now, a file that is not rules.
        var rulesExist = File.Exists(_statPaths.RulesFile);
        var rulesText = ReadTextOrNull(_statPaths.RulesFile);
        var rulesBusy = rulesExist && rulesText is null;
        var rules = StatTuningRules.Parse(rulesText);
        var processIds = GameProcessIds();

        // The party comes from the Reincarnation mod's feed; without it, characters already in the rules can still be edited.
        var party = new List<object>();
        if (processIds.Count > 0)
        {
            var reading = RosterFeed.Evaluate(ReadTextOrNull(_paths.RosterFile), DateTimeOffset.UtcNow, processIds);
            if (reading.Status is FeedStatus.Ok or FeedStatus.Stale)
                party.AddRange(reading.Roster!.Units.Select(u => (object)new { u.Fingerprint, u.Name, u.JobId, u.JobName, u.Level, u.Slot }));
        }

        return new
        {
            status,
            gameRunning = processIds.Count > 0,
            jobs = ReadJobs().Values,
            rules = JsonNode.Parse((rules ?? StatTuningRules.Empty).Serialize()),
            rulesUnreadable = rulesText is not null && rules is null,
            rulesBusy,
            party,
            limits = new
            {
                minValue = StatTuningRules.MinByte, maxValue = StatTuningRules.MaxByte,
                minPercent = StatTuningRules.MinPercent, maxPercent = StatTuningRules.MaxPercent,
                minMove = StatTuningRules.MinMove, maxMove = StatTuningRules.MaxMove,
                minJump = StatTuningRules.MinJump, maxJump = StatTuningRules.MaxJump,
                minBonus = StatTuningRules.MinBonus, maxBonus = StatTuningRules.MaxBonus,
            },
            rulesFile = _statPaths.RulesFile,
        };
    }

    /// <summary>The game's own job values as the mod last wrote them, with names; empty until the game ran once with the mod.</summary>
    private Dictionary<int, JobDefaults> ReadJobs()
    {
        var jobs = new Dictionary<int, JobDefaults>();
        var text = ReadTextOrNull(_statPaths.JobsFile);
        if (text is null) return jobs;
        try
        {
            using var document = JsonDocument.Parse(text);
            foreach (var entry in document.RootElement.GetProperty("jobs").EnumerateArray())
            {
                var id = entry.GetProperty("job").GetInt32();
                var growth = entry.GetProperty("growth").EnumerateArray().Select(v => v.GetInt32()).ToArray();
                var multiplier = entry.GetProperty("multiplier").EnumerateArray().Select(v => v.GetInt32()).ToArray();
                // Rows without values are unused by the game. Two rows it does use have no name in the list:
                // job 0xA1 and the row it switches to (0xA4) once its job level reaches 8.
                // Catalogs written before the mod recorded Move and Jump have neither; they read as 0, "unknown".
                var job = new JobDefaults(id, growth, multiplier)
                {
                    Move = entry.TryGetProperty("move", out var move) && move.TryGetInt32(out var m) ? m : 0,
                    Jump = entry.TryGetProperty("jump", out var jump) && jump.TryGetInt32(out var j) ? j : 0,
                };
                if (id < 0 || id >= JobTable.Rows || job.Name is null || multiplier.All(v => v == 0)) continue;
                if (growth.Length != StatBlock.Count || multiplier.Length != StatBlock.Count ||
                    growth.Concat(multiplier).Any(v => v < 0 || v > byte.MaxValue)) continue;
                jobs[id] = job;
            }
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            jobs.Clear();
        }
        return jobs;
    }

    private object ApplyStatPreset(JsonObject args)
    {
        var draft = StatTuningRules.Parse(args["rules"]?.ToJsonString())
            ?? throw new ModOperationException("Draf aturan tidak bisa dibaca.");
        var preset = args["preset"]?.Deserialize<StatPreset>(Json)
            ?? throw new ModOperationException("Preset tidak bisa dibaca.");
        var jobs = ReadJobs();
        if (jobs.Count == 0) throw new ModOperationException("Nilai bawaan job belum terbaca.");
        return JsonNode.Parse(preset.Apply(draft, jobs.Values).Serialize())!;
    }

    private object SaveStatTuning(JsonNode? rules)
    {
        var parsed = StatTuningRules.Parse(rules?.ToJsonString())
            ?? throw new ModOperationException("Aturan tidak bisa dibaca, jadi tidak disimpan.");
        // The page may have been filled from "no rules" while the saved file could not be opened; do not replace
        // a file that still cannot be read.
        if (File.Exists(_statPaths.RulesFile) && ReadTextOrNull(_statPaths.RulesFile) is null)
            throw new ModOperationException("Berkas aturan sedang dipakai program lain, jadi tidak ditimpa. Coba simpan lagi sebentar lagi.");
        try
        {
            Directory.CreateDirectory(_statPaths.Root);
            // Written under another name first, so the mod never reads half a file.
            var temporary = _statPaths.RulesFile + ".tmp";
            File.WriteAllText(temporary, parsed.Serialize());
            File.Move(temporary, _statPaths.RulesFile, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new ModOperationException("Aturan tidak bisa disimpan: " + e.Message);
        }
        return ReadStatTuning();
    }

    private static Guid ParseCommandId(string commandId) =>
        Guid.TryParse(commandId, out var id) ? id : throw new ModOperationException("Nomor perintah tidak sah.");

    private static string? ReadTextOrNull(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path) : null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    private void Launch()
    {
        var layout = Layout();
        var state = new ModManager(layout, _catalog).GetState();
        if (IsGameRunning()) throw new ModOperationException("Game sudah berjalan.");
        if (state.GamePath is null || !File.Exists(state.GamePath))
            throw new ModOperationException("Berkas game tidak ditemukan. Periksa pendaftaran game di Reloaded-II.");
        if (!File.Exists(layout.LauncherPath))
            throw new ModOperationException("Reloaded-II tidak ditemukan di lokasi yang tercatat.");

        var blocking = state.Notices.FirstOrDefault(n => n.Level == NoticeLevel.Error);
        if (blocking is not null)
            throw new ModOperationException("Perbaiki dulu masalah ini: " + blocking.Message);

        var start = new ProcessStartInfo(layout.LauncherPath)
        {
            WorkingDirectory = Path.GetDirectoryName(layout.LauncherPath)!,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("--launch");
        start.ArgumentList.Add(state.GamePath);
        Process.Start(start)?.Dispose();
    }

    private static object ReadLog()
    {
        var layout = ReloadedLayout.TryLoad();
        var summary = layout is null ? null : new LogReader(layout).ReadLatest(GameProcess);
        if (summary is null) return new { found = false };
        return new
        {
            found = true,
            summary.Path,
            writtenAt = summary.WrittenAt.ToString("yyyy-MM-dd HH:mm:ss"),
            summary.LoadedModIds,
            summary.Problems,
            lines = summary.Lines.TakeLast(600),
            totalLines = summary.Lines.Count,
        };
    }

    private void Open(string target, string? value)
    {
        if (target == "saveBackups")
        {
            Directory.CreateDirectory(_paths.SaveBackupsDirectory);
            Shell(_paths.SaveBackupsDirectory);
            return;
        }

        var layout = Layout();
        switch (target)
        {
            case "mods": Shell(layout.ModsDirectory); break;
            case "logs": Shell(layout.LogsDirectory); break;
            case "reloaded": Shell(layout.LauncherPath); break;
            case "url":
                // Only web links that the catalog itself lists may be opened.
                if (value is not null && _catalog.Mods.Any(m => m.SourceUrl == value)
                    && Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
                    Shell(uri.AbsoluteUri);
                break;
            default:
                throw new ModOperationException($"Tujuan '{target}' tidak dikenal.");
        }
    }

    private static void Shell(string pathOrUrl)
    {
        var isUrl = pathOrUrl.StartsWith("https://", StringComparison.Ordinal);
        if (!isUrl && !File.Exists(pathOrUrl) && !Directory.Exists(pathOrUrl))
            throw new ModOperationException("Lokasi tidak ditemukan: " + pathOrUrl);
        Process.Start(new ProcessStartInfo(pathOrUrl) { UseShellExecute = true })?.Dispose();
    }
}
