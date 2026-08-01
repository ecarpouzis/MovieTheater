# Arcade cheats

Room creators pick cheats in the game modal, next to the version dropdown. The cheats apply to that room
only, for its whole life, and nothing about them is shared with other rooms or persisted to a save.

> **⚠ Cheats are now codes-only.** The emulator/quality **OPTION** toggles that used to appear here
> (DC/GC widescreen, PS2 ghosting-fix/deblur/super-sample, PS1 PGXP, and the PS2 per-game
> widescreen/no-interlacing) MOVED to the per-game **config tool** (the game modal's ⚙ Configure button →
> `ArcadeCoreOptionCatalog` + `ArcadeGameProfile`). Those are persistent per-game emulator settings, not
> per-room memory pokes, and they are applied server-side at Start — see docs/arcade-per-game-config.md.
> The Cheats dropdown offers only real cheat **codes** now (`Kind="code"`). The `ArcadeCheat`
> `Kind="option"` rows below still exist (PS2 widescreen data), but they feed the config tool, not the
> picker.

## The one thing to understand

There is no single "cheat" mechanism in libretro. There are two, they are unrelated, and the difference is
what the whole design is shaped around:

| | **Cheat codes** | **Core-option cheats** |
|---|---|---|
| Delivered by | `retro_cheat_set(index, enabled, code)` | a libretro core option, set before load |
| What it is | a raw memory poke ("`80165FBD 0001`") | a patch the **emulator already ships** |
| Source | `libretro-database/cht`, per ROM — **except gc/wii, whose codes come from Dolphin's own `Sys/GameSettings/<GAMEID>.ini`; see below** | the core's own compiled-in table |
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
sega32x, gb, gbc, gba, n64, ps1, **nds**, **gc**, **wii**. Each has a core whose `retro_cheat_set` is a
*real implementation* (see "Is this core's `retro_cheat_set` real?" below) plus a source of data: an upstream
`cht` folder for all but gc/wii, which come from Dolphin's own INIs instead (next section).

> **The allowlist has to be re-probed when systems are added.** The original probe ran 2026-07-09; roughly
> twenty systems arrived after it and nobody re-ran it, so nds/gc/wii sat at **zero** cheats for weeks while
> their cores could apply them perfectly well. A system arriving with no cheats looks exactly like a system
> whose core can't do cheats. Re-probing all 43 live cores on 2026-07-31 is what found them.

### GameCube and Wii — the one system whose cheats do NOT come from a code list

Dolphin's `retro_cheat_set` is real, but it does not take a code. Read it:

```cpp
// DolphinLibretro/Main.cpp
void retro_cheat_set(unsigned index, bool enabled, const char* code) {
  if (!Config::AreCheatsEnabled()) { OSD::AddMessage("..."); return; }
  enable_cheat_by_code(enabled, code);   // finds an ALREADY-LOADED code by re-serializing and comparing
  ...
}
```

It only flips a cheat Dolphin already loaded from `Sys/GameSettings/<GAMEID>.ini`. Hand it a Gecko code it
has never seen and it matches nothing and returns — silently, like everything else here. So the site's job is
not to supply codes; it is to **mirror the ones the core already has** and hand back the exact bytes the
comparison expects. Three consequences shape `DolphinGameIni`:

1. **The serialization is a wire format, not a display choice.** ActionReplay ops are re-emitted as
   `{cmd_addr:X8} {value:X8}` (uppercase, one space, '+'-joined) because Dolphin parses to integers and
   formats them back; Gecko lines go back **verbatim** (it keeps `original_line`). One byte off = dead cheat.
   Proven against an oracle rather than by re-reading the source: the core writes its own RetroArch `.cht`
   of everything it loaded (`generate_cht_from_ini`), and the parser reproduces those files byte for byte —
   97/97 cheats over three games mixing both kinds.
2. **A disc's codes are the UNION of an INI chain**, not one file: `G.ini` → `GFZ.ini` → `GFZE01.ini` →
   `GFZE01r0.ini`. Read only the generic 3-char file and F-Zero GX loses all four of its codes.
3. **`dolphin_cheats_enabled` defaults to FALSE**, so every GameCube/Wii cheat is inert without it. It rides
   `ArcadeCheatCatalog.ImpliedOptionsForSystem` — the same shape as PS2's `pcsx2_enable_hw_hacks` gate, one
   level up — and is merged only into rooms that actually took a cheat.

Matching is by the disc's **game id** read out of the image with `DolphinTool header -j` (~0.1 s local,
~0.3 s over the NAS), never by filename. The region is the 4th character of the id, so these are the only
systems here with **no cross-region matching risk at all**.

Yield (2026-07-31): **gc 134 games / 3,430 codes; wii 9 games / 72 codes.** Wii is genuinely thin — Dolphin's
shipped INIs are overwhelmingly GameCube ActionReplay codes. 924 of the 1,870 INIs carry any codes at all.

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

  Titles **confirmed** to benefit can get a per-game row with `DefaultOn = 1` and `Source = 'curated-fix'`
  — but ONLY titles whose GameDB entry has no gsHWFixes to lose (default-on for a GameDB-covered title
  would strictly regress it: ticked, San Andreas would trade its automatic `autoFlush + halfPixelOffset`
  for the manual HPO alone). A stored row on the same option key replaces the system-wide entry in the
  offer, so the card shows one pre-ticked toggle, not two. `curated-fix` rows survive
  `arcade-cheats-import` re-runs (deletes are Source-scoped).

  **The GTA VC/III verdict (2026-07-09, tested live): no hack fixes their trails ghosting.** Both HPO
  modes ("Align to Native", "Special (Texture)") were verified delivered AND gated-in; the artifact
  didn't move — consistent with upstream never shipping a VC/III gsHWFix in twelve years. The ghosting
  is the games' own Trails post-effect misaligned at 2x upscale; the answer is the game's OWN menu
  option (Display Settings → Trails → OFF), which persists in the player's vaulted save. The
  pre-ticked curated-fix rows were deleted for honesty — never offer a toggle that provably does
  nothing. (`pcsx2_native_scaling` was staged as a last-shot experiment and withdrawn untested when
  Trails-off was accepted as the fix; it needs the hw_hacks implication too, now in the catalog.)

**Dreamcast and GameCube** get one system-wide option cheat each (`reicast_widescreen_cheats`,
`dolphin_widescreen_hack`), **off by default and labelled with what they do**. flycast's widescreen table is
a binary struct array in the DLL rather than log strings, so unlike PCSX2 we can't tell per game whether it
will fire; Dolphin's is a rendering hack, not a per-game patch, and can reveal un-drawn geometry at the
edges. Neither can honestly be pre-selected.

**Not offered at all**, and the reason is never "we didn't get to it":

- **Confirmed STUB cores** — ps2 (pcsx2), dc/naomi/atomiswave (flycast), arcade/neogeo (fbneo), pce, a2600,
  a7800, lynx, vb, wsc, ngpc, coleco, intv, 3do, cdi, 3ds, scummvm, o2em, vectrex, channelf, pokemini,
  supervision, arcadia. Note that dc (292 files) and arcade (71) *do* have upstream `cht` folders: **a folder
  is not evidence the core will use it.**
- **psp** — ppsspp's `retro_cheat_set` is REAL, but held back for two reasons, neither about the core:
  (1) our psp filenames carry no region tag at all, so every match would be an unchecked wildcard, and a EUR
  code on a USA disc corrupts memory rather than no-op'ing; (2) PPSSPP does not hold cheats in memory — it
  **writes them to a cheat file on the memstick** and re-parses it, so they would outlive the room and leak
  into the next player's session. Fixable (read `DISC_ID` from `PARAM.SFO`; clear the file per room); until
  then it stays off. 2,654 upstream files are waiting.
- **saturn** — kronos is REAL (`CheatAddARCode`), but upstream has only 192 files against our 2,522 discs and
  our saturn filenames are coded (`0029-discworld-eur-v2`), so matches would be region-unchecked. Needs a
  saturn-specific filename parse to be safe.
- **segacd** shows near-zero coverage and that is not a bug: upstream ships **14** files for the whole system.

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

# 3. GameCube / Wii, from Dolphin's OWN GameSettings INIs. --dolphin-tool is required, not optional:
#    it reads each disc's game id, and guessing one would hand a game another game's memory pokes.
dotnet run --project src/MovieTheater -- arcade-cheats-import \
    --dolphin-ini D:/ArcadeStorage/worker-gl/libretro/system/dolphin-emu/Sys/GameSettings \
    --dolphin-tool D:/Tools/dolphin-2512/Dolphin-x64/DolphinTool.exe \
    --roms-dir D:/ArcadeStorage/roms --system gc --limit 150 --after-id 0 --apply
```

Each source writes its own `Source` value (`libretro-cht`, `pcsx2-gamedb`, `dolphin-ini`) and deletes are
scoped to it, so any one can be re-run without touching the others.

**Over-length codes are DROPPED, never truncated.** Gecko codes are whole PowerPC subroutines and a few run
past the `nvarchar(4000)` column. Half a code is not a weaker cheat — it is a poke at the wrong addresses.
Same rule as `ArcadeChtFile`, and it is reported (`over-length-dropped=N`), never silent. Encrypted
ActionReplay codes are dropped for the related reason that we cannot reproduce Dolphin's decrypted ops and
therefore cannot produce a string that will ever match.

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

Several files can pass both rules, and the tiebreak is a **correctness** rule, not tidiness: take the
**least decorated** name — fewest words inside `(...)`/`[...]` — then the name itself, so the answer never
depends on directory order. Upstream keeps **ROM HACK** cheat files beside the stock game's, and the hack's
name lives in a parenthetical the title signature strips, so `Mario Kart DS (USA)` and
`Mario Kart DS (USA) (CTGP Nitro (v1.0.0))` are indistinguishable to the signature *and* agree on region.
On the first nds run the hack won and our stock Mario Kart DS was handed the hack's addresses. The same
tiebreak prefers a plain dump over `(Rev 1)` and over a device-suffixed file.

> Before this, the pick among equally-valid candidates was whatever the filesystem enumerated first. The
> tiebreak changes which file **1,311** of the already-imported matches on the other 13 systems would resolve
> to — no matches are lost (same candidate pool), but they were never re-imported with it. Re-running those
> systems with `--overwrite` is a deliberate, separate decision.

### Per-system naming profiles

The rules above assume No-Intro naming (`Title (Region) (Tags)`). Not every collection obeys that, so
`ArcadeChtIndex.NamingProfile` turns three relaxations on **per system**, via
`ArcadeCheatCatalog.NamingProfileFor`. They are opt-in because they are not universally safe: enabling the
short-region-code rule globally was measured to silently rewrite **87** working matches on the live systems
(18 lost outright).

`nds` is the one non-default profile today, and needed all three — under the default rules it matched
**zero** of 6,604 DS ROMs:

| Rule | Why nds needs it |
|---|---|
| `StripCatalogNumber` | `0168 - Mario Kart DS` — the index prefix poisons the title token set. Kept per-system because `007 - Agent Under Fire` looks identical to the pattern. |
| `RegionsFromEveryTag` | The region is not the first tag: `Alice in Wonderland (DSi Enhanced) [b] (US)`. Reading only the first made that name look region-LESS, which matched it to a **Europe** cheat file. |
| `ShortRegionCodes` | `(US)`/`(EU)`/`(JP)`/`(FR)`… These collide with GoodTools single letters elsewhere. |

With all three: **3,221 of 6,604 DS games matched, 81,949 codes, essentially all region-verified** —
a language-specific dump (`(IT)`, `(FR)`) deliberately MISSES a generic `(Europe)` file rather than guessing.

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

Results (**2026-07-31**, all 43 cores this stack loads — supersedes the 2026-07-09 run, which predated ~20
systems):

| verdict | cores |
|---|---|
| **REAL** | mupen64plus_next, parallel_n64, pcsx_rearmed, mednafen_psx_hw, snes9x, nestopia, genesis_plus_gx, picodrive, mgba, **melonds / melondsds**, **dolphin**, **kronos**, **ppsspp** |
| **STUB** | pcsx2, flycast, fbneo, stella, mednafen_pce, mednafen_lynx, mednafen_ngp, mednafen_vb, mednafen_wswan, gearcoleco, freeintv, o2em, opera, same_cdi, citra, scummvm, prosystem, potator, pokemini, vecx, freechaf, amiarcadia, dosbox_pure |

`mednafen_pce` is why this section exists. It was allowlisted on the reasoning that "the mednafen cores
implement the cheat API" — they don't, and 621 rows across 173 PC Engine games shipped as toggles that could
never do anything. Caught by the probe, not by testing, because a stub is indistinguishable from a code that
simply didn't change anything visible. Note the whole mednafen family reads STUB, which is the useful shape
of that lesson.

Re-run this whenever a system is added. It is the cheapest step in the whole runbook and it is the one that
was skipped for twenty systems.

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

Coverage after the 2026-07-31 pass:

| system | games with cheats | codes | source |
|---|---:|---:|---|
| nds | 3,221 | 81,949 | libretro-cht (new) |
| n64 | 819 | 68,330 | libretro-cht |
| snes | 1,797 | 48,474 | libretro-cht |
| nes | 1,825 | 35,954 | libretro-cht |
| genesis | 1,408 | 31,996 | libretro-cht |
| ps1 | 1,187 | 14,172 | libretro-cht |
| gb / gba / gbc / gg / sms | 2,223 | 24,458 | libretro-cht |
| **gc** | **134** | **3,430** | **dolphin-ini (new)** |
| sega32x / fds | 51 | 596 | libretro-cht |
| **wii** | **9** | **72** | **dolphin-ini (new)** |

## Adding a system

1. **Run the probe.** If `retro_cheat_set` is a stub, stop — no amount of testing will distinguish it from a
   code that had no visible effect.
2. **Check the matching is region-safe before the data looks tempting.** Dry-run the import and read the
   chosen filenames, not just the counts. Two failures found this way, both of which would have shipped
   silently: a numbered-set naming convention that matched *nothing*, and a romhack's cheat file winning over
   the stock dump's. If our filenames can't establish a region, the system does not get codes (see psp).
3. Confirm a code actually takes effect in a real room.
4. Add the system to `CodeCapable` *and* its upstream folder to `ChtFolders` in `ArcadeCheatCatalog`, plus a
   `NamingProfileFor` entry if its filenames aren't No-Intro shaped. A folder without an allowlist entry is
   how a stub core gets silently re-imported; a unit test guards this.
5. If the core needs a core option before it will honour codes at all (Dolphin does), add it to
   `SystemImplied` — otherwise every cheat is accepted and discarded.
6. Run the import for it: `arcade-cheats-import --cht <dir> --system <code> --apply`.
