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

## Not yet built (follow-ups)

- **Admin "Configure" panel** — editor-gated per-game UI over `ArcadeGameProfile` (the same gate as the
  save-state dropdown), with friendly controls (forced fps, region, widescreen) plus a raw core-options
  editor. Until then, profiles are seeded via SQL / the CLI.
- **Bulk import** of curated community preset lists into `ArcadeGameProfile`.
- **Auto-regen hook** so editing a profile in the admin UI regenerates the manifest for all worker pools.
