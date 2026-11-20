using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ivalice.Core;

/// <summary>A refused operation. The message is written for the player and shown as-is.</summary>
public sealed class ModOperationException(string message) : Exception(message);

public enum NoticeLevel { Info, Warning, Error }

public sealed record Notice(string Code, NoticeLevel Level, string Message, IReadOnlyList<string> ModIds);

public sealed record SettingState(
    string Key, string Type, string Label, string Help, JsonNode? Value, JsonNode? Default,
    double? Min, double? Max, double? Step, string? Unit, bool Advanced);

public sealed record ModState(
    string Id, string Name, string Author, string Role, string Category, string Summary, string Details,
    string SourceUrl, string License, bool InCatalog, bool Installed, bool Enabled, string? Version,
    IReadOnlyList<string> Requires, IReadOnlyList<string> RequiredBy, IReadOnlyList<SettingState> Settings);

public sealed record AppState(
    string AppId, string AppName, string? GamePath, bool AppRegistered,
    IReadOnlyList<ModState> Mods, IReadOnlyList<Notice> Notices);

/// <summary>
/// The one place that reads and changes a Reloaded-II profile: which mods are enabled for the game
/// and what each mod's settings are. Every change goes straight to the files Reloaded-II itself reads.
/// </summary>
public sealed class ModManager(ReloadedLayout layout, Catalog catalog)
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private string AppConfigPath => Path.Combine(layout.AppsDirectory, catalog.App.Id, "AppConfig.json");
    private string UserConfigPath(string modId) => Path.Combine(layout.UserConfigDirectory, modId, "Config.json");

    public AppState GetState()
    {
        var installed = ReadInstalled();
        var appConfig = ReadAppConfig();
        var enabled = EnabledIds(appConfig);
        var mods = new List<ModState>();

        foreach (var entry in catalog.Mods)
        {
            installed.TryGetValue(entry.Id, out var disk);
            mods.Add(new ModState(
                entry.Id, entry.Name, entry.Author, entry.Role, entry.Category, entry.Summary, entry.Details,
                entry.SourceUrl, entry.License, InCatalog: true, Installed: disk is not null,
                Enabled: enabled.Contains(entry.Id, StringComparer.OrdinalIgnoreCase), disk?.Version,
                entry.Requires, [], ReadSettings(entry)));
        }

        foreach (var disk in installed.Values
                     .Where(d => catalog.Find(d.Id) is null && d.Supports(catalog.App.Id)).OrderBy(d => d.Name))
        {
            mods.Add(new ModState(
                disk.Id, disk.Name, disk.Author, "feature", "Lainnya", disk.Description, disk.Description,
                disk.ProjectUrl, "Tidak diketahui", InCatalog: false, Installed: true,
                Enabled: enabled.Contains(disk.Id, StringComparer.OrdinalIgnoreCase), disk.Version,
                disk.Dependencies, [], []));
        }

        mods = mods.Select(m => m with
        {
            RequiredBy = mods.Where(o => o.Enabled && Has(o.Requires, m.Id)).Select(o => o.Id).ToList(),
        }).ToList();

        return new AppState(
            catalog.App.Id, catalog.App.Name, AsText(appConfig?["AppLocation"]),
            appConfig is not null, mods, DiagnoseAll(mods, appConfig is not null));
    }

    /// <summary>Turns a mod on or off for the game. Turning one on also turns on what it needs.</summary>
    public void SetEnabled(string modId, bool enable)
    {
        var state = GetState();
        if (!state.AppRegistered)
            throw new ModOperationException("Game belum terdaftar di Reloaded-II. Jalankan skrip instalasi lebih dulu.");
        var mod = state.Mods.FirstOrDefault(m => Same(m.Id, modId))
            ?? throw new ModOperationException($"Mod '{modId}' tidak dikenal.");

        var appConfig = ReadAppConfig()!;
        var enabled = EnabledIds(appConfig);

        if (enable)
        {
            foreach (var id in DependenciesFirst(mod, state.Mods, []))
            {
                var needed = state.Mods.FirstOrDefault(m => Same(m.Id, id));
                if (needed is null || !needed.Installed)
                {
                    var label = needed?.Name ?? id;
                    throw new ModOperationException(Same(id, mod.Id)
                        ? $"{label} belum terpasang, jadi tidak bisa diaktifkan."
                        : $"{mod.Name} membutuhkan {label}, yang belum terpasang.");
                }
                if (!Has(enabled, id)) enabled.Add(needed.Id);
            }
        }
        else
        {
            var dependants = state.Mods.Where(m => m.Enabled && !Same(m.Id, mod.Id) && Has(m.Requires, mod.Id)).ToList();
            if (dependants.Count > 0)
                throw new ModOperationException(
                    $"{mod.Name} masih dibutuhkan oleh {string.Join(", ", dependants.Select(d => d.Name))}. Nonaktifkan mod itu lebih dulu.");
            enabled.RemoveAll(id => Same(id, mod.Id));
        }

        appConfig["EnabledMods"] = new JsonArray(enabled.Select(id => (JsonNode)JsonValue.Create(id)!).ToArray());
        WriteJson(AppConfigPath, appConfig);
    }

    /// <summary>Saves one setting of a mod after checking it against the catalog's type and range.</summary>
    public void SetSetting(string modId, string key, JsonNode? value)
    {
        var entry = catalog.Find(modId)
            ?? throw new ModOperationException($"Mod '{modId}' tidak punya pengaturan yang dikenal.");
        var spec = entry.Settings.FirstOrDefault(s => s.Key == key)
            ?? throw new ModOperationException($"Pengaturan '{key}' tidak dikenal untuk {entry.Name}.");

        var checkedValue = Validate(spec, value);

        var path = UserConfigPath(entry.Id);
        var config = ReadObject(path);
        if (config is null && File.Exists(path))
            throw new ModOperationException(
                $"Berkas pengaturan {entry.Name} tidak bisa dibaca, jadi tidak diubah agar isinya tidak hilang. Periksa {path}.");
        config ??= [];
        foreach (var s in entry.Settings)
            if (!config.ContainsKey(s.Key)) config[s.Key] = s.Default?.DeepClone();
        config[spec.Key] = checkedValue;
        WriteJson(path, config);
    }

    private static JsonNode Validate(SettingSpec spec, JsonNode? value)
    {
        var kind = (value as JsonValue)?.GetValueKind();
        switch (spec.Type)
        {
            case "bool":
                if (kind is JsonValueKind.True or JsonValueKind.False) return JsonValue.Create(value!.GetValue<bool>());
                throw new ModOperationException($"{spec.Label} harus berupa nyala atau mati.");

            case "integer":
            case "number":
                if (kind is not JsonValueKind.Number)
                    throw new ModOperationException($"{spec.Label} harus berupa angka.");
                var number = value!.GetValue<double>();
                if (double.IsNaN(number) || double.IsInfinity(number))
                    throw new ModOperationException($"{spec.Label} harus berupa angka.");
                if ((spec.Min is { } min && number < min) || (spec.Max is { } max && number > max))
                    throw new ModOperationException($"{spec.Label} harus di antara {spec.Min} dan {spec.Max}.");
                if (spec.Type == "number") return JsonValue.Create(number);
                if (number != Math.Floor(number))
                    throw new ModOperationException($"{spec.Label} harus berupa bilangan bulat.");
                return JsonValue.Create((long)number);

            default:
                throw new ModOperationException($"Jenis pengaturan '{spec.Type}' belum didukung.");
        }
    }

    private List<SettingState> ReadSettings(CatalogMod entry)
    {
        if (entry.Settings.Count == 0) return [];
        var saved = ReadObject(UserConfigPath(entry.Id));
        return entry.Settings.Select(s =>
        {
            var value = saved is not null && saved.TryGetPropertyValue(s.Key, out var node) && node is not null
                ? node.DeepClone()
                : s.Default?.DeepClone();
            return new SettingState(s.Key, s.Type, s.Label, s.Help, value, s.Default?.DeepClone(),
                s.Min, s.Max, s.Step, s.Unit, s.Advanced);
        }).ToList();
    }

    private static List<Notice> Diagnose(List<ModState> mods, bool appRegistered)
    {
        var notices = new List<Notice>();
        if (!appRegistered)
            notices.Add(new Notice("app-not-registered", NoticeLevel.Error,
                "Game belum terdaftar di Reloaded-II, jadi mod belum bisa dimuat.", []));

        foreach (var mod in mods.Where(m => m.Enabled))
        {
            if (!mod.Installed)
                notices.Add(new Notice("enabled-not-installed", NoticeLevel.Error,
                    $"{mod.Name} aktif tetapi berkasnya tidak ditemukan.", [mod.Id]));

            foreach (var need in mod.Requires)
            {
                var dep = mods.FirstOrDefault(m => Same(m.Id, need));
                if (dep is { Installed: true, Enabled: true }) continue;
                notices.Add(new Notice("missing-dependency", NoticeLevel.Error,
                    $"{mod.Name} membutuhkan {dep?.Name ?? need}, yang {(dep?.Installed == true ? "sedang nonaktif" : "belum terpasang")}.",
                    [mod.Id, dep?.Id ?? need]));
            }
        }

        return notices;
    }

    private IReadOnlyList<Notice> DiagnoseAll(List<ModState> mods, bool appRegistered)
    {
        var notices = Diagnose(mods, appRegistered);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in catalog.Mods)
        {
            var mod = mods.First(m => Same(m.Id, entry.Id));
            if (!mod.Enabled) continue;
            foreach (var otherId in entry.SharesHookWith ?? [])
            {
                var other = mods.FirstOrDefault(m => Same(m.Id, otherId));
                if (other is not { Enabled: true }) continue;
                var pair = string.Join('|', new[] { mod.Id, other.Id }.Order(StringComparer.OrdinalIgnoreCase));
                if (!seen.Add(pair)) continue;
                notices.Add(new Notice("shared-hook", NoticeLevel.Warning,
                    $"{mod.Name} dan {other.Name} mengubah fungsi game yang sama. Keduanya termuat tanpa galat, tetapi belum diuji bersama dalam pertarungan. Jika XP atau JP terasa janggal, coba nonaktifkan salah satunya.",
                    [mod.Id, other.Id]));
            }
        }
        return notices;
    }

    private static IEnumerable<string> DependenciesFirst(ModState mod, IReadOnlyList<ModState> all, HashSet<string> visiting)
    {
        if (!visiting.Add(mod.Id.ToLowerInvariant())) yield break;
        foreach (var need in mod.Requires)
        {
            var dep = all.FirstOrDefault(m => Same(m.Id, need));
            if (dep is null) { yield return need; continue; }
            foreach (var id in DependenciesFirst(dep, all, visiting)) yield return id;
        }
        yield return mod.Id;
    }

    private sealed record DiskMod(string Id, string Name, string Author, string Version, string Description,
        string ProjectUrl, IReadOnlyList<string> Dependencies, IReadOnlyList<string> SupportedApps, bool Universal)
    {
        public bool Supports(string appId) => Universal || Has(SupportedApps, appId);
    }

    private Dictionary<string, DiskMod> ReadInstalled()
    {
        var result = new Dictionary<string, DiskMod>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(layout.ModsDirectory)) return result;

        foreach (var dir in Directory.EnumerateDirectories(layout.ModsDirectory))
        {
            // Third-party files: anything missing or of the wrong type is skipped rather than trusted.
            var cfg = ReadObject(Path.Combine(dir, "ModConfig.json"));
            var id = AsText(cfg?["ModId"]);
            if (cfg is null || string.IsNullOrWhiteSpace(id)) continue;
            string Text(string key) => AsText(cfg[key]) ?? "";
            result[id] = new DiskMod(id, Text("ModName") is { Length: > 0 } n ? n : id, Text("ModAuthor"),
                Text("ModVersion"), Text("ModDescription"), Text("ProjectUrl"),
                Texts(cfg["ModDependencies"]), Texts(cfg["SupportedAppId"]),
                cfg["IsUniversalMod"] is JsonValue u && u.GetValueKind() == JsonValueKind.True);
        }
        return result;
    }

    private JsonObject? ReadAppConfig() => ReadObject(AppConfigPath);

    private static List<string> EnabledIds(JsonObject? appConfig) => Texts(appConfig?["EnabledMods"]);

    private static string? AsText(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;

    private static List<string> Texts(JsonNode? node) =>
        (node as JsonArray)?.Select(AsText).OfType<string>().ToList() ?? [];

    private static JsonObject? ReadObject(string path)
    {
        if (!File.Exists(path)) return null;
        try { return JsonNode.Parse(File.ReadAllText(path)) as JsonObject; }
        catch (JsonException) { return null; }
    }

    /// <summary>Writes through a temporary file so a crash mid-write cannot leave a half-written config.</summary>
    private static void WriteJson(string path, JsonObject value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, value.ToJsonString(WriteOptions));
        File.Move(temp, path, overwrite: true);
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    private static bool Has(IEnumerable<string> ids, string id) => ids.Contains(id, StringComparer.OrdinalIgnoreCase);
}
