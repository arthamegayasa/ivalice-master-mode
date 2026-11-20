using System.ComponentModel;
using ivalice.party.reincarnation.Template.Configuration;

namespace ivalice.party.reincarnation.Configuration;

public class Config : Configurable<Config>
{
    [DisplayName("Safe mode")]
    [Description("The mod lists the party and checks every command, but never changes the game. A command is answered with what it would have done.")]
    [DefaultValue(false)]
    public bool SafeMode { get; set; } = false;

    [DisplayName("Show unit names")]
    [Description("Asks the game for each unit's name. Turn off if the game misbehaves with this mod; units are then listed by job and slot.")]
    [DefaultValue(true)]
    public bool ResolveNames { get; set; } = true;

    [DisplayName("Minimum level")]
    [Description("The lowest level a character may reincarnate at. 2 to 99; the default is 10.")]
    [DefaultValue(10)]
    public int MinimumLevel { get; set; } = 10;

    [DisplayName("Diagnostic log")]
    [Description("Writes the roster address, every change of the battle check and every command with its outcome to the Reloaded-II log. For troubleshooting only.")]
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
