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
--   CoreOptionsJson = '{"reicast_widescreen_hack":"disabled"}'   -- NB: reicast_, not flycast_ (see below)
```
```bash
# 2. Regenerate the worker manifest (re-run after ANY profile change; like arcade-romcache-export).
dotnet run --project src/MovieTheater/MovieTheater.csproj -- \
  arcade-gameconfig-export --out D:/ArcadeStorage/worker-gl/game-overrides.json
# takes effect on the next room load (mtime hot-reload) — no worker restart needed.
```

## The DC double-speed — SOLVED at the stack level by audio-driven pacing (patch 0010)

This whole class is **fixed in the worker, with no per-game data** — `forcedFps` is obsolete for it.

Root cause (measured): flycast runs a Dreamcast game's **30fps sections at 2 vblanks per `retro_run`**
(emitting 2× the audio) but its **60fps sections at 1 vblank per `retro_run`**. Our old loop paced
`retro_run` at a fixed 59.94Hz, so 30fps gameplay ran at **2×** (Sonic Adventure, Jet Set Radio) while
60fps menus were correct — and the 2× flooded the pipeline with 88.2kHz-worth of audio (the "clipping"
/ "looping"). Instrumenting `audioFrames/retro_run` showed the signal cleanly: **735 (one vblank) in
60fps content vs 1470 (two) in 30fps content.**

Fix (`frontend.go` Start, patch 0010): pace `retro_run` against the **emulated audio clock** — the game
must produce exactly `sampleRate` (44100) audio frames per real second — with the video frame time kept
as a **max-rate floor** (never faster than the core fps; also the whole pace during silence / for
no-audio cores). Each section then plays at its true rate automatically. This is what stock emulators do
("sync to audio").

Verified 2026-07-05 (audio/s should be ~44100 everywhere):

| Game | old (fixed pace) | new (audio pace) |
|---|---|---|
| Sonic gameplay | 60 run/s, 88200 audio (2×) | **30 run/s, 44100** ✓ |
| Sonic menus | 60 run/s, 44100 | **60 run/s, 44100** ✓ |
| Jet Grind Radio | 60 run/s, 88200 (2×) | **30 run/s, 44100** ✓ |
| Crazy Taxi (60fps DC) | 60 run/s, 44100 | **60 run/s, 44100** (unchanged) ✓ |
| Loco Roco 2 (PSP) | 60 run/s, 44100 | **60 run/s, 44100** (unchanged) ✓ |

Constant-rate cores (PSP, 60fps DC, all 2D) produce audio at a steady `sampleRate/fps` per frame, so the
floor dominates and their behavior is unchanged. `forcedFps` remains in the model as a static fallback
for any core that emits no audio, but the Sonic/JGR-class profiles were **removed** — audio pacing is
strictly better (correct in menus too, audio in sync, no per-game data).

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

**flycast (dc/naomi/atomiswave)** — ⚠ **the prefix is `reicast_`, NOT `flycast_`.** The core exposes
140 `reicast_*` keys and **zero** real `flycast_*` keys (verified 2026-07-08 by parsing the option
structs out of `flycast_libretro.dll`). libretro **silently ignores unknown option keys**, so a
`flycast_…` override is a no-op with no error — it looks applied and does nothing. This document
previously named them all wrong; corrected below.

`reicast_widescreen_hack` / `reicast_widescreen_cheats` (16:9), `reicast_internal_resolution`
(`640x480`…`1280x960`…, default `640x480`), `reicast_anisotropic_filtering` (`off|2|4|8|16`,
default `4`), `reicast_region` (Default|Japan|USA|Europe), `reicast_broadcast`
(Default|PAL-M|PAL-N|NTSC|PAL) — **NTSC avoids the auto→50Hz wrong-speed class for US ROMs**,
`reicast_cable_type`, `reicast_alpha_sorting` (`per-pixel (accurate)` fixes transparency glitches),
`reicast_delay_frame_swapping` (fixes flashing), `reicast_force_wince` (Full MMU for Windows-CE
titles, e.g. Resident Evil 2, Sega Rally 2), `reicast_enable_dsp` (audio DSP).

Values are the **value tokens**, not the display labels — same class of bug as `dolphin_efb_scale`
(which takes `"1"`…`"6"`, not `"2x Native (1280x1056)"`). PPSSPP's MSAA key is genuinely misspelled
in the core: `ppsspp_mulitsample_level`. `pcsx2_upscale_multiplier` takes the whole string
`"2x Native (~720p)"`. When in doubt, extract from the DLL rather than trusting any doc, including
this one.

**ppsspp (psp)** — `ppsspp_internal_resolution`, `ppsspp_cpu_core` (jit|IR jit|interpreter),
`ppsspp_locked_cpu_speed` (222/266/333MHz — stability for slowdown-prone games), `ppsspp_frameskip` /
`ppsspp_auto_frameskip`, `ppsspp_texture_scaling_type`/`_level`, `ppsspp_texture_deposterize`
(fixes upscale glitches). (`ppsspp_fast_memory` stays **disabled** — see the AV-crash note in config.)

Candidate global default (not yet applied — needs a no-regression check on a known-good 60fps DC game):
`reicast_broadcast: NTSC` on the dc/naomi/atomiswave cores, since the whole catalog is US/NTSC.

Sources: [flycast core options](https://docs.libretro.com/library/flycast/),
[ppsspp core options](https://docs.libretro.com/library/ppsspp/),
[Skies of Arcadia 60Hz/desync patch](https://www.dreamcast-talk.com/forum/viewtopic.php?t=18173).

## Not yet built (follow-ups)

- **Admin "Configure" panel** — editor-gated per-game UI over `ArcadeGameProfile` (the same gate as the
  save-state dropdown), with friendly controls (forced fps, region, widescreen) plus a raw core-options
  editor. Until then, profiles are seeded via SQL / the CLI.
- **Bulk import** of curated community preset lists into `ArcadeGameProfile`.
- **Auto-regen hook** so editing a profile in the admin UI regenerates the manifest for all worker pools.
