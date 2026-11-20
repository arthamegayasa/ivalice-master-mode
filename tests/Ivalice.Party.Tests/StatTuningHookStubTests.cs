using System.Globalization;
using System.Runtime.CompilerServices;
using ivalice.party.reincarnation;
using Reloaded.Hooks;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.Enums;
using Reloaded.Hooks.Definitions.X64;
using StatTuningStub = ivalice.party.stattuning.HookStub;

namespace Ivalice.Party.Tests;

/// <summary>
/// The Stat Tuning mod hooks the middle of two game functions and hands the unit record (rbx) to
/// managed code. Its stub must give back every register this game's compiler relies on, and must keep
/// the stack aligned in both places it can be used.
/// </summary>
public class StatTuningHookStubTests
{
    [Function(CallingConventions.Microsoft)]
    private delegate void Callback(nint unit);

    private int _calls;
    private double _scratch;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ManagedWork(nint unit)
    {
        _calls++;
        var text = string.Concat(Environment.TickCount64.ToString(), "/", unit.ToString());
        _scratch = Math.Sqrt(text.Length * 1.5) + Math.Sin(_calls) * Math.Pow(text.Length, 0.3);
    }

    [Fact]
    public void The_stub_hands_every_register_back_after_managed_code_ran()
    {
        var hooks = ReloadedHooks.Instance;
        var quiet = RegisterSelfTest.AllocateQuietFunction();
        Assert.NotEqual(0, quiet);

        // The throwaway function is hooked at its entry, as the mod's own self-test does.
        var assembly = StatTuningStub.Build(hooks.Utilities.GetAbsoluteCallMnemonics<Callback>(ManagedWork, out var wrapper), atFunctionEntry: true);
        var hook = hooks.CreateAsmHook(assembly, quiet, AsmHookBehaviour.ExecuteFirst).Activate();
        try
        {
            Assert.Equal(LostRegisters.None, RegisterSelfTest.Probe(quiet));
            Assert.Equal(1, _calls);
        }
        finally
        {
            hook.Disable();
            GC.KeepAlive(wrapper);
        }
    }

    [Theory]
    [InlineData(true, 8)]  // at a function's entry the return address leaves the stack 8 bytes off
    [InlineData(false, 0)] // inside a function body the stack is aligned
    public void The_stack_is_16_byte_aligned_when_managed_code_is_called(bool atFunctionEntry, int offsetOnArrival)
    {
        var lines = StatTuningStub.Build("call managed", atFunctionEntry);
        var call = Array.IndexOf(lines, "call managed");
        var pushed = lines.Take(call).Count(l => l.StartsWith("push ")) * 8;
        var reserved = lines.Take(call).Where(l => l.StartsWith("sub rsp, "))
            .Sum(l => int.Parse(l["sub rsp, 0x".Length..], NumberStyles.HexNumber));

        Assert.Equal(0, (offsetOnArrival + pushed + reserved) % 16);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Everything_saved_is_restored_in_reverse_and_the_unit_is_passed_in_rcx(bool atFunctionEntry)
    {
        var lines = StatTuningStub.Build("call managed", atFunctionEntry);
        var call = Array.IndexOf(lines, "call managed");

        Assert.Equal("mov rcx, rbx", lines[call - 1]);
        var pushes = lines.Where(l => l.StartsWith("push ")).Select(l => l[5..]).ToArray();
        var pops = lines.Where(l => l.StartsWith("pop ")).Select(l => l[4..]).ToArray();
        Assert.Equal(new[] { "rax", "rcx", "rdx", "r8", "r9", "r10", "r11" }, pushes);
        Assert.Equal(pushes.Reverse(), pops);
        Assert.Equal(lines.Single(l => l.StartsWith("sub rsp, "))["sub rsp, ".Length..], lines.Single(l => l.StartsWith("add rsp, "))["add rsp, ".Length..]);
    }
}
