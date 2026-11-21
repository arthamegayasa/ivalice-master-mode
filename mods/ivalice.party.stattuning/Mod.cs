using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using Ivalice.Party;
using ivalice.party.stattuning.Configuration;
using ivalice.party.stattuning.Template;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.Enums;
using Reloaded.Hooks.Definitions.X64;
using Reloaded.Memory.SigScan.ReloadedII.Interfaces;
using Reloaded.Mod.Interfaces;

namespace ivalice.party.stattuning;

/// <summary>
/// Stat Tuning: the player's own growth constants and stat multipliers, per job and per character.
/// The game copies ten bytes (growth and multiplier for five stats) from a job's row into every unit
/// it builds, and computes stats and level-ups from that copy. This mod runs right after each of the
/// two places that make the copy and rewrites it according to <see cref="StatTuningRules"/>. The job
/// table itself is never changed. See docs/stat-tuning.md for the evidence behind every address.
/// </summary>
public unsafe class Mod : ModBase // <= Do not Remove.
{
    private const string Tag = "[Stat Tuning]";

    // A function that finds a free roster slot; the roster's address is the target of the
    // "lea rcx, [rip+disp]" found RosterLeaOffset bytes into the match.
    private const string RosterPattern =
        "48 81 EC B8 01 00 00 C6 02 00 4C 8D 04 24 33 D2 44 8B C9 83 FA 36 72 04 33 C0 EB 14 48 63 C2 48 8D 0D ?? ?? ?? ??";
    private const int RosterLeaOffset = 0x1F;

    // In the game's roster level-up routine: "lea rax, [rip+disp]" loads the address of the job
    // table's growth columns, JobTableLeaOffset bytes into the match.
    private const string JobTablePattern = "0F B6 49 1D BD 01 00 00 00 48 6B D8 31 48 8D 05 ?? ?? ?? ??";
    private const int JobTableLeaOffset = 0x0D;

    // The two places that copy a job row's ten growth and multiplier bytes into a unit record (rbx):
    //   lea rcx, [rbx+0x8A] / call copy / mov rdx, rbp / mov rcx, rbx / call <stat routine>
    // The hook goes on the two "mov" instructions, after the copy and before the stats are computed.
    // Site A is in the routine that builds a whole unit, site B in the one that applies a job to it.
    private const string CopySiteAPattern = "4C 03 C0 48 8D 8B 8A 00 00 00 E8 ?? ?? ?? ?? 48 8B D5 48 8B CB E8";
    private const int CopySiteAHookOffset = 0x0F;
    private const string CopySiteBPattern = "4C 8D 47 0E 48 8D 8B 8A 00 00 00 E8 ?? ?? ?? ?? 48 8B D5 48 8B CB E8";
    private const int CopySiteBHookOffset = 0x10;

    // The three places that put a job row's base Move and Jump into a unit record (+0x42, +0x43).
    // Equipment and ability bonuses are added later by another routine, on top of what is there.
    //   Site 1, in the routine that applies a job (unit in rbx):
    //     mov [rbx+0x42], al / mov al, [rdi+0x19] / and al, 0x7F / mov [rbx+0x43], al / mov al, cl / and al, 0xBF / or cl, 0x40
    //     The hook goes on the last three instructions and runs before them.
    //   Sites 2 and 3, in the two routines that recompute a unit's derived values (unit in rdi):
    //     mov [rdi+0x42], al / mov al, [r8+r10+0x19] / and al, 0x7F / mov [rdi+0x43], al / add rdx, r8 / je ...
    //     The hook covers the three instructions from "mov al" on and runs after them, before "add".
    //     The two differ only in the distance of the "je".
    private const string MoveSite1Pattern = "88 43 42 8A 47 19 24 7F 88 43 43 8A C1 24 BF 80 C9 40";
    private const int MoveSite1HookOffset = 0x0B;
    private const string MoveSite2Pattern = "88 47 42 43 8A 44 10 19 24 7F 88 47 43 49 03 D0 74 18";
    private const string MoveSite3Pattern = "88 47 42 43 8A 44 10 19 24 7F 88 47 43 49 03 D0 74 19";
    private const int MoveSiteHookOffset = 0x03;
    private const int MoveSiteHookLength = 10;

    /// <summary>How often the rules file is checked for a change.</summary>
    private const int PollMilliseconds = 1000;
    private const int MaxDiagnosticLines = 60;

    [Function(CallingConventions.Microsoft)]
    private delegate void UnitCallback(byte* unit);

    [Function(CallingConventions.Microsoft)]
    private delegate void LimitCallback(LimitRegisters* frame, LimitSite site);

    private readonly IReloadedHooks? _hooks;
    private readonly ILogger _logger;
    private volatile Config _configuration;
    private readonly StatTuningPaths _paths = StatTuningPaths.Default;

    private nint _gameBase;
    private int _gameSize;
    private byte* _roster;
    private byte* _jobTable;
    private int _siteA = -1;
    private int _moveSite1 = -1;
    private int _moveSite2 = -1;
    private int _moveSite3 = -1;

    private readonly List<IAsmHook> _unitHooks = [];
    private readonly List<IReverseWrapper<UnitCallback>> _unitCallbacks = [];
    private readonly List<IReverseWrapper<LimitCallback>> _limitCallbacks = [];
    private volatile bool _limitsReady;
    private bool? _publishedLimitsReady;
    private IAsmHook? _selfTestHook;
    private IReverseWrapper<UnitCallback>? _selfTestCallback;
    private volatile bool _selfTesting;
    private int _selfTestCalls;
    private double _selfTestScratch;

    private volatile StatTuningRules _rules = StatTuningRules.Empty;
    private Timer? _poll;
    private int _polling;
    private (DateTime WrittenAt, long Length)? _loadedFile;
    private string? _exportedJobs;
    private bool _reportedBadRules;

    private bool _reportedFailure;
    private int _changedUnits;
    private readonly HashSet<string> _diagnosed = [];

    public Mod(ModContext context)
    {
        _hooks = context.Hooks;
        _logger = context.Logger;
        _configuration = context.Configuration;
        PublishLimitsReady();

        if (_hooks is null || !context.ModLoader.GetController<IStartupScanner>().TryGetTarget(out var scanner))
        {
            Log("Hooks or signature scanner unavailable. The mod is inactive.");
            return;
        }

        var module = Process.GetCurrentProcess().MainModule!;
        _gameBase = module.BaseAddress;
        _gameSize = module.ModuleMemorySize;

        // Results arrive in the order the scans were added; the last one activates the mod.
        scanner.AddMainModuleScan(RosterPattern, r => _roster = RipTarget(r.Found, r.Offset, RosterLeaOffset, 3, 7, 0, RosterLayout.TableSize));
        scanner.AddMainModuleScan(JobTablePattern, r =>
            _jobTable = RipTarget(r.Found, r.Offset, JobTableLeaOffset, 3, 7, -JobTable.GrowthOffset, JobTable.TableSize));
        scanner.AddMainModuleScan(MoveSite1Pattern, r => _moveSite1 = r.Found ? r.Offset + MoveSite1HookOffset : -1);
        scanner.AddMainModuleScan(MoveSite2Pattern, r => _moveSite2 = r.Found ? r.Offset + MoveSiteHookOffset : -1);
        scanner.AddMainModuleScan(MoveSite3Pattern, r => _moveSite3 = r.Found ? r.Offset + MoveSiteHookOffset : -1);
        scanner.AddMainModuleScan(CopySiteAPattern, r => _siteA = r.Found ? r.Offset + CopySiteAHookOffset : -1);
        scanner.AddMainModuleScan(CopySiteBPattern, r => Activate(_siteA, r.Found ? r.Offset + CopySiteBHookOffset : -1));
    }

    private void Log(string message) => _logger.WriteLine($"{Tag} {message}");

    /// <summary>
    /// The start of the table a rip-relative instruction inside a pattern match refers to, or null.
    /// </summary>
    /// <param name="adjust">Added to the address the instruction names to reach the table's first byte.</param>
    /// <param name="length">The table's size; all of it must lie inside the game's image.</param>
    private byte* RipTarget(bool found, int matchOffset, int instructionOffset, int displacementOffset, int instructionLength,
        int adjust, int length)
    {
        if (!found) return null;
        var instruction = (byte*)(_gameBase + matchOffset + instructionOffset);
        var start = (nint)(instruction + instructionLength + *(int*)(instruction + displacementOffset)) + adjust;
        // A table that is not wholly inside the game's own image means the pattern matched something else.
        return start >= _gameBase && start + length <= _gameBase + _gameSize ? (byte*)start : null;
    }

    private void Activate(int siteA, int siteB)
    {
        if (siteA < 0 || siteB < 0)
        {
            Log("Could not find the places where the game copies a job's growth data into a unit. The game version is probably unsupported; the mod is inactive.");
            return;
        }
        if (_roster == null) Log("Party roster not found: rules per character are off, rules per job still work.");
        if (_jobTable == null) Log("Job table not found: the Control Center cannot show the game's own job values.");

        try
        {
            var lost = TestHookStub();
            if (lost != LostRegisters.None)
            {
                Log($"Self-test FAILED: the hook stub does not hand back these registers unchanged: {lost}. No hook was installed and the mod is inactive.");
                return;
            }
            Log("Self-test passed: the hook stub preserves rax, rcx, rdx, r8-r11 and xmm0-xmm5.");

            foreach (var site in new[] { siteA, siteB })
            {
                var assembly = HookStub.Build(_hooks!.Utilities.GetAbsoluteCallMnemonics<UnitCallback>(OnUnitData, out var callback), atFunctionEntry: false);
                _unitCallbacks.Add(callback);
                var hook = _hooks.CreateAsmHook(assembly, _gameBase + site,
                    new AsmHookOptions { Behaviour = AsmHookBehaviour.ExecuteFirst, PreferRelativeJump = true, MaxOpcodeSize = 5 });
                hook.Activate();
                _unitHooks.Add(hook);
            }

            // Move and Jump need all three places: with one missing, a unit would get the tuned value in
            // some situations and the game's in others.
            var movement = _moveSite1 >= 0 && _moveSite2 >= 0 && _moveSite3 >= 0;
            if (movement)
            {
                InstallMovementHook(_moveSite1, "rbx", new AsmHookOptions { Behaviour = AsmHookBehaviour.ExecuteFirst, PreferRelativeJump = true, MaxOpcodeSize = 5 });
                foreach (var site in new[] { _moveSite2, _moveSite3 })
                    InstallMovementHook(site, "rdi", new AsmHookOptions { Behaviour = AsmHookBehaviour.ExecuteAfter, PreferRelativeJump = true, MaxOpcodeSize = 5, hookLength = MoveSiteHookLength });
            }
            else Log("Could not find all three places where the game sets a unit's Move and Jump: rules for Move and Jump are off, the other rules still work.");

            InstallLimitHooks();

            Directory.CreateDirectory(_paths.Root);
            LoadRules();
            PublishLimitsReady();
            _poll = new Timer(_ => Poll(), null, PollMilliseconds, PollMilliseconds);

            var rules = _rules;
            Log($"Ready: copy sites hooked at 0x{_gameBase + siteA:X} and 0x{_gameBase + siteB:X}, " +
                (movement ? $"Move and Jump sites at 0x{_gameBase + _moveSite1:X}, 0x{_gameBase + _moveSite2:X} and 0x{_gameBase + _moveSite3:X}, " : "") +
                $"roster at 0x{(nint)_roster:X}, " +
                $"job table at 0x{(nint)_jobTable:X}. Rules: {Describe(rules)}. File: {_paths.RulesFile}");
        }
        catch (Exception e)
        {
            _limitsReady = false;
            PublishLimitsReady();
            // Never leave one of the two places hooked without the other.
            foreach (var hook in _unitHooks)
            {
                try { hook.Disable(); }
                catch (Exception) { /* Nothing more can be done for this one. */ }
            }
            _unitHooks.Clear();
            _poll?.Dispose();
            _poll = null;
            Log($"The hooks could not be installed ({e.Message}). The mod is inactive.");
        }
    }

    private void InstallMovementHook(int site, string unitRegister, AsmHookOptions options)
    {
        var assembly = HookStub.Build(_hooks!.Utilities.GetAbsoluteCallMnemonics<UnitCallback>(OnUnitMovement, out var callback),
            atFunctionEntry: false, unitRegister);
        _unitCallbacks.Add(callback);
        var hook = _hooks.CreateAsmHook(assembly, _gameBase + site, options);
        hook.Activate();
        _unitHooks.Add(hook);
    }

    private void InstallLimitHooks()
    {
        if (_roster == null) { Log("Player stat limits inactive: missing roster."); return; }
        var installed = new List<IAsmHook>();
        try
        {
            using var file = File.OpenRead(Environment.ProcessPath!);
            if (!Convert.ToHexString(SHA256.HashData(file)).Equals(StatLimitHooks.GameHash, StringComparison.Ordinal))
            {
                Log("Player stat limits inactive: this executable has not been verified.");
                return;
            }
            foreach (var site in StatLimitHooks.Sites)
            {
                var expected = Convert.FromHexString(site.ExpectedBytes);
                if (site.Rva > _gameSize - expected.Length ||
                    !new ReadOnlySpan<byte>((void*)(_gameBase + site.Rva), expected.Length).SequenceEqual(expected))
                {
                    Log($"Player stat limits inactive: {site.Kind} does not match the expected game code.");
                    return;
                }
            }
            foreach (var site in StatLimitHooks.Sites)
            {
                var call = _hooks!.Utilities.GetAbsoluteCallMnemonics<LimitCallback>(OnStatLimit, out var wrapper);
                _limitCallbacks.Add(wrapper);
                var hook = _hooks.CreateAsmHook(StatLimitHooks.Stub(site, call), _gameBase + site.Rva,
                    new AsmHookOptions { Behaviour = AsmHookBehaviour.ExecuteFirst, PreferRelativeJump = true, MaxOpcodeSize = 5, hookLength = site.HookLength });
                installed.Add(hook);
                hook.Activate();
            }
            _unitHooks.AddRange(installed);
            _limitsReady = true;
            Log("Player stat limits ready: base stats, armor, equipped totals and three status views.");
        }
        catch (Exception e)
        {
            _limitsReady = false;
            foreach (var hook in installed)
                try { hook.Disable(); }
                catch (Exception) { /* Callbacks remain inactive when activation fails. */ }
            Log($"Player stat limits inactive: {e.Message}");
        }
    }

    private void OnStatLimit(LimitRegisters* frame, LimitSite site)
    {
        try
        {
            if (_limitsReady)
                StatLimitHooks.Apply(site, ref *frame, _rules.PlayerLimits, new ReadOnlySpan<byte>(_roster, RosterLayout.TableSize));
        }
        catch (Exception e)
        {
            if (_reportedFailure) return;
            _reportedFailure = true;
            try { Log($"Could not apply a player stat limit at {site}: {e.Message}"); }
            catch (Exception) { /* Keep the exception out of the game's thread. */ }
        }
    }

    // The PID and start time let Control Center reject another session's cached readiness.
    private void PublishLimitsReady()
    {
        var ready = _limitsReady;
        if (_publishedLimitsReady == ready) return;
        try
        {
            using var process = Process.GetCurrentProcess();
            var session = new PlayerLimitSession(process.Id, process.StartTime.ToUniversalTime().Ticks, ready);
            var status = JsonSerializer.Serialize(session, BridgeJson.Default.PlayerLimitSession);
            Directory.CreateDirectory(_paths.Root);
            var path = _paths.LimitsReadyFile;
            var temporary = path + "." + process.Id + ".tmp";
            File.WriteAllText(temporary, status);
            File.Move(temporary, path, overwrite: true);
            _publishedLimitsReady = ready;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Keep the game running. Poll retries if Control Center had the file open.
        }
    }

    private static string Describe(StatTuningRules rules) =>
        rules.IsEmpty ? "none (the game's own values are used)"
            : $"{rules.Jobs.Count} job(s), {rules.Characters.Count} character(s), enemies {(rules.ApplyJobsToEnemies ? "included" : "untouched")}";

    /// <summary>
    /// Puts the stub on a throwaway function that does nothing and calls it with marker values in every
    /// register the stub must preserve. The throwaway is hooked at its entry, so the stub is built for
    /// that stack alignment; its saved registers are the same as at the real sites.
    /// </summary>
    private LostRegisters TestHookStub()
    {
        var quiet = RegisterSelfTest.AllocateQuietFunction();
        if (quiet == 0) return LostRegisters.ProbeFailed;

        // The throwaway function is never freed: the hook's own code keeps pointing at it.
        var assembly = HookStub.Build(_hooks!.Utilities.GetAbsoluteCallMnemonics<UnitCallback>(OnUnitData, out _selfTestCallback), atFunctionEntry: true);
        _selfTestHook = _hooks.CreateAsmHook(assembly, quiet, AsmHookBehaviour.ExecuteFirst);
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

    /// <summary>
    /// Runs on a game thread right after the game copied a job row's pairs into <paramref name="unit"/>.
    /// Nothing here may throw: an exception would unwind into the game's native code.
    /// </summary>
    private void OnUnitData(byte* unit)
    {
        try
        {
            if (_selfTesting)
            {
                ExerciseRegisters();
                return;
            }

            var rules = _rules;
            if (unit == null || (rules.Jobs.Count == 0 && rules.Characters.Count == 0)) return;

            var record = new ReadOnlySpan<byte>(unit, BattleBinding.Stride);
            var roster = _roster != null ? new ReadOnlySpan<byte>(_roster, RosterLayout.TableSize) : default;
            var identity = TunedUnit.Identify(record, roster);

            var pairs = new Span<byte>(unit + TunedUnit.Pairs, StatTuningRules.PairBytes);
            Span<byte> before = stackalloc byte[StatTuningRules.PairBytes];
            pairs.CopyTo(before);
            if (!rules.Apply(pairs, identity.JobRow, identity.PlayerOwned, identity.Fingerprint)) return;

            _changedUnits++;
            if (_configuration.DebugLog) Diagnose(identity, before, pairs);
        }
        catch (Exception e)
        {
            // Set first: if the log itself is what fails, it is not tried again.
            var first = !_reportedFailure;
            _reportedFailure = true;
            try
            {
                if (first) Log($"Unexpected error while tuning a unit ({e.Message}). The unit may hold the game's values or the tuned ones.");
            }
            catch (Exception)
            {
                // Nothing may leave this method.
            }
        }
    }

    /// <summary>
    /// Runs on a game thread right after the game put a job's base Move and Jump into <paramref name="unit"/>,
    /// before it adds equipment and abilities. Nothing here may throw.
    /// </summary>
    private void OnUnitMovement(byte* unit)
    {
        try
        {
            var rules = _rules;
            if (_selfTesting || unit == null || (rules.Jobs.Count == 0 && rules.Characters.Count == 0)) return;

            var record = new ReadOnlySpan<byte>(unit, BattleBinding.Stride);
            var roster = _roster != null ? new ReadOnlySpan<byte>(_roster, RosterLayout.TableSize) : default;
            var identity = TunedUnit.Identify(record, roster);

            Span<byte> before = [unit[TunedUnit.Move], unit[TunedUnit.Jump]];
            if (!rules.ApplyMovement(ref unit[TunedUnit.Move], ref unit[TunedUnit.Jump], identity.JobRow, identity.PlayerOwned, identity.Fingerprint)) return;

            _changedUnits++;
            if (_configuration.DebugLog) Diagnose(identity, before, [unit[TunedUnit.Move], unit[TunedUnit.Jump]]);
        }
        catch (Exception e)
        {
            var first = !_reportedFailure;
            _reportedFailure = true;
            try
            {
                if (first) Log($"Unexpected error while tuning a unit's Move and Jump ({e.Message}). The unit may hold the game's values or the tuned ones.");
            }
            catch (Exception)
            {
                // Nothing may leave this method.
            }
        }
    }

    private void Diagnose(TunedUnit identity, ReadOnlySpan<byte> before, ReadOnlySpan<byte> after)
    {
        var line = $"job 0x{identity.JobRow:X2} ({JobNames.Find(identity.JobRow) ?? "unnamed"}), {(identity.PlayerOwned ? "player" : "other")}, " +
                   $"character {identity.Fingerprint ?? "none"}: {(before.Length == 2 ? "move/jump " : "")}{Convert.ToHexString(before)} -> {Convert.ToHexString(after)}";
        lock (_diagnosed)
        {
            if (_diagnosed.Count >= MaxDiagnosticLines || !_diagnosed.Add(line)) return;
        }
        Log(line);
    }

    /// <summary>On a timer thread: picks up a changed rules file and keeps jobs.json current.</summary>
    private void Poll()
    {
        if (Interlocked.Exchange(ref _polling, 1) != 0) return;
        try
        {
            LoadRules();
            PublishLimitsReady();
            ExportJobs();
        }
        catch (Exception e)
        {
            if (!_reportedFailure) Log($"Unexpected error while reading the rules: {e.Message}");
            _reportedFailure = true;
        }
        finally
        {
            Interlocked.Exchange(ref _polling, 0);
        }
    }

    private void LoadRules()
    {
        try
        {
            var file = new FileInfo(_paths.RulesFile);
            (DateTime, long)? seen = file.Exists ? (file.LastWriteTimeUtc, file.Length) : null;
            if (seen == _loadedFile) return;

            if (seen is null)
            {
                _loadedFile = null;
                if (!_rules.IsEmpty) Log("The rules file is gone: the game's own values are used for units built from now on.");
                _rules = StatTuningRules.Empty;
                return;
            }

            var parsed = StatTuningRules.Parse(File.ReadAllText(_paths.RulesFile));
            _loadedFile = seen;
            if (parsed is null)
            {
                if (!_reportedBadRules) Log($"The rules file cannot be read as rules; the rules loaded before stay in use. File: {_paths.RulesFile}");
                _reportedBadRules = true;
                return;
            }

            _reportedBadRules = false;
            _rules = parsed;
            lock (_diagnosed) _diagnosed.Clear();
            Log($"Rules loaded: {Describe(parsed)}. They apply to units built from now on (the next battle or the next time a menu shows a unit).");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Being written right now; the next look tries again.
        }
    }

    /// <summary>Writes the game's own job values for the Control Center, whenever they differ from what was written.</summary>
    private void ExportJobs()
    {
        if (_jobTable == null) return;
        var content = JobCatalog.Describe(new ReadOnlySpan<byte>(_jobTable, JobTable.TableSize));
        if (content == _exportedJobs) return;
        try
        {
            var temporary = _paths.JobsFile + ".tmp";
            File.WriteAllText(temporary, content);
            File.Move(temporary, _paths.JobsFile, overwrite: true);
            _exportedJobs = content;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The Control Center may be reading it; the next look tries again.
        }
    }

    #region Standard Overrides
    public override void ConfigurationUpdated(Config configuration)
    {
        _configuration = configuration;
        Log($"Settings updated: diagnostic log {(configuration.DebugLog ? "on" : "off")}. Units changed so far: {_changedUnits}.");
    }
    #endregion

    #region For Exports, Serialization etc.
#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
    public Mod() { }
#pragma warning restore CS8618
    #endregion
}
