using System.Diagnostics;
using System.Runtime.InteropServices;
using Ivalice.Battle;
using ivalice.battle.smartautobattle.Configuration;
using ivalice.battle.smartautobattle.Template;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.Enums;
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
    /// <summary>Offset, in the same object, of the control flags the game acts on this turn.</summary>
    private const int SceneControlFlags = 0x154;

    // In the game's per-frame task runner: loads the speed manager, calls the function that says how many
    // logic ticks to run this frame, then keeps the answer. The hook sits right after that call.
    private const string TickCountPattern = "4C 8B 05 ?? ?? ?? ?? 49 8B C8 E8 ?? ?? ?? ?? 45 33 FF 44 8B F0";
    private const int TickCountHookOffset = 0x0F;
    private const int SpeedContextOffset = 0x60;

    // The function that returns the scene object of the unit whose turn it is (or null).
    private const string ActiveActorPattern =
        "48 8B 0D ?? ?? ?? ?? EB 0F 0F B6 41 08 3B 05 ?? ?? ?? ?? 74 0B 48 8B 09 48 85 C9 75 EC 33 C0 C3 48 8B C1 C3 4C 8B 0D";

    // A read of the battle state machine's current state: "mov eax, [rip+disp]".
    private const string BattleStatePattern =
        "8B 05 ?? ?? ?? ?? 83 F8 34 0F 84 ?? ?? ?? ?? 83 F8 3A 0F 84 ?? ?? ?? ?? 48 63 05 ?? ?? ?? ?? 48 8D 3D";

    private const int VkF8 = 0x77;
    private const ushort BothSticks = 0x0040 | 0x0080;

    [Function(CallingConventions.Microsoft)]
    private delegate void BeforeRefreshControl(nint sceneUnit);

    [Function(CallingConventions.Microsoft)]
    private delegate int AdjustTickCount(int iterations);

    private readonly IReloadedHooks? _hooks;
    private readonly ILogger _logger;
    private volatile Config _configuration;

    private IAsmHook? _refreshControlHook;
    private IReverseWrapper<BeforeRefreshControl>? _beforeRefreshControl;

    private IAsmHook? _tickCountHook;
    private IReverseWrapper<AdjustTickCount>? _adjustTickCount;
    private readonly TurboFollower _turboFollower = new();
    private byte** _speedManagerGlobal;
    private int* _battleStateGlobal;
    private delegate* unmanaged<byte*> _getActiveActor;

    /// <summary>The battle state in which the game shows an AI unit's chosen action before running it.</summary>
    private const int ConfirmActionState = 25;
    /// <summary>Diagnostics: when the current pre-action hold of a unit on auto began, or 0.</summary>
    private long _holdStartedAt;
    private byte* _unitTable;
    private volatile PlannerSettings _settings;
    private volatile bool _active;
    private bool _xinputAvailable = true;
    private bool _reportedFailure;
    /// <summary>Set when the mod decided it is not safe to hook this game build.</summary>
    private bool _unsafeToHook;

    // Touched only on the game's thread, inside the hook.
    private bool _wasActive;
    /// <summary>Slots whose auto-battle mode this mod wrote, so it can be taken back when switched off.</summary>
    private readonly bool[] _ordered = new bool[BattleLayout.Capacity];
    /// <summary>Last HP seen per slot, for the diagnostic log only; -1 when the slot was empty.</summary>
    private readonly int[] _lastHp = Enumerable.Repeat(-1, BattleLayout.Capacity).ToArray();
    /// <summary>Last level * 100 + experience seen per slot, for the diagnostic log only.</summary>
    private readonly int[] _lastXp = Enumerable.Repeat(-1, BattleLayout.Capacity).ToArray();

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
            _refreshControlHook = _hooks.CreateAsmHook(BuildHookAssembly(), address, AsmHookBehaviour.ExecuteFirst);
            _refreshControlHook.Activate();

            if (!RegisterSelfTest.RegistersSurvive(address))
            {
                _refreshControlHook.Disable();
                _unsafeToHook = true;
                _logger.WriteLine($"{Tag} Self-test FAILED: the hook does not preserve the registers the game relies on. The hook was removed and the mod is inactive.");
                return;
            }

            _logger.WriteLine($"{Tag} Self-test passed: registers are preserved across the hooked call.");
            _logger.WriteLine($"{Tag} Ready: unit table at 0x{(nint)_unitTable:X}, control refresh hooked at 0x{address:X}. Auto battle is {(_active ? "ON" : "OFF")}; toggle with F8 or L3+R3.");

            new Thread(WatchToggle) { IsBackground = true, Name = "SmartAutoBattle toggle" }.Start();
        });

        scanner.AddMainModuleScan(ActiveActorPattern, result =>
        {
            if (result.Found) _getActiveActor = (delegate* unmanaged<byte*>)(gameBase + result.Offset);
        });

        scanner.AddMainModuleScan(BattleStatePattern, result =>
        {
            if (!result.Found) return;
            var read = (byte*)(gameBase + result.Offset);
            _battleStateGlobal = (int*)(read + 6 + *(int*)(read + 2));
        });

        // Registered last, so the scans it depends on have already reported.
        scanner.AddMainModuleScan(TickCountPattern, result =>
        {
            if (_unsafeToHook) return;
            if (!result.Found || _unitTable == null || _getActiveActor == null || _battleStateGlobal == null)
            {
                _logger.WriteLine($"{Tag} Turbo follow is unavailable on this game version; auto-battle itself is not affected.");
                return;
            }

            var load = (byte*)(gameBase + result.Offset);
            _speedManagerGlobal = (byte**)(load + 7 + *(int*)(load + 3));
            try
            {
                _tickCountHook = _hooks.CreateAsmHook(BuildTickCountAssembly(), gameBase + result.Offset + TickCountHookOffset,
                    new AsmHookOptions { Behaviour = AsmHookBehaviour.ExecuteFirst, PreferRelativeJump = true, MaxOpcodeSize = 5 });
                _tickCountHook.Activate();
                _logger.WriteLine($"{Tag} Turbo follow ready: units on auto-battle keep the turbo speed.");
            }
            catch (Exception e)
            {
                _logger.WriteLine($"{Tag} Turbo follow could not be installed ({e.Message}); auto-battle itself is not affected.");
            }
        });
    }

    /// <summary>
    /// Runs right after the game asked how many logic ticks to run this frame (the answer is in eax),
    /// and replaces the answer with <see cref="OnTickCount"/>'s. Every other register is preserved.
    /// The stack is 16-byte aligned at this point in the game's function, and stays so after six pushes.
    /// </summary>
    private string[] BuildTickCountAssembly() =>
    [
        "use64",
        "push rcx", "push rdx", "push r8", "push r9", "push r10", "push r11",
        "sub rsp, 0x80",
        "movdqu [rsp+0x20], xmm0", "movdqu [rsp+0x30], xmm1", "movdqu [rsp+0x40], xmm2",
        "movdqu [rsp+0x50], xmm3", "movdqu [rsp+0x60], xmm4", "movdqu [rsp+0x70], xmm5",
        "mov ecx, eax",
        _hooks!.Utilities.GetAbsoluteCallMnemonics<AdjustTickCount>(OnTickCount, out _adjustTickCount),
        "movdqu xmm0, [rsp+0x20]", "movdqu xmm1, [rsp+0x30]", "movdqu xmm2, [rsp+0x40]",
        "movdqu xmm3, [rsp+0x50]", "movdqu xmm4, [rsp+0x60]", "movdqu xmm5, [rsp+0x70]",
        "add rsp, 0x80",
        "pop r11", "pop r10", "pop r9", "pop r8", "pop rdx", "pop rcx",
    ];

    private int OnTickCount(int iterations)
    {
        // Runs once per frame on the game's main thread. Nothing here may throw.
        try
        {
            var follow = _configuration.FollowTurbo;
            var logging = _configuration.DebugLog;

            var speedManager = *_speedManagerGlobal;
            if (speedManager == null) return iterations;

            ActingUnit? acting = null;
            var actor = _getActiveActor();
            if (actor != null)
            {
                var unit = *(byte**)(actor + SceneUnitPointer);
                var offset = unit - _unitTable;
                if (unit != null && offset >= 0 && offset < BattleLayout.TableSize && offset % BattleLayout.Stride == 0)
                    acting = new ActingUnit(
                        PlayerOwned: (unit[BattleLayout.PermanentFlags] & BattleLayout.ControlBit) != 0,
                        AiDriven: (actor[SceneControlFlags] & BattleLayout.ControlBit) == 0);
            }

            var adjusted = _turboFollower.Adjust(
                iterations, *(int*)(speedManager + SpeedContextOffset), *_battleStateGlobal, acting, follow);
            if (logging) LogHoldDuration(acting, *_battleStateGlobal, iterations, adjusted);
            else _holdStartedAt = 0;
            return adjusted;
        }
        catch (Exception e)
        {
            if (!_reportedFailure) _logger.WriteLine($"{Tag} Unexpected error in turbo follow, speed left alone: {e.Message}");
            _reportedFailure = true;
            return iterations;
        }
    }

    /// <summary>
    /// Code that runs at the top of the game's function, before its own instructions.
    /// The game's callers keep live values in volatile registers across this call (its compiler knew the
    /// function never writes them; r8 is used right after the call at RVA 0x20289C), so every register
    /// that managed code may overwrite is saved and restored around the call into this mod.
    /// </summary>
    private string[] BuildHookAssembly() =>
    [
        "use64",
        // Seven pushes leave the stack 16-byte aligned for the call below.
        "push rax", "push rcx", "push rdx", "push r8", "push r9", "push r10", "push r11",
        "sub rsp, 0x80", // 0x20 shadow space + six 16-byte slots
        "movdqu [rsp+0x20], xmm0", "movdqu [rsp+0x30], xmm1", "movdqu [rsp+0x40], xmm2",
        "movdqu [rsp+0x50], xmm3", "movdqu [rsp+0x60], xmm4", "movdqu [rsp+0x70], xmm5",
        _hooks!.Utilities.GetAbsoluteCallMnemonics<BeforeRefreshControl>(OnBeforeRefreshControl, out _beforeRefreshControl),
        "movdqu xmm0, [rsp+0x20]", "movdqu xmm1, [rsp+0x30]", "movdqu xmm2, [rsp+0x40]",
        "movdqu xmm3, [rsp+0x50]", "movdqu xmm4, [rsp+0x60]", "movdqu xmm5, [rsp+0x70]",
        "add rsp, 0x80",
        "pop r11", "pop r10", "pop r9", "pop r8", "pop rdx", "pop rcx", "pop rax",
    ];

    private void OnBeforeRefreshControl(nint sceneUnit)
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

        if (_configuration.DebugLog) LogHpChanges();

        var order = AutoBattlePlanner.Plan(new ReadOnlySpan<byte>(_unitTable, BattleLayout.TableSize), slot, _settings);
        if (order is not { } o) return;

        if (_configuration.DebugLog && (unit[BattleLayout.AutoMode] != o.Mode || unit[BattleLayout.AutoTarget] != o.Target))
            _logger.WriteLine($"{Tag} Unit {slot}: {(o.Mode == AutoMode.SaveFadingLife ? "save" : "fight")} -> unit {o.Target}.");

        unit[BattleLayout.AutoTarget] = o.Target;
        unit[BattleLayout.AutoMode] = o.Mode;
        _ordered[slot] = true;
    }

    /// <summary>Diagnostics: how long the game showed an auto unit's chosen action before running it.</summary>
    private void LogHoldDuration(ActingUnit? acting, int battleState, int iterations, int adjusted)
    {
        var holding = battleState == ConfirmActionState && acting is { PlayerOwned: true, AiDriven: true };
        if (holding && _holdStartedAt == 0)
        {
            _holdStartedAt = Stopwatch.GetTimestamp();
            _logger.WriteLine($"{Tag} Pre-action hold begins: turbo mod asked for {iterations} tick(s) per frame, running {adjusted}.");
        }
        else if (!holding && _holdStartedAt != 0)
        {
            _logger.WriteLine($"{Tag} Pre-action hold lasted {Stopwatch.GetElapsedTime(_holdStartedAt).TotalMilliseconds:F0} ms.");
            _holdStartedAt = 0;
        }
    }

    /// <summary>Diagnostics: writes a line whenever a unit's HP differs from the last time it was looked at.</summary>
    private void LogHpChanges()
    {
        for (var slot = 0; slot < BattleLayout.Capacity; slot++)
        {
            var unit = _unitTable + slot * BattleLayout.Stride;
            if (unit[BattleLayout.InUse] == BattleLayout.EmptySlot) { _lastHp[slot] = _lastXp[slot] = -1; continue; }

            var side = (unit[BattleLayout.PermanentFlags] & BattleLayout.TeamMask) == 0 ? "ally" : "enemy";
            int hp = *(ushort*)(unit + BattleLayout.Hp), max = *(ushort*)(unit + BattleLayout.MaxHp);
            if (_lastHp[slot] >= 0 && _lastHp[slot] != hp)
                _logger.WriteLine($"{Tag} HP {side} {slot}: {_lastHp[slot]} -> {hp}/{max}.");
            _lastHp[slot] = hp;

            // Experience runs 0-99 within a level, so level * 100 + experience grows by exactly what was earned.
            var xp = unit[BattleLayout.Level] * 100 + unit[BattleLayout.Experience];
            if (_lastXp[slot] >= 0 && xp > _lastXp[slot])
                _logger.WriteLine($"{Tag} XP {side} {slot}: +{xp - _lastXp[slot]} (level {unit[BattleLayout.Level]}, exp {unit[BattleLayout.Experience]}).");
            _lastXp[slot] = xp;
        }
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
