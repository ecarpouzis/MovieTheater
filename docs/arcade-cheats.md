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
sega32x, gb, gbc, gba, n64, ps1, pce. Each has both a core that really implements the cheat API and an
upstream `cht` folder.

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

Matching is by **exact ROM filename first** (both sides use No-Intro/Redump names), then a normalized
filename match for tag drift. It deliberately stops there: a title-only guess could hand a USA code to a
Japanese dump, and that corrupts state rather than failing cleanly.

Entries with no `cheatN_code` are RetroArch's own memory-scanner cheats (address/value triples it pokes
through the core's memory map). We have no scanner, so those are counted and skipped — 9 of them across the
whole corpus.

**Volume.** ~15.5k upstream files cover our systems, ~477k codes in total, capped at
`MaxCheatsPerGame = 300` (→ ~278k rows if every file matched a ROM we own; in practice far fewer). The cap
does real work: `Turok - Dinosaur Hunter (USA).cht` alone declares **30,911** cheats, and a handful of the
big files are machine-generated address dumps rather than curated lists. Truncation is always logged, never
silent. A room may enable at most `MaxCheatsPerRoom = 24` — conflicting codes from the same group reliably
wedge a game.

## Verification

Whether a *code* does anything cannot be settled by reading the core's exports, only by watching a game.
The worker logs `[room-cheat] applied N cheat code(s)` and `[room-cheat] option k=v` on every room start;
that proves delivery. Effect is confirmed by playing (see `.claude/skills/test-roms`).

## Adding a system

1. Confirm a code actually takes effect in a real room on that system's core.
2. Add the system code to `CodeCapable` and its upstream folder name to `ChtFolders` in `ArcadeCheatCatalog`.
3. Run the import for it: `arcade-cheats-import --cht <dir> --system <code> --apply`.
