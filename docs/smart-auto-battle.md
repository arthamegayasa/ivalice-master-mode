# Smart Auto Battle: how it works

Smart Auto Battle does not contain its own battle AI. It switches the player's units to the auto-battle feature the game already has, and picks which auto-battle mode each unit uses.

Everything below was read from `FFT_enhanced.exe` version 1.00 (SHA-256 `937233F7FE76182A665C487C8802F5CEC6662DDD09967E87CD09FB146FC6B5D5`) by static disassembly. Addresses are relative to the image base (RVAs).

**Nothing here has been tested inside a battle yet.** Save before trying it, try a random battle before a story battle, and press F8 or L3 + R3 to switch it off if a fight goes wrong.

## What the mod does

On every step of a unit's turn the game calls a small function that decides whether the unit is driven by the player or by the AI. The mod hooks that function and, just before it runs, looks at the acting unit:

1. It leaves the unit alone unless the unit is on the player's team and is one the player gives orders to. Guests and enemies are never touched.
2. It leaves the unit alone if its auto-battle mode is one the mod never hands out (Protect Allies or Run Like a Rabbit, picked by the player in the in-game menu).
3. It writes **Save Fading Life**, aimed at the ally most in need, when the unit has a healing skillset and an ally is knocked out or below the heal threshold. A knocked-out ally comes first, then the ally with the lowest share of HP.
4. Otherwise it writes **Fight for Life**. The unit keeps the enemy it is already aimed at; a new one is picked only when that enemy is down, petrified or gone.

Switching the mod off (F8 or L3 + R3) clears every mode it wrote, on the next turn step. The on/off state lasts until it is toggled again or the game is closed; it is not reset for each battle.

## Why the hook saves every register

The hooked function is small and only writes `rax`, `rcx` and `rdx`. The game's compiler knew that, so callers keep live values in other volatile registers across the call. At RVA `0x20288C` to `0x20289F` the game does `mov r8, rax`, calls the hooked function, then `mov rcx, r8` and calls the next function with it. A normal function hook runs managed code, which is free to overwrite `r8` to `r11` and `xmm0` to `xmm5`; version 0.2.0 did exactly that and the game crashed at RVA `0x2026F6` on the first battle.

The hook is therefore an assembly stub that pushes `rax`, `rcx`, `rdx` and `r8` to `r11`, saves `xmm0` to `xmm5`, calls into the mod, and restores everything before the game's own instructions run. On load the mod tests this inside the game: it calls the hooked function with marker values in `r8` to `r11` and a throwaway object, and removes the hook if any marker is lost.

## Limits to know about

- While the mod is on, choosing Manual in the in-game Auto-battle menu does not stick: the game stores Manual as "no mode", and the mod fills that in again. Use the toggle to take control back.
- The mod cannot tell a Fight for Life or Save Fading Life the player picked from one it wrote, so it manages both and clears both when switched off.
- A healer does not count itself as someone to save.
- A unit is a healer because of its skillset, not its learned abilities. A healer with no way to revive still stops fighting for a knocked-out ally unless "Healers revive fallen allies" is turned off.
- The heal threshold cannot go above 50%, because the game's own search for someone to save only reacts below half HP.
- Any unit whose team bits are non-zero is treated as an enemy. Battles with a third, friendly team have not been examined.
- Auto-battle modes are part of the battle state. A suspend save made while units are on auto keeps them on auto when loaded, with or without the mod; the in-game menu can change them.

## Evidence

| What | Where | Observed |
|---|---|---|
| Battle unit table | RVA `0x1853CE0`, 21 records of `0x200` bytes | `30c8a6 lea r15,[rip+0x1547433]`, `30c8b2 shl rdi,9`, `30c8b6 cmp byte [rdi+r15+1],0xff` |
| Slot in use | unit `+0x01`, `0xFF` when empty | same lines |
| Flags (team `0x30`, player control `0x08`) | unit `+0x05` current, `+0x1EE` permanent | `30c973 mov al,[rcx+0x1ee]; and al,8; or [rcx+5],al` |
| Skillsets | unit `+0x12`, `+0x13` | `316b38 mov al,[unit+0x12]`, `316c1e movzx edx,[unit+0x13]` |
| HP, max HP | unit `+0x30`, `+0x32` (16-bit) | `31f888 movzx eax,word [rcx+0x30]; movzx ecx,word [rcx+0x32]` |
| Statuses | unit `+0x61..`, one bit per status | `38d637 test byte [rax+r9+0x61],dl` |
| Auto-battle mode and target | unit `+0x1EC`, `+0x1ED` | written by the menu at `212341 mov [rcx],r8b` and `212347 mov [rdx],al` |
| Control refresh (the hook) | RVA `0x202800` | `mov rax,[rcx+0x148]; mov dl,[rax+5]; mov [rcx+0x154],dl; cmp byte [rax+0x1ec],0; je +9; and dl,0xf7` |
| Any non-zero mode means AI-driven | same function | the `cmp byte [rax+0x1ec],0` above clears control bit `0x08` for every non-zero value |
| AI dispatch on mode | RVA `0x38C95C` | `0x0C`, `0x0E`, `0x10`, `0x11` branch to four routines; anything else is the plain AI |
| Mode reset | unit creation | `275bf2 mov word [rdi+0x1ec],ax` with `ax = 0` |

The hook is found with the pattern `48 83 EC 28 48 8B 81 48 01 00 00 8A 50 05`, which matches exactly once in the code section. The table address is taken from a `lea` inside a function found by a second pattern (also one match), so neither address is hard-coded.

Mode `0x10` first searches for an ally who is knocked out, critical, or below half HP. When it finds none, it falls back to the target slot stored in the unit, which is why the mod only hands out this mode together with a valid target.

## Inferred, not observed

- Mode values `0x0C`, `0x0E`, `0x10` and `0x11` are named Fight for Life, Protect Allies, Save Fading Life and Run Like a Rabbit after the original game's menu order. The labels were not seen next to the values.
- Which status bit is which: knocked out is taken as status 2 (bit `0x20` of the first status byte), crystal as status 1, petrify as status 8 and treasure as status 15, following the original game's status order and the status numbers the AI passes around `38d4c5` (`mov edx,2` for the knocked-out check, `mov edx,8` for petrify).
- Skillset ids (Items 6, Martial Arts 9, White Magicks 10) come from the mod loader's `JobCommandData.xml`.
- That the game re-aims a fighting unit by itself. The mod does not depend on it: it re-aims whenever the stored target is no longer a living enemy.

## To confirm in play

- units act on their own from the first turn, with no menu;
- a healer heals or revives when an ally drops below the threshold;
- toggling off returns control on each unit's next turn;
- story battles with guests and special win conditions still behave;
- a suspend save made during an auto battle loads correctly.

## Prior work

Two community projects were used as leads and re-checked against the executable: [skeewirt/fft-tic-reference](https://github.com/skeewirt/fft-tic-reference) for function and field names, and [dicene/fftivc.unitcontrol](https://github.com/dicene/fftivc.unitcontrol), a mod that hands units to the plain AI by clearing the control bit. The byte signature this mod uses to locate the unit table is the same one that mod uses; the rest of the code was written for this project.
