using System.Text.Json.Nodes;
using Ivalice.Core;

namespace Ivalice.Core.Tests;

/// <summary>Builds a throwaway Reloaded-II folder layout on disk.</summary>
internal sealed class Sandbox : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "ivalice-tests-" + Guid.NewGuid().ToString("N"));
    public ReloadedLayout Layout { get; }
    public Catalog Catalog { get; }

    public const string CatalogJson = """
    {
      "schema": 1,
      "app": { "id": "game.exe", "name": "Game", "steamAppId": 1 },
      "mods": [
        { "id": "lib", "name": "Lib", "author": "a", "role": "foundation", "category": "Fondasi",
          "summary": "s", "details": "d", "sourceUrl": "u", "license": "MIT", "requires": [], "settings": [] },
        { "id": "jp", "name": "JP", "author": "a", "role": "feature", "category": "Progres",
          "summary": "s", "details": "d", "sourceUrl": "u", "license": "GPL-3.0", "requires": ["lib"],
          "sharesHookWith": ["xp"],
          "settings": [
            { "key": "JpMultiplier", "type": "number", "label": "l", "help": "h", "min": 0, "max": 10, "step": 0.1, "default": 1.5 },
            { "key": "RemoveJpBoost", "type": "bool", "label": "l", "help": "h", "default": true }
          ] },
        { "id": "xp", "name": "XP", "author": "a", "role": "feature", "category": "Progres",
          "summary": "s", "details": "d", "sourceUrl": "u", "license": "GPL-3.0", "requires": ["lib"],
          "sharesHookWith": ["jp"],
          "settings": [
            { "key": "LevelCap", "type": "integer", "label": "l", "help": "h", "min": 1, "max": 99, "step": 1, "default": 99 }
          ] }
      ]
    }
    """;

    public Sandbox()
    {
        Layout = new ReloadedLayout(
            LauncherPath: Path.Combine(Root, "Reloaded-II.exe"),
            ModsDirectory: Path.Combine(Root, "Mods"),
            AppsDirectory: Path.Combine(Root, "Apps"),
            UserConfigDirectory: Path.Combine(Root, "User", "Mods"),
            LogsDirectory: Path.Combine(Root, "Logs"));
        Catalog = Catalog.Parse(CatalogJson);
        Directory.CreateDirectory(Layout.ModsDirectory);
        Directory.CreateDirectory(Layout.LogsDirectory);
    }

    public void InstallRawModConfig(string folder, string json)
    {
        var dir = Path.Combine(Layout.ModsDirectory, folder);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "ModConfig.json"), json);
    }

    public void InstallMod(string id, string version = "1.0.0", string? name = null, string[]? deps = null,
        string[]? supportedApps = null)
    {
        var dir = Path.Combine(Layout.ModsDirectory, id);
        Directory.CreateDirectory(dir);
        var cfg = new JsonObject
        {
            ["ModId"] = id,
            ["ModName"] = name ?? id,
            ["ModAuthor"] = "someone",
            ["ModVersion"] = version,
            ["ModDescription"] = "from ModConfig",
            ["ModDependencies"] = new JsonArray((deps ?? []).Select(d => (JsonNode)d!).ToArray()),
            ["SupportedAppId"] = new JsonArray((supportedApps ?? ["game.exe"]).Select(d => (JsonNode)d!).ToArray()),
        };
        File.WriteAllText(Path.Combine(dir, "ModConfig.json"), cfg.ToJsonString());
    }

    public void WriteAppConfig(params string[] enabled)
    {
        var dir = Path.Combine(Layout.AppsDirectory, "game.exe");
        Directory.CreateDirectory(dir);
        var cfg = new JsonObject
        {
            ["AppId"] = "game.exe",
            ["AppLocation"] = @"C:\Games\game.exe",
            ["CustomField"] = "keep me",
            ["EnabledMods"] = new JsonArray(enabled.Select(d => (JsonNode)d!).ToArray()),
        };
        File.WriteAllText(Path.Combine(dir, "AppConfig.json"), cfg.ToJsonString());
    }

    public JsonObject ReadAppConfig() =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(Layout.AppsDirectory, "game.exe", "AppConfig.json")))!.AsObject();

    public string[] EnabledMods() =>
        ReadAppConfig()["EnabledMods"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray();

    public string UserConfigPath(string id) => Path.Combine(Layout.UserConfigDirectory, id, "Config.json");

    public ModManager Manager() => new(Layout, Catalog);

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { }
    }
}

public class ModManagerTests
{
    [Fact]
    public void State_merges_catalog_install_status_and_default_settings()
    {
        using var sb = new Sandbox();
        sb.InstallMod("lib", "2.1.0");
        sb.InstallMod("jp", "0.2.0", deps: ["lib"]);
        sb.WriteAppConfig("lib", "jp");

        var state = sb.Manager().GetState();

        var jp = state.Mods.Single(m => m.Id == "jp");
        Assert.True(jp.Installed);
        Assert.True(jp.Enabled);
        Assert.Equal("0.2.0", jp.Version);
        Assert.Equal(1.5, jp.Settings.Single(s => s.Key == "JpMultiplier").Value!.GetValue<double>());

        var xp = state.Mods.Single(m => m.Id == "xp");
        Assert.False(xp.Installed);
        Assert.False(xp.Enabled);
    }

    [Fact]
    public void State_reads_saved_setting_values_over_defaults()
    {
        using var sb = new Sandbox();
        sb.InstallMod("lib");
        sb.InstallMod("jp");
        sb.WriteAppConfig("lib", "jp");
        Directory.CreateDirectory(Path.GetDirectoryName(sb.UserConfigPath("jp"))!);
        File.WriteAllText(sb.UserConfigPath("jp"), """{ "JpMultiplier": 3.0 }""");

        var jp = sb.Manager().GetState().Mods.Single(m => m.Id == "jp");

        Assert.Equal(3.0, jp.Settings.Single(s => s.Key == "JpMultiplier").Value!.GetValue<double>());
        Assert.True(jp.Settings.Single(s => s.Key == "RemoveJpBoost").Value!.GetValue<bool>());
    }

    [Fact]
    public void Mods_missing_from_catalog_are_still_listed_from_their_ModConfig()
    {
        using var sb = new Sandbox();
        sb.InstallMod("community.extra", "9.9.9", name: "Extra Mod");
        sb.WriteAppConfig();

        var extra = sb.Manager().GetState().Mods.Single(m => m.Id == "community.extra");

        Assert.False(extra.InCatalog);
        Assert.Equal("Extra Mod", extra.Name);
        Assert.Equal("from ModConfig", extra.Summary);
        Assert.True(extra.Installed);
    }

    [Fact]
    public void Enabling_a_mod_also_enables_its_dependencies_and_keeps_other_fields()
    {
        using var sb = new Sandbox();
        sb.InstallMod("lib");
        sb.InstallMod("jp");
        sb.WriteAppConfig();

        sb.Manager().SetEnabled("jp", true);

        Assert.Equal(["lib", "jp"], sb.EnabledMods());
        Assert.Equal("keep me", sb.ReadAppConfig()["CustomField"]!.GetValue<string>());
    }

    [Fact]
    public void Enabling_a_mod_that_is_not_installed_is_refused()
    {
        using var sb = new Sandbox();
        sb.InstallMod("lib");
        sb.WriteAppConfig();

        var ex = Assert.Throws<ModOperationException>(() => sb.Manager().SetEnabled("jp", true));

        Assert.Equal("JP belum terpasang, jadi tidak bisa diaktifkan.", ex.Message);
        Assert.Empty(sb.EnabledMods());
    }

    [Fact]
    public void Enabling_is_refused_when_a_dependency_is_not_installed()
    {
        using var sb = new Sandbox();
        sb.InstallMod("jp");
        sb.WriteAppConfig();

        var ex = Assert.Throws<ModOperationException>(() => sb.Manager().SetEnabled("jp", true));

        Assert.Equal("JP membutuhkan Lib, yang belum terpasang.", ex.Message);
        Assert.Empty(sb.EnabledMods());
    }

    [Fact]
    public void Disabling_a_mod_that_enabled_mods_depend_on_is_refused()
    {
        using var sb = new Sandbox();
        sb.InstallMod("lib");
        sb.InstallMod("jp");
        sb.WriteAppConfig("lib", "jp");

        var ex = Assert.Throws<ModOperationException>(() => sb.Manager().SetEnabled("lib", false));

        Assert.Contains("JP", ex.Message);
        Assert.Equal(["lib", "jp"], sb.EnabledMods());
    }

    [Fact]
    public void Disabling_a_mod_removes_only_that_mod()
    {
        using var sb = new Sandbox();
        sb.InstallMod("lib");
        sb.InstallMod("jp");
        sb.WriteAppConfig("lib", "jp");

        sb.Manager().SetEnabled("jp", false);

        Assert.Equal(["lib"], sb.EnabledMods());
    }

    [Fact]
    public void Saving_a_setting_writes_it_and_keeps_unknown_keys()
    {
        using var sb = new Sandbox();
        sb.InstallMod("jp");
        sb.WriteAppConfig();
        Directory.CreateDirectory(Path.GetDirectoryName(sb.UserConfigPath("jp"))!);
        File.WriteAllText(sb.UserConfigPath("jp"), """{ "Future": "x", "JpMultiplier": 1.5 }""");

        sb.Manager().SetSetting("jp", "JpMultiplier", JsonValue.Create(2.5));

        var saved = JsonNode.Parse(File.ReadAllText(sb.UserConfigPath("jp")))!.AsObject();
        Assert.Equal(2.5, saved["JpMultiplier"]!.GetValue<double>());
        Assert.Equal("x", saved["Future"]!.GetValue<string>());
    }

    [Fact]
    public void Saving_a_setting_creates_the_config_file_with_all_defaults()
    {
        using var sb = new Sandbox();
        sb.InstallMod("jp");
        sb.WriteAppConfig();

        sb.Manager().SetSetting("jp", "RemoveJpBoost", JsonValue.Create(false));

        var saved = JsonNode.Parse(File.ReadAllText(sb.UserConfigPath("jp")))!.AsObject();
        Assert.False(saved["RemoveJpBoost"]!.GetValue<bool>());
        Assert.Equal(1.5, saved["JpMultiplier"]!.GetValue<double>());
    }

    [Theory]
    [InlineData("JpMultiplier", "11")]
    [InlineData("JpMultiplier", "-1")]
    [InlineData("JpMultiplier", "true")]
    [InlineData("RemoveJpBoost", "1")]
    [InlineData("NoSuchKey", "1")]
    public void Invalid_setting_values_are_refused(string key, string rawJson)
    {
        using var sb = new Sandbox();
        sb.InstallMod("jp");
        sb.WriteAppConfig();

        Assert.Throws<ModOperationException>(() => sb.Manager().SetSetting("jp", key, JsonNode.Parse(rawJson)));
        Assert.False(File.Exists(sb.UserConfigPath("jp")));
    }

    [Fact]
    public void Integer_settings_refuse_fractions_and_store_whole_numbers()
    {
        using var sb = new Sandbox();
        sb.InstallMod("xp");
        sb.WriteAppConfig();
        var manager = sb.Manager();

        Assert.Throws<ModOperationException>(() => manager.SetSetting("xp", "LevelCap", JsonValue.Create(50.5)));
        manager.SetSetting("xp", "LevelCap", JsonValue.Create(50.0));

        var saved = File.ReadAllText(sb.UserConfigPath("xp"));
        Assert.Contains("\"LevelCap\": 50", saved);
        Assert.DoesNotContain("50.0", saved);
    }

    [Fact]
    public void Diagnostics_warn_when_two_enabled_mods_share_a_hook()
    {
        using var sb = new Sandbox();
        sb.InstallMod("lib");
        sb.InstallMod("jp");
        sb.InstallMod("xp");
        sb.WriteAppConfig("lib", "jp", "xp");

        var notices = sb.Manager().GetState().Notices;

        var shared = Assert.Single(notices, n => n.Code == "shared-hook");
        Assert.Equal(NoticeLevel.Warning, shared.Level);
        Assert.Contains("JP", shared.Message);
        Assert.Contains("XP", shared.Message);
    }

    [Fact]
    public void Diagnostics_report_an_enabled_mod_whose_dependency_is_disabled()
    {
        using var sb = new Sandbox();
        sb.InstallMod("lib");
        sb.InstallMod("jp");
        sb.WriteAppConfig("jp");

        var notices = sb.Manager().GetState().Notices;

        var missing = Assert.Single(notices, n => n.Code == "missing-dependency");
        Assert.Equal(NoticeLevel.Error, missing.Level);
        Assert.Contains("sedang nonaktif", missing.Message);
    }

    [Fact]
    public void Diagnostics_say_when_a_dependency_is_not_installed_at_all()
    {
        using var sb = new Sandbox();
        sb.InstallMod("jp");
        sb.WriteAppConfig("jp");

        var missing = Assert.Single(sb.Manager().GetState().Notices, n => n.Code == "missing-dependency");

        Assert.Contains("belum terpasang", missing.Message);
    }

    [Fact]
    public void Saving_a_setting_refuses_to_overwrite_a_config_file_it_cannot_read()
    {
        using var sb = new Sandbox();
        sb.InstallMod("jp");
        sb.WriteAppConfig();
        Directory.CreateDirectory(Path.GetDirectoryName(sb.UserConfigPath("jp"))!);
        const string handEdited = "{ \"JpMultiplier\": 3.0, \"Mine\": 1, }";
        File.WriteAllText(sb.UserConfigPath("jp"), handEdited);

        var ex = Assert.Throws<ModOperationException>(
            () => sb.Manager().SetSetting("jp", "JpMultiplier", JsonValue.Create(2.0)));

        Assert.Contains("tidak bisa dibaca", ex.Message);
        Assert.Equal(handEdited, File.ReadAllText(sb.UserConfigPath("jp")));
    }

    [Fact]
    public void A_malformed_mod_folder_does_not_break_the_list()
    {
        using var sb = new Sandbox();
        sb.InstallMod("lib");
        sb.InstallRawModConfig("broken-id", "{ \"ModId\": 5, \"ModDependencies\": [null] }");
        sb.InstallRawModConfig("broken-json", "{ not json");
        sb.InstallRawModConfig("odd-deps",
            "{ \"ModId\": \"odd\", \"ModName\": 7, \"ModDependencies\": [null, 3, \"lib\"], \"SupportedAppId\": [\"game.exe\"] }");
        sb.WriteAppConfig("lib");

        var mods = sb.Manager().GetState().Mods;

        Assert.Contains(mods, m => m.Id == "lib" && m.Installed);
        var odd = mods.Single(m => m.Id == "odd");
        Assert.Equal("odd", odd.Name);
        Assert.Equal(["lib"], odd.Requires);
        Assert.DoesNotContain(mods, m => m.Id == "5");
    }

    [Fact]
    public void Uncatalogued_mods_made_for_another_game_are_not_listed()
    {
        using var sb = new Sandbox();
        sb.InstallMod("other.game.mod", supportedApps: ["othergame.exe"]);
        sb.InstallMod("this.game.mod", supportedApps: ["GAME.EXE"]);
        sb.WriteAppConfig();

        var ids = sb.Manager().GetState().Mods.Select(m => m.Id).ToList();

        Assert.Contains("this.game.mod", ids);
        Assert.DoesNotContain("other.game.mod", ids);
    }

    [Fact]
    public void A_catalog_entry_may_omit_requires_and_settings()
    {
        var catalog = Catalog.Parse(
            "{ \"schema\": 1, \"app\": { \"id\": \"game.exe\", \"name\": \"Game\", \"steamAppId\": 1 }," +
            " \"mods\": [ { \"id\": \"bare\", \"name\": \"Bare\", \"author\": \"a\", \"role\": \"feature\", \"category\": \"c\"," +
            " \"summary\": \"s\", \"details\": \"d\", \"sourceUrl\": \"u\", \"license\": \"MIT\" } ] }");

        Assert.Empty(catalog.Mods[0].Requires);
        Assert.Empty(catalog.Mods[0].Settings);
    }

    [Fact]
    public void Diagnostics_report_a_game_that_is_not_registered()
    {
        using var sb = new Sandbox();
        sb.InstallMod("lib");

        var state = sb.Manager().GetState();

        Assert.Contains(state.Notices, n => n.Code == "app-not-registered" && n.Level == NoticeLevel.Error);
    }
}

public class LogReaderTests
{
    [Fact]
    public void Latest_log_is_summarised_with_loaded_mods_and_problems()
    {
        using var sb = new Sandbox();
        File.WriteAllText(Path.Combine(sb.Layout.LogsDirectory, "2026-01-01 10.00.00 ~ game.txt"), "old");
        var latest = Path.Combine(sb.Layout.LogsDirectory, "2026-01-02 10.00.00 ~ game.txt");
        File.WriteAllText(latest, string.Join('\n',
            "[21:26:12] [Reloaded] Loading: Lib",
            "[21:26:12] [Reloaded] - AppId   : lib",
            "[21:26:13] [Reloaded] Loading: JP",
            "[21:26:13] [Reloaded] - AppId   : jp",
            "[21:26:12] [loader] Installed SetUnhandledExceptionFilter hook.",
            "[21:26:13] [ScanManager] Pattern CFILE_DAT__Decode not found!",
            "[21:26:13] [ScanManager] Pattern JobDataTable not found!",
            "[21:26:13] [jp] Successfully hooked EarnJp function at 0x7FF631553664.",
            "[21:26:14] System.NullReferenceException: boom"));
        File.SetLastWriteTimeUtc(latest, DateTime.UtcNow.AddMinutes(1));

        var summary = new LogReader(sb.Layout).ReadLatest("game");

        Assert.NotNull(summary);
        Assert.Equal(latest, summary!.Path);
        Assert.Equal(["lib", "jp"], summary.LoadedModIds);
        Assert.Equal(2, summary.Problems.Count);
        Assert.Contains(summary.Problems, p => p.Contains("JobDataTable"));
        Assert.Contains(summary.Problems, p => p.Contains("NullReferenceException"));
        Assert.Equal(9, summary.Lines.Count);
    }

    [Fact]
    public void No_log_yields_null()
    {
        using var sb = new Sandbox();
        Assert.Null(new LogReader(sb.Layout).ReadLatest("game"));
    }
}
