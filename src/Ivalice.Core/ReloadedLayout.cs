using System.Text.Json.Nodes;

namespace Ivalice.Core;

/// <summary>Where a Reloaded-II installation keeps its launcher, mods, app profiles, mod settings and logs.</summary>
public sealed record ReloadedLayout(
    string LauncherPath,
    string ModsDirectory,
    string AppsDirectory,
    string UserConfigDirectory,
    string LogsDirectory)
{
    public static string DefaultLoaderConfigPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Reloaded-Mod-Loader-II", "ReloadedII.json");

    /// <summary>Reads the layout from Reloaded-II's own loader config, or returns null when Reloaded-II is not set up.</summary>
    public static ReloadedLayout? TryLoad(string? loaderConfigPath = null)
    {
        loaderConfigPath ??= DefaultLoaderConfigPath;
        if (!File.Exists(loaderConfigPath)) return null;

        JsonObject? cfg;
        try { cfg = JsonNode.Parse(File.ReadAllText(loaderConfigPath)) as JsonObject; }
        catch (System.Text.Json.JsonException) { return null; }
        if (cfg is null) return null;

        string? Get(string key) => cfg[key]?.GetValue<string>();
        var launcher = Get("LauncherPath");
        var mods = Get("ModConfigDirectory");
        var apps = Get("ApplicationConfigDirectory");
        var user = Get("ModUserConfigDirectory");
        if (launcher is null || mods is null || apps is null || user is null) return null;

        return new ReloadedLayout(launcher, mods, apps, user,
            Path.Combine(Path.GetDirectoryName(loaderConfigPath)!, "Logs"));
    }
}
