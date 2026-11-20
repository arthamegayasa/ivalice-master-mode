namespace Ivalice.Battle;

/// <summary>
/// Layout of the game's battle unit table in FFT_enhanced.exe, as far as this mod needs it.
/// Every offset was read from the game's own code; see docs/smart-auto-battle.md for the evidence.
/// </summary>
public static class BattleLayout
{
    public const int Capacity = 21;
    public const int Stride = 0x200;
    public const int TableSize = Capacity * Stride;

    /// <summary>Slot number of the unit, or <see cref="EmptySlot"/> when the slot is unused.</summary>
    public const int InUse = 0x01;
    public const byte EmptySlot = 0xFF;

    /// <summary>Team and control flags as they are right now (statuses such as Confusion change these).</summary>
    public const int CurrentFlags = 0x05;
    /// <summary>The unit's own team and control flags, which the game restores the current flags from.</summary>
    public const int PermanentFlags = 0x1EE;
    /// <summary>Non-zero team bits mean the unit is not on the player's team.</summary>
    public const byte TeamMask = 0x30;
    /// <summary>Set when the player gives this unit its orders.</summary>
    public const byte ControlBit = 0x08;

    public const int PrimarySkillset = 0x12;
    public const int SecondarySkillset = 0x13;
    public const int Hp = 0x30;
    public const int MaxHp = 0x32;

    public const int Statuses = 0x61;
    public const byte CrystalBit = 0x40;
    public const byte KoBit = 0x20;
    /// <summary>In the second status byte.</summary>
    public const byte PetrifyBit = 0x80;
    /// <summary>In the second status byte.</summary>
    public const byte TreasureBit = 0x01;

    /// <summary>The auto-battle mode the in-game menu writes; 0 means none.</summary>
    public const int AutoMode = 0x1EC;
    /// <summary>The unit slot the auto-battle mode is aimed at.</summary>
    public const int AutoTarget = 0x1ED;
}

/// <summary>
/// Values of the auto-battle mode byte. The game treats any non-zero value as "driven by the AI"
/// and branches on these four; the names follow the original game's Auto-battle menu.
/// </summary>
public static class AutoMode
{
    public const byte None = 0x00;
    public const byte Fight = 0x0C;
    public const byte ProtectAllies = 0x0E;
    public const byte SaveFadingLife = 0x10;
    public const byte RunLikeARabbit = 0x11;
}

/// <summary>Skillset ids from the game's job command table.</summary>
public static class Skillsets
{
    public const byte Items = 6;
    public const byte MartialArts = 9;
    public const byte WhiteMagicks = 10;
}

/// <summary>What to write into a unit: an auto-battle mode and the slot it is aimed at.</summary>
public readonly record struct AutoOrder(byte Mode, byte Target);

/// <param name="HealThresholdPercent">A healer saves an ally whose HP is below this share of its maximum.</param>
/// <param name="HealerSkillsets">A unit with one of these as primary or secondary skillset is a healer.</param>
/// <param name="ReviveFallenAllies">Whether a knocked-out ally is a reason for a healer to stop fighting.</param>
public sealed record PlannerSettings(
    int HealThresholdPercent, IReadOnlySet<byte> HealerSkillsets, bool ReviveFallenAllies = true)
{
    /// <summary>
    /// The game's own "save fading life" search only reacts below half HP, so a higher threshold
    /// would hand out an order the game then does nothing with.
    /// </summary>
    public int EffectiveThreshold => Math.Clamp(HealThresholdPercent, 10, 50);
}

/// <summary>Decides, from a snapshot of the battle unit table, which auto-battle order a player unit should get.</summary>
public static class AutoBattlePlanner
{
    /// <returns>The order to write, or null to leave the unit exactly as it is.</returns>
    public static AutoOrder? Plan(ReadOnlySpan<byte> table, int slot, PlannerSettings settings)
    {
        if (table.Length != BattleLayout.TableSize || slot < 0 || slot >= BattleLayout.Capacity) return null;

        var unit = Unit(table, slot);
        if (!IsPresent(unit) || IsKo(unit)) return null;

        var own = unit[BattleLayout.PermanentFlags];
        if ((own & BattleLayout.TeamMask) != 0 || (own & BattleLayout.ControlBit) == 0) return null;

        // Any other mode was picked by the player in the in-game menu, so it is theirs to keep.
        var mode = unit[BattleLayout.AutoMode];
        if (mode != AutoMode.None && !IsHandedOut(mode)) return null;

        var isHealer = settings.HealerSkillsets.Contains(unit[BattleLayout.PrimarySkillset])
                       || settings.HealerSkillsets.Contains(unit[BattleLayout.SecondarySkillset]);
        if (isHealer && FindAllyToSave(table, slot, settings) is { } ally)
            return new AutoOrder(AutoMode.SaveFadingLife, (byte)ally);

        // Stay on the enemy the unit is already fighting; pick another only when that one is no longer a target.
        var aimedAt = unit[BattleLayout.AutoTarget];
        if (mode == AutoMode.Fight && aimedAt < BattleLayout.Capacity && IsLivingEnemy(Unit(table, aimedAt)))
            return new AutoOrder(AutoMode.Fight, aimedAt);

        return FindEnemy(table) is { } enemy ? new AutoOrder(AutoMode.Fight, (byte)enemy) : null;
    }

    /// <summary>Whether a mode is one this mod writes, and so one it clears again when switched off.</summary>
    public static bool IsHandedOut(byte mode) => mode is AutoMode.Fight or AutoMode.SaveFadingLife;

    private static int? FindAllyToSave(ReadOnlySpan<byte> table, int self, PlannerSettings settings)
    {
        var thresholdPercent = settings.EffectiveThreshold;
        int? best = null;
        long bestHp = 0, bestMax = 1;

        for (var slot = 0; slot < BattleLayout.Capacity; slot++)
        {
            if (slot == self) continue;
            var unit = Unit(table, slot);
            if (!IsPresent(unit) || IsGone(unit) || (unit[BattleLayout.PermanentFlags] & BattleLayout.TeamMask) != 0) continue;

            if (IsKo(unit))
            {
                if (settings.ReviveFallenAllies) return slot;
                continue;
            }

            long hp = ReadU16(unit, BattleLayout.Hp), max = ReadU16(unit, BattleLayout.MaxHp);
            if (max == 0 || hp * 100 >= thresholdPercent * max) continue;
            // Compare hp/max against bestHp/bestMax without dividing.
            if (best is null || hp * bestMax < bestHp * max) (best, bestHp, bestMax) = (slot, hp, max);
        }

        return best;
    }

    private static int? FindEnemy(ReadOnlySpan<byte> table)
    {
        for (var slot = 0; slot < BattleLayout.Capacity; slot++)
            if (IsLivingEnemy(Unit(table, slot))) return slot;
        return null;
    }

    private static bool IsLivingEnemy(ReadOnlySpan<byte> unit) =>
        IsPresent(unit) && !IsKo(unit) && !IsGone(unit)
        && (unit[BattleLayout.Statuses + 1] & BattleLayout.PetrifyBit) == 0
        && (unit[BattleLayout.PermanentFlags] & BattleLayout.TeamMask) != 0;

    private static ReadOnlySpan<byte> Unit(ReadOnlySpan<byte> table, int slot) =>
        table.Slice(slot * BattleLayout.Stride, BattleLayout.Stride);

    private static bool IsPresent(ReadOnlySpan<byte> unit) => unit[BattleLayout.InUse] != BattleLayout.EmptySlot;
    private static bool IsKo(ReadOnlySpan<byte> unit) => (unit[BattleLayout.Statuses] & BattleLayout.KoBit) != 0;

    /// <summary>A fallen unit that has already become a crystal or a treasure chest cannot be brought back.</summary>
    private static bool IsGone(ReadOnlySpan<byte> unit) =>
        (unit[BattleLayout.Statuses] & BattleLayout.CrystalBit) != 0
        || (unit[BattleLayout.Statuses + 1] & BattleLayout.TreasureBit) != 0;

    private static int ReadU16(ReadOnlySpan<byte> unit, int offset) => unit[offset] | (unit[offset + 1] << 8);
}
