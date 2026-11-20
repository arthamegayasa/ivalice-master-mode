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

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly Catalog _catalog = Catalog.Load(Path.Combine(AppContext.BaseDirectory, "catalog", "mods.json"));
    private readonly BridgePaths _paths = BridgePaths.Default;

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
                    args["level"]!.GetValue<int>());

            case "getReincarnationResult":
                return ReadReincarnationResult(args["commandId"]!.GetValue<string>());

            case "cancelReincarnation":
                return CancelReincarnation(args["commandId"]!.GetValue<string>());

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
        var mod = layout is null ? null
            : new ModManager(layout, _catalog).GetState().Mods.FirstOrDefault(m => m.Id == ReincarnationMod);
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
        return new
        {
            status,
            ageSeconds = usable ? reading!.AgeSeconds : null,
            roster = usable ? reading!.Roster : null,
            commandPending = File.Exists(_paths.CommandFile),
            rules = new
            {
                minimumLevel = ReincarnationRules.MinimumLevel,
                permillePerLevel = ReincarnationRules.PermillePerLevel,
                maxPowerPermille = ReincarnationRules.MaxPowerPermille,
                commandMaxAgeSeconds = CommandProcessor.MaxAgeSeconds,
            },
            journalFile = _paths.JournalFile,
            saveBackupsDirectory = _paths.SaveBackupsDirectory,
        };
    }

    /// <summary>Backs the save files up, then leaves a command for the mod to pick up.</summary>
    private object SendReincarnation(int slot, string fingerprint, int level)
    {
        if (!IsGameRunning()) throw new ModOperationException("Game tidak sedang berjalan.");
        if (File.Exists(_paths.CommandFile))
            throw new ModOperationException("Masih ada perintah yang menunggu game. Tunggu sampai selesai atau batalkan dulu.");

        var backup = SaveBackup.Create(SaveBackup.DefaultSaveDirectory, _paths.SaveBackupsDirectory, DateTime.Now);

        var command = new ReincarnationCommand(Guid.NewGuid().ToString("D"), ReincarnationCommand.Reincarnate, slot,
            fingerprint, level, CommandProcessor.Stamp(DateTimeOffset.UtcNow));
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
