using System.ComponentModel;
using ivalice.party.stattuning.Template.Configuration;

namespace ivalice.party.stattuning.Configuration;

public class Config : Configurable<Config>
{
    [DisplayName("Diagnostic log")]
    [Description("Writes every kind of unit whose growth or multipliers were changed to the Reloaded-II log. For troubleshooting only.")]
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
