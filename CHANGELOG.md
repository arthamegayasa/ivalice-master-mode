# Changelog

All notable changes to this project are recorded here. Versions follow [Semantic Versioning](https://semver.org/).

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
- A catalog of seven entries: two Reloaded-II libraries, FFT Mod Loader 1.7.5, Improved Fast Forward 0.4.5, AntiAutoRotate 1.0.0, Global JP Boost 0.2.0 and XP Multiplier 0.1.0.

### Verified

- All seven entries load in the game and their hooks install, according to the Reloaded-II session log.
- Changing turbo speed in the app and pressing Main produced `Turbo speed set to 6x` in the game log.

### Not verified

- Behaviour inside a battle (speed, camera, JP and XP gains) has not been play-tested.
- The install script was only run on a machine where everything was already installed.

[0.1.0]: https://github.com/arthamegayasa/ivalice-master-mode/releases/tag/v0.1.0
