using System.Runtime.CompilerServices;
using ivalice.party.reincarnation;
using Reloaded.Hooks;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.Enums;
using Reloaded.Hooks.Definitions.X64;

namespace Ivalice.Party.Tests;

/// <summary>
/// Installs the mod's hook stub with the same hooking library the game loads, on a throwaway native
/// function, and checks what a caller sees: the registers this game's compiler relies on must survive
/// a call into managed code.
/// </summary>
public class HookStubTests
{
    [Function(CallingConventions.Microsoft)]
    private delegate void Callback();

    private int _calls;
    private double _scratch;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ManagedWork()
    {
        _calls++;
        var text = string.Concat(Environment.TickCount64.ToString(), "/", _calls.ToString());
        _scratch = Math.Sqrt(text.Length * 1.5) + Math.Sin(_calls) * Math.Pow(text.Length, 0.3);
    }

    private LostRegisters ProbeWith(Func<string, string[]> stub)
    {
        var hooks = ReloadedHooks.Instance;
        var quiet = RegisterSelfTest.AllocateQuietFunction();
        Assert.NotEqual(0, quiet);

        var assembly = stub(hooks.Utilities.GetAbsoluteCallMnemonics<Callback>(ManagedWork, out var wrapper));
        var hook = hooks.CreateAsmHook(assembly, quiet, AsmHookBehaviour.ExecuteFirst).Activate();
        try
        {
            return RegisterSelfTest.Probe(quiet);
        }
        finally
        {
            hook.Disable();
            GC.KeepAlive(wrapper);
        }
    }

    [Fact]
    public void The_hook_stub_hands_every_register_back_after_managed_code_ran()
    {
        var lost = ProbeWith(HookStub.Build);

        Assert.Equal(LostRegisters.None, lost);
        Assert.Equal(1, _calls);
    }

    [Fact]
    public void A_stub_that_calls_managed_code_without_saving_registers_is_caught()
    {
        // What a plain function hook amounts to: align the stack, call, return.
        var lost = ProbeWith(call => ["use64", "sub rsp, 0x28", call, "add rsp, 0x28"]);

        Assert.NotEqual(LostRegisters.None, lost);
        Assert.Equal(1, _calls);
    }

    [Fact]
    public void A_stub_that_forgets_one_register_is_caught_for_that_register()
    {
        var lost = ProbeWith(call => [.. HookStub.Build(call), "xor r9, r9"]);

        Assert.Equal(LostRegisters.R9, lost);
    }
}
