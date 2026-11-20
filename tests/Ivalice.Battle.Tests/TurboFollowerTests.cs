using Ivalice.Battle;

namespace Ivalice.Battle.Tests;

public class TurboFollowerTests
{
    private const int Battle = 1;
    private const int ConfirmAction = 25; // a state the turbo mod treats as "the player is choosing"
    private const int ActionPlaying = 41; // a state the turbo mod always accelerates

    private static readonly ActingUnit Enemy = new(PlayerOwned: false, AiDriven: true);
    private static readonly ActingUnit Manual = new(PlayerOwned: true, AiDriven: false);
    private static readonly ActingUnit OnAuto = new(PlayerOwned: true, AiDriven: true);

    /// <summary>A follower that has just watched an enemy act at the given turbo speed.</summary>
    private static TurboFollower After(int turboSpeed)
    {
        var follower = new TurboFollower();
        follower.Adjust(turboSpeed, Battle, ActionPlaying, Enemy);
        return follower;
    }

    [Fact]
    public void A_unit_on_auto_gets_the_turbo_speed_in_a_state_the_turbo_mod_holds_back()
    {
        Assert.Equal(6, After(turboSpeed: 6).Adjust(1, Battle, ConfirmAction, OnAuto));
    }

    [Fact]
    public void A_unit_the_player_is_really_controlling_keeps_normal_speed()
    {
        Assert.Equal(1, After(turboSpeed: 6).Adjust(1, Battle, ConfirmAction, Manual));
    }

    [Fact]
    public void Nothing_is_added_while_turbo_is_off()
    {
        Assert.Equal(1, After(turboSpeed: 1).Adjust(1, Battle, ConfirmAction, OnAuto));
    }

    [Fact]
    public void Switching_turbo_off_is_noticed_on_the_next_state_the_turbo_mod_accelerates()
    {
        var follower = After(turboSpeed: 6);

        follower.Adjust(1, Battle, ActionPlaying, OnAuto); // turbo mod now reports normal speed

        Assert.Equal(1, follower.Adjust(1, Battle, ConfirmAction, OnAuto));
    }

    [Fact]
    public void Speed_the_turbo_mod_already_applied_is_passed_through_unchanged()
    {
        var follower = After(turboSpeed: 6);

        Assert.Equal(6, follower.Adjust(6, Battle, ActionPlaying, OnAuto));
        Assert.Equal(6, follower.Adjust(6, Battle, ConfirmAction, Enemy));
    }

    [Fact]
    public void A_unit_on_auto_is_also_sped_up_in_states_the_game_marks_as_outside_battle_flow()
    {
        // The game tags some battle states with context 0; the turbo mod leaves those at normal speed for player units.
        Assert.Equal(6, After(turboSpeed: 6).Adjust(1, context: 0, ConfirmAction, OnAuto));
    }

    [Theory]
    [InlineData(2)] // event scene
    [InlineData(3)] // world map
    public void Outside_battle_the_speed_is_left_alone_and_the_remembered_turbo_is_forgotten(int context)
    {
        var follower = After(turboSpeed: 6);

        Assert.Equal(1, follower.Adjust(1, context, ConfirmAction, OnAuto));
        Assert.Equal(1, follower.Adjust(1, Battle, ConfirmAction, OnAuto));
    }

    [Fact]
    public void With_no_acting_unit_the_speed_is_left_alone()
    {
        Assert.Equal(1, After(turboSpeed: 6).Adjust(1, Battle, ConfirmAction, acting: null));
    }

    [Fact]
    public void A_player_choosing_orders_does_not_reset_the_remembered_turbo_speed()
    {
        var follower = After(turboSpeed: 6);

        follower.Adjust(1, Battle, ConfirmAction, Manual); // held at 1x on purpose, says nothing about turbo

        Assert.Equal(6, follower.Adjust(1, Battle, ConfirmAction, OnAuto));
    }

    [Fact]
    public void Nonsense_iteration_counts_are_passed_through()
    {
        Assert.Equal(0, After(turboSpeed: 6).Adjust(0, Battle, ConfirmAction, OnAuto));
    }
}
