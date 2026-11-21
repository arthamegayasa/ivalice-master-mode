using ivalice.party.stattuning;
using Reloaded.Hooks;
using Reloaded.Hooks.Definitions.Enums;
using Reloaded.Hooks.Definitions.X64;
using Probe = ivalice.party.reincarnation.RegisterSelfTest;
using ProbeResult = ivalice.party.reincarnation.LostRegisters;

namespace Ivalice.Party.Tests;

public unsafe class PlayerLimitHookViewTests
{
    [Function(CallingConventions.Microsoft)]
    private delegate void Callback(LimitRegisters* frame, LimitSite site);

    [Fact]
    public void Frame_passes_the_original_registers_and_site_and_restores_registers_on_return()
    {
        var target = Probe.AllocateQuietFunction();
        Assert.NotEqual(0, target);
        var hooks = ReloadedHooks.Instance;
        nuint incoming = 0;
        LimitSite? kind = null;
        Callback inspect = (frame, site) => { incoming = frame->Rcx; kind = site; };
        var call = hooks.Utilities.GetAbsoluteCallMnemonics(inspect, out var wrapper);
        var site = StatLimitHooks.Sites.Single(s => s.Kind == LimitSite.BaseSpeed);
        var hook = hooks.CreateAsmHook(StatLimitHooks.Stub(site, call), target, AsmHookBehaviour.ExecuteFirst).Activate();
        try
        {
            Assert.Equal(ProbeResult.None, Probe.Probe(target));
            Assert.Equal(site.Kind, kind);
            Assert.Equal(0x1111111111111111ul, (ulong)incoming);
        }
        finally
        {
            hook.Disable();
            GC.KeepAlive(wrapper);
        }
    }

    [Fact]
    public void Only_the_selected_armor_stat_and_equipment_totals_get_a_new_ceiling()
    {
        var roster = Fake.Roster(Fake.Unit());
        var unit = new byte[BattleBinding.Stride];
        unit[0] = 0x80;
        unit[2] = 1;
        unit[0x38] = 253;
        unit[0x39] = 22;
        unit[0x3A] = 97;
        unit[0x3B] = 4;
        unit[0x3D] = 1;
        unit[0x3F] = 22;
        var limits = new PlayerStatLimits(Hp: true, Pa: true, Speed: true);
        fixed (byte* p = unit)
        {
            var frame = new LimitRegisters { Rbx = (nuint)p, Rdi = 999 };
            StatLimitHooks.Apply(LimitSite.ArmorHp, ref frame, limits, roster);
            Assert.Equal((nuint)9999, frame.Rdi);
            StatLimitHooks.Apply(LimitSite.ArmorMp, ref frame, limits, roster);
            Assert.Equal((nuint)999, frame.Rdi);
            frame.Rbx += 0x41;
            StatLimitHooks.Apply(LimitSite.EquippedStats, ref frame, limits, roster);
            Assert.Equal(new byte[] { 255, 22, 98 }, unit[0x3E..0x41]);
        }
    }

    [Theory]
    [InlineData(true, false, 10040, 11000, 9999, 9999, 200, 10800)]
    [InlineData(true, true, 0, 11000, 0, 9999, 200, 9999)]
    [InlineData(false, true, 0, 11000, 0, 11000, 200, 9999)]
    public void Final_maxima_include_traits_and_preserve_injuries_and_KO(
        bool hp, bool mp, int currentHp, int maxHp, int expectedHp, int expectedMaxHp, int expectedMp, int expectedMaxMp)
    {
        var roster = Fake.Roster(Fake.Unit());
        var unit = new byte[BattleBinding.Stride];
        unit[0] = 0x80;
        unit[2] = 1;
        fixed (byte* p = unit)
        {
            *(ushort*)(p + 0x30) = (ushort)currentHp;
            *(ushort*)(p + 0x32) = (ushort)maxHp;
            *(ushort*)(p + 0x34) = 200;
            *(ushort*)(p + 0x36) = 10800;
            var frame = new LimitRegisters { Rbx = (nuint)(p + 0x41) };
            StatLimitHooks.Apply(LimitSite.EquippedStats, ref frame, new(Hp: hp, Mp: mp), roster);
            Assert.Equal(expectedHp, *(ushort*)(p + 0x30));
            Assert.Equal(expectedMaxHp, *(ushort*)(p + 0x32));
            Assert.Equal(expectedMp, *(ushort*)(p + 0x34));
            Assert.Equal(expectedMaxMp, *(ushort*)(p + 0x36));
        }
    }

    [Fact]
    public void Armor_clamp_for_mp_only_keeps_hp_and_guest_caps_at_999()
    {
        var roster = Fake.Roster(Fake.Unit(), Fake.Unit(slot: RosterLayout.PartySlots));
        var unit = new byte[BattleBinding.Stride];
        unit[0] = 0x80;
        unit[2] = 1;
        fixed (byte* p = unit)
        {
            var frame = new LimitRegisters { Rbx = (nuint)p, Rdi = 999 };
            var limits = new PlayerStatLimits(Mp: true);
            StatLimitHooks.Apply(LimitSite.ArmorHp, ref frame, limits, roster);
            Assert.Equal((nuint)999, frame.Rdi);
            StatLimitHooks.Apply(LimitSite.ArmorMp, ref frame, limits, roster);
            Assert.Equal((nuint)9999, frame.Rdi);
            unit[2] = RosterLayout.PartySlots;
            frame.Rdi = 999;
            StatLimitHooks.Apply(LimitSite.ArmorMp, ref frame, limits, roster);
            Assert.Equal((nuint)999, frame.Rdi);
        }
    }

    [Theory]
    [InlineData("Menu", 2000, 3)]
    [InlineData("Battle", 1111, 2)]
    [InlineData("BattlePreview", 1111, 2)]
    public void Status_views_preserve_maximums_and_the_games_choice_of_current_values(string kind, int hp, int mp)
    {
        var roster = Fake.Roster(Fake.Unit());
        var unit = new byte[BattleBinding.Stride];
        var view = new byte[0x100];
        unit[0] = 0x80;
        unit[2] = 1;
        fixed (byte* p = unit, v = view)
        {
            *(ushort*)(p + 0x30) = 1111;
            *(ushort*)(p + 0x32) = 2000;
            *(ushort*)(p + 0x34) = 2;
            *(ushort*)(p + 0x36) = 3;
            var site = Enum.Parse<LimitSite>(kind);
            var frame = new LimitRegisters { Rsi = (nuint)p, Rdi = (nuint)p, Rbx = (nuint)v };
            if (site == LimitSite.BattlePreview) (frame.Rdi, frame.Rbx) = (frame.Rbx, frame.Rdi);
            StatLimitHooks.Apply(site, ref frame, new(Hp: true, Mp: true), roster);
            Assert.Equal(hp, *(ushort*)(v + 0xC));
            Assert.Equal(2000, *(ushort*)(v + 0x10));
            Assert.Equal(mp, *(ushort*)(v + 0x12));
            Assert.Equal((nuint)3, frame.Rax);
        }
    }
}
