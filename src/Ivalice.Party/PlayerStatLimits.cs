using System.Text.Json.Serialization;

namespace Ivalice.Party;

/// <summary>Opt-in limits on a party member's calculated stats, before equipment.</summary>
public sealed record PlayerStatLimits(bool Hp = false, bool Mp = false, bool Speed = false,
    bool Pa = false, bool Ma = false)
{
    public static PlayerStatLimits Default { get; } = new();

    [JsonIgnore]
    public bool AnyEnabled => Hp || Mp || Speed || Pa || Ma;

    /// <summary>The upper bound to use in the game's existing calculation for a verified party member.</summary>
    public int CapFor(int statIndex, int vanillaCap) => statIndex switch
    {
        0 when Hp => 9999,
        1 when Mp => 9999,
        2 when Speed => 99,
        3 when Pa => 255,
        4 when Ma => 255,
        _ => vanillaCap,
    };

}
