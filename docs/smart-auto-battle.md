# Smart Auto Battle: how it works

Smart Auto Battle does not contain its own battle AI. It switches the player's units to the auto-battle feature the game already has, and picks which auto-battle mode each unit uses.

Everything below was read from `FFT_enhanced.exe` version 1.00 (SHA-256 `937233F7FE76182A665C487C8802F5CEC6662DDD09967E87CD09FB146FC6B5D5`) by static disassembly. Addresses are relative to the image base (RVAs).

## What the mod does

On every step of a unit's turn the game calls a small function that decides whether the unit is driven by the player or by the AI. The mod hooks that function and, just before it runs:

1. Ignores the unit unless it belongs to the player's team and is one the player gives orders to. Guests and enemies are never touched.
2. Ignores the unit if the player picked a mode other than Fight for Life or Save Fading Life in the in-game Auto-battle menu. Those choices are kept.
3. Writes **Save Fading Life**, aimed at the ally most in need, when the unit has a healing skillset and an ally is knocked out or below the heal threshold.
4. Otherwise writes **Fight for Life**, aimed at a living enemy.

When the mod is toggled off, it clears the modes it wrote, one unit at a time as their turns come up.

## Evidence

| What | Where | Observed |
|---|---|---|
| Battle unit table | RVA `0x1853CE0`, 21 records of `0x200` bytes | `30c8a6 lea r15,[rip+0x1547433]`, `30c8b2 shl rdi,9`, `30c8b6 cmp byte [rdi+r15+1],0xff` |
| Slot in use | unit `+0x01`, `0xFF` when empty | same lines |
| Flags (team `0x30`, player control `0x08`) | unit `+0x05` current, `+0x1EE` permanent | `30c973 mov al,[rcx+0x1ee]; and al,8; or [rcx+5],al` |
| Skillsets | unit `+0x12`, `+0x13` | `316b38 mov al,[unit+0x12]`, `316c1e movzx edx,[unit+0x13]` |
| HP, max HP | unit `+0x30`, `+0x32` (16-bit) | `31f888 movzx eax,word [rcx+0x30]; movzx ecx,word [rcx+0x32]` |
| Statuses | unit `+0x61..`; KO is bit `0x20` of the first byte | `38d637 test byte [rax+r9+0x61],dl` |
| Auto-battle mode and target | unit `+0x1EC`, `+0x1ED` | written by the menu at `212341 mov [rcx],r8b` and `212347 mov [rdx],al` |
| Control refresh (the hook) | RVA `0x202800` | `mov rax,[rcx+0x148]; mov dl,[rax+5]; mov [rcx+0x154],dl; cmp byte [rax+0x1ec],0; je +9; and dl,0xf7` |
| AI dispatch on mode | RVA `0x38C95C` | `0x0C`, `0x0E`, `0x10`, `0x11` branch to four routines; anything else is the plain AI |
| Mode reset | unit creation | `275bf2 mov word [rdi+0x1ec],ax` with `ax = 0` |

The hook is found with the pattern `48 83 EC 28 48 8B 81 48 01 00 00 8A 50 05`, which matches exactly once in the code section. The table address is taken from a `lea` inside a function found by a second pattern (also one match), so neither address is hard-coded.

Mode `0x10` first searches for an ally who is knocked out, critical, or below half HP. When it finds none, it falls back to the target slot stored in the unit, which is why the mod only hands out this mode together with a valid target, and why the heal threshold is capped at 50%.

## Inferred, not observed

- The five menu rows map to mode values `0x0A`, `0x0C`, `0x0E`, `0x10`, `0x11`. The names Manual, Fight for Life, Protect Allies, Save Fading Life and Run Like a Rabbit are assumed from the original game's menu order.
- Skillset ids (Items 6, Martial Arts 9, White Magicks 10) come from the mod loader's `JobCommandData.xml`.
- Crystal and treasure status bits follow the original game's status order.

## Not yet verified in play

Nothing here has been tested inside a battle. Still to confirm:

- units act on their own from the first turn, with no menu;
- a healer heals or revives when an ally drops below the threshold;
- toggling off returns control on each unit's next turn;
- story battles with guests and special win conditions still behave;
- a suspend save made during an auto battle loads correctly.

Until then, save before trying it and toggle it off with F8 or L3 + R3 if a battle goes wrong.

## Prior work

Two community projects were used as leads and re-checked against the executable: [skeewirt/fft-tic-reference](https://github.com/skeewirt/fft-tic-reference) for function and field names, and [dicene/fftivc.unitcontrol](https://github.com/dicene/fftivc.unitcontrol), a mod that hands units to the plain AI by clearing the control bit, for the unit table locator pattern. No code was copied from either.
