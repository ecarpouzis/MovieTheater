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

## The config tool (BUILT) — game modal ⚙ Configure

The editor-gated per-game UI over `ArcadeGameProfile` is now built. It lives on the game modal
(`src/ui/src/Pages/Arcade/ArcadeGameConfig.js`, opened by the **⚙ Configure** button, shown only when
`userData.canEditMovies` and the game's `configurable` flag is set) and is the successor to the
"quality modifier" cheats: those toggles (DC/GC widescreen, PS2 ghosting-fix/deblur/super-sample, PS1
PGXP, …) MOVED out of the Cheats dropdown (now codes-only) into this tool.

**Delivery is per-room at Start, NOT the manifest.** The k8s pod can't write Ziggy's worker ConfDir, so
there is no auto-regen; instead the controller reads the profile at `POST /API/Arcade/Room`
(`ResolveGameConfigAsync`) and injects the game's core options into the room's `CoreOptions` (the
patch-0027 per-room path) and its renderer into `&hwctx`. A saved change therefore takes effect on the
**next room** with no CLI and no worker restart. The `arcade-gameconfig-export` manifest still exists and
reads the same rows (so values can't disagree) — it remains the delivery path for **ForcedFps**, which is
deliberately NOT exposed in the UI (it isn't per-room-deliverable and audio-pacing made it largely
obsolete; see §"DC double-speed"). Renderer maps to `ArcadeGameProfile.HwContext` (Auto/Vulkan/GL) and is
shown only for `CloudRetroHost.SupportsHwToggle` systems; an explicit play-button "Force GL/Vulkan" is a
per-launch override that still wins over the configured renderer.

**What the tool offers is `ArcadeCoreOptionCatalog`** (`src/MovieTheater/Arcade/`): a per-system list of
quality-relevant core options, each with the core's EXACT value tokens. It is BOTH the UI's control
source AND the server's validation allowlist — the `PUT /API/Arcade/Game/{id}/Config` handler rejects an
unknown value for a known key (a wrong token is a silent no-op) and stores only values that differ from
the game's effective default (so "reset to default" = drop the key, and PS2 widescreen stays default-on
via its `ArcadeCheat` row without being restored redundantly). Unknown keys are accepted only via the
Advanced raw editor (the editor's own risk). Hand-tuned entries (the relocated cheats) are in the C# file;
the broad per-core set is layered in at startup from the committed, embedded `core-options-catalog.json`
(generated from each core's `libretro_core_options.h` on Ziggy — regenerate and re-embed after a core
update).

## Renderer selection (OpenGL vs Vulkan) — the surface is NOT the whole story

`hwctx` (the config tool's Renderer control + the play-button Force GL/Vulkan) selects only the frontend
**surface**. Each 3D core's actual renderer is a **separate** lever, and for several systems the pre-Vulkan
OpenGL setup is a genuinely different renderer/plugin (and, for PS1, a different core):

| System | Vulkan (default) | OpenGL | How GL is selected |
|---|---|---|---|
| N64 (mupen64plus_next) | paraLLEl-RDP (`mupen64plus-rdp-plugin: parallel`, `-rsp-plugin: parallel`) | **GLideN64** (`gliden64` + `-rsp-plugin: hle`) | core-option (same core) |
| PS2 (LRPS2) | paraLLEl-GS (`pcsx2_renderer: paraLLEl-GS`) | **OpenGL** (`pcsx2_renderer: OpenGL`) | core-option (same core) |
| PS1 (Beetle PSX HW) | `beetle_psx_hw_renderer: hardware_vk` | `hardware_gl` | core-option; worker W3 pin also flips it |
| PS1 (pre-Vulkan) | — | **`pcsx_rearmed`** (a different CORE) | **core-lib swap** (not built — see below) |
| PSP/DC/GC/Wii/NAOMI/AW | surface Vulkan | surface GL | frontend surface only (no renderer option) |

**Why forcing GL alone broke N64/PS2:** the worker's W3 pin (`PinRendererForHwContext`, overrides.go)
reconciles surface↔renderer for **Beetle only**. Forcing a GL surface while the core option still said
`parallel`/`paraLLEl-GS` left the core asking for a Vulkan context on a GL surface → the GL path rejects it
(`rejected non-GL hw render context type`, nanoarch.go:1476) → `initVideo` fail-soft → **no video**.

**The fix (`ArcadeRendererProfiles`, site-side, no worker change):** when a renderer is explicitly chosen,
the controller injects the matching renderer-selecting core options as a base beneath the game's saved
config (an explicit per-game option still wins), delivered per-room (patch-0027 `CoreOptions`, which
override config **and** the manifest — nanoarch.go "LAST writer wins"). The GL companion settings (GLideN64
FB opts/res; pcsx2 GL options) already sit inert in `config.worker-gl.yaml` and activate when the renderer
flips. Surface-only systems (psp/dc/gc/…) carry no injected option — `hwctx` alone selects.

### The SOTN / Beetle finding (confirmed, documented for the future Beetle-Vulkan fix)

Castlevania: SOTN's per-game override was `{"hwContext":"gl"}`. It was believed to be "GL surface pushed at
Beetle's Vulkan renderer" (a mismatch). **It is not:** the W3 pin rewrites `beetle_psx_hw_renderer`
`hardware_vk`→`hardware_gl` (logs `[hwctx-pin]`, nanoarch.go:459), so SOTN runs on **Beetle's OpenGL
hardware renderer** — a clean GL surface + GL renderer, right-side-up, no fallback. That is what works
around SOTN's pillarbox + texture-blending/flashing, which is a **Beetle Vulkan-renderer artifact**. The
real future fix is to root-cause that Vulkan artifact; until then Beetle-GL (and eventually pcsx_rearmed)
are the workarounds. Live-verify which renderer a PS1 room used by grepping the worker log for
`[hwctx-pin] beetle_psx_hw_renderer=hardware_gl` (GL) vs `hw render context: Vulkan` (Vulkan).

## Render profiles + per-room core swap (BUILT 2026-07-21)

The graphics choice is now a **render profile** per game (`ArcadeRendererProfiles`), stored in
`ArcadeGameProfile.RenderProfile`. A profile = `{CoreKey?, HwContext, OptionCore, Options}` — the core lib
to boot (optional override), the surface, which core's option catalog the module shows, and the
renderer-selecting options. The config module's **Graphics** selector lists the system's profiles; **Start
Room** launches whatever profile the game is set to; the play-button Force GL/Vulkan is a per-launch
override that maps to the system's gl/vulkan profile. Uniform across systems:

- N64 → Vulkan (paraLLEl-RDP) / OpenGL (GLideN64); PS2 → Vulkan (paraLLEl-GS) / OpenGL; PSP/DC/GC → surface.
- **PS1 → Beetle (Vulkan) / Beetle (OpenGL) / pcsx_rearmed (OpenGL)** — the third is a real **core-lib swap**.

**Per-room core override (worker fork + coordinator):** `StartGameRequest`/`GameStartUserRequest` gained a
`Core` field (relayed in `workerapi.go`, sent by the shim as `&core=`), and `HandleGameStart` overrides
`game.System` with it AFTER `FindApp` (ROM still resolves by the real system; the whole room then uses the
alternate core's config coherently). A `pcsx_rearmed` core-key was added to `config.worker-gl.yaml`.

**Save landmine — SOLVED:** save-states are core-specific, so a room on an alternate core mints its saveId
with `system = roomSystem + "-" + coreKey` (e.g. `ps1-pcsx_rearmed`), giving it a separate save namespace
from Beetle. Both Beetle renderers share the core (save-states compatible across renderers) so they keep the
bare `ps1`. This makes seeding crash-safe (a pcsx_rearmed room can never be seeded a Beetle state) and the
gateway's `ArcadeSaveId.TryParse` handles the `-`-containing system cleanly.

**Options are per (game, core):** the module shows the selected profile's `OptionCore` options; the flat
`CoreOptionsJson` holds both cores' overrides (keys don't collide), and PUT preserves the other core's saved
options when you switch. The catalog is keyed by CORE, seeded from the embedded `core-options-catalog.json`
(185 quality-relevant options across 15 cores, extracted from each core's `libretro_core_options.h`).

### Deploy checklist (this touches the live emulation stack)
1. **Worker + coordinator:** rebuild from the fork (`D:\Arcade\build\cloud-game-gl`, UCRT64 `go build`),
   regenerate `docker/arcade/patches/fork.patch` via `scripts/export-arcade-fork.ps1`, drain rooms, stop
   both worker tasks + the coordinator, swap the binaries (keep `.pre-*` copies), restart. **Both** must be
   rebuilt — the coordinator silently drops the new `Core` StartGameRequest field otherwise.
2. **Config:** `diff` then `cp docker/arcade/config.worker-gl.yaml → D:\ArcadeStorage\worker-gl\config.yaml`
   (adds the `pcsx_rearmed` core-key), restart workers.
3. **DB:** apply migration `20260722002358_AddArcadeGameRenderProfile` (idempotent SQL; adds `RenderProfile`
   + `HwContext` only if missing — the shared DB may already have `HwContext`). Read the SQL first.
4. **Site:** normal deploy (push to master → CI). 5. **SOTN:** remove its `game-overrides.json` override →
   Beetle-Vulkan default (then reconfigure to pcsx_rearmed via the module if wanted).

## Not yet built (follow-ups)

- **Bulk import** of curated community preset lists into `ArcadeGameProfile`.
- **ForcedFps in the UI** — still SQL/CLI-managed on purpose (see above).
- **Auto-regen hook** so a profile edit also refreshes the worker manifest (only needed for ForcedFps now
  that core options ride the per-room path).

---

**⚠ READ FIRST (2026-07-09): most per-ROM fixes need NO rows here.** The emulators bundle their own
per-game databases and auto-apply them at load — LRPS2's built-in GameDB (12,806 games, verified
applying on this stack), PPSSPP's `compat.ini`, Dolphin's `Sys/GameSettings`, fbneo's drivers,
GLideN64's compiled-in profiles. See docs/arcade-quality-plan.md §18 for what is staged where and
the two rules that keep it working (`pcsx2_enable_hw_hacks` stays off; refresh compat.ini
occasionally). `ArcadeGameProfile` is for the residue only: fps locks, PS1 enhanced-res opt-outs,
per-title downgrades, widescreen opt-ins.
