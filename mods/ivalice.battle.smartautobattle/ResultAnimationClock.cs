using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Ivalice.Battle;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.Enums;

namespace ivalice.battle.smartautobattle;

/// <summary>Scales the delta of the active BattleResult layout. Its clips drive the Victory title,
/// trophy reveals and their completion events. Input and reward logic run on their original clocks.</summary>
internal sealed unsafe class ResultAnimationClock
{
    private const string EnhancedHash = "937233F7FE76182A665C487C8802F5CEC6662DDD09967E87CD09FB146FC6B5D5";
    private const int UiManager = 0x3CD9DA8;
    private const int ResultVtable = 0x710588;
    private const int LayoutClock = 0x409A08;
    private readonly nint _gameBase;
    private readonly ResultTurbo _speed = new();
    private readonly IAsmHook _hook;

    [StructLayout(LayoutKind.Sequential)]
    private struct Target { public nint Layout; public float Factor; }
    private readonly Target[] _target = GC.AllocateArray<Target>(1, pinned: true);

    public ResultAnimationClock(IReloadedHooks hooks, nint gameBase)
    {
        using var exe = File.OpenRead(Environment.ProcessPath!);
        if (Convert.ToHexString(SHA256.HashData(exe)) != EnhancedHash)
            throw new NotSupportedException("This Enhanced executable has not been verified.");
        _gameBase = gameBase;
        var site = (byte*)(gameBase + LayoutClock);
        // movaps [rsp+20h],xmm6; movaps xmm6,xmm1; cmp byte ptr [rax+50h],0
        ReadOnlySpan<byte> expected = [0x0F,0x29,0x74,0x24,0x20,0x0F,0x28,0xF1,0x80,0x78,0x50,0x00];
        if (!new ReadOnlySpan<byte>(site, expected.Length).SequenceEqual(expected))
            throw new NotSupportedException("The result animation clock does not match.");

        fixed (Target* target = _target)
        {
            // RCX is the live layout. The original routine copies XMM1 to XMM6 for its clips.
            // All other layouts take just the comparison path, without a managed callback.
            string[] code = ["use64", "pushfq", "push r11", $"mov r11, 0x{(nuint)target:X}",
                "cmp rcx, [r11]", "jne unchanged", "mulss xmm1, dword [r11+8]",
                "unchanged:", "pop r11", "popfq"];
            _hook = hooks.CreateAsmHook(code, (nint)site, new AsmHookOptions {
                Behaviour = AsmHookBehaviour.ExecuteFirst, PreferRelativeJump = true, MaxOpcodeSize = 5 });
            _hook.Activate();
        }
    }

    public void Update(int context, int battleState, int turbo, bool follow)
    {
        byte* layout = null;
        if (follow && ResultTurbo.IsResultEvent(context, battleState))
        {
            // The game's result presenter at 0xEAF58 uses this exact chain on the UI thread.
            var manager = *(byte**)(_gameBase + UiManager);
            var agents = manager != null ? *(byte**)(manager + 0x10) : null;
            var battle = agents != null ? *(byte**)(agents + 0x90) : null;
            var addon = battle != null ? *(byte**)(battle + 0x50) : null;
            if (addon != null && *(nint*)addon == _gameBase + ResultVtable)
                layout = *(byte**)(addon + 0x30);
        }

        bool visible = layout != null && layout[0x1A0] != 0;
        int factor = _speed.Update(context, battleState, turbo, follow, visible);
        _target[0].Factor = factor;
        _target[0].Layout = visible && factor > 1 ? (nint)layout : 0;
    }

    public void Reset()
    {
        _target[0] = default;
        _speed.Update(0, 0, 1, follow: false, resultVisible: false);
    }
}
