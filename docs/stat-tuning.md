# Stat Tuning

Changes how fast stats grow on a level-up (growth) and how much of a raw stat the game uses (multiplier), per job and per character. The rules are edited in the **Stat** tab of Ivalice Control Center and applied inside the game by the Reloaded-II mod `ivalice.party.stattuning`.

**Status: run in the game on one save (2026-11-21).** The menu display, a battle, level-ups under a changed growth, the enemy filter and saving from the Control Center while the game runs were confirmed in play; see "Confirmed in play" for exactly what was observed and "Not verified" for what was not.

## What the numbers mean

Every job has ten numbers: a growth constant and a multiplier for each of HP, MP, Speed, Physical Attack (PA) and Magick Attack (MA).

- **Growth.** On a level-up each raw stat gains `raw / (growth + level)`, rounded down. A lower growth number grows faster. It only matters at the moment of a level-up: levels already gained are not recomputed.
- **Multiplier.** The stat the game uses is `raw x multiplier / 100`, in units of 16384, rounded down (and at least 1). A change shows at once, on the Units screen and in the next battle.

Raw stats live in the save; growth and multipliers do not. So a growth rule leaves a permanent trace (the raw stats it helped raise), while a multiplier rule stops mattering the moment it is removed.

## The rules

- **Per job**: replacement values, 1 to 255, for any of the ten numbers. A value left empty keeps the game's own. The rule applies to every unit of the player that currently has that job.
- **Per character**: percentages, 25 to 400, applied on top of whatever the character's current job gives (after any job rule), and following the character from job to job. Growth 200% divides the growth constant by two; multiplier 150% multiplies the job's multiplier by 1.5. Results are rounded and held within 1 to 255, and a stat left at 100% keeps the job's byte exactly as it is. Because a level-up divides by `growth + level`, halving the constant matters most at low levels: for a Squire (HP growth 11) it turns `raw / 12` into `raw / 7` at level 1, but only `raw / 61` into `raw / 56` at level 50.
- **Enemies** keep the game's values, also when they have a job the player changed. One switch ("Aturan job berlaku juga untuk musuh") extends job rules to every unit. Character rules never apply to a unit that is not the player's.

"The player's" means the unit's team bits (battle unit `+0x05`, mask `0x30`) are zero. A character rule also needs the unit's roster slot (`+0x02`) to hold a record with the same character marker; the rule is found by the same fingerprint the Reinkarnasi tab uses (character marker, sex, birthday, name key).

## How it works

The game does not read the job table when it computes a unit's stats or levels it up in battle. It copies the ten bytes from the job's row (`+0x0E` of a `0x31`-byte row) into the unit record at `+0x8A` when the unit is built, and works from that copy:

- `0x275CA4` builds a whole unit; at `0x275DF7` it copies the ten bytes, then calls the routines that compute the stats.
- `0x276C64` applies a job to a unit; at `0x276D1C` it makes the same copy.
- `0x276EBC` computes the shown stats from the unit's own copy (`[unit+0x8A]` growth, `[unit+0x8B]` multiplier).
- `0x278C1C`, the battle level-up, reads the growth constants from the unit's own copy.

The mod places an assembly hook right after each of the two copies (on `mov rdx, rbp` / `mov rcx, rbx`, with the unit in `rbx`) and rewrites the ten bytes in the unit according to the rules. The job table itself is never written, which is why enemies are untouched and why switching the mod off restores the game's behaviour without anything to undo.

The game builds the same kind of unit record for its menus, so the Units screen shows the tuned stats too.

Right after each copy the game calls `0x1EA910`, which can add a signed correction of its own to four of the ten bytes (HP growth, HP multiplier, Speed growth, Speed multiplier), taken from a table indexed by a game setting. The mod's values are therefore the values before that correction, as the job table's own values are. On the save used for testing the correction was zero: the bytes read back from a unit in battle were exactly the ones the rules set.

The hook stub saves and restores `rax`, `rcx`, `rdx`, `r8` to `r11` and `xmm0` to `xmm5` around the call into managed code, for the reason given in [smart-auto-battle.md](smart-auto-battle.md) ("Why the hook saves every register"). The mod tests the stub on a throwaway function before it installs anything and stays inactive if a register is lost.

Addresses are RVAs in `FFT_enhanced.exe` (the build whose title screen reads v1.5.2). The mod finds both copy sites, the roster and the job table by byte patterns, each of which matches exactly once in that executable; if a copy site is missing the mod logs it and stays inactive. Without the roster it runs with job rules only, and without the job table it runs but cannot write `jobs.json`. A roster or job table address is only accepted when the whole table lies inside the game's image.

## Files

All under `%LOCALAPPDATA%\IvaliceControlCenter\stat-tuning`:

- `rules.json`, written by the Control Center. The mod checks it once a second and loads it when it changed, so saving while the game runs needs no restart. New rules apply to units built afterwards: the next battle, or the next time a menu shows a unit. A battle already under way keeps the values its units were built with. A file that cannot be read as rules is ignored and the rules loaded before stay in use.
- `jobs.json`, written by the mod: the game's own values for every job row, which the tab shows as "bawaan". It appears the first time the game runs with the mod.

```json
{
  "schema": 1,
  "applyJobsToEnemies": false,
  "jobs": [ { "job": 74, "growth": [9, null, null, null, null], "multiplier": [130, null, null, null, null] } ],
  "characters": [ { "fingerprint": "2AA22F74", "name": "Ramza", "growthPercent": [100, 100, 100, 100, 200], "multiplierPercent": [150, 100, 100, 100, 100] } ]
}
```

Each list is in the order HP, MP, Speed, PA, MA. The character list of the tab comes from the Reincarnation mod's roster feed, so that mod has to be enabled to pick a character; rules already saved can be edited without it.

## Confirmed in play

On one Chapter 1 save, with the seven other mods of this project loaded:

- Log: `Self-test passed`, both copy sites hooked, roster and job table found, `jobs.json` written. Squire's own values read `11/100, 15/75, 100/100, 60/90, 50/80`, the known ones.
- Rules written while the game was on the world map were loaded within a second.
- Units screen: with job 1 (Ramza's Squire) HP multiplier 120 → 240, Ramza's HP went from 62 to 111. His base HP is 49 and his equipment adds 13: `49 x 2 + 13 = 111`.
- In battle (bytes read from the game's memory): the player's units held the rewritten values, Ramza had 111 HP, the generic Squires had double MP from an MP multiplier of 150 over 75, and a character rule of Speed 200% gave one unit Speed 12 instead of 6.
- Level-ups: with Squire HP growth 11 → 1, a level 5 unit's raw HP went from 690566 to 805660, a gain of exactly `690566 / (1 + 5)`. Ramza, whose growth was left at 11, gained exactly `669338 / (11 + 5)`.
- Enemies: Chocobos (job 94) in a battle built after a rule set their HP multiplier to 250 still held the game's 108.
- From the Control Center, with the game running: the tab listed the party, a rule of HP multiplier 150% for Ramza was saved, and reopening the Units screen showed 86 HP (`49 x 1.5 + 13`, rounded down).
- Two battles were played through with the hooks active, without a crash.

An independent review (GPT 6.2 Astra) found no fault in the hook stub, the stack alignment at both sites or the relocation of the two hooked instructions, and confirmed that all four patterns match exactly once. Its findings in the rules and the app are fixed: a saved rules file that cannot be opened is no longer treated as "no rules" and is never overwritten in that state, an edit made while the app waits for the host is kept, a character stat left at 100% keeps the job's byte, a rules document with lists of the wrong shape is refused instead of read as empty, table addresses are checked over their whole length, and a failed installation of the second hook removes the first.

## Not verified

- "Aturan job berlaku juga untuk musuh" switched on, in the game. The rule logic is covered by tests only.
- Story battles, guests in battle, and special jobs other than Ramza's.
- A job change, and a unit changing form, during a battle.
- Loading a save made after tuned level-ups (the raw stats are ordinary save data, but the round trip was not played).
- Values at the extremes (growth 1 with a high level, multiplier 255), and the game's own caps (999 HP and MP, 50 Speed, 99 PA and MA).

## Limits to know about

- The values in the tab are the ones before the game's own correction described under "How it works"; where that correction is not zero, the HP and Speed numbers the game ends up using differ from the tab's by that amount.
- Job `0xA1` and the row it switches to at job level 8 (`0xA4`) have no name in the job list and appear as "Job tanpa nama". The "Hasil di job sekarang" column uses the character's job id, so for that one job it does not follow the switch.
- The game has a second level-up routine that works on a roster record and reads the job table directly (`0x278B24`, called from `0x289F19`). The mod does not cover it. What triggers it was not determined; level-ups earned in battle do not go through it.
- A battle in progress is not changed by new rules, and neither is a unit that was built before the rules were saved.
- The Reinkarnasi tab computes its stat preview from the game's own job values, so it does not reflect Stat Tuning.
- Speed, PA and MA are small whole numbers, so a modest multiplier change may not move them.
- Characters are matched by fingerprint. Two generic units with the same sex, birthday and name key would share a rule.
- Tuning is not balance-checked: high multipliers make the game trivially easy, and raw stats raised through a faster growth stay in the save after the rule is removed.
