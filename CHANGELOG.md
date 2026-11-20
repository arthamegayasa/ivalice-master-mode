# Changelog

All notable changes to this project are recorded here. Versions follow [Semantic Versioning](https://semver.org/).

## [0.3.0] - 2026-11-20

### Added

- **Turbo follow** in Smart Auto Battle: with Improved Fast Forward on, a party member on auto-battle is now fast-forwarded like an enemy. Before, the game paused for about three seconds before each of their actions even at 6x.
  - Cause: the turbo mod holds certain battle states at normal speed whenever the acting unit belongs to the player, so the player can read and choose. It judges that from the unit's permanent ownership flag, so a unit on auto-battle was held back too. The longest of those states is a 180-tick preview of the chosen action.
  - Fix: Smart Auto Battle hooks the point where the game asks how many logic ticks to run this frame and, for a player-owned unit that the AI is driving, restores the tick count the turbo mod last used.
  - Setting "Keep turbo speed on auto-battle" (on by default) in Ivalice Control Center.

### Verified

- Measured in one battle with turbo at 6x: the pre-action hold lasted 3037 ms without the setting and 400 to 501 ms with it, switched live in the same battle. With turbo off it stayed at 3037 ms.
- 50 tests cover the battle logic, including the turbo-follow rules.

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

[0.3.0]: https://github.com/arthamegayasa/ivalice-master-mode/releases/tag/v0.3.0
[0.2.1]: https://github.com/arthamegayasa/ivalice-master-mode/releases/tag/v0.2.1
[0.2.0]: https://github.com/arthamegayasa/ivalice-master-mode/releases/tag/v0.2.0
[0.1.0]: https://github.com/arthamegayasa/ivalice-master-mode/releases/tag/v0.1.0
