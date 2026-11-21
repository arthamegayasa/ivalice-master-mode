namespace Ivalice.Battle;

/// <summary>Supplies the animation speed for the victory and reward layout.</summary>
public sealed class ResultTurbo
{
    private const int Battle = 1;
    private const int Event = 2;
    private const int BattleResults = 52;
    private int _battleFactor = 1;
    private bool _observedEventOff;

    public static bool IsResultEvent(int context, int battleState) => context == Event && battleState == BattleResults;

    public int Update(int context, int battleState, int currentTurboFactor, bool follow, bool resultVisible)
    {
        // Improved Fast Forward clears its own switch when the game enters the results event.
        if (!follow || !IsResultEvent(context, battleState))
        {
            _battleFactor = follow && context == Battle ? currentTurboFactor : 1;
            _observedEventOff = false;
            return 1;
        }

        if (currentTurboFactor == 1)
            _observedEventOff = true;
        else if (_observedEventOff)
            _battleFactor = 1; // The player turned turbo back on. Subsequent toggles belong to them.

        return resultVisible ? (currentTurboFactor > 1 ? currentTurboFactor : _battleFactor) : 1;
    }
}
