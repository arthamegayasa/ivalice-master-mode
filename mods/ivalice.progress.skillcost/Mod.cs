using fftivc.utility.modloader.Interfaces;
using Reloaded.Mod.Interfaces;
using Reloaded.Mod.Interfaces.Internal;

namespace Ivalice.SkillCost;

public sealed class Mod : IMod
{
    private static readonly string[] Languages = ["en", "ja", "de", "fr", "cs", "ct", "ko"];

    public void StartEx(IModLoaderV1 loaderApi, IModConfigV1 config)
    {
        var loader = (IModLoader)loaderApi;
        var logger = (ILogger)loader.GetLogger();
        const FFTOGameMode mode = FFTOGameMode.Enhanced;
        try
        {
            if (!loader.GetController<IFFTOModPackManager>().TryGetTarget(out var packs) || !packs.Initialized)
                throw new InvalidOperationException("FFTIVC Mod Loader's pack manager is unavailable.");
            if (packs.GameMode != mode) return;

            // Validate all installed languages before registering any changes with the pack merger.
            var tables = new Dictionary<string, byte[]>();
            foreach (var language in Languages)
            {
                string path = $"nxd/ability.{language}.nxd";
                if (packs.FileExists(mode, path))
                    tables.Add(path, AbilityPrices.WithFlatPrices(packs.GetFileData(mode, path)));
            }
            if (tables.Count == 0)
                throw new InvalidDataException("No localized Enhanced Ability tables were found.");
            foreach (var (path, data) in tables)
                packs.AddModdedFile(config.ModId, mode, path, data);
            logger.WriteLine($"[{config.ModId}] Registered 50 JP prices for {tables.Count} language tables.");
        }
        catch (Exception ex)
        {
            logger.WriteLine($"[{config.ModId}] Could not register 50 JP prices: {ex.Message}", logger.ColorRed);
        }
    }

    public bool CanUnload() => false;
    public bool CanSuspend() => false;
    public void Suspend() { }
    public void Resume() { }
    public void Unload() { }
    public Action Disposing => () => { };
}
