namespace Ivalice.Battle;

/// <summary>The unit whose turn it is, as far as speed is concerned.</summary>
/// <param name="PlayerOwned">The unit belongs to the player (its own control flag is set).</param>
/// <param name="AiDriven">Right now the game's AI gives it orders: auto-battle, or a status such as Berserk.</param>
public readonly record struct ActingUnit(bool PlayerOwned, bool AiDriven);

/// <summary>
/// Works out how many logic ticks the game should run this frame so that a player's unit on auto-battle
/// is fast-forwarded like an enemy.
///
/// The Improved Fast Forward mod multiplies ticks per frame, but holds a list of battle states at normal
/// speed whenever the acting unit belongs to the player, so the player can read and choose. It judges
/// ownership from the unit's permanent flag, so a unit on auto-battle is held back as well: about three
/// seconds before each attack. This class restores the turbo speed in exactly that case.
/// </summary>
public sealed class TurboFollower
{
    private const int MenuContext = 0;
    private const int BattleContext = 1;

    /// <summary>Battle states the turbo mod holds at normal speed for a player-owned unit.</summary>
    private static readonly HashSet<int> HeldForPlayers =
    [
        0, 4, 10, 13, 14, 15, 19, 23, 25, 32, 36, 50, 60, 61, 82, 87, 90, 91, 97, 98, 99, 100, 101, 102, 103, 104, 105,
    ];

    /// <summary>The tick count last seen in a moment the turbo mod does not hold back; 1 means turbo is off.</summary>
    private int _turboSpeed = 1;

    /// <param name="iterations">Ticks per frame the game is about to run, after the turbo mod had its say.</param>
    /// <param name="context">The game's speed context: 0 menus, 1 battle, 2 event scene, 3 world map.</param>
    /// <param name="battleState">The battle state machine's current state.</param>
    /// <param name="acting">The unit whose turn it is, or null when there is none.</param>
    /// <param name="follow">When false nothing is changed, but turbo is still tracked so no stale speed is kept.</param>
    /// <returns>Ticks per frame to run instead.</returns>
    public int Adjust(int iterations, int context, int battleState, ActingUnit? acting, bool follow = true)
    {
        if (context is not (MenuContext or BattleContext))
        {
            _turboSpeed = 1;
            return iterations;
        }
        if (acting is not { } unit) return iterations;

        var heldState = HeldForPlayers.Contains(battleState);
        if (context == BattleContext && !(unit.PlayerOwned && heldState))
        {
            // The turbo mod applies its speed here, so this is the truth about whether turbo is on.
            if (iterations >= 1) _turboSpeed = iterations;
            return iterations;
        }

        // Only the battle states the turbo mod holds back are sped up. Anything else that runs at one
        // tick per frame (a pause menu, a dialog) is left at the speed the player expects.
        var onAuto = unit is { PlayerOwned: true, AiDriven: true };
        return follow && onAuto && heldState && iterations == 1 ? _turboSpeed : iterations;
    }
}
