using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Ivalice.Core;

namespace Ivalice.ControlCenter;

/// <summary>
/// Answers requests from the UI. Each request is <c>{ id, method, args }</c> and each reply is
/// <c>{ id, ok, result }</c> or <c>{ id, ok: false, error }</c>, where error is text meant for the player.
/// </summary>
public sealed class Bridge
{
    private const string GameProcess = "FFT_enhanced";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly Catalog _catalog = Catalog.Load(Path.Combine(AppContext.BaseDirectory, "catalog", "mods.json"));

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

    private static bool IsGameRunning()
    {
        var found = Process.GetProcessesByName(GameProcess);
        foreach (var p in found) p.Dispose();
        return found.Length > 0;
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
