# Changelog

All notable changes to this project are recorded here. Versions follow [Semantic Versioning](https://semver.org/).

## [0.9.0] - 2026-11-21

- Add **All Skills for 50 JP** to Control Center's Progres group and the installer. Across the seven Enhanced language tables, 324 eligible stock prices change and nine already cost 50 JP. Paid abilities below 50 JP also use the flat price. Free entries, unused commands and special acquisition rules keep their original values.
- On a disposable save, Curaja cost 50 JP instead of 800 and the purchase reduced the White Mage balance from 50 to zero. Buying Potion as the last outstanding Chemist ability awarded the game's Mastered badge.

## [0.8.0] - 2026-11-21

- Smart Auto Battle carries the active turbo speed into the Victory and reward animations on Steam Enhanced v1.5.2 with Improved Fast Forward 0.4.5. The existing **FollowTurbo** switch controls this behavior. Normal-speed results, manual confirmation, the reading timeout and reward accounting are preserved.
- In a replayed Mandalia Plain battle at 6×, Victory through the **Next** prompt took 1.14 seconds versus 6.81 seconds before. Both runs awarded 3,000 gil once.

## [0.7.0] - 2026-11-21

- Add five independent **Batas stat party** switches to Stat Tuning. The available ceilings are HP 9,999, MP 9,999, Speed 99, PA 255 and MA 255. They keep existing raw stats, job multipliers, growth and reincarnation calculations. Existing profiles start with all five switches off.
- The Reinkarnasi tab uses the selected caps for current and level-1 previews. With the existing 24-bit raw format, even a 255% multiplier gives at most 2,611 HP or MP before equipment.

## [0.6.1] - 2026-11-21

- The custom Stat preset now restores selected growth or multiplier values to the job defaults at 100%. Quick presets preserve unrelated draft values. The job table labels distinguish the game's original values from the editable draft.

## [0.6.0] - 2026-11-21

### Added

- **Move and Jump** in Stat Tuning: a job rule replaces the job's base Move (1 to 12) and Jump (1 to 8); a character rule adds a bonus (-3 to +5) on top of whatever job the character has. The game still adds equipment and ability bonuses afterwards. Enemies are untouched unless job rules are extended to them. The Stat tab shows and edits both, the preset form takes a Move and a Jump bonus, and there is a "Move +1" quick preset.
- The mod hooks the three places where the game sets a unit's base Move and Jump. With any of the three missing it switches Move and Jump rules off and keeps the others.
- Draft-only Stat presets for common jobs, character-specific jobs, or both, with four quick choices, a custom stat selection and a reset for all job rules.
- The Reinkarnasi tab applies saved Stat Tuning rules to current and level-1 stats before equipment and shows a note when tuning contributes. Job 0xA1 keeps its feed values because its effective row is unknown.

### Verified

- 232 Party tests and 34 Core tests passed. The Release Control Center build completed with zero warnings and errors. The new tests cover job groups, per-job defaults, neutral columns, rounding and clamping, zero bytes, preservation of other rules, invalid inputs, both roster stat blocks and feed fallbacks.
- Ran the Release Control Center in WebView2 with the real 134-job catalog. Exercised all four quick presets, custom HP-only tuning, all three scopes, input bounds, reset, discard, tab switching and an explicit save. Checked that a concurrent manual edit survived both preset calculation and saving, and that discarding a pending preset prevented a late reply from restoring it.
- Inspected screenshots of the preset card and populated Squire editor at the default window size. The card fit its viewport without horizontal overflow. The app was closed and the test rules file removed, restoring its initial absence.

- 15 further tests cover Move and Jump (job and character rules, the enemy filter, ranges, the write and read round trip, older rule files, the job export, presets, the stub's unit register). The Party test project now has 247 tests.

### Confirmed in play

- On one Chapter 1 save with all eight mods loaded: self-test passed, the two copy sites and the three Move and Jump sites hooked, three battles started and two played for several rounds without a crash.
- Move and Jump, read from the game's memory in battle: with Ramza's job at Move 6 and Jump 5 his unit held 8 and 5 (6 plus 2 from equipment); a Squire rule of Move 5 gave 5 or 6 depending on equipment; a character bonus of +2 Move and +1 Jump on one Squire gave base 7 and 4. The values were unchanged after level-ups, which recompute them.
- Enemy Chocobos and Goblins kept the game's Move while a rule set their jobs' Move to 9.
- From the Control Center with the game running: the "Move +1" preset, a Jump edit and a character rule (HP multiplier 150%, Move +2) were saved; the next battle held exactly those values, and the Reinkarnasi tab showed the tuned HP (73 instead of 49) with its note.

### Not verified

- Which of the game's situations reach the third Move and Jump site (`0x278971`); it is hooked the same way as the second, which level-ups exercised.
- Move and Jump at the ends of their ranges, on special jobs other than Ramza's, and in story battles.
- The preset results other than "Move +1" in the game (their arithmetic is the tested one the rules use).

## [0.5.0] - 2026-11-21

### Added

- **Stat Tuning** (`ivalice.party.stattuning`), a Reloaded-II mod, and a **Stat** tab in Ivalice Control Center to drive it:
  - per job: replacement growth constants and stat multipliers for HP, MP, Speed, PA and MA, shown next to the game's own values;
  - per character: percentages on top of whatever the character's current job gives, which follow the character from job to job;
  - only the player's units are changed; enemies with the same job keep the game's values unless "Aturan job berlaku juga untuk musuh" is switched on;
  - rules saved while the game runs are picked up within a second and apply to units built afterwards (the next battle, or the next time a menu shows a unit);
  - the game's job table is never written: the mod rewrites the copy of the ten bytes the game puts into each unit, right after the game makes it.
- `src/Ivalice.Party/StatTuning.cs`: the rules, their file format and which rules a battle unit gets.
- The install script builds and installs Stat Tuning; CI runs its tests and builds it.
- [docs/stat-tuning.md](docs/stat-tuning.md): what the numbers mean, how the mod works, the evidence and the limits.

### Verified

- 31 new tests cover the rules (job and character rules, stacking, rounding, the enemy filter, tolerant parsing, the write and read round trip), unit recognition, the job export, and the hook stub's register preservation and stack alignment. The Party test project now has 220 tests.
- An independent review (GPT 6.2 Astra) found no fault in the hook stub, the stack alignment or the relocated instructions. Its findings elsewhere are fixed: an unreadable saved rules file is never treated as empty or overwritten, edits made while the app waits for the host are kept, a character stat left at 100% keeps the job's byte, malformed rule lists are refused, table addresses are checked over their whole length, and a half-installed pair of hooks is removed.
- Both copy-site patterns match exactly once in `FFT_enhanced.exe`.

### Confirmed in play

- On one Chapter 1 save with all eight mods loaded: self-test passed, both sites hooked, two battles played through without a crash.
- Units screen: Ramza's HP went from 62 to 111 with his job's HP multiplier doubled, and to 86 with a character rule of 150% saved from the Control Center while the game ran; both match the arithmetic.
- Level-ups in battle, read from the game's memory: with Squire HP growth 11 → 1 a level 5 unit gained exactly `raw / 6`; Ramza, left at 11, gained exactly `raw / 16`.
- Enemy Chocobos kept the game's HP multiplier while a rule for their job was active.

### Not verified

- The switch that extends job rules to enemies, in the game (tests only).
- Story battles, guests, special jobs other than Ramza's, a job change during battle, and loading a save made after tuned level-ups.
- The game's second, roster-side level-up routine (`0x278B24`) is not covered by the mod; what triggers it was not determined.
- The game can add its own correction to the HP and Speed bytes after the mod sets them (`0x1EA910`); it was zero on the test save and was not seen non-zero.

## [0.4.0] - 2026-11-21

### Added

- **Reincarnation** (`ivalice.party.reincarnation`), a Reloaded-II mod, and a **Reinkarnasi** tab in Ivalice Control Center to drive it:
  - a party member of level 10 or higher (the minimum level is a setting) goes back to level 1 and restarts from the average starting stats of a new recruit, raised by its Power;
  - Power grows by 0.5% for every level given up (level 10: +5%, level 99: +49.5%), adds up over reincarnations and stops at +100%;
  - job, JP, abilities, equipment, Brave and Faith are untouched; guests, monsters and units away on an errand cannot reincarnate;
  - the tab lists each character's stats, reincarnation count and Power, and previews the next reincarnation; the preview is computed by the same code that changes the game;
  - a confirmation dialog states what will happen; before the command is sent, every save file is copied to `%LOCALAPPDATA%\IvaliceControlCenter\save-backups` (the 20 newest backups are kept);
  - the mod refuses a command during a battle or story scene, when the slot holds another unit than the player confirmed, and when the command is older than two minutes;
  - every applied reincarnation is first written to a journal with the unit's record before and after, so it can be undone by hand;
  - count and Power are stored in spare bytes of the unit's own roster record, tied to the unit by a fingerprint, so they travel with the save and a new recruit in a reused slot does not inherit them.
- A battle copy of a unit left over from an earlier fight no longer blocks it: the change is written into that copy as well, so a later write-back by the game carries the new values.
- Mod settings in the Control Center: "Level minimum" (2 to 99, default 10), **Mode aman** (safe mode: the mod answers with what it would do and changes nothing), "Tampilkan nama karakter" and "Log diagnostik".
- `src/Ivalice.Party`: the reincarnation rules and the file formats shared by the mod and the app.
- The install script builds and installs Reincarnation; CI runs its tests and builds it.
- [docs/reincarnation.md](docs/reincarnation.md): how it works, the disassembly evidence, the file protocol, and the list of things to confirm in the game.

### Verified

- An independent review (GPT 6.2 Astra) confirmed the battle-record offsets against the game's code; its findings are fixed: the change is mirrored into every battle copy of the unit, a simulated command stays a simulation, the mod takes ownership of a command before reading it and never repeats an id, a failed save backup leaves no partial folder, and a timeout no longer claims nothing changed.
- 189 tests cover the rules, the stamp, eligibility, the battle checks, the roster export, the command processor and the mod's file handling, including records copied from a real save. Eleven deliberate rule breaks (battle check removed, fingerprint ignored, game written before the journal, and others) each made tests fail.
- The mod's hook stub, assembled and installed by Reloaded.Hooks on a throwaway native function, hands back `rax`, `rcx`, `rdx`, `r8` to `r11` and `xmm0` to `xmm5` unchanged after running managed code; a stub that calls managed code without saving registers is caught by the same test.
- Each of the nine byte patterns the mod uses matches exactly once in the code section of `FFT_enhanced.exe` 1.00.
- The game's battle-to-roster write-back and the party menu's write-back were read in the disassembly: neither writes the spare bytes the stamp uses.
- The Reinkarnasi tab was rendered in a browser with sample data.

### Confirmed in play

- Loaded in the game with the other mods: `Self-test passed`, all patterns resolved, roster at image base + `0x11A7D10`. The frame routine only starts running once the game is past its version-select screen.
- The roster matched the game: names (Ramza, Ealdwine, Patrick, Radcliffe, Giselle, Melusine, Wulfhilda and the guests Delita and Argath), jobs and levels were the same as on the game's Units screen.
- "Battle in progress" read `false` at the title menu and on the world map, and `true` from the deployment screen through a battle.
- A real reincarnation on the world map (Giselle, level 4, with the minimum level set to 2 for the test): the game's Units screen showed her at level 1 with 0 experience at once, MP 11 as predicted, and JP and job level unchanged.
- The game's next autosave, decoded from a copy, held her record at level 1 with the stamp in the spare bytes.
- From the Control Center: the tab listed the party from the running game, showed the in-battle refusal during deployment, and on the world map the Reinkarnasi button backed up the saves, sent the command and showed the result (Ealdwine, Power +1.5%) while the game window was not focused.

- After a battle, with the game still holding battle copies of the units that fought, one of them was reincarnated. Starting the next battle did not bring the old level back; the deployment panel showed her at level 1, she fought that battle on auto, and after it her record was still level 1 with the stamp. Sending the same command again was refused.

### Not verified

- Loading a save that holds a reincarnated unit was not played through; the save file itself was decoded and holds the change.
- A second reincarnation of the same unit in the game (stacking is covered by tests only).
- Story characters other than Ramza.

## [0.3.0] - 2026-11-20

### Added

- **Turbo follow** in Smart Auto Battle: with Improved Fast Forward on, a party member on auto-battle is now fast-forwarded like an enemy. Before, the game paused for about three seconds before each of their actions even at 6x.
  - Cause: the turbo mod holds certain battle states at normal speed whenever the acting unit belongs to the player, so the player can read and choose. It judges that from the unit's permanent ownership flag, so a unit on auto-battle was held back too. The longest of those states is a 180-tick preview of the chosen action.
  - Fix: Smart Auto Battle hooks the point where the game asks how many logic ticks to run this frame and, for a player-owned unit that the AI is driving, restores the tick count the turbo mod last used.
  - Setting "Keep turbo speed on auto-battle" (on by default) in Ivalice Control Center.

### Changed

- XP Multiplier and Global JP Boost patches: when the game's unit table cannot be located and "Player units only" is on, the multiplier now applies to no one instead of to everyone.
- The install script no longer switches a mod back on when it rebuilds an older, unpatched copy.
- If the register self-test fails, the turbo-follow hook is not installed either.

### Verified

- Measured in one battle with turbo at 6x: the pre-action hold lasted 3037 ms without the setting and 400 to 501 ms with it, switched live in the same battle. With turbo off it stayed at 3037 ms.
- 53 tests cover the battle logic, including the turbo-follow rules: only the battle states the turbo mod holds back are sped up, never a menu or dialog.
- An independent review (GPT 6.2 Astra) checked both assembly stubs and the self-test's machine code; its six findings are fixed in this release.

### Not verified

- Other turbo speeds than 6x, and units driven by the AI because of a status such as Berserk.

## [0.2.1] - 2026-11-20

### Changed

- **XP Multiplier and Global JP Boost now apply to your side only.** The original mods multiply what every unit earns, so enemies levelled up as fast as the party. The install script now builds both from a pinned commit with a small patch (`patches/`) that checks the unit's team before multiplying, and adds a "Player units only" setting (on by default) to each. Guests on your side still get the multiplier.

### Fixed

- **Crash when a battle started with Smart Auto Battle enabled** (access violation in `FFT_enhanced.exe` at RVA `0x2026F6`). The game's code keeps a pointer in register `r8` across its call to the function the mod hooks, because the original function never writes that register. The mod's hook ran managed code there and overwrote it. The hook is now an assembly hook that saves and restores every volatile register around the call into the mod.

### Added

- A self-test that runs inside the game when the mod loads: it calls the hooked function with marker values in `r8` to `r11` and checks they survive. If they do not, the mod removes its hook and stays inactive instead of risking a crash. The Reloaded-II log shows `Self-test passed` or `Self-test FAILED`.

### Verified

- In a battle with XP Multiplier at 5x, an attack by a party member earned +45 experience and an attack by an enemy earned +10.
- A battle restarted from its beginning ran on auto without the crash for more than seven minutes; a healer switched to saving a wounded ally; F8 returned control and the command menu.
- The self-test reported `CLOBBERED` with the 0.2.0 hook and passes with the new one, on the same game build.
- The crash dump showed `r8 = rcx = 0x200` at the faulting instruction, with the return address inside the caller that reloads `rcx` from `r8`.

### Not verified

- JP gains with the patched Global JP Boost were not measured.
- Story battles with guests and special win conditions.

### Known limits

- The game's own pop-ups during a battle (item found, tutorials) still wait for a confirm press.
- The auto-battle AI is the game's own; a party below the enemies' level can still lose.

## [0.2.0] - 2026-11-20

Pre-release: Smart Auto Battle has not been tested inside a battle yet.

### Added

- **Smart Auto Battle** (`ivalice.battle.smartautobattle`), a Reloaded-II mod that hands the player's units to the game's own auto-battle:
  - healers (White Magicks or Items users by default) switch to Save Fading Life when an ally is knocked out or below the heal threshold;
  - every other unit, and healers with nobody to help, use Fight for Life;
  - a fighting unit stays on its enemy and is aimed again when that enemy is down, petrified or gone;
  - F8 or L3 + R3 toggles it during play, with a beep, and switching off returns every unit it ordered to manual;
  - Protect Allies and Run Like a Rabbit picked in the in-game menu, guests and enemies are left alone.
- Control Center: a "Pertarungan" group with Smart Auto Battle's settings (active at start, heal threshold, which skillsets are healers, whether healers revive, toggle options).
- The install script builds and installs Smart Auto Battle.
- [docs/smart-auto-battle.md](docs/smart-auto-battle.md): how the mod works and the disassembly evidence behind it.

### Verified

- The mod loads in the game, resolves the battle unit table and installs its hook at the addresses found by static analysis; the game stayed responsive at the title screen for 60 seconds.
- 39 tests cover the decision logic; the hook patterns each match exactly once in the executable.

### Not verified

- Any behaviour inside a battle: units acting on their own, healers healing, toggling, story battles with guests, suspend saves.
- The names of the game's auto-battle modes and the meaning of individual status bits are inferred from the original game.

### Known limits

- While the mod is on, choosing Manual in the in-game Auto-battle menu does not stick; use the toggle instead.
- The on/off state lasts until toggled or until the game closes; it is not reset per battle.
- A healer does not count itself as someone to save, and the heal threshold cannot exceed 50%.

## [0.1.0] - 2026-11-20

First release.

### Added

- **Ivalice Control Center**, a desktop app to manage Reloaded-II mods for the Enhanced version of the game:
  - mod list grouped by purpose, with Indonesian descriptions, author, licence and source;
  - on/off switches that enable required mods automatically and refuse to disable a mod others need;
  - settings for Improved Fast Forward (turbo speed), Global JP Boost (JP multiplier, remove JP Boost ability), XP Multiplier (XP multiplier, level cap) and FFT Mod Loader;
  - a warning when Global JP Boost and XP Multiplier are both enabled, because they hook the same game function;
  - a button that launches the game through Reloaded-II;
  - a log view that summarises the latest session.
- `scripts/install.ps1`, which installs Reloaded-II, the catalog mods and the app, and registers the game.
- A catalog of seven entries: two Reloaded-II libraries, FFT Mod Loader, Improved Fast Forward, AntiAutoRotate, Global JP Boost and XP Multiplier. The install script takes the latest version of each; the versions tested for this release were FFT Mod Loader 1.7.5, Improved Fast Forward 0.4.5, AntiAutoRotate 1.0.0, Global JP Boost 0.2.0 and XP Multiplier 0.1.0.

### Verified

- All seven entries load in the game and their hooks install, according to the Reloaded-II session log.
- Changing turbo speed in the app and pressing Main produced `Turbo speed set to 6x` in the game log.

### Not verified

- Behaviour inside a battle (speed, camera, JP and XP gains) has not been play-tested.
- The install script was only run on a Windows 11 machine where everything was already installed, so its download, build and first-run paths have not been exercised end to end.
- Global JP Boost and XP Multiplier have not been tested together in a battle.

[0.7.0]: https://github.com/arthamegayasa/ivalice-master-mode/releases/tag/v0.7.0
[0.6.1]: https://github.com/arthamegayasa/ivalice-master-mode/releases/tag/v0.6.1
[0.6.0]: https://github.com/arthamegayasa/ivalice-master-mode/releases/tag/v0.6.0
[0.5.0]: https://github.com/arthamegayasa/ivalice-master-mode/releases/tag/v0.5.0
[0.4.0]: https://github.com/arthamegayasa/ivalice-master-mode/releases/tag/v0.4.0
[0.3.0]: https://github.com/arthamegayasa/ivalice-master-mode/releases/tag/v0.3.0
[0.2.1]: https://github.com/arthamegayasa/ivalice-master-mode/releases/tag/v0.2.1
[0.2.0]: https://github.com/arthamegayasa/ivalice-master-mode/releases/tag/v0.2.0
[0.1.0]: https://github.com/arthamegayasa/ivalice-master-mode/releases/tag/v0.1.0
