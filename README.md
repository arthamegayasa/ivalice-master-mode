![Ivalice Master Mode](assets/banner.jpg)

# Ivalice Master Mode

One place to install, watch and tune mods for **FINAL FANTASY TACTICS - The Ivalice Chronicles** (Steam, Enhanced version).

[Reloaded-II](https://github.com/Reloaded-Project/Reloaded-II) stays the foundation that loads mods into the game. This project adds **Ivalice Control Center**, a desktop app that sits on top of it, plus an install script that sets everything up.

![Ivalice Control Center](docs/screenshot.png)

## What the app does

- Lists every installed mod with a plain-language explanation of what it changes. The interface is in Indonesian.
- Turns mods on and off. Turning one on also turns on what it needs; a mod that others still need cannot be turned off.
- Edits each mod's settings with sliders and switches, checked against a safe range before saving.
- Warns about conflicts, such as two mods that hook the same game function.
- Launches the game with the enabled mods.
- Shows the latest session log and picks out the lines worth checking.

The app changes the same files Reloaded-II reads, so Reloaded-II's own launcher keeps working alongside it. Changes apply the next time the game starts.

## Mods in the catalog

| Mod | Author | What it does | Licence |
|---|---|---|---|
| [FFT Mod Loader](https://github.com/Nenkai/fftivc.utility.modloader) | Nenkai | File, table and texture mods for this game | MIT |
| Improved Fast Forward | masklovesmirror | R1 toggles 2x to 10x speed in battles and events | Not stated |
| [AntiAutoRotate](https://github.com/rexx-oss/FFT-AntiAutoRotate) | wtfjajaja | Stops the battle camera rotating on its own | Not stated |
| [Global JP Boost](https://github.com/knightsong/global-jp-boost) | knightsong | JP multiplier for every unit | GPL-3.0 |
| [XP Multiplier](https://github.com/knightsong/xp-multiplier) | knightsong | XP multiplier and level cap | GPL-3.0 |

Two Reloaded-II libraries (Signature Scanner and Shared Hooks) are installed as dependencies. Any other mod you drop into the Reloaded-II `Mods` folder also shows up in the app, with an on/off switch.

**This repository contains none of these mods.** The install script downloads the ones with open licences from their authors' releases, builds the two GPL mods from their source, and installs the two mods without a stated licence only from archives you downloaded yourself from Nexus Mods.

## Install

Requirements: Windows 10 or 11, the Steam version of the game, `git` and `winget`. The script installs the .NET 9 SDK and the x86 .NET 9 Desktop Runtime if they are missing.

1. Download [Improved Fast Forward](https://www.nexusmods.com/finalfantasytacticstheivalicechronicles/mods/192) and [AntiAutoRotate](https://www.nexusmods.com/finalfantasytacticstheivalicechronicles/mods/173) from Nexus Mods and leave the zip files in one folder. Both are optional.
2. Run:

```powershell
git clone https://github.com/arthamegayasa/ivalice-master-mode
cd ivalice-master-mode
.\scripts\install.ps1 -ArchiveDir "$env:USERPROFILE\Downloads"
```

3. Open **Ivalice Control Center** from the desktop shortcut and press **Main**.

The script is safe to run again; it skips what is already installed. Useful parameters: `-ReloadedDir` (where Reloaded-II goes, default `D:\Modding\Reloaded-II`), `-GameDir` (if the game is not found through Steam), `-Force` (reinstall mods), `-NoShortcut`.

To remove everything, delete the Reloaded-II folder, `%APPDATA%\Reloaded-Mod-Loader-II`, and the desktop shortcut. The game folder itself is only given a `steam_appid.txt`.

## Roadmap

- **v0.2: Smart Auto Battle.** A mod that switches auto battle on when a fight starts and picks actions by role: healers heal allies below a health threshold, attackers attack. This needs reverse engineering of the game's battle code and is not built yet.

See [CHANGELOG.md](CHANGELOG.md) for what has shipped.

## Adding a mod to the catalog

Edit [`catalog/mods.json`](catalog/mods.json). Each entry gives the Reloaded-II `ModId`, the text shown in the app, what the mod requires, and its settings. A setting's `key` must match the property name in the mod's own `Config.json`; `type` is `bool`, `integer` or `number`, and numeric settings take `min`, `max` and `step`.

## Development

```powershell
dotnet test tests/Ivalice.Core.Tests
dotnet build src/Ivalice.ControlCenter -c Release
```

- `src/Ivalice.Core`: reads and changes a Reloaded-II profile. All behaviour lives here and is covered by tests.
- `src/Ivalice.ControlCenter`: a WPF window hosting the HTML interface in `ui/` through WebView2.
- `catalog/mods.json`: the curated mod descriptions.

## Credits and licence

The code in this repository is MIT licensed. Each mod belongs to its author and keeps its own licence. The banner and icon were generated with OpenAI's image model for this project. This is a fan project and is not affiliated with Square Enix.
