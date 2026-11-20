using System.Diagnostics;
using System.Runtime.InteropServices;
using Ivalice.Battle;
using ivalice.battle.smartautobattle.Configuration;
using ivalice.battle.smartautobattle.Template;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X64;
using Reloaded.Memory.SigScan.ReloadedII.Interfaces;
using Reloaded.Mod.Interfaces;

namespace ivalice.battle.smartautobattle;

/// <summary>
/// Hands the player's units to the game's own auto-battle. The game decides who is AI-driven from one
/// byte per unit (the auto-battle mode the in-game menu writes); this mod writes that byte just before
/// the game reads it, choosing the mode with <see cref="AutoBattlePlanner"/>.
/// </summary>
public unsafe class Mod : ModBase // <= Do not Remove.
{
    private const string Tag = "[Smart Auto Battle]";

    // Start of the function that refreshes whether the acting unit is player- or AI-driven.
    // It reads the unit's auto-battle mode, so writing the mode right before it runs takes effect at once.
    private const string RefreshControlPattern = "48 83 EC 28 48 8B 81 48 01 00 00 8A 50 05";

    // A function that indexes the battle unit table; the table address is the target of the
    // "lea r15, [rip+disp]" found UnitTableLeaOffset bytes into the match.
    private const string UnitTablePattern =
        "48 89 5C 24 ?? 48 89 6C 24 ?? 48 89 74 24 ?? 57 41 56 41 57 48 83 EC ?? 48 8B 05 ?? ?? ?? ?? " +
        "48 ?? ?? 48 89 44 24 ?? 48 63 F1 4C 8D 3D";
    private const int UnitTableLeaOffset = 0x2A;

    /// <summary>Offset, in the object passed to the hooked function, of the pointer to its battle unit.</summary>
    private const int SceneUnitPointer = 0x148;

    private const int VkF8 = 0x77;
    private const ushort BothSticks = 0x0040 | 0x0080;

    [Function(CallingConventions.Microsoft)]
    private delegate void RefreshControl(nint sceneUnit);

    private readonly IReloadedHooks? _hooks;
    private readonly ILogger _logger;
    private volatile Config _configuration;

    private IHook<RefreshControl>? _refreshControlHook;
    private byte* _unitTable;
    private volatile PlannerSettings _settings;
    private volatile bool _active;
    private bool _xinputAvailable = true;
    private bool _reportedFailure;

    // Touched only on the game's thread, inside the hook.
    private bool _wasActive;
    /// <summary>Slots whose auto-battle mode this mod wrote, so it can be taken back when switched off.</summary>
    private readonly bool[] _ordered = new bool[BattleLayout.Capacity];

    public Mod(ModContext context)
    {
        _hooks = context.Hooks;
        _logger = context.Logger;
        _configuration = context.Configuration;
        _settings = ToSettings(_configuration);
        _active = _configuration.ActiveAtStart;

        if (_hooks is null || !context.ModLoader.GetController<IStartupScanner>().TryGetTarget(out var scanner))
        {
            _logger.WriteLine($"{Tag} Hooks or signature scanner unavailable. The mod is inactive.");
            return;
        }

        var gameBase = Process.GetCurrentProcess().MainModule!.BaseAddress;

        scanner.AddMainModuleScan(UnitTablePattern, result =>
        {
            if (!result.Found) return;
            var lea = (byte*)(gameBase + result.Offset + UnitTableLeaOffset);
            _unitTable = lea + 7 + *(int*)(lea + 3);
        });

        // Registered second, so the table scan above has already run when this one reports.
        scanner.AddMainModuleScan(RefreshControlPattern, result =>
        {
            if (!result.Found || _unitTable == null)
            {
                _logger.WriteLine($"{Tag} Could not find the game code this mod relies on (unit table: {(_unitTable != null ? "found" : "missing")}, control refresh: {(result.Found ? "found" : "missing")}). The game version is probably unsupported; the mod is inactive.");
                return;
            }

            var address = gameBase + result.Offset;
            _refreshControlHook = _hooks.CreateHook<RefreshControl>(RefreshControlImpl, address);
            _refreshControlHook.Activate();
            _logger.WriteLine($"{Tag} Ready: unit table at 0x{(nint)_unitTable:X}, control refresh hooked at 0x{address:X}. Auto battle is {(_active ? "ON" : "OFF")}; toggle with F8 or L3+R3.");

            new Thread(WatchToggle) { IsBackground = true, Name = "SmartAutoBattle toggle" }.Start();
        });
    }

    private void RefreshControlImpl(nint sceneUnit)
    {
        // Runs on the game's own thread, once per step of the acting unit's turn.
        // Nothing here may throw: an exception would unwind into the game's native code.
        try
        {
            if (sceneUnit != 0) Apply(*(byte**)(sceneUnit + SceneUnitPointer));
        }
        catch (Exception e)
        {
            if (!_reportedFailure) _logger.WriteLine($"{Tag} Unexpected error, the unit was left alone: {e.Message}");
            _reportedFailure = true;
        }

        _refreshControlHook!.OriginalFunction(sceneUnit);
    }

    private void Apply(byte* unit)
    {
        var active = _active;
        if (!active)
        {
            // Switched off: take back every order at once, so nothing lingers into a later battle.
            if (_wasActive) ReleaseAll();
            _wasActive = false;
            return;
        }
        _wasActive = true;

        var offset = unit - _unitTable;
        if (unit == null || offset < 0 || offset >= BattleLayout.TableSize || offset % BattleLayout.Stride != 0) return;
        var slot = (int)(offset / BattleLayout.Stride);

        var order = AutoBattlePlanner.Plan(new ReadOnlySpan<byte>(_unitTable, BattleLayout.TableSize), slot, _settings);
        if (order is not { } o) return;

        if (_configuration.DebugLog && (unit[BattleLayout.AutoMode] != o.Mode || unit[BattleLayout.AutoTarget] != o.Target))
            _logger.WriteLine($"{Tag} Unit {slot}: {(o.Mode == AutoMode.SaveFadingLife ? "save" : "fight")} -> unit {o.Target}.");

        unit[BattleLayout.AutoTarget] = o.Target;
        unit[BattleLayout.AutoMode] = o.Mode;
        _ordered[slot] = true;
    }

    /// <summary>Returns to manual every unit this mod ordered, leaving modes the player picked in the menu.</summary>
    private void ReleaseAll()
    {
        for (var slot = 0; slot < BattleLayout.Capacity; slot++)
        {
            if (!_ordered[slot]) continue;
            _ordered[slot] = false;

            var unit = _unitTable + slot * BattleLayout.Stride;
            if (unit[BattleLayout.InUse] != BattleLayout.EmptySlot && AutoBattlePlanner.IsHandedOut(unit[BattleLayout.AutoMode]))
                unit[BattleLayout.AutoMode] = AutoMode.None;
        }
        if (_configuration.DebugLog) _logger.WriteLine($"{Tag} All units returned to manual.");
    }

    private void WatchToggle()
    {
        var wasDown = false;
        while (true)
        {
            Thread.Sleep(50);
            var down = IsToggleDown();
            if (down && !wasDown)
            {
                _active = !_active;
                _logger.WriteLine($"{Tag} Auto battle {(_active ? "ON" : "OFF")}.");
                if (_configuration.SoundOnToggle) Beep(_active ? 880u : 440u, 120);
            }
            wasDown = down;
        }
    }

    private bool IsToggleDown()
    {
        var config = _configuration;
        GetWindowThreadProcessId(GetForegroundWindow(), out var foregroundProcess);
        if (foregroundProcess != (uint)Environment.ProcessId) return false;

        // With both toggles switched off there would be no way to take control back, so F8 stays.
        var f8Enabled = config.ToggleWithF8 || !config.ToggleWithSticks;
        if (f8Enabled && (GetAsyncKeyState(VkF8) & 0x8000) != 0) return true;

        if (config.ToggleWithSticks && _xinputAvailable)
        {
            try
            {
                for (uint pad = 0; pad < 4; pad++)
                    if (XInputGetState(pad, out var state) == 0 && (state.Buttons & BothSticks) == BothSticks)
                        return true;
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
            {
                _xinputAvailable = false;
            }
        }

        return false;
    }

    private static PlannerSettings ToSettings(Config config)
    {
        var healers = new HashSet<byte>();
        if (config.HealWithWhiteMagicks) healers.Add(Skillsets.WhiteMagicks);
        if (config.HealWithItems) healers.Add(Skillsets.Items);
        if (config.HealWithMartialArts) healers.Add(Skillsets.MartialArts);
        return new PlannerSettings(config.HealThresholdPercent, healers, config.ReviveFallenAllies);
    }

    #region Standard Overrides
    public override void ConfigurationUpdated(Config configuration)
    {
        _configuration = configuration;
        _settings = ToSettings(configuration);
        _logger.WriteLine($"{Tag} Settings updated: heal below {_settings.EffectiveThreshold}%.");
    }
    #endregion

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputState
    {
        public uint PacketNumber;
        public ushort Buttons;
        public byte LeftTrigger, RightTrigger;
        public short ThumbLX, ThumbLY, ThumbRX, ThumbRY;
    }

    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("kernel32.dll")] private static extern bool Beep(uint frequency, uint duration);
    [DllImport("xinput1_4.dll")] private static extern uint XInputGetState(uint userIndex, out XInputState state);

    #region For Exports, Serialization etc.
#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
    public Mod() { }
#pragma warning restore CS8618
    #endregion
}
