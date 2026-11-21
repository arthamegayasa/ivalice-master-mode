using System.Runtime.InteropServices;
using Ivalice.Party;

namespace ivalice.party.stattuning;

internal enum LimitSite { BaseOther, BaseSpeed, ArmorHp, ArmorMp, EquippedStats, Menu, Battle, BattlePreview }

/// <summary>The ten saved registers at the stack address passed to the limit callback.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct LimitRegisters
{
    public nuint Rax, Rcx, Rdx, R8, R9, R10, R11, Rbx, Rsi, Rdi;
}

internal sealed record LimitHookSite(LimitSite Kind, int Rva, string ExpectedBytes, int HookLength, bool LeafStack = false);

/// <summary>Per-unit limits in the Enhanced v1.5.2 calculation, equipment and status routines.</summary>
internal static unsafe class StatLimitHooks
{
    public const string GameHash = "937233F7FE76182A665C487C8802F5CEC6662DDD09967E87CD09FB146FC6B5D5";

    // Byte checks cover whole instructions and their context. No game file is modified.
    public static readonly LimitHookSite[] Sites =
    [
        new(LimitSite.BaseOther, 0x27701E, "418BC94585C9743983E901742383", 6, true),
        new(LimitSite.BaseSpeed, 0x276FF5, "85ED0F858A0000004D85DB", 8, true),
        new(LimitSite.ArmorHp, 0x277B9D, "3BD70F47D766895332", 5),
        new(LimitSite.ArmorMp, 0x277BB5, "3BCF0F47CF66894B364883C602", 5),
        new(LimitSite.EquippedStats, 0x277C38, "488B5C2440488B6C2448488B742450488B7C2458", 5),
        new(LimitSite.Menu, 0x284AC4, "6689431638564176050FB7C2EB04", 7),
        new(LimitSite.Battle, 0x36017F, "668943164038AFB80100007506410FB7C7", 11),
        new(LimitSite.BattlePreview, 0x396327, "668947164038ABB80100007506410FB7C6", 11),
    ];

    public static string[] Stub(LimitHookSite site, string call)
    {
        // Flags and ten registers take 88 bytes. The stat calculation is a leaf with six pushes
        // (RSP mod 16 = 8); all other sites have aligned stacks. Keep 32 bytes of shadow space.
        var frame = site.LeafStack ? "0x80" : "0x88";
        return
        [
            "use64", "pushfq", "push rdi", "push rsi", "push rbx", "push r11", "push r10", "push r9",
            "push r8", "push rdx", "push rcx", "push rax", $"sub rsp, {frame}",
            "movdqu [rsp+0x20], xmm0", "movdqu [rsp+0x30], xmm1", "movdqu [rsp+0x40], xmm2",
            "movdqu [rsp+0x50], xmm3", "movdqu [rsp+0x60], xmm4", "movdqu [rsp+0x70], xmm5",
            $"lea rcx, [rsp+{frame}]", $"mov edx, {(int)site.Kind}", call,
            "movdqu xmm0, [rsp+0x20]", "movdqu xmm1, [rsp+0x30]", "movdqu xmm2, [rsp+0x40]",
            "movdqu xmm3, [rsp+0x50]", "movdqu xmm4, [rsp+0x60]", "movdqu xmm5, [rsp+0x70]",
            $"add rsp, {frame}", "pop rax", "pop rcx", "pop rdx", "pop r8", "pop r9", "pop r10", "pop r11",
            "pop rbx", "pop rsi", "pop rdi", "popfq",
        ];
    }

    public static void Apply(LimitSite site, ref LimitRegisters frame, PlayerStatLimits limits, ReadOnlySpan<byte> roster)
    {
        // The two armor clamps share EDI. The game sets it to 999 for each piece of armor:
        // restore that value for MP even when HP alone was extended (or rules changed in between).
        if (site == LimitSite.ArmorMp && frame.Rdi == 9999) frame.Rdi = 999;
        if (!limits.AnyEnabled || roster.Length != RosterLayout.TableSize) return;
        var unit = (byte*)(site switch
        {
            LimitSite.BaseOther or LimitSite.BaseSpeed => frame.R10,
            LimitSite.ArmorHp or LimitSite.ArmorMp or LimitSite.BattlePreview => frame.Rbx,
            LimitSite.EquippedStats => frame.Rbx - 0x41, // end of the game's three-step equipment loop
            LimitSite.Menu => frame.Rsi,
            LimitSite.Battle => frame.Rdi,
            _ => 0u,
        });
        if (unit == null || !TunedUnit.IsPartyMember(new ReadOnlySpan<byte>(unit, BattleBinding.Stride), roster)) return;

        switch (site)
        {
            case LimitSite.BaseOther:
            case LimitSite.BaseSpeed:
                // EAX still holds the game's complete result before its clamp, including its
                // unit-specific divisor and 1-point floor. EDX is the clipped value about to be used.
                var cap = limits.CapFor((int)frame.R9, 0);
                if (cap > 0) frame.Rdx = Math.Min((uint)frame.Rax, (uint)cap);
                break;
            case LimitSite.ArmorHp:
                frame.Rdi = (uint)limits.CapFor(0, (int)frame.Rdi);
                break;
            case LimitSite.ArmorMp:
                frame.Rdi = (uint)limits.CapFor(1, (int)frame.Rdi);
                break;
            case LimitSite.EquippedStats:
                // HP traits are applied after the base clamp. Finalize the word-valued stats
                // even if no armor supplied a second clamp, retaining current HP/MP and KO.
                for (int stat = 0; stat < 2; stat++)
                {
                    var resourceCap = limits.CapFor(stat, 0);
                    if (resourceCap == 0) continue;
                    var resource = (ushort*)(unit + 0x30 + stat * 4);
                    resource[1] = (ushort)Math.Min((int)resource[1], resourceCap);
                    resource[0] = Math.Min(resource[0], resource[1]);
                }
                // Original loop sums base and gear bytes into a word, then applies the vanilla cap.
                // Repeat that final sum only for stats whose cap changed. This preserves bonuses
                // and prevents a 255 + equipment wraparound.
                for (int offset = 0; offset < 3; offset++)
                {
                    var equippedCap = limits.CapFor(offset == 2 ? 2 : 3 + offset, 0);
                    if (equippedCap > 0) unit[0x3E + offset] = (byte)Math.Min(unit[0x38 + offset] + unit[0x3B + offset], equippedCap);
                }
                break;
            default:
                var view = (byte*)(site == LimitSite.BattlePreview ? frame.Rdi : frame.Rbx);
                // Menu previews intentionally use the maximum in both columns. In battle, current
                // HP/MP can be smaller, including zero. The next instruction stores AX to +0x16.
                if (limits.Hp)
                {
                    *(ushort*)(view + 0x0C) = *(ushort*)(unit + (site == LimitSite.Menu ? 0x32 : 0x30));
                    *(ushort*)(view + 0x10) = *(ushort*)(unit + 0x32);
                }
                if (limits.Mp)
                {
                    *(ushort*)(view + 0x12) = *(ushort*)(unit + (site == LimitSite.Menu ? 0x36 : 0x34));
                    frame.Rax = *(ushort*)(unit + 0x36);
                }
                break;
        }
    }
}
