namespace Ivalice.Party;

/// <summary>
/// The part of the game's battle unit table that decides whether the game will copy a battle unit back
/// over a roster record. Offsets are from the game's own write-back code; see docs/reincarnation.md.
/// </summary>
public static class BattleBinding
{
    public const int Capacity = 21;
    public const int Stride = 0x200;
    public const int TableSize = Capacity * Stride;

    private const int InUse = 0x01;
    private const byte Empty = 0xFF;
    /// <summary>The roster slot the battle unit was built from and is written back to.</summary>
    private const int RosterSlot = 0x02;
    /// <summary>The battle unit's id; zero for a record that was never filled.</summary>
    private const int UnitId = 0x191;
    /// <summary>1 on the field, 0 benched, 0xFF for an empty record.</summary>
    private const int OnField = 0x1B5;

    /// <summary>
    /// The battle record that the game would write back into this roster slot, or -1 when there is none.
    /// It is the test the game's own write-back makes (RVA 0x2C6199: same roster slot, a unit id, not
    /// marked empty), widened to also count a record that is merely in use.
    /// </summary>
    public static int Find(ReadOnlySpan<byte> battleTable, int rosterSlot)
    {
        if (battleTable.Length != TableSize) return -1;
        for (var index = 0; index < Capacity; index++)
        {
            var unit = battleTable.Slice(index * Stride, Stride);
            if (unit[RosterSlot] == rosterSlot && IsLive(unit)) return index;
        }
        return -1;
    }

    public static int CountInUse(ReadOnlySpan<byte> battleTable) => Count(battleTable, unit => unit[InUse] != Empty);

    /// <summary>How many battle records the game would still write back into some roster slot.</summary>
    public static int CountBound(ReadOnlySpan<byte> battleTable) =>
        Count(battleTable, unit => unit[RosterSlot] < RosterLayout.Capacity && IsLive(unit));

    private static bool IsLive(ReadOnlySpan<byte> unit) =>
        unit[UnitId] != 0 && (unit[InUse] != Empty || unit[OnField] != Empty);

    private delegate bool UnitTest(ReadOnlySpan<byte> unit);

    private static int Count(ReadOnlySpan<byte> battleTable, UnitTest test)
    {
        if (battleTable.Length != TableSize) return 0;
        var count = 0;
        for (var index = 0; index < Capacity; index++)
            if (test(battleTable.Slice(index * Stride, Stride))) count++;
        return count;
    }
}

/// <summary>What the mod read from the game to judge whether a battle is in progress.</summary>
/// <param name="BattleControlPresent">The game's battle controller object exists. It is created when the battle scene routine starts and destroyed when it ends.</param>
/// <param name="ActiveActorPresent">A unit is taking its turn; null when the mod could not find that function.</param>
/// <param name="BattleUnitsInUse">Battle unit records marked in use. They are not cleared when a battle ends, so this is a hint only.</param>
/// <param name="BattleUnitsBound">Battle unit records the game would still write back into the roster.</param>
/// <param name="BattleModeFlag">The game's battle mode variable (0 on the world map); null when not found. Not used for the decision.</param>
/// <param name="BattleState">The battle state machine's current state; null when not found. Not used for the decision.</param>
public sealed record SafetyInputs(
    bool BattleControlPresent,
    bool? ActiveActorPresent,
    int BattleUnitsInUse,
    int BattleUnitsBound,
    int? BattleModeFlag,
    int? BattleState);

/// <param name="InBattle">True when the roster must not be changed because a battle or story scene is running.</param>
/// <param name="DecidedBy">Which input settled it.</param>
public sealed record GameStatus(bool InBattle, string DecidedBy, SafetyInputs Inputs);

public static class GameSafety
{
    /// <summary>
    /// A battle is in progress while the battle controller object exists. A unit taking its turn without
    /// that object should not happen; it is treated as a battle too, since refusing is the safe answer.
    /// </summary>
    public static GameStatus Decide(SafetyInputs inputs)
    {
        if (inputs.BattleControlPresent) return new GameStatus(true, "battleControlPresent", inputs);
        if (inputs.ActiveActorPresent == true) return new GameStatus(true, "activeActorPresent", inputs);
        return new GameStatus(false, "battleControlAbsent", inputs);
    }

    /// <summary>
    /// Whether the unit in a slot may reincarnate right now: its own record first, then the moment.
    /// </summary>
    public static Refusal Evaluate(ReadOnlySpan<byte> record, int slot, ReadOnlySpan<byte> battleTable, GameStatus game)
    {
        var refusal = Reincarnation.Check(record, slot);
        if (refusal != Refusal.None) return refusal;
        if (game.InBattle) return Refusal.InBattle;
        if (battleTable.Length != BattleBinding.TableSize) return Refusal.GameStateUnknown;
        return BattleBinding.Find(battleTable, slot) >= 0 ? Refusal.BoundToBattleUnit : Refusal.None;
    }
}
