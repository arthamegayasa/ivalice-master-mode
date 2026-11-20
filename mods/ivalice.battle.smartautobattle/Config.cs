using System.ComponentModel;
using ivalice.battle.smartautobattle.Template.Configuration;

namespace ivalice.battle.smartautobattle.Configuration;

public class Config : Configurable<Config>
{
    [DisplayName("Active when the game starts")]
    [Description("When on, your units fight on their own from the first battle. Toggle it during play with F8 or by clicking both sticks.")]
    [DefaultValue(true)]
    public bool ActiveAtStart { get; set; } = true;

    [DisplayName("Heal threshold (%)")]
    [Description("A healer switches from fighting to saving an ally whose HP is below this share of its maximum. 10 to 50; the game's own healing logic only reacts below 50.")]
    [DefaultValue(50)]
    public int HealThresholdPercent { get; set; } = 50;

    [DisplayName("White Magicks users are healers")]
    [Description("Units with White Magicks as primary or secondary skillset save fading allies.")]
    [DefaultValue(true)]
    public bool HealWithWhiteMagicks { get; set; } = true;

    [DisplayName("Items users are healers")]
    [Description("Units with Items as primary or secondary skillset save fading allies.")]
    [DefaultValue(true)]
    public bool HealWithItems { get; set; } = true;

    [DisplayName("Martial Arts users are healers")]
    [Description("Units with Martial Arts (Chakra, Revive) as primary or secondary skillset save fading allies.")]
    [DefaultValue(false)]
    public bool HealWithMartialArts { get; set; } = false;

    [DisplayName("Healers revive fallen allies")]
    [Description("A knocked-out ally makes a healer stop fighting. Turn off if your healers carry no Phoenix Down and know no Raise.")]
    [DefaultValue(true)]
    public bool ReviveFallenAllies { get; set; } = true;

    [DisplayName("Toggle with F8")]
    [Description("Press F8 while the game window is focused to switch Smart Auto Battle on or off. F8 always works when the stick toggle is off.")]
    [DefaultValue(true)]
    public bool ToggleWithF8 { get; set; } = true;

    [DisplayName("Toggle with both sticks")]
    [Description("Click the left and right sticks together (L3 + R3) to switch Smart Auto Battle on or off.")]
    [DefaultValue(true)]
    public bool ToggleWithSticks { get; set; } = true;

    [DisplayName("Sound on toggle")]
    [Description("Plays a high beep when switched on and a low beep when switched off.")]
    [DefaultValue(true)]
    public bool SoundOnToggle { get; set; } = true;

    [DisplayName("Diagnostic log")]
    [Description("Writes every order the mod gives to the Reloaded-II log. For troubleshooting only.")]
    [DefaultValue(false)]
    public bool DebugLog { get; set; } = false;
}

/// <summary>
/// Allows you to override certain aspects of the configuration creation process (e.g. create multiple configurations).
/// Override elements in <see cref="ConfiguratorMixinBase"/> for finer control.
/// </summary>
public class ConfiguratorMixin : ConfiguratorMixinBase
{
    //
}
