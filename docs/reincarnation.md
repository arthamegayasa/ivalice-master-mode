# Reincarnation: how it works

Reincarnation lets a party member give up its levels in exchange for better starting stats. The character goes back to level 1, and the stats it restarts from are raised by its **Power**, which grows with every reincarnation.

Everything about the game below was read from `FFT_enhanced.exe` version 1.00 (SHA-256 `937233F7FE76182A665C487C8802F5CEC6662DDD09967E87CD09FB146FC6B5D5`) by static disassembly. Addresses are relative to the image base (RVAs).

**Status: run in the game on one save.** Loading, the roster, the battle check, one real reincarnation, its presence in the next autosave and the Control Center tab were confirmed in play on 2026-11-21 (see "Confirmed in play"). A battle fought by a reincarnated unit and a second reincarnation of the same unit have not been played. **Safe mode** still lets you see what a command would do without changing the game.

## The rules

- Reincarnating sets the level to 1 and experience to 0, and sets the five raw stats (HP, MP, Speed, Physical Attack, Magick Attack) to the average starting stats of a new unit of that kind, multiplied by 1 + Power.
- Power is kept per character. Each reincarnation adds 0.5% for every level the character had: level 10 adds 5%, level 99 adds 49.5%. Power adds up over reincarnations and stops at +100%.
- The character must be at least level 10, in the party (slots 0 to 49), not a monster and not away on an errand. Guests (slots 50 to 53) and monsters are listed with the reason they cannot.
- Job, JP, learned abilities, equipment, Brave, Faith and every other byte of the record stay as they are.
- It is refused while a battle or story scene is running, and while the game still holds a battle copy of the unit that it would write back over the change.

"Average starting stats" is the middle of the roll the game makes for a new unit. The game's starting rows (RVA `0x67F7D0`, spans at `0x67F800`) give a base and a span per stat, in whole stat points:

| Kind | HP | MP | Speed | PA | MA |
|---|---|---|---|---|---|
| Man | 30 + up to 2 | 14 + up to 1 | 6 | 5 | 4 |
| Woman | 28 + up to 2 | 15 + up to 1 | 6 | 4 | 5 |
| Ramza | 30 + up to 2 | 15 + up to 1 | 6 | 5 | 5 |

A raw stat is a 24-bit number in which 16384 is one point, so the baseline is `(base << 14) + (span << 13)`, and the new raw stat is `baseline * (1000 + power in permille) / 1000`, rounded down. A level 1 woman in the owner's save has HP 28.27, MP 15.11, Speed 6, PA 4, MA 5: inside the roll.

What the game shows for a stat is `raw * M / 100`, shifted right 14 bits, where `M` is the multiplier of the unit's current job, never below 1 and capped at 999 (HP, MP), 50 (Speed) and 99 (PA, MA). The Control Center shows these numbers **before equipment**, so they can be lower than the game's status screen.

When Stat Tuning is installed and enabled, the Control Center applies the saved job rule and then the character rule to the current job's multipliers. It shows the current raw stats and the planned level-1 raw stats with those multipliers, both before equipment. A note appears when the roster includes tuned values. Without applicable rules or catalog data, the tab shows the feed values. It always keeps the feed values for job `0xA1` because the feed omits the job level that selects its upgraded row `0xA4`.

Reincarnation does not change how levels are gained: a level-up still adds `raw / (C + level)` to each raw stat, with `C` the job's growth constant. Because the gain is a share of the current value, a higher start carries through every later level.

## How the pieces fit

```
Ivalice Control Center                         the game (FFT_enhanced.exe)
  tab "Reinkarnasi"                              mod ivalice.party.reincarnation
        |   reads  bridge\roster.json   <------------  written about once a second
        |   writes bridge\command.json  ------------>  read on the game's main thread
        |   reads  bridge\result-<id>.json  <--------  one answer per command
        v
  save-backups\<time>\  (copy of every save .png, made before a command is sent)
                                                 reincarnation-journal.jsonl (record before and after)
```

All files live under `%LOCALAPPDATA%\IvaliceControlCenter`.

- `src/Ivalice.Party` holds every rule as pure functions over bytes: reading a unit, the plan, applying it, the stamp, the battle check, the roster export and the command processor. Both the mod and the Control Center use it, so the preview in the interface and the change in the game come from the same code.
- `mods/ivalice.party.reincarnation` finds the game's data by byte pattern, hooks the entry of the game's per-frame routine, and calls `BridgeWorker` there about four times a second. `BridgeWorker` does the file exchange.
- `src/Ivalice.ControlCenter` shows the roster, backs up the saves and sends commands.

### The moment the mod acts

The game's per-frame task runner (RVA `0x2574E0`) runs on the main thread, and the game logic it dispatches runs after its `call 0x1e8ac4` at `0x257502`. The mod hooks the first instruction of that function, so it runs once per frame before any game logic of that frame. The first five bytes are one instruction, `mov [rsp+8], rbx`, which does not depend on its address; the hook replaces exactly that. Smart Auto Battle's hook sits at `0x257507`, 39 bytes further, and neither touches the other's bytes. Improved Fast Forward uses the same function only to read a pointer from the instruction at `0x2574F8`.

The hook is an assembly stub, not a function hook, for the reason given in [Smart Auto Battle: why the hook saves every register](smart-auto-battle.md#why-the-hook-saves-every-register): it pushes `rax`, `rcx`, `rdx` and `r8` to `r11`, saves `xmm0` to `xmm5`, calls into the mod with the stack 16-byte aligned, and restores everything. On load the mod puts the very same stub on a throwaway function that does nothing, calls it with marker values in all thirteen registers, and installs the game hook only if every marker survived. The Reloaded-II log shows `Self-test passed` or `Self-test FAILED`.

Inside the hook the mod works at most every 250 ms: it looks for a command, and at most once a second it rebuilds the roster file. Nothing in the callback may throw; every error is caught and logged once.

### "A battle is in progress"

The roster must not be changed while battle units exist, because the game copies them back into the roster (see the evidence table). The mod decides from one thing:

- **The battle controller object exists.** The battle scene routine at RVA `0x210AF0` creates it as its first call (`210b2a call 0x14020ef10`, which stores the pointer at RVA `0x3CD9EF0`) and destroys it at its end (`213e50 call 0x14020f328`, which clears the pointer). The routine loops in between, so the pointer is non-null from the start of a battle or story scene to its end. No other code writes a non-null value there.

As a cross-check the mod also treats "a unit is taking its turn" as a battle. It exports four more readings for diagnosis without using them: how many battle records are in use, how many are still tied to a roster slot, the battle mode variable (RVA `0x1853C08`, reported as 0 on the world map by the community reference) and the battle state word.

There is a second, per-unit check. At the start of every scene the game walks the roster and, for each slot, looks for a battle record built from it (`2c6199`: same roster slot in byte `+0x02`, byte `+0x1B5` not `0xFF`, byte `+0x191` not zero); if it finds one, it writes that battle unit's level, experience and raw stats back into the roster record. The battle routine resets records at its start only by marking them unused (`+0x01 = 0xFF`) and benched (`+0x1B5 = 0`), which that test still accepts. The mod therefore refuses to reincarnate a unit for which such a record exists, whatever the battle controller says. If stale records do stay behind after a battle, units that just fought will show "the game still holds a battle copy"; that is the mod being careful, and it is the first thing to look at in play.

### The stamp

Reincarnation count and Power are kept in the roster record itself, in the first 20 of its 36 spare bytes (`+0x234` to `+0x247`):

| Offset in stamp | Size | Content |
|---|---|---|
| 0 | 4 | `IVRC` |
| 4 | 1 | version, 1 |
| 5 | 1 | zero |
| 6 | 2 | reincarnation count |
| 8 | 2 | Power in permille (0 to 1000) |
| 10 | 2 | zero |
| 12 | 4 | fingerprint of the unit |
| 16 | 4 | FNV-1a checksum of bytes 0 to 15 |

The game saves and loads all 600 bytes of a record, so the stamp travels with the save. It does not clear those bytes when a slot is freed, so a new recruit could find an old stamp in its slot. The fingerprint prevents that: it is a hash of the unit's marker, sex bits, the two birthday bytes and the name key. A stamp whose magic, checksum or fingerprint does not match counts as "never reincarnated". For Ramza the marker (1 to 3, by chapter) and the name key are left out, so his stamp survives a chapter change. Job, level, stats and the slot number are not part of the fingerprint.

Spare bytes that are neither zero nor this project's stamp are left alone, and the unit is refused (`spareBytesInUse`). The other 16 spare bytes are never written.

### Names

The game's name lookup at RVA `0x1E8B7C` is `const char *GetDisplayName(int nameKey, const char *ownName)`: it finds the name table (caching the pointer), looks the key up and returns the row's text; with a non-empty `ownName` and a row that allows it, it copies that string through a larger routine instead. The mod calls it with `ownName` null, which keeps it to table lookups, and checks the returned pointer with `VirtualQuery` before reading at most 64 bytes of UTF-8. A name stored in the record itself (`+0xDC`, empty in the owner's save) is used first when present. The setting "Show unit names" turns the call off; units are then listed by job and slot.

## The file protocol

`bridge\roster.json` (written by the mod; to a temporary file first, then renamed; skipped while nothing changed, but rewritten at least every 5 seconds):

```json
{
  "schema": 1, "modVersion": "0.4.0", "processId": 4242,
  "writtenAt": "2026-11-20T15:00:00.000Z", "safeMode": false,
  "game": {
    "inBattle": false, "decidedBy": "battleControlAbsent",
    "inputs": { "battleControlPresent": false, "activeActorPresent": false, "battleUnitsInUse": 0,
                "battleUnitsBound": 0, "battleModeFlag": 0, "battleState": 12 }
  },
  "units": [ {
    "slot": 1, "name": null, "jobId": 74, "jobName": "Squire", "kind": "male",
    "level": 30, "experience": 42, "brave": 60, "faith": 55,
    "raw": { "hp": 507904, "mp": 237568, "speed": 98304, "physicalAttack": 81920, "magickAttack": 65536 },
    "shown": { "hp": 31, "mp": 10, "speed": 6, "physicalAttack": 4, "magickAttack": 3 },
    "reincarnations": 0, "powerPermille": 0, "eligible": true, "reason": "eligible",
    "fingerprint": "5A0C17E2", "onErrand": false, "battleSlot": null,
    "next": { "gainPermille": 150, "totalPermille": 150, "raw": { }, "shown": { } }
  } ]
}
```

`decidedBy` is `battleControlPresent`, `activeActorPresent` or `battleControlAbsent`. `reason` is `eligible` or one of: `emptySlot`, `guest`, `monster`, `unknownKind`, `onErrand`, `levelTooLow`, `spareBytesInUse`, `inBattle`, `boundToBattleUnit`, `gameStateUnknown`. `battleSlot` is the battle record tied to the unit, if any. `next` is what a reincarnation would give at the unit's current level; it is null for guests and monsters.

`bridge\command.json` (written by the Control Center, or by hand):

```json
{ "id": "0f8fad5b-d9cb-469f-a165-70867728950e", "action": "reincarnate", "slot": 1,
  "expectedFingerprint": "5A0C17E2", "expectedLevel": 30, "createdAt": "2026-11-20T15:00:02.000Z" }
```

`id` must be a GUID. `expectedLevel` and `createdAt` are optional; without `createdAt` the file's own time is used. A command more than 120 seconds old is refused, and a command left over from an earlier game session is deleted when the mod starts.

`bridge\result-<id>.json` (written by the mod, then `command.json` is deleted):

```json
{ "id": "0f8fad5b-…", "ok": true, "applied": true, "safeMode": false, "reason": "eligible",
  "message": "Reincarnated: level 30 to 1, Power +15.0% (total 15.0%).",
  "unit": { "slot": 1, "level": 1, "reincarnations": 1, "powerPermille": 150 },
  "finishedAt": "2026-11-20T15:00:02.250Z" }
```

`ok` means the command passed every check; `applied` means game memory was changed. In safe mode `ok` is true, `applied` is false and `unit` is what the unit would have become. Further refusal reasons for commands: `badSlot`, `fingerprintMismatch`, `levelChanged`, `expired`, `malformed`, `unknownAction`, `journalFailed`. A command without a usable id is answered in `bridge\result-invalid.json`.

`reincarnation-journal.jsonl` gets one line per applied reincarnation, written **before** the game is touched: time, command id, slot, fingerprint, level before, Power before and after, and the whole 600-byte record before and after as hex. If the line cannot be written, nothing is changed.

Before the Control Center sends a command it copies every `.png` under `Documents\My Games\FINAL FANTASY TACTICS - The Ivalice Chronicles` to `save-backups\<yyyyMMdd-HHmmss>\`, keeping the sub-folders, and keeps the 20 newest backups. A reincarnation only changes the running game; it becomes permanent when the game saves, by a manual save or an autosave. Until then, loading an older save undoes it.

## Undoing a reincarnation

- Not saved yet: load a save in the game.
- Saved: close the game and copy the files of the matching folder in `save-backups` back over the save folder.
- By hand: the journal line holds the record as it was. Writing those 600 bytes back at `roster + slot * 0x258` restores the unit exactly, stamp included.

One caveat when undoing by hand: if the unit still has a battle copy left over from a fight, that copy was given the new level, experience and raw stats too, and the game may write it back over the roster later. Restoring only the roster record is then not enough. The reliable way to undo is to load a save from before the reincarnation; the Control Center backs the save files up before every command.

A command is carried out at most once per game session (the mod remembers the ids it answered), and once the mod has taken a command the Control Center can no longer withdraw it. If the answer never arrives, the app says so instead of claiming nothing changed.

## Evidence

| What | Where | Observed |
|---|---|---|
| Party roster | RVA `0x11A7D10`, 54 records of `0x258` bytes | `2751ef lea rcx,[rip+…]` in the free-slot finder, found by pattern |
| Slot in use | record `+0x01`, own slot number, `0xFF` when free | `274f85 mov [r14+1],bpl`; all 9 records of the owner's save carry their slot number |
| Level, experience, raw stats | record `+0x1D`, `+0x1C`, `+0x20..+0x2E` (five 24-bit values) | level-up routine `278b54 movzx ecx,[rcx+0x1d]`, `278b8f lea r11,[r9+0x22]` |
| Level-up formula | RVA `0x278B24` | `div ecx` with `ecx = C + level`, `add r8d,eax`, capped at `0xFFFFFF` |
| Job table, growth pairs | RVA `0x785E30`, rows of `0x31`, pairs at `+0x0E` | `278b5d imul rbx,rax,0x31; lea rax,[rip+0x50d2d6]` (the address of `+0x0E`) |
| Job `0xA1` uses row `0xA4` at job level 8 | same routine | `278b39 cmp byte [rcx+2],0xa1 … mov al,[rcx+0x7e]; and al,0xf; cmp al,8; mov eax,0xa4` |
| Shown stat | RVA `0x276EBC` | `imul eax,r8d; div ecx` (100); `shr edx,0xe`; caps `0x3e7`, `0x32`, `0x63`; minimum 1 |
| Starting rows | RVA `0x67F7D0`, `0x67F800` | bytes `1e 0e 06 05 04`, `1c 0f 06 04 05`, `1e 0f 06 05 05`; spans `02 01 00 00 00` |
| Battle to roster write-back | RVA `0x274EB8` | writes `+0x1C`, `+0x1D`, `+0x20..+0x2E` from the battle unit (`27502b`..`2750af`), the name key (`2751a2`), and nothing at or after `+0x234` |
| Write-back at scene start | RVA `0x2C60E4`, first call of the scene set-up at `0x2C8818` | loop over the 54 roster slots; match at `2c6199`..`2c61a8`; `2c6215 call 0x140274eb8` |
| Battle records reset at battle start | RVA `0x210C25` | `mov byte [rax+rcx+0x1853ce1],0xff; mov byte [rax+rcx+0x1853e95],0` for 21 records |
| Battle controller pointer | RVA `0x3CD9EF0` | created `20ef7f mov [rip+…],rax`, cleared `20f38d and qword [rip+…],0`; calls at `210b2a` and `213e50` |
| Menu cache write-back | RVA `0x285744` | stores to `+0x02`, `+0x04`, `+0x07..+0x1B`, `+0x32..+0xDB`, `+0x122..+0x22D`; none to `+0x1C..+0x2F` or `+0x234..` |
| Per-frame routine | RVA `0x2574E0` | `mov [rsp+8],rbx; mov [rsp+0x10],rbp; mov [rsp+0x18],rsi; push rdi; push r14; push r15; sub rsp,0x20` |
| Name lookup | RVA `0x1E8B7C` | `mov rdi,rdx; mov ebx,ecx`; table cache; `test ebx,ebx; je` (returns 0); returns `row + 8 + [row + 8]` |

Byte patterns the mod uses, each checked against the code section of the executable:

| Pattern for | Matches | At |
|---|---|---|
| Roster (free-slot finder) | 1 | `0x2751D0` |
| Battle controller creation | 1 | `0x20EF29` |
| Battle unit table (shared with Smart Auto Battle) | 1 | `0x30C87C` |
| Job table (level-up routine) | 1 | `0x278B54` |
| Per-frame routine entry | 1 | `0x2574E0` |
| Name lookup (optional) | 1 | `0x1E8B7C` |
| Acting unit (optional) | 1 | `0x2607C0` |
| Battle state word (optional) | 1 | `0x211878` |
| Battle mode variable (optional) | 1 | `0x210E65` |

When one of the first five is missing the mod logs which and stays inactive. Each address taken from a pattern is also checked to lie inside the game's own image.

## What has been checked

Without the game:

- 184 tests of `Ivalice.Party` and the mod's game-independent parts, including records copied byte for byte from the owner's save: the rules, the stamp, eligibility, the battle checks, the export, the command processor, and `BridgeWorker` against a temporary folder (unchanged roster skipped, heartbeat, half-written and malformed commands, safe mode, journal failure, a roster file held open by another program).
- The hook stub is assembled and installed by Reloaded.Hooks (the library the game loads) on a throwaway native function, called with markers in `rax`, `rcx`, `rdx`, `r8` to `r11` and `xmm0` to `xmm5`, with real managed code as the callback: every register comes back unchanged. The same test with a stub that calls managed code without saving registers fails, as does one that forgets a single register.
- The register probe itself is run against thirteen native functions that each overwrite one register, and reports exactly that register.
- Breaking a rule on purpose makes the tests fail: eleven such changes were tried (battle check removed, fingerprint ignored, game written before the journal, safe mode writing, and others) and each was caught.
- The interface was rendered in a browser with sample data produced by `Ivalice.Party`.

## Confirmed in play

- Loaded in the game with the other mods: `Self-test passed`, all patterns resolved, roster at image base + `0x11A7D10`. The frame routine only starts running once the game is past its version-select screen.
- The roster matched the game: names (Ramza, Ealdwine, Patrick, Radcliffe, Giselle, Melusine, Wulfhilda and the guests Delita and Argath), jobs and levels were the same as on the game's Units screen.
- "Battle in progress" read `false` at the title menu and on the world map, and `true` from the deployment screen through a battle.
- A real reincarnation on the world map (Giselle, level 4, with the minimum level set to 2 for the test): the game's Units screen showed her at level 1 with 0 experience at once, MP 11 as predicted, and JP and job level unchanged.
- The game's next autosave, decoded from a copy, held her record at level 1 with the stamp in the spare bytes.
- From the Control Center: the tab listed the party from the running game, showed the in-battle refusal during deployment, and on the world map the Reinkarnasi button backed up the saves, sent the command and showed the result (Ealdwine, Power +1.5%) while the game window was not focused.

After a battle the game does leave battle copies of the units that fought (`battleUnitsBound` was 4 on the world map, each unit with a `battleSlot`). They no longer block a reincarnation: the mod writes the new level, experience and raw stats into the copy as well (`BattleBinding.Mirror`), so a write-back by the game changes nothing.

## Still to confirm in the running game

The table below is the original checklist; the rows about loading, the roster, names, the battle check, the unfocused window, the refusal in battle and the visible change are confirmed above.

Turn on "Log diagnostik" for the mod first. Each line is one open point and its cheapest check.

| Open point | Cheapest check |
|---|---|
| The mod loads and its patterns resolve | The Reloaded-II log has `[Reincarnation] Self-test passed` and `Ready: roster at 0x…`, with the roster at image base + `0x11A7D10` |
| The game runs normally with the hook | Title screen, load a save, walk the world map and fight one battle with the mod enabled, with and without Improved Fast Forward |
| The roster is read correctly | After loading a save, the Reinkarnasi tab lists the same characters, levels and jobs as the game's party list |
| "Battle in progress" is right | `game.inBattle` in `roster.json` is `false` on the world map and in the party menu, `true` from the deployment screen to the end of a battle, `true` in a story scene |
| What `battleModeFlag` and `activeActorPresent` read in each of those places | Same file, `game.inputs` |
| Stale battle records after a battle | Back on the world map after a battle: `game.inputs.battleUnitsBound` and each unit's `battleSlot`. Non-zero means the per-unit check will refuse units that fought |
| The game keeps running its frame routine when its window is not focused | With the Control Center in front, `writtenAt` in `roster.json` keeps advancing. If not, commands wait until the game is focused again and expire after two minutes |
| Names | The tab shows the names from the game's party list. If the game misbehaves, turn "Tampilkan nama karakter" off |
| A command is refused in a battle | In safe mode, send a command during a battle: the answer is `inBattle` |
| What a command would do | In safe mode on the world map, send a command for a unit of level 10 or more: the answer has `ok: true`, `applied: false` and the unit at level 1 |
| The change shows in the game | Safe mode off, reincarnate one unit on the world map, open its status screen: level 1, and HP / MP as predicted plus equipment |
| The party menu shows the change without a reload | Same step; if the menu still shows the old level, leave and re-enter the menu or the world map and look again |
| The change survives a battle | Fight one battle with the reincarnated unit: afterwards it has the levels it gained from 1, not its old level |
| The stamp survives save and load | Save, quit to the title, load: the tab still shows the count and Power |
| The stat formula matches the game | A unit with no equipment shows the same HP, MP, Speed, PA and MA in the tab and in the game |

## Limits to know about

- The numbers in the tab are before equipment.
- A character at +100% Power can still reincarnate; it gains nothing more and restarts at level 1.
- Named characters other than Ramza restart from the generic man's or woman's row, chosen by their sex flag. Whether the game rolled them from that row when they joined was not checked.
- A guest who later joins the party is written into a party slot by the game field by field, without the spare bytes, so it starts without a stamp, as it should.
- The fingerprint of two different recruits can collide only if marker, sex, birthday and name key all match.
- The mod writes game memory in one copy of 600 bytes on the game's main thread. A save that the game might write from another thread at that very moment has not been ruled out.

## Prior work

The community reference [skeewirt/fft-tic-reference](https://github.com/skeewirt/fft-tic-reference) was used for leads (the save record's field names, the battle controller object, the name lookup's prototype); every address and offset this mod relies on was re-read in the executable. Job names come from the comments in [FFT Mod Loader](https://github.com/Nenkai/fftivc.utility.modloader)'s `JobData.xml`.
