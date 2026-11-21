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

## Player stat limits

In **Stat → Batas stat party**, select which limits to raise. The new ceilings are 9,999 for HP and MP, 99 for Speed, and 255 for PA and MA. Each switch starts off. These limits apply to player units independently of the job-rule setting for enemies.

The character's raw stat and multiplier still determine its calculated value. With the stock 24-bit raw stat and a multiplier of up to 255%, the theoretical maximum before equipment is 2,611. Raising the HP ceiling to 9,999 leaves a character with 400 calculated HP at 400 HP.

Limit edits share the existing draft. **Simpan** writes them to the mod's rules file. **Batalkan perubahan** restores the saved settings. Job presets and job resets keep the chosen limits. The game save continues to hold the character's underlying raw stats.

While the game runs, the card reports whether the native mod confirmed support for these caps in the current session. It matches the game's process ID and start time to the mod's readiness signal. A missing signal, an unsupported game build, or a failed activation leaves support unconfirmed. In that case the Reinkarnasi preview uses stock caps while retaining any ordinary job or character tuning.

## Move and Jump

Move and Jump are whole numbers of tiles, so they are not tuned by percentage:

- **Per job**: a replacement for the job's base Move (1 to 12) or Jump (1 to 8).
- **Per character**: a bonus from -3 to +5, added to the base value of whatever job the character has (after any job rule) and held within the same ranges. A bonus of 0 leaves the value exactly as the job gives it.

Both act on the base value. The game adds equipment and ability bonuses (shoes, Move +1 and the like) afterwards, so the number in the game can be higher than the one in the tab. The enemy rule is the same as for the other stats.

The job table holds Move at `+0x18` and Jump at `+0x19` of a row (the top bit of the Jump byte is a flag the game masks off). Three places copy them into a unit record at `+0x42` and `+0x43`: in the routine that applies a job (`0x276D35`) and in the two routines that recompute a unit's derived values (`0x278727`, `0x27896E`; the first of these runs on a level-up). A fourth routine (`0x2779EC`) then adds the equipment bonuses to what is there. The mod hooks right after each of the three copies and rewrites the two bytes. Because every recomputation starts again from the job table, a bonus is never added twice. If any of the three places is not found, the mod switches Move and Jump rules off rather than apply them in some situations only.

The preset form takes a Move and a Jump bonus too: it adds them to each job's own base value and stores the result as a job rule.

## Presets in the Stat tab

Choose **Job umum** (IDs 0x4A through 0x5D), **Job khusus karakter** (IDs below 0x4A), or both. Quick presets apply to the selected group in one click. The two growth buttons set speed to 125% or 150% for all stats. **HP dan MP +20%** sets the two multipliers to 120%, and **Semua stat +10%** sets all multipliers to 110%.

For a custom preset, select the stats, enter growth speed and multiplier percentages from 25 to 400, then press **Terapkan ke draf**. Each application starts from each job's original values. For Chemist HP, setting both fields to 200% gives growth 6 and multiplier 160%. At 100% they return to 12 and 80%, and at 150% they become 8 and 120%. Lower growth values make stats grow faster. The actual gain depends on level. Results round to the nearest integer and fit in one byte. A zero game byte or a result equal to the original leaves the override empty. Quick presets change only the values named on their buttons.

Scaling skips monster jobs. **Kembalikan semua job ke bawaan** clears every job rule, including manually edited monster rules. Both operations preserve character rules and the enemy switch. **Simpan** writes the draft. **Batalkan perubahan** restores the last saved rules.

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

A job entry may also carry `"move"` and `"jump"`, and a character entry `"moveBonus"` and `"jumpBonus"`; rule files written before these existed read as before. Each list is in the order HP, MP, Speed, PA, MA. The character list of the tab comes from the Reincarnation mod's roster feed, so that mod has to be enabled to pick a character; rules already saved can be edited without it.

## Confirmed in play

On Steam Enhanced v1.5.2, a high-stat Ramza on a disposable save showed 999 HP, 999 MP, 50 Speed, 99 PA and 99 MA with stock caps. Enabling only the five caps gave him 1,241 HP (including 13 from equipment), 1,075 MP, 99 Speed, 255 PA and 255 MA in the Units screen and in a Mandalia Plain battle. He advanced from level 5 to 8. After a full game restart, the post-battle autosave showed the same five stats at level 8. In an ordinary save with the same settings, level-5 Ramza still showed 62 HP and 21 MP.

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

Move and Jump (same day, same save): the three sites hooked; in battle Ramza's unit held Move 8 and Jump 5 under a job rule of 6 and 5 (his equipment adds 2), a Squire rule of Move 5 gave 5 or 6 depending on equipment, and a character bonus of +2 and +1 gave base 7 and 4. The values were the same after level-ups. Enemy Chocobos and Goblins kept the game's Move under a rule of 9 for their jobs. Rules saved from the Control Center while the game ran ("Move +1" preset, a Jump edit, a character bonus) were the ones the next battle held.

## Not verified

- Which situations reach the third Move and Jump site (`0x278971`), and Move and Jump at the ends of their ranges.
- "Aturan job berlaku juga untuk musuh" switched on, in the game. The rule logic is covered by tests only.
- Story battles, guests in battle, and special jobs other than Ramza's.
- A job change, and a unit changing form, during a battle.
- Loading a save made after level-ups with a modified job growth rate. The autosave round trip above used stock growth.
- Growth 1 at a high level, a job multiplier of 255 in play, and absolute HP or MP of 9,999. The playtest reached 1,241 HP and 1,075 MP. The original raw-stat format constrains what can be reached through growth.

## Limits to know about

- The values in the tab are the ones before the game's own correction described under "How it works"; where that correction is not zero, the HP and Speed numbers the game ends up using differ from the tab's by that amount.
- Job `0xA1` and the row it switches to at job level 8 (`0xA4`) appear as "Job tanpa nama". Character projections use the effective row reported by the game mod. For an older feed that lacks the row, the Stat tab shows a dash for that job.
- The game has a second level-up routine that works on a roster record and reads the job table directly (`0x278B24`, called from `0x289F19`). The mod does not cover it. What triggers it was not determined; level-ups earned in battle do not go through it.
- A battle in progress is not changed by new rules, and neither is a unit that was built before the rules were saved.
- With Stat Tuning installed and enabled, the Reinkarnasi tab uses the saved job and character rules for current stats and the level-1 preview, before equipment. The feed supplies the raw stats for each separately. A missing catalog, an unknown job, or a legacy feed with an ambiguous `0xA1` row leaves the feed values intact.
- Speed, PA and MA are small whole numbers, so a modest multiplier change may not move them.
- Characters are matched by fingerprint. Two generic units with the same sex, birthday and name key would share a rule.
- Tuning is not balance-checked: high multipliers make the game trivially easy, and raw stats raised through a faster growth stay in the save after the rule is removed.
