using ivalice.party.stattuning;

namespace Ivalice.Party.Tests;

public unsafe class PlayerLimitHookTests
{
    [Theory]
    [InlineData(0, 2611, 999, 2611)]
    [InlineData(1, 10000, 999, 9999)]
    [InlineData(2, 167, 50, 99)]
    [InlineData(3, 180, 99, 180)]
    [InlineData(4, 256, 99, 255)]
    public void Native_calculation_keeps_the_games_unclamped_value_up_to_the_selected_limit(
        int stat, int computed, int vanilla, int expected)
    {
        var roster = Fake.Roster(Fake.Unit());
        var unit = new byte[BattleBinding.Stride];
        unit[0] = 0x80;
        unit[2] = 1;
        fixed (byte* p = unit)
        {
            var frame = new LimitRegisters { R10 = (nuint)p, R9 = (uint)stat, Rax = (uint)computed, Rdx = (uint)vanilla };
            var site = stat == 2 ? LimitSite.BaseSpeed : LimitSite.BaseOther;
            StatLimitHooks.Apply(site, ref frame, new(true, true, true, true, true), roster);
            Assert.Equal((nuint)expected, frame.Rdx);
            unit[5] = 0x10;
            frame.Rdx = (uint)vanilla;
            StatLimitHooks.Apply(site, ref frame, new(true, true, true, true, true), roster);
            Assert.Equal((nuint)vanilla, frame.Rdx);
        }
    }
}
