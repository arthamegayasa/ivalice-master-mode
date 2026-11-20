# Patches

These patches change two third-party mods when the install script builds them from source. Each patch is a derivative of the mod it applies to and is licensed under that mod's licence, **GPL-3.0**, not under this repository's MIT licence.

| Patch | Applies to | Commit | What it does |
|---|---|---|---|
| `xp-multiplier-player-units-only.patch` | [knightsong/xp-multiplier](https://github.com/knightsong/xp-multiplier) | `fede4e0` | Multiplies XP only for units on the player's side; adds a "Player units only" setting |
| `global-jp-boost-player-units-only.patch` | [knightsong/global-jp-boost](https://github.com/knightsong/global-jp-boost) | `e60aaf1` | Multiplies JP only for units on the player's side; adds a "Player units only" setting |

Both hooked game functions receive the battle unit's slot number. The patch reads that unit's team bits (byte `0x1EE` of its record, mask `0x30`, zero for the player's side) from the battle unit table, whose address it takes from the hooked function's own code.
