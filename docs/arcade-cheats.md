# Arcade cheats

Room creators pick cheats on the lobby card, next to the version dropdown. The cheats apply to that room
only, for its whole life, and nothing about them is shared with other rooms or persisted to a save.

## The one thing to understand

There is no single "cheat" mechanism in libretro. There are two, they are unrelated, and the difference is
what the whole design is shaped around:

| | **Cheat codes** | **Core-option cheats** |
|---|---|---|
| Delivered by | `retro_cheat_set(index, enabled, code)` | a libretro core option, set before load |
| What it is | a raw memory poke ("`80165FBD 0001`") | a patch the **emulator already ships** |
| Source | `libretro-database/cht`, per ROM | the core's own compiled-in table |
| Stored as | `ArcadeCheat` rows, `Kind="code"` | `ArcadeCheat` rows, `Kind="option"`, or `ArcadeCheatCatalog` |
| Fails how? | **silently** | silently |

Both fail silently, and that is the hazard. **Every libretro core exports `retro_cheat_set`** — the API is
mandatory — but a large number implement it as an empty function body, because they read their own cheat
format from disk instead (pcsx2 → `.pnach`, Dolphin → Gecko/ActionReplay INIs, PPSSPP → a cwcheat database).
Nothing you can observe at runtime distinguishes "code applied" from "code accepted and thrown away".
Likewise, libretro **ignores an unrecognized option value** without complaint, so `pcsx2_widescreen_hint =
enabled` does nothing at all: the core declares its values as `enabled (16:9)`, `enabled (16:10)`, and so on.

So the *site* decides what may be offered, from an explicit allowlist, rather than offering everything and
hoping. That is `ArcadeCheatCatalog`.

## What each system gets

**Cheat codes** (`ArcadeCheatCatalog.SupportsCheatCodes`) — nes, fds, snes, genesis, sms, gg, segacd,
sega32x, gb, gbc, gba, n64, ps1. Each has both a core whose `retro_cheat_set` is a *real implementation*
(see "Is this core's `retro_cheat_set` real?" below) and an upstream `cht` folder.

**PS2** gets no codes (LRPS2 ignores them, and upstream has no `cht` folder for it) but does get the two
patches compiled into the core:

- **Widescreen (16:9)** — `pcsx2_widescreen_hint = enabled (16:9)`, **pre-selected**.
- **No interlacing** — `pcsx2_nointerlacing_hint = enabled`, off by default (it can shake the picture).

These are written **per game**, only for the ~150 titles the core can actually patch. The list is ground
truth pulled out of the core binary itself — `pcsx2_libretro.dll` logs `[PATCH] [<title> (<region>)]: 16:9
(Hor+) Widescreen patch applied.`, one string per patched dump — and is checked in at
`docs/arcade/ps2-core-patches.tsv`. Regenerate it after a core update.

This is why widescreen can be default-on without being a lie: a game only shows the toggle *because* the
emulator has a patch for it, so a pre-ticked box means "this is applied — untick for original 4:3".

PS2 also gets one **system-wide** option cheat (every game, off by default):

- **Fix ghosting / double image** — `pcsx2_half_pixel_offset = Align to Native`. Upscaling PS2 (our
  global 2x) exposes half-pixel misalignment as texture ghosting. LRPS2's GameDB normally auto-fixes it
  per game — the DLL's embedded 12.8k-entry YAML gives San Andreas `autoFlush + halfPixelOffset: 4`,
  LCS `halfPixelOffset: 4`, VCS `halfPixelOffset: 2`, all applied automatically at boot. But the
  database has real **gaps**: **Vice City and GTA III carry no gsHWFixes at all — in our core's copy
  AND in current upstream PCSX2** (verified 2026-07-09 against master GameIndex.yaml; VC's boot log
  applies zero fixes where God of War's applies three). Nobody can predict which other titles are gaps,
  so the toggle is the discovery mechanism: a player who sees ghosting fixes it themselves, per room,
  no admin loop.

  This option is **gated behind `pcsx2_enable_hw_hacks`** — picking it makes the room-create resolver add
  that master switch automatically (`ArcadeCheatCatalog.ImpliedOptionsFor`; an explicit pick of the gate
  key wins). The gate also disables the GameDB auto-fixes for that room (the core's own words: "This will
  disable automatic settings from the database"), which is why the cheat's note warns "untick if anything
  looks worse" and why `pcsx2_enable_hw_hacks` must never be a *config* default.

  Titles **confirmed** to need it get a per-game row with `DefaultOn = 1` and `Source = 'curated-fix'` —
  but ONLY titles whose GameDB entry has no gsHWFixes to lose (currently the 4 no-fix GTA dumps:
  Vice City ×3 + GTA III). Default-on for a GameDB-covered title would strictly regress it: ticked,
  San Andreas would trade its automatic `autoFlush + halfPixelOffset` for the manual HPO alone. A stored
  row on the same option key replaces the system-wide entry in the offer, so the card shows one
  pre-ticked toggle, not two. `curated-fix` rows survive `arcade-cheats-import` re-runs (deletes are
  Source-scoped).

**Dreamcast and GameCube** get one system-wide option cheat each (`reicast_widescreen_cheats`,
`dolphin_widescreen_hack`), **off by default and labelled with what they do**. flycast's widescreen table is
a binary struct array in the DLL rather than log strings, so unlike PCSX2 we can't tell per game whether it
will fire; Dolphin's is a rendering hack, not a per-game patch, and can reveal un-drawn geometry at the
edges. Neither can honestly be pre-selected.

**Not offered at all:** psp, naomi, atomiswave, arcade/neogeo (fbneo and flycast have internal cheat engines
we don't drive), and a2600/a7800/lynx/vb/wsc/ngpc (upstream `cht` folders exist, but nobody has confirmed a
code takes effect on those cores here — adding one is a single line in `CodeCapable` once verified).

## How a cheat reaches the emulator

```
lobby card  ──POST /API/Arcade/Room {cheats:["c500","s:reicast_widescreen_cheats"]}
                │  ArcadeController resolves ids against THIS ROM's offer (never trusts the client:
                │  a code aimed at another dump's addresses corrupts memory, it doesn't no-op)
                ▼
        join descriptor {coreOptions:{...}, cheats:["80165FBD 0001", ...]}
                │  cloudRetroClient copies them into the t=104 GAME_START packet body
                │  (not the WS URL — vbr/fec fit in a query string, a code list does not)
                ▼
        coordinator relays StartGameRequest ──▶ worker
                │
                ▼  patch 0027, in nanoarch around the load:
        core options merged into n.options ──▶ retro_load_game ──▶ retro_cheat_reset + retro_cheat_set
```

Two traps are baked into that path, both learned the hard way:

1. **The coordinator drops JSON fields it doesn't know.** `api.StartGameRequest` must carry the fields, not
   just `GameStartUserRequest`. Rebuilding the worker alone leaves the feature dead with no error anywhere —
   exactly how per-room bitrate (patch 0018) silently did nothing for days. **Rebuild the coordinator too.**
2. **`nanoarch.Nan0` is a process-wide singleton** and one worker serves rooms back to back, so
   `retro_cheat_reset` is called on *every* load, not only when the room has cheats, and the staged
   options/codes are cleared once consumed. Otherwise a room that asked for no cheats inherits the previous
   room's.

## Importing the data

`arcade-cheats-import` — dry-run by default, chunked, resumable, idempotent (global bulk-job rule).

```bash
# 0. One-time: a sparse clone of the community cheat database (~218 MB of .cht text).
git clone --depth 1 --filter=blob:none --sparse https://github.com/libretro/libretro-database.git \
    D:/ArcadeStorage/cheats/libretro-database
cd D:/ArcadeStorage/cheats/libretro-database && git sparse-checkout set cht

# 1. PS2 widescreen / no-interlacing, gated by the core's own table. Small and fast.
dotnet run --project src/MovieTheater -- arcade-cheats-import \
    --ps2-patches docs/arcade/ps2-core-patches.tsv --apply

# 2. Community cheat codes. Bounded per call — loop on the printed nextCursor until remaining=0.
dotnet run --project src/MovieTheater -- arcade-cheats-import \
    --cht D:/ArcadeStorage/cheats/libretro-database/cht --limit 500 --after-id 0 --apply
```

### Matching a ROM to a cheat file (`ArcadeChtIndex`)

Upstream `.cht` names are ROM names **plus a cheat-device suffix**, and usually carry a broader region tag
than the individual dump:

```
ours:      Ape Escape (USA).cue                 007 - GoldenEye (USA).z64
upstream:  Ape Escape (USA, Europe) (Game Buster).cht    GoldenEye 007 (USA).cht
```

So exact-compare finds only about a quarter of them. The fallback is exactly two rules wide, because a
mismatch here is not a harmless miss — a code is an address poke, and one from the PAL dump aimed at the
NTSC binary corrupts state instead of failing:

1. the same **title token set**, order-insensitive, nothing added or dropped ("007 - GoldenEye" ⇄ "GoldenEye
   007"; never "Super Return of the Jedi" ⇄ "Super Star Wars - Return of the Jedi"), **and**
2. **overlapping regions, not equal ones.** `(USA)` ⊂ `(USA, Europe)` matches; `(World)` expands to every
   region; `Micro Machines V3 (USA)` against a lone `(Europe) (Xploder)` file does **not**. A parenthetical
   naming no region at all — the device-only `(GameShark)` — carries no dump information and is treated as a
   last-resort wildcard, never chosen over a file that actually agrees on the region.

Measured against the materialized ROM mount: **90% matched, 0 cross-region**. The naive
"strip all tags and compare" that this replaced matched 92% — but 44 of those were cross-region, including
`Micro Machines V3 (USA)` → the PAL Xploder file.

Entries with no `cheatN_code` are RetroArch's own memory-scanner cheats (address/value triples it pokes
through the core's memory map). We have no scanner, so those are counted and skipped — 9 of them across the
whole corpus.

**Volume.** ~15.5k upstream files cover our systems, ~477k codes in total, capped at
`MaxCheatsPerGame = 300` (→ ~278k rows if every file matched a ROM we own; in practice far fewer). The cap
does real work: `Turok - Dinosaur Hunter (USA).cht` alone declares **30,911** cheats, and a handful of the
big files are machine-generated address dumps rather than curated lists. Truncation is always logged, never
silent. A room may enable at most `MaxCheatsPerRoom = 24` — conflicting codes from the same group reliably
wedge a game.

## Is this core's `retro_cheat_set` real? (the probe)

**Do not reason about this. Measure it.** The API is mandatory, so *every* core exports the symbol; a stub
accepts a code and discards it, and nothing observable at runtime tells the two apart. The test is one line
of disassembly: **a stub's first instruction is `ret`.**

```bash
python scripts/probe-libretro-cheat-support.py D:/ArcadeStorage/worker-gl/assets/cores/*.dll
#   mednafen_pce     cheat_set=STUB (ret )   <- accepts every code, does nothing
#   mupen64plus_next cheat_set=REAL (push)
```

Do **not** use "the body contains some opcode other than `ret`": disassembling past a stub's single `ret`
runs into inter-function alignment padding (`data16 nopl …`) that belongs to no function, and that test
reports every stub as REAL. It's how the pce bug survived its first check.

Results (2026-07-09, the cores this stack actually loads):

| verdict | cores |
|---|---|
| **REAL** | mupen64plus_next, pcsx_rearmed, snes9x, nestopia, genesis_plus_gx, picodrive, mgba, **dolphin, ppsspp** |
| **STUB** | pcsx2, flycast, fbneo, stella, **mednafen_pce** |

`mednafen_pce` is why this section exists. It was allowlisted on the reasoning that "the mednafen cores
implement the cheat API" — they don't, and 621 rows across 173 PC Engine games shipped as toggles that could
never do anything. Caught by the probe, not by testing, because a stub is indistinguishable from a code that
simply didn't change anything visible.

`dolphin` and `ppsspp` are real but not enabled: upstream has no `cht` folder for GameCube, and PSP is
unverified end-to-end.

## Verification

Delivery is proven by the worker log — `[room-cheat] applied N cheat code(s)` / `[room-cheat] option k=v` on
every room start. Effect is confirmed by playing (see `.claude/skills/test-roms`).

Verified live 2026-07-09 on the real product path (login → lobby card → picker → WebRTC room):

- **PS2 option cheat**, zero clicks — Ape Escape 2's card offered one pre-ticked cheat; the room-create
  response carried `coreOptions: {"pcsx2_widescreen_hint": "enabled (16:9)"}`; the worker logged
  `[room-cheat] option pcsx2_widescreen_hint=enabled (16:9)`; and PCSX2 then logged
  `[PATCH] [Ape Escape 2 (NTSC-U)]: Force native widescreen mode patch applied.` — the very string the
  patch table was extracted from.
- **N64 cheat code** — Mario Kart 64's picker offered 300; ticking "Multi Bananas" sent
  `cheats: ["80165FBD 0002"]`; the worker logged `Per-room cheats: 0 core option(s), 1 code(s)` then
  `[room-cheat] applied 1 cheat code(s)`. Stream stayed alive at 1280×960.
- **Correct absence** — God of War shows no picker: it isn't in the core's widescreen table (it gets GS
  hardware fixes instead), so there is nothing honest to offer.

## Adding a system

1. **Run the probe.** If `retro_cheat_set` is a stub, stop — no amount of testing will distinguish it from a
   code that had no visible effect.
2. Confirm a code actually takes effect in a real room.
3. Add the system to `CodeCapable` *and* its upstream folder to `ChtFolders` in `ArcadeCheatCatalog`. A
   folder without an allowlist entry is how a stub core gets silently re-imported; a unit test guards this.
4. Run the import for it: `arcade-cheats-import --cht <dir> --system <code> --apply`.
