using System.Reflection;
using Reloaded.Mod.Interfaces;
using Reloaded.Mod.Interfaces.Internal;

namespace ivalice.battle.smartautobattle;

/// <summary>Reads the actual Improved Fast Forward toggle and factor. Version 0.4.5 has no controller
/// for these settings. Bind its getters once at load time, then only read them on the game thread.</summary>
internal sealed class TurboModState
{
    private const string ModId = "fftivc.qol.turbo";
    private static readonly Version SupportedAssembly = new(0, 4, 5, 0);
    private volatile Func<int>? _readMultiplier;

    public TurboModState(IModLoader loader)
    {
        // Reloaded can start the two optional mods in either order. Turbo 0.4.5 cannot unload.
        loader.ModLoaded += Bind;
        foreach (var item in loader.GetActiveMods())
            Bind(item.Mod, item.Generic);
    }

    public int Multiplier => _readMultiplier?.Invoke() ?? 1;

    private void Bind(IModV1 mod, IModConfigV1 config)
    {
        if (config.ModId != ModId || config.ModVersion != "0.4.5") return;
        try
        {
            _readMultiplier = BindGetters(mod);
        }
        catch (Exception ex) when (ex is AmbiguousMatchException or ArgumentException or MemberAccessException or TypeLoadException)
        {
            // An incompatible optional mod must not interrupt a Reloaded mod-load notification.
            _readMultiplier = null;
        }
    }

    private static Func<int>? BindGetters(IModV1 mod)
    {
        var type = mod.GetType();
        var assembly = type.Assembly.GetName();
        if (type.FullName != "fftivc.qol.turbo.Mod" || assembly.Name != ModId || assembly.Version != SupportedAssembly)
            return null;

        const BindingFlags instance = BindingFlags.Instance | BindingFlags.DeclaredOnly;
        var field = type.GetField("_turbo", instance | BindingFlags.NonPublic);
        if (field?.FieldType.FullName != "fftivc.qol.turbo.Turbo" || field.GetValue(mod) is not { } turbo) return null;
        var on = field.FieldType.GetProperty("On", instance | BindingFlags.NonPublic)?.GetGetMethod(true);
        var factor = field.FieldType.GetProperty("Factor", instance | BindingFlags.Public)?.GetGetMethod();
        if (on?.ReturnType != typeof(bool) || factor?.ReturnType != typeof(int)
            || on.GetParameters().Length != 0 || factor.GetParameters().Length != 0) return null;

        var isOn = on.CreateDelegate<Func<bool>>(turbo);
        var getFactor = factor.CreateDelegate<Func<int>>(turbo);
        return () =>
        {
            if (!isOn()) return 1;
            var speed = getFactor();
            return speed is >= 2 and <= 10 ? speed : 1;
        };
    }
}
