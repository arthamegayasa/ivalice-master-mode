using Ivalice.Battle;

namespace Ivalice.Battle.Tests;

public class ResultTurboTests
{
    [Fact]
    public void Results_keep_the_battle_speed_when_Improved_Fast_Forward_switches_off_at_event_entry()
    {
        var turbo = new ResultTurbo();

        Assert.Equal(1, turbo.Update(1, 37, 6, follow: true, resultVisible: false));
        Assert.Equal(1, turbo.Update(2, 52, 1, follow: true, resultVisible: false));
        Assert.Equal(6, turbo.Update(2, 52, 1, follow: true, resultVisible: true));
    }

    [Fact]
    public void Explicit_turbo_changes_in_results_take_precedence_over_the_captured_battle_speed()
    {
        var turbo = new ResultTurbo();
        turbo.Update(1, 37, 6, true, false);
        turbo.Update(2, 52, 6, true, true); // Input has not processed the event transition yet.

        Assert.Equal(6, turbo.Update(2, 52, 1, true, true));
        Assert.Equal(4, turbo.Update(2, 52, 4, true, true));
        Assert.Equal(1, turbo.Update(2, 52, 1, true, true));
    }

    [Theory]
    [InlineData(0, 59)] // Returning through menus
    [InlineData(2, 37)] // Unrelated event
    [InlineData(3, 59)] // World map
    public void Exiting_to_another_scene_forgets_the_battle_speed(int context, int state)
    {
        var turbo = new ResultTurbo();
        turbo.Update(1, 37, 10, true, false);

        Assert.Equal(1, turbo.Update(context, state, 1, true, true));
        Assert.Equal(1, turbo.Update(2, 52, 1, true, true));
    }

    [Fact]
    public void Turning_turbo_off_in_battle_or_disabling_follow_keeps_results_at_normal_speed()
    {
        var turbo = new ResultTurbo();
        turbo.Update(1, 25, 6, true, false);
        turbo.Update(1, 37, 1, true, false);
        Assert.Equal(1, turbo.Update(2, 52, 1, true, true));

        turbo.Update(1, 37, 10, true, false);
        Assert.Equal(1, turbo.Update(2, 52, 10, false, true));
        Assert.Equal(1, turbo.Update(2, 52, 1, true, true));
    }
}
