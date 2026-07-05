# Arcade per-game config

Some games need emulation overrides that must NOT apply globally: a frame-rate-locked title needs
its true rate forced, a 4:3 game shouldn't get a widescreen hack, a specific ROM needs a core option.
This is the data-driven, importable system for those overrides. It is deliberately **keyed by
normalized game identity, not by individual ROM file**, so one entry covers every region / revision /
edition of a game — present and future imports — and the fixes survive re-ingesting the romset.

## Why identity-keyed (the load-bearing decision)

The naive approach — hardcode `"Sonic Adventure v1.005 (1999)(Sega)(US)[!][%51000-A]": 30` per exact
ROM filename in the worker config — is brittle at romset scale: six Sonic Adventure dumps mean six
lines, a re-rip or a new region breaks the match, and nothing is importable in bulk. Instead the
source of truth is `(System, TitleKey)` where `TitleKey` is the normalized (lowercased) `ArcadeGame.Title`.
`(dc, "sonic adventure")` covers all six ROM variants at once. This is the same identity the lobby uses
to collapse ROM revisions into one card (see `ArcadeVersions` / the TOSEC bare-version peel in
`ArcadeNaming.CleanTitle`), so cards and config share one notion of "the game."

## The three layers

| Layer | Keyed by | Role |
|---|---|---|
| **Source of truth** — `ArcadeGameProfile` (DB) | normalized identity `(System, TitleKey)` | one row per game: `ForcedFps`, `CoreOptionsJson`, `Notes`. Bulk/rule editable; importable from curated community lists. |
| **Generator** — `arcade-gameconfig-export` (CLI) | joins identity → all matching ROMs | expands each profile to every `ArcadeGame` row whose `Title` lowercases to `TitleKey`, and writes the worker manifest keyed by `CloudRetroGameKey`. |
| **Delivery** — `game-overrides.json` (worker) | ROM filename (`CloudRetroGameKey`) | the emulator's last-mile match. nanoarch reads it at each game load. |

```
ArcadeGameProfile (dc, "sonic adventure", fps=30)
        │  arcade-gameconfig-export --out <ConfDir>/game-overrides.json
        ▼
game-overrides.json  { "Sonic Adventure v1.004 (1999)…": {"fps":30}, "…v1.005 …[24S]": {"fps":30}, … }
        │  worker reads at CoreLoad (mtime-cached), keyed by ROM filename
        ▼
nanoarch overrides the libretro AV timing → frontend paces retro_run to 30Hz
```

## Data model — `ArcadeGameProfile`

- `System` — matches `ArcadeGame.System` (`dc`, `psp`, `n64`, …).
- `TitleKey` — lowercased normalized Title (e.g. `sonic adventure`). Unique with `System`.
- `ForcedFps` (double?, null = leave core default) — the game's true engine rate.
- `CoreOptionsJson` (string?, JSON object `{"key":"value"}`) — libretro core-option overrides; the
  universal escape hatch (widescreen, region, internal resolution, frameskip, …). Mirrors RetroArch
  per-game core options.
- `Notes` — provenance / why.

Migration `20260705191507_AddArcadeGameProfile` (applied to the shared DB).

## Worker side (`nanoarch`, patch 0009)

At `CoreLoad`, keyed by the ROM filename sans extension (`romName`, == `CloudRetroGameKey`):

- **Core options** from the manifest are merged into the core's option map **before** `retro_load_game`
  (alongside the static `options4rom` from config).
- **Forced fps** is applied **after** `retro_get_system_av_info`: it overwrites the stored
  `av.timing.fps`, so `tickTime`, `VideoFramerate()`, and the frontend pacing loop all target the true
  rate. Manifest wins; the config `fps4rom:` map is a static fallback.

`gameOverrides()` reads `game-overrides.json` from `CLOUD_GAME_GAME_OVERRIDES` (else `game-overrides.json`
in the worker CWD = the ConfDir), reloading when the file's mtime changes — so a regen takes effect on
the next room with no restart, and a malformed regen keeps the last-good map.

## How to add / change a fix

```sql
-- 1. Add or edit the profile (identity-keyed; covers all ROM variants).
INSERT INTO ArcadeGameProfile (System, TitleKey, ForcedFps, Notes)
VALUES ('dc', 'sonic adventure', 30, '30fps-locked engine; physics tied to frame limit');
-- core options instead of / alongside fps:
--   CoreOptionsJson = '{"flycast_widescreen_hacks":"disabled"}'
```
```bash
# 2. Regenerate the worker manifest (re-run after ANY profile change; like arcade-romcache-export).
dotnet run --project src/MovieTheater/MovieTheater.csproj -- \
  arcade-gameconfig-export --out D:/ArcadeStorage/worker-gl/game-overrides.json
# takes effect on the next room load (mtime hot-reload) — no worker restart needed.
```

## Case study — the DC double-speed (why fps override, not a global cap)

Sonic Adventure's engine is hardcoded to 30fps with physics tied to that limit; flycast advertises the
59.94Hz Dreamcast **display** refresh, so pacing `retro_run` at 59.94 runs the game at **2×**. Measured
with pacing instrumentation: `retro_run=59.9/s` (our loop was correct), the core was simply advancing
the game at the display rate. A global 30fps cap would wrongly cripple the *majority* of DC games that
are genuinely 60fps (Crazy Taxi, Power Stone, …). So the fix is per-game: profile `(dc,"sonic
adventure") → 30`, which forces just those titles. Verified live: `[game-override] forcing 30.000 fps …
(core advertised 59.940)`.

There are curated per-ROM datasets for seeding these (RetroArch per-game core-option overrides, the
flycast/RetroArch DC threads listing the 30fps-locked and widescreen-problem games), so a known-problem
preset table can be imported rather than discovered game by game.

## Importing curated fixes from online sources

There is **no clean machine-readable per-game settings database** to pull: RetroArch's per-game
overrides are user-authored, the libretro-database is game *metadata* (names/hashes) not settings, and
flycast's game database is compiled into the core. So documented fixes are curated by hand into a
reviewed, source-cited dataset and applied with a guarded importer.

- **Dataset**: `docs/arcade/game-fixes.json` — `{ "fixes": [ { system, title, forcedFps?, coreOptions?, notes, source, confidence } ] }`.
- **Importer**: `arcade-gameconfig-import [--file …] [--system dc] [--min-confidence …] [--apply] [--overwrite]`
  — matches each fix to the catalog by normalized identity, dry-run default, skips titles with no catalog
  match, preserves existing profiles unless `--overwrite`.

**Confidence gate (important for `forcedFps`).** Forcing fps is only correct for games that *double-speed
in our stack* — ones that advance one engine step per `retro_run`, like Sonic Adventure. A **self-limiting**
30fps game would be **halved** by the same setting. So "confirmed 30fps online" is necessary but not
sufficient. Entries carry a `confidence`:
- `verified` — observed to double-speed here (Sonic Adventure 1/2). Applies by default.
- `high` — confirmed native 30fps online and in the same frame-locked class, but not yet play-tested here
  (Skies of Arcadia, Shenmue, Grandia II). Requires `--min-confidence high` **and a play-test**.

`coreOptions` fixes (widescreen, region, rendering) carry no such speed risk.

## Core-options reference (what to put in `coreOptions` / the dataset)

The universal escape hatch is `CoreOptionsJson` — any libretro core option. The fix-relevant ones:

**flycast (dc/naomi/atomiswave)** — `flycast_widescreen_hack` / `flycast_widescreen_cheats` (16:9),
`flycast_internal_resolution`, `flycast_region` (Default|Japan|USA|Europe), `flycast_brodcast`
(Default|PAL-M|PAL-N|NTSC|PAL) — **NTSC avoids the auto→50Hz wrong-speed class for US ROMs**,
`flycast_cable_type`, `flycast_alpha_sorting` (Per-Pixel fixes transparency glitches),
`flycast_delay_frame_swapping` (fixes flashing), `flycast_force_windows_ce_mode` (Full MMU for Windows-CE
titles, e.g. Resident Evil 2, Sega Rally 2), `flycast_enable_dsp` (audio DSP).

**ppsspp (psp)** — `ppsspp_internal_resolution`, `ppsspp_cpu_core` (jit|IR jit|interpreter),
`ppsspp_locked_cpu_speed` (222/266/333MHz — stability for slowdown-prone games), `ppsspp_frameskip` /
`ppsspp_auto_frameskip`, `ppsspp_texture_scaling_type`/`_level`, `ppsspp_texture_deposterize`
(fixes upscale glitches). (`ppsspp_fast_memory` stays **disabled** — see the AV-crash note in config.)

Candidate global default (not yet applied — needs a no-regression check on a known-good 60fps DC game):
`flycast_brodcast: NTSC` on the dc/naomi/atomiswave cores, since the whole catalog is US/NTSC.

Sources: [flycast core options](https://docs.libretro.com/library/flycast/),
[ppsspp core options](https://docs.libretro.com/library/ppsspp/),
[Skies of Arcadia 60Hz/desync patch](https://www.dreamcast-talk.com/forum/viewtopic.php?t=18173).

## Not yet built (follow-ups)

- **Admin "Configure" panel** — editor-gated per-game UI over `ArcadeGameProfile` (the same gate as the
  save-state dropdown), with friendly controls (forced fps, region, widescreen) plus a raw core-options
  editor. Until then, profiles are seeded via SQL / the CLI.
- **Bulk import** of curated community preset lists into `ArcadeGameProfile`.
- **Auto-regen hook** so editing a profile in the admin UI regenerates the manifest for all worker pools.
