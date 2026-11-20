using Ivalice.Battle;

namespace Ivalice.Battle.Tests;

/// <summary>Builds a fake copy of the game's battle unit table.</summary>
internal sealed class Field
{
    public byte[] Table { get; } = new byte[BattleLayout.TableSize];

    public Field()
    {
        for (var slot = 0; slot < BattleLayout.Capacity; slot++)
            Table[slot * BattleLayout.Stride + BattleLayout.InUse] = BattleLayout.EmptySlot;
    }

    private Span<byte> Unit(int slot) => Table.AsSpan(slot * BattleLayout.Stride, BattleLayout.Stride);

    public Field Add(int slot, bool enemy = false, bool playerControlled = true, int hp = 100, int maxHp = 100,
        byte primary = 0, byte secondary = 0, byte mode = 0, byte target = 0, bool ko = false, bool crystal = false,
        bool treasure = false, bool petrified = false)
    {
        var unit = Unit(slot);
        unit[BattleLayout.InUse] = (byte)slot;
        var flags = (byte)((enemy ? 0x10 : 0) | (playerControlled && !enemy ? BattleLayout.ControlBit : 0));
        unit[BattleLayout.CurrentFlags] = flags;
        unit[BattleLayout.PermanentFlags] = flags;
        BitConverter.TryWriteBytes(unit[BattleLayout.Hp..], (ushort)hp);
        BitConverter.TryWriteBytes(unit[BattleLayout.MaxHp..], (ushort)maxHp);
        unit[BattleLayout.PrimarySkillset] = primary;
        unit[BattleLayout.SecondarySkillset] = secondary;
        unit[BattleLayout.AutoMode] = mode;
        unit[BattleLayout.AutoTarget] = target;
        if (petrified) unit[BattleLayout.Statuses + 1] |= BattleLayout.PetrifyBit;
        if (ko) unit[BattleLayout.Statuses] |= BattleLayout.KoBit;
        if (crystal) unit[BattleLayout.Statuses] |= BattleLayout.CrystalBit;
        if (treasure) unit[BattleLayout.Statuses + 1] |= BattleLayout.TreasureBit;
        return this;
    }

    public AutoOrder? Plan(int slot, PlannerSettings? settings = null) =>
        AutoBattlePlanner.Plan(Table, slot, settings ?? Defaults);

    public static readonly PlannerSettings Defaults = new(HealThresholdPercent: 50, HealerSkillsets: new HashSet<byte> { Skillsets.WhiteMagicks, Skillsets.Items });
}

public class AutoBattlePlannerTests
{
    private const byte Knightly = 7;

    [Fact]
    public void A_fighter_is_told_to_fight_a_living_enemy()
    {
        var field = new Field().Add(0, primary: Knightly).Add(5, enemy: true, ko: true).Add(6, enemy: true);

        Assert.Equal(new AutoOrder(AutoMode.Fight, 6), field.Plan(0));
    }

    [Fact]
    public void A_healer_saves_an_ally_below_the_threshold()
    {
        var field = new Field()
            .Add(0, primary: Skillsets.WhiteMagicks)
            .Add(1, hp: 49, maxHp: 100)
            .Add(6, enemy: true);

        Assert.Equal(new AutoOrder(AutoMode.SaveFadingLife, 1), field.Plan(0));
    }

    [Fact]
    public void A_healer_fights_when_every_ally_is_at_or_above_the_threshold()
    {
        var field = new Field()
            .Add(0, primary: Skillsets.WhiteMagicks)
            .Add(1, hp: 50, maxHp: 100)
            .Add(6, enemy: true);

        Assert.Equal(new AutoOrder(AutoMode.Fight, 6), field.Plan(0));
    }

    [Fact]
    public void A_secondary_healing_skillset_also_makes_a_healer()
    {
        var field = new Field()
            .Add(0, primary: Knightly, secondary: Skillsets.Items)
            .Add(1, hp: 10)
            .Add(6, enemy: true);

        Assert.Equal(AutoMode.SaveFadingLife, field.Plan(0)!.Value.Mode);
    }

    [Fact]
    public void A_fighter_keeps_fighting_even_when_an_ally_is_dying()
    {
        var field = new Field().Add(0, primary: Knightly).Add(1, hp: 1).Add(6, enemy: true);

        Assert.Equal(new AutoOrder(AutoMode.Fight, 6), field.Plan(0));
    }

    [Fact]
    public void A_knocked_out_ally_is_saved_before_a_wounded_one()
    {
        var field = new Field()
            .Add(0, primary: Skillsets.WhiteMagicks)
            .Add(1, hp: 5)
            .Add(2, hp: 0, ko: true)
            .Add(6, enemy: true);

        Assert.Equal(new AutoOrder(AutoMode.SaveFadingLife, 2), field.Plan(0));
    }

    [Fact]
    public void Among_wounded_allies_the_one_with_the_lowest_share_of_hp_is_saved()
    {
        var field = new Field()
            .Add(0, primary: Skillsets.WhiteMagicks)
            .Add(1, hp: 40, maxHp: 100)
            .Add(2, hp: 60, maxHp: 400)
            .Add(6, enemy: true);

        Assert.Equal(new AutoOrder(AutoMode.SaveFadingLife, 2), field.Plan(0));
    }

    [Fact]
    public void Guests_on_the_player_team_count_as_allies_to_save()
    {
        var field = new Field()
            .Add(0, primary: Skillsets.WhiteMagicks)
            .Add(3, playerControlled: false, hp: 10)
            .Add(6, enemy: true);

        Assert.Equal(new AutoOrder(AutoMode.SaveFadingLife, 3), field.Plan(0));
    }

    [Fact]
    public void Allies_that_turned_to_crystal_or_treasure_and_wounded_enemies_are_not_saved()
    {
        var field = new Field()
            .Add(0, primary: Skillsets.WhiteMagicks)
            .Add(1, hp: 0, ko: true, crystal: true)
            .Add(2, hp: 0, ko: true, treasure: true)
            .Add(6, enemy: true, hp: 1);

        Assert.Equal(new AutoOrder(AutoMode.Fight, 6), field.Plan(0));
    }

    [Fact]
    public void The_healer_does_not_pick_itself()
    {
        var field = new Field().Add(0, primary: Skillsets.WhiteMagicks, hp: 5).Add(6, enemy: true);

        Assert.Equal(new AutoOrder(AutoMode.Fight, 6), field.Plan(0));
    }

    [Fact]
    public void A_lower_threshold_makes_healers_wait_longer()
    {
        var field = new Field().Add(0, primary: Skillsets.WhiteMagicks).Add(1, hp: 30).Add(6, enemy: true);
        var patient = Field.Defaults with { HealThresholdPercent = 25 };

        Assert.Equal(AutoMode.Fight, field.Plan(0, patient)!.Value.Mode);
        Assert.Equal(AutoMode.SaveFadingLife, field.Plan(0)!.Value.Mode);
    }

    [Theory]
    [InlineData(AutoMode.ProtectAllies)]
    [InlineData(AutoMode.RunLikeARabbit)]
    [InlineData((byte)0x0A)]
    public void A_mode_this_mod_never_hands_out_is_left_alone(byte chosen)
    {
        var field = new Field().Add(0, mode: chosen).Add(6, enemy: true);

        Assert.Null(field.Plan(0));
    }

    [Fact]
    public void A_fighter_keeps_the_enemy_it_is_already_aimed_at()
    {
        var field = new Field()
            .Add(0, mode: AutoMode.Fight, target: 7)
            .Add(6, enemy: true)
            .Add(7, enemy: true);

        Assert.Equal(new AutoOrder(AutoMode.Fight, 7), field.Plan(0));
    }

    [Theory]
    [InlineData(7)]   // knocked out
    [InlineData(1)]   // an ally
    [InlineData(9)]   // empty slot
    [InlineData(200)] // not a slot at all
    public void A_fighter_aimed_at_something_that_is_not_a_living_enemy_is_aimed_again(byte staleTarget)
    {
        var field = new Field()
            .Add(0, mode: AutoMode.Fight, target: staleTarget)
            .Add(1)
            .Add(6, enemy: true)
            .Add(7, enemy: true, ko: true);

        Assert.Equal(new AutoOrder(AutoMode.Fight, 6), field.Plan(0));
    }

    [Fact]
    public void A_healer_goes_back_to_fighting_once_nobody_needs_saving()
    {
        var field = new Field()
            .Add(0, primary: Skillsets.WhiteMagicks, mode: AutoMode.SaveFadingLife, target: 1)
            .Add(1, hp: 90)
            .Add(6, enemy: true);

        Assert.Equal(new AutoOrder(AutoMode.Fight, 6), field.Plan(0));
    }

    [Fact]
    public void Petrified_enemies_are_not_picked_while_another_enemy_stands()
    {
        var field = new Field().Add(0).Add(5, enemy: true, petrified: true).Add(6, enemy: true);

        Assert.Equal(new AutoOrder(AutoMode.Fight, 6), field.Plan(0));
    }

    [Fact]
    public void Healers_can_be_told_not_to_chase_knocked_out_allies()
    {
        var field = new Field()
            .Add(0, primary: Skillsets.Items)
            .Add(1, hp: 0, ko: true)
            .Add(2, hp: 20)
            .Add(6, enemy: true);
        var noRevive = Field.Defaults with { ReviveFallenAllies = false };

        Assert.Equal(new AutoOrder(AutoMode.SaveFadingLife, 2), field.Plan(0, noRevive));
        Assert.Equal(new AutoOrder(AutoMode.SaveFadingLife, 1), field.Plan(0));
    }

    [Fact]
    public void A_threshold_above_half_is_treated_as_half_when_planning()
    {
        var field = new Field().Add(0, primary: Skillsets.WhiteMagicks).Add(1, hp: 60).Add(6, enemy: true);

        Assert.Equal(AutoMode.Fight, field.Plan(0, Field.Defaults with { HealThresholdPercent = 90 })!.Value.Mode);
    }

    [Fact]
    public void An_ally_with_no_maximum_hp_recorded_is_not_treated_as_wounded()
    {
        var field = new Field().Add(0, primary: Skillsets.WhiteMagicks).Add(1, hp: 0, maxHp: 0).Add(6, enemy: true);

        Assert.Equal(AutoMode.Fight, field.Plan(0)!.Value.Mode);
    }

    [Fact]
    public void A_healer_with_someone_to_save_gets_an_order_even_when_no_enemy_stands()
    {
        var field = new Field().Add(0, primary: Skillsets.WhiteMagicks).Add(1, hp: 5).Add(6, enemy: true, ko: true);

        Assert.Equal(new AutoOrder(AutoMode.SaveFadingLife, 1), field.Plan(0));
    }

    [Theory]
    [InlineData(AutoMode.Fight, true)]
    [InlineData(AutoMode.SaveFadingLife, true)]
    [InlineData(AutoMode.None, false)]
    [InlineData(AutoMode.ProtectAllies, false)]
    [InlineData(AutoMode.RunLikeARabbit, false)]
    public void Only_the_two_modes_this_mod_hands_out_are_taken_back(byte mode, bool takenBack)
    {
        Assert.Equal(takenBack, AutoBattlePlanner.IsHandedOut(mode));
    }

    [Fact]
    public void An_order_this_mod_wrote_earlier_is_re_evaluated()
    {
        var field = new Field()
            .Add(0, primary: Skillsets.WhiteMagicks, mode: AutoMode.Fight)
            .Add(1, hp: 10)
            .Add(6, enemy: true);

        Assert.Equal(new AutoOrder(AutoMode.SaveFadingLife, 1), field.Plan(0));
    }

    [Fact]
    public void Units_the_player_does_not_control_are_left_alone()
    {
        var field = new Field()
            .Add(0, enemy: true)
            .Add(1, playerControlled: false)
            .Add(2);

        Assert.Null(field.Plan(0));
        Assert.Null(field.Plan(1));
    }

    [Fact]
    public void A_unit_that_temporarily_lost_control_is_still_planned_from_its_permanent_flags()
    {
        // Confusion, Berserk and similar statuses clear the control bit in the current flags only.
        var field = new Field().Add(0).Add(6, enemy: true);
        field.Table[BattleLayout.CurrentFlags] = 0;

        Assert.Equal(AutoMode.Fight, field.Plan(0)!.Value.Mode);
    }

    [Fact]
    public void Empty_slots_knocked_out_units_and_bad_slot_numbers_are_left_alone()
    {
        var field = new Field().Add(0, ko: true).Add(6, enemy: true);

        Assert.Null(field.Plan(0));
        Assert.Null(field.Plan(1));
        Assert.Null(field.Plan(-1));
        Assert.Null(field.Plan(BattleLayout.Capacity));
    }

    [Fact]
    public void With_no_living_enemy_left_a_fighter_gets_no_order()
    {
        var field = new Field().Add(0).Add(6, enemy: true, ko: true).Add(7, enemy: true, crystal: true);

        Assert.Null(field.Plan(0));
    }

    [Fact]
    public void A_table_of_the_wrong_size_is_rejected_rather_than_read()
    {
        Assert.Null(AutoBattlePlanner.Plan(new byte[100], 0, Field.Defaults));
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(80, 50)]
    [InlineData(35, 35)]
    public void The_threshold_is_kept_within_what_the_game_itself_acts_on(int asked, int effective)
    {
        Assert.Equal(effective, (Field.Defaults with { HealThresholdPercent = asked }).EffectiveThreshold);
    }
}
