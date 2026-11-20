using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using Ivalice.Party;
using ivalice.party.reincarnation.Configuration;
using ivalice.party.reincarnation.Template;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.Enums;
using Reloaded.Hooks.Definitions.X64;
using Reloaded.Memory.SigScan.ReloadedII.Interfaces;
using Reloaded.Mod.Interfaces;

namespace ivalice.party.reincarnation;

/// <summary>
/// Reincarnation: a party member gives up its levels for better starting stats. The rules live in
/// <see cref="Reincarnation"/>; this class finds the game's data, gets a safe moment on the game's main
/// thread once per frame, and there exchanges files with Ivalice Control Center: it writes the roster
/// out, and reads, checks and carries out commands. See docs/reincarnation.md for the evidence.
/// </summary>
public unsafe class Mod : ModBase // <= Do not Remove.
{
    private const string Tag = "[Reincarnation]";
    private const string ModVersion = "0.4.0";

    // A function that finds a free roster slot; the roster's address is the target of the
    // "lea rcx, [rip+disp]" found RosterLeaOffset bytes into the match.
    private const string RosterPattern =
        "48 81 EC B8 01 00 00 C6 02 00 4C 8D 04 24 33 D2 44 8B C9 83 FA 36 72 04 33 C0 EB 14 48 63 C2 48 8D 0D ?? ?? ?? ??";
    private const int RosterLeaOffset = 0x1F;

    // Start of the function that creates the battle controller object: "cmp [rip+disp], rdi" tests
    // the pointer to it, BattleControlCmpOffset bytes into the match.
    private const string BattleControlPattern = "33 FF BD 58 01 00 00 48 39 3D ?? ?? ?? ?? 44 8D 77 40";
    private const int BattleControlCmpOffset = 0x07;

    // The same function Smart Auto Battle uses to find the battle unit table ("lea r15, [rip+disp]").
    private const string BattleTablePattern =
        "48 89 5C 24 ?? 48 89 6C 24 ?? 48 89 74 24 ?? 57 41 56 41 57 48 83 EC ?? 48 8B 05 ?? ?? ?? ?? " +
        "48 ?? ?? 48 89 44 24 ?? 48 63 F1 4C 8D 3D";
    private const int BattleTableLeaOffset = 0x2A;

    // In the game's level-up routine: "lea rax, [rip+disp]" loads the address of the job table's
    // growth columns, JobTableLeaOffset bytes into the match.
    private const string JobTablePattern = "0F B6 49 1D BD 01 00 00 00 48 6B D8 31 48 8D 05 ?? ?? ?? ??";
    private const int JobTableLeaOffset = 0x0D;

    // Entry of the game's per-frame task runner. The hook replaces its first instruction only, and the
    // pattern stops before the bytes Smart Auto Battle's own hook changes further down.
    private const string TaskRunnerPattern =
        "48 89 5C 24 08 48 89 6C 24 10 48 89 74 24 18 57 41 56 41 57 48 83 EC 20 4C 8B 05 ?? ?? ?? ?? 49 8B C8";

    // Optional: const char* GetDisplayName(int nameKey, const char* ownName).
    private const string DisplayNamePattern =
        "48 89 5C 24 18 57 48 83 EC 20 48 8B 05 ?? ?? ?? ?? 48 8B FA 8B D9 48 85 C0 75 28 48 8B 0D ?? ?? ?? ??";

    // Optional, for the diagnostic inputs only: the acting unit's scene object, the battle state word
    // and the battle mode variable.
    private const string ActiveActorPattern =
        "48 8B 0D ?? ?? ?? ?? EB 0F 0F B6 41 08 3B 05 ?? ?? ?? ?? 74 0B 48 8B 09 48 85 C9 75 EC 33 C0 C3 48 8B C1 C3 4C 8B 0D";
    private const string BattleStatePattern =
        "8B 05 ?? ?? ?? ?? 83 F8 34 0F 84 ?? ?? ?? ?? 83 F8 3A 0F 84 ?? ?? ?? ?? 48 63 05 ?? ?? ?? ?? 48 8D 3D";
    private const string BattleModePattern = "83 3D ?? ?? ?? ?? 04 75 29 48 8B 0D ?? ?? ?? ?? 41 8B D5 48 85 C9 74 1A";

    /// <summary>How often the mod looks for a command.</summary>
    private const int TickMilliseconds = 250;
    /// <summary>How often the roster is compared with what was last written out.</summary>
    private const int ExportMilliseconds = 1000;
    private const int MaxNameBytes = 64;

    [Function(CallingConventions.Microsoft)]
    private delegate void FrameCallback();

    private readonly IReloadedHooks? _hooks;
    private readonly ILogger _logger;
    private volatile Config _configuration;
    private readonly BridgePaths _paths = BridgePaths.Default;

    private nint _gameBase;
    private int _gameSize;
    private byte* _roster;
    private nint* _battleControlPointer;
    private byte* _battleTable;
    private byte* _jobTable;
    private delegate* unmanaged<int, byte*, byte*> _getDisplayName;
    private delegate* unmanaged<byte*> _getActiveActor;
    private int* _battleState;
    private int* _battleMode;

    private IAsmHook? _frameHook;
    private IReverseWrapper<FrameCallback>? _frameCallback;
    private IAsmHook? _selfTestHook;
    private IReverseWrapper<FrameCallback>? _selfTestCallback;
    private volatile bool _selfTesting;
    private int _selfTestCalls;
    private double _selfTestScratch;

    // Touched only on the game's thread, inside the hook.
    private int _gameThread;
    private bool _inTick;
    private long _lastTickAt;
    private long _lastExportAt;
    private string? _lastContent;
    private DateTimeOffset _lastWriteAt;
    private string? _lastHandledCommand;
    private string? _lastLoggedStatus;
    private bool _reportedFailure;
    private bool _reportedWrongThread;
    private bool _reportedExportFailure;

    public Mod(ModContext context)
    {
        _hooks = context.Hooks;
        _logger = context.Logger;
        _configuration = context.Configuration;

        if (_hooks is null || !context.ModLoader.GetController<IStartupScanner>().TryGetTarget(out var scanner))
        {
            _logger.WriteLine($"{Tag} Hooks or signature scanner unavailable. The mod is inactive.");
            return;
        }

        var module = Process.GetCurrentProcess().MainModule!;
        _gameBase = module.BaseAddress;
        _gameSize = module.ModuleMemorySize;

        scanner.AddMainModuleScan(RosterPattern, r => _roster = RipTarget(r.Found, r.Offset, RosterLeaOffset, 3, 7));
        scanner.AddMainModuleScan(BattleControlPattern,
            r => _battleControlPointer = (nint*)RipTarget(r.Found, r.Offset, BattleControlCmpOffset, 3, 7));
        scanner.AddMainModuleScan(BattleTablePattern, r => _battleTable = RipTarget(r.Found, r.Offset, BattleTableLeaOffset, 3, 7));
        scanner.AddMainModuleScan(JobTablePattern, r =>
        {
            var growthColumns = RipTarget(r.Found, r.Offset, JobTableLeaOffset, 3, 7);
            if (growthColumns != null) _jobTable = growthColumns - JobTable.GrowthOffset;
        });
        scanner.AddMainModuleScan(DisplayNamePattern, r =>
        {
            if (r.Found) _getDisplayName = (delegate* unmanaged<int, byte*, byte*>)(_gameBase + r.Offset);
        });
        scanner.AddMainModuleScan(ActiveActorPattern, r =>
        {
            if (r.Found) _getActiveActor = (delegate* unmanaged<byte*>)(_gameBase + r.Offset);
        });
        scanner.AddMainModuleScan(BattleStatePattern, r => _battleState = (int*)RipTarget(r.Found, r.Offset, 0, 2, 6));
        scanner.AddMainModuleScan(BattleModePattern, r => _battleMode = (int*)RipTarget(r.Found, r.Offset, 0, 2, 7));

        // Registered last, so every scan it depends on has already reported.
        scanner.AddMainModuleScan(TaskRunnerPattern, r => Activate(r.Found, r.Offset));
    }

    /// <summary>The address a rip-relative instruction inside a pattern match refers to, or null.</summary>
    private byte* RipTarget(bool found, int matchOffset, int instructionOffset, int displacementOffset, int instructionLength)
    {
        if (!found) return null;
        var instruction = (byte*)(_gameBase + matchOffset + instructionOffset);
        var target = instruction + instructionLength + *(int*)(instruction + displacementOffset);
        // An address outside the game's own image means the pattern matched something else.
        return (nint)target >= _gameBase && (nint)target < _gameBase + _gameSize ? target : null;
    }

    private void Activate(bool taskRunnerFound, int taskRunnerOffset)
    {
        var missing = new List<string>();
        if (_roster == null) missing.Add("party roster");
        if (_battleControlPointer == null) missing.Add("battle controller pointer");
        if (_battleTable == null) missing.Add("battle unit table");
        if (_jobTable == null) missing.Add("job table");
        if (!taskRunnerFound) missing.Add("frame routine");
        if (missing.Count > 0)
        {
            _logger.WriteLine($"{Tag} Could not find the game data this mod relies on ({string.Join(", ", missing)}). The game version is probably unsupported; the mod is inactive.");
            return;
        }

        try
        {
            Directory.CreateDirectory(_paths.BridgeDirectory);
            // A command never outlives the game session it was meant for.
            File.Delete(_paths.CommandFile);
            foreach (var old in Directory.EnumerateFiles(_paths.BridgeDirectory, "result-*.json"))
                if (File.GetLastWriteTimeUtc(old) < DateTime.UtcNow.AddDays(-1)) File.Delete(old);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _logger.WriteLine($"{Tag} Could not prepare {_paths.BridgeDirectory} ({e.Message}). The mod is inactive.");
            return;
        }

        try
        {
            var lost = TestHookStub();
            if (lost != LostRegisters.None)
            {
                _logger.WriteLine($"{Tag} Self-test FAILED: the hook stub does not hand back these registers unchanged: {lost}. No hook was installed and the mod is inactive.");
                return;
            }
            _logger.WriteLine($"{Tag} Self-test passed: the hook stub preserves rax, rcx, rdx, r8-r11 and xmm0-xmm5.");

            var address = _gameBase + taskRunnerOffset;
            _frameHook = _hooks!.CreateAsmHook(BuildHookAssembly(out _frameCallback), address,
                new AsmHookOptions { Behaviour = AsmHookBehaviour.ExecuteFirst, PreferRelativeJump = true, MaxOpcodeSize = 5 });
            _frameHook.Activate();

            _logger.WriteLine($"{Tag} Ready: roster at 0x{(nint)_roster:X}, battle controller pointer at 0x{(nint)_battleControlPointer:X}, " +
                              $"battle unit table at 0x{(nint)_battleTable:X}, job table at 0x{(nint)_jobTable:X}, frame routine hooked at 0x{address:X}. " +
                              $"Names: {(_getDisplayName != null ? "found" : "not found")}. Safe mode is {(_configuration.SafeMode ? "ON" : "OFF")}. " +
                              $"Files: {_paths.BridgeDirectory}");
        }
        catch (Exception e)
        {
            _logger.WriteLine($"{Tag} The frame hook could not be installed ({e.Message}). The mod is inactive.");
        }
    }

    /// <summary>The register-saving stub of <see cref="HookStub"/>, calling <see cref="OnFrame"/>.</summary>
    private string[] BuildHookAssembly(out IReverseWrapper<FrameCallback> callback) =>
        HookStub.Build(_hooks!.Utilities.GetAbsoluteCallMnemonics<FrameCallback>(OnFrame, out callback));

    /// <summary>
    /// Puts the very same stub on a throwaway function that does nothing and calls it with marker
    /// values in every register the stub must preserve.
    /// </summary>
    private LostRegisters TestHookStub()
    {
        var quiet = RegisterSelfTest.AllocateQuietFunction();
        if (quiet == 0) return LostRegisters.ProbeFailed;

        // The throwaway function is never freed: the hook's own code keeps pointing at it.
        _selfTestHook = _hooks!.CreateAsmHook(BuildHookAssembly(out _selfTestCallback), quiet, AsmHookBehaviour.ExecuteFirst);
        _selfTestHook.Activate();
        _selfTesting = true;
        try
        {
            var lost = RegisterSelfTest.Probe(quiet);
            // A probe during which the callback never ran has proven nothing.
            return _selfTestCalls > 0 ? lost : lost | LostRegisters.ProbeFailed;
        }
        finally
        {
            _selfTesting = false;
            _selfTestHook.Disable();
        }
    }

    /// <summary>Work for the self-test: managed code that uses integer and floating-point registers.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ExerciseRegisters()
    {
        _selfTestCalls++;
        var text = string.Concat(Environment.TickCount64.ToString(), "/", _selfTestCalls.ToString());
        _selfTestScratch = Math.Sqrt(text.Length * 1.5) + Math.Sin(_selfTestCalls) * Math.Pow(text.Length, 0.3);
    }

    private void OnFrame()
    {
        // Runs once per frame on the game's main thread. Nothing here may throw: an exception would
        // unwind into the game's native code.
        try
        {
            if (_selfTesting)
            {
                ExerciseRegisters();
                return;
            }

            var now = Environment.TickCount64;
            if (now - _lastTickAt < TickMilliseconds || _inTick) return;
            _lastTickAt = now;

            var thread = Environment.CurrentManagedThreadId;
            if (_gameThread == 0) _gameThread = thread;
            if (thread != _gameThread)
            {
                if (!_reportedWrongThread) _logger.WriteLine($"{Tag} The frame routine ran on a second thread; that call was ignored.");
                _reportedWrongThread = true;
                return;
            }

            _inTick = true;
            try { Tick(now); }
            finally { _inTick = false; }
        }
        catch (Exception e)
        {
            if (!_reportedFailure) _logger.WriteLine($"{Tag} Unexpected error, nothing was changed: {e.Message}");
            _reportedFailure = true;
        }
    }

    private void Tick(long now)
    {
        var config = _configuration;
        var roster = new Span<byte>(_roster, RosterLayout.TableSize);
        var battleTable = new ReadOnlySpan<byte>(_battleTable, BattleBinding.TableSize);
        var jobTable = new ReadOnlySpan<byte>(_jobTable, JobTable.TableSize);
        var game = ReadGameStatus(battleTable);
        if (config.DebugLog) LogStatusChange(game);

        var handled = HandleCommand(roster, battleTable, jobTable, game, config);
        if (!handled && now - _lastExportAt < ExportMilliseconds) return;
        _lastExportAt = now;
        Export(roster, battleTable, jobTable, game, config);
    }

    private GameStatus ReadGameStatus(ReadOnlySpan<byte> battleTable) => GameSafety.Decide(new SafetyInputs(
        BattleControlPresent: *_battleControlPointer != 0,
        ActiveActorPresent: _getActiveActor != null ? _getActiveActor() != null : null,
        BattleUnitsInUse: BattleBinding.CountInUse(battleTable),
        BattleUnitsBound: BattleBinding.CountBound(battleTable),
        BattleModeFlag: _battleMode != null ? *_battleMode : null,
        BattleState: _battleState != null ? *_battleState : null));

    private void LogStatusChange(GameStatus game)
    {
        var inputs = game.Inputs;
        var status = $"inBattle={game.InBattle} ({game.DecidedBy}); battleControl={inputs.BattleControlPresent}, " +
                     $"activeActor={inputs.ActiveActorPresent?.ToString() ?? "unknown"}, battleMode={inputs.BattleModeFlag?.ToString() ?? "unknown"}, " +
                     $"boundBattleUnits={inputs.BattleUnitsBound}";
        if (status == _lastLoggedStatus) return;
        _lastLoggedStatus = status;
        _logger.WriteLine($"{Tag} Roster at 0x{(nint)_roster:X}. {status}, battleUnitsInUse={inputs.BattleUnitsInUse}, " +
                          $"battleState={inputs.BattleState?.ToString() ?? "unknown"}.");
    }

    /// <returns>True when a command was answered, so the roster file should be refreshed at once.</returns>
    private bool HandleCommand(Span<byte> roster, ReadOnlySpan<byte> battleTable, ReadOnlySpan<byte> jobTable,
        GameStatus game, Config config)
    {
        if (!File.Exists(_paths.CommandFile)) return false;

        string text;
        DateTimeOffset fileTime;
        try
        {
            text = File.ReadAllText(_paths.CommandFile);
            fileTime = new DateTimeOffset(File.GetLastWriteTimeUtc(_paths.CommandFile), TimeSpan.Zero);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false; // Still being written; look again on the next tick.
        }

        var now = DateTimeOffset.UtcNow;
        var parsed = CommandProcessor.Parse(text);
        // A file caught half-written reads as garbage; give its writer a moment before answering.
        if (parsed is { Id: null, Refusal: Refusal.Malformed } && (now - fileTime).TotalSeconds < 2) return false;

        // The same file seen twice was already answered; only removing it failed last time.
        var fileKey = $"{parsed.Id}@{fileTime.UtcTicks}";
        if (fileKey == _lastHandledCommand)
        {
            TryDelete(_paths.CommandFile);
            return false;
        }
        _lastHandledCommand = fileKey;

        CommandResult result;
        if (parsed.Command is not { } command)
            result = CommandProcessor.Refuse(parsed.Id, parsed.Refusal, config.SafeMode, now);
        else
        {
            if (config.DebugLog)
                _logger.WriteLine($"{Tag} Command {command.Id}: reincarnate slot {command.Slot}, fingerprint {command.ExpectedFingerprint ?? "none"}, level {command.ExpectedLevel?.ToString() ?? "any"}.");
            result = CommandProcessor.Execute(command, roster, battleTable, jobTable, game, now, fileTime, config.SafeMode,
                WriteJournal, ResolveName);
        }

        if (result.Applied)
            _logger.WriteLine($"{Tag} Slot {result.Unit?.Slot}: {result.Message} Journal: {_paths.JournalFile}");
        else if (config.DebugLog)
            _logger.WriteLine($"{Tag} Command {result.Id ?? "without id"}: {(result.Ok ? "accepted" : "refused")} ({result.Reason}). {result.Message}");

        try
        {
            var path = parsed.Id is { } id ? _paths.ResultFile(id) : _paths.InvalidResultFile;
            WriteAtomically(path, JsonSerializer.Serialize(result, BridgeJson.Default.CommandResult));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _logger.WriteLine($"{Tag} Could not write the answer to command {result.Id ?? "without id"} ({e.Message}). Outcome: {result.Reason}, applied: {result.Applied}.");
        }
        TryDelete(_paths.CommandFile);
        return true;
    }

    private bool WriteJournal(JournalEntry entry)
    {
        File.AppendAllText(_paths.JournalFile, JsonSerializer.Serialize(entry, BridgeJson.Default.JournalEntry) + "\n");
        return true;
    }

    private void Export(ReadOnlySpan<byte> roster, ReadOnlySpan<byte> battleTable, ReadOnlySpan<byte> jobTable,
        GameStatus game, Config config)
    {
        var units = RosterExport.DescribeAll(roster, battleTable, jobTable, game, ResolveName);
        var document = new RosterDocument(RosterDocument.CurrentSchema, ModVersion, Environment.ProcessId, WrittenAt: "",
            config.SafeMode, game, units);

        // Written when something changed, and otherwise often enough to show the game is still alive.
        var content = RosterFeed.Serialize(document);
        var now = DateTimeOffset.UtcNow;
        if (content == _lastContent && (now - _lastWriteAt).TotalSeconds < RosterFeed.HeartbeatSeconds) return;

        try
        {
            WriteAtomically(_paths.RosterFile, RosterFeed.Serialize(document with { WrittenAt = CommandProcessor.Stamp(now) }));
            _lastContent = content;
            _lastWriteAt = now;
            _reportedExportFailure = false;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The Control Center may be reading the file; the next tick tries again.
            if (!_reportedExportFailure && config.DebugLog) _logger.WriteLine($"{Tag} Could not write the roster file ({e.Message}); will retry.");
            _reportedExportFailure = true;
        }
    }

    /// <summary>
    /// Asks the game for the name behind a name key. The game's function is called with no "own name",
    /// which keeps it to table lookups; the pointer it returns is checked before it is read.
    /// </summary>
    private string? ResolveName(int nameKey)
    {
        if (_getDisplayName == null || nameKey <= 0 || !_configuration.ResolveNames) return null;

        var text = _getDisplayName(nameKey, null);
        var readable = ReadableBytes(text, MaxNameBytes);
        if (readable <= 0) return null;

        var bytes = new ReadOnlySpan<byte>(text, readable);
        var end = bytes.IndexOf((byte)0);
        return end <= 0 ? null : NameText.Decode(bytes[..end]);
    }

    /// <summary>How many bytes from <paramref name="address"/> on are committed, readable memory, up to a limit.</summary>
    private static int ReadableBytes(byte* address, int limit)
    {
        if (address == null || VirtualQuery(address, out var info, (nuint)sizeof(MemoryInformation)) == 0) return 0;
        const uint commit = 0x1000, noAccess = 0x01, guard = 0x100;
        if (info.State != commit || info.Protect == 0 || (info.Protect & (noAccess | guard)) != 0) return 0;
        var regionEnd = (byte*)info.BaseAddress + info.RegionSize;
        return (int)Math.Min(limit, regionEnd - address);
    }

    private static void WriteAtomically(string path, string content)
    {
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, content);
        File.Move(temporary, path, overwrite: true);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    #region Standard Overrides
    public override void ConfigurationUpdated(Config configuration)
    {
        _configuration = configuration;
        _logger.WriteLine($"{Tag} Settings updated: safe mode {(configuration.SafeMode ? "ON" : "OFF")}, names {(configuration.ResolveNames ? "on" : "off")}, diagnostic log {(configuration.DebugLog ? "on" : "off")}.");
    }
    #endregion

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryInformation
    {
        public nint BaseAddress;
        public nint AllocationBase;
        public uint AllocationProtect;
        public ushort PartitionId;
        public nint RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

    [DllImport("kernel32.dll")]
    private static extern nuint VirtualQuery(byte* address, out MemoryInformation information, nuint length);

    #region For Exports, Serialization etc.
#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
    public Mod() { }
#pragma warning restore CS8618
    #endregion
}
