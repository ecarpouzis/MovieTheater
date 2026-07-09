# Arcade stream-quality plan

Goal: deliver the highest picture quality the stack can actually sustain, per system. Support and
performance are ironed out; almost none of the *quality* knobs the cores expose have been touched.

Everything below was verified against the live worker config, the live worker logs, the actual core
DLLs (option structs parsed out of the PE, not guessed), and `gst-inspect-1.0` on the real MSYS2
GStreamer build. Where something is **unverified**, it says so.

---

## 1. How quality is actually determined (the model)

The encoded frame the browser receives is:

```
core framebuffer  ──►  CloudRetro `scale` (videoconvertscale)  ──►  I420  ──►  NVENC  ──►  WebRTC
 (per-core options)      (nearest by default, integer-ish)              (4:2:0)   (bitrate)
```

Three independent levers, and they are frequently confused:

| Lever | Where it lives | What it does |
|---|---|---|
| **A. Core render/output resolution** | libretro core option (`config.yaml` → `options:`) | More real detail. The core's libretro framebuffer *is* the encoder input. |
| **B. CloudRetro `scale`** | `config.yaml` → `scale:` / `scaleMethod:` | Post-core nearest upscale. Adds **no detail**, but see the chroma rule below. |
| **C. Encoder bitrate/preset** | `encoder.list.h264.params`, or per-room via t=104 | How much of the frame survives compression. |

`buildVideoPipeline` (`pkg/worker/media/gstreamer.go:506`) confirms encoded size = `round(w*scale)`,
and `frontend.go:194` shows `scale` only applies when `> 1`.

### The chroma rule (why `scale` matters even though it adds no detail)

H.264 is 4:2:0 — chroma is stored at **half** the encoded resolution. At `scale: 1`, a 2D game's
chroma is subsampled *below* its native pixel grid: colour bleeds across hard pixel edges,
permanently. At `scale: ≥ 2`, chroma lands at ≥ native resolution and every native pixel keeps its
exact colour.

**So `scale: ≥ 2` is a correctness requirement for every 2D core, not a taste preference.**

---

## 2. What we ship today (measured)

Encoded resolution, read from `Gstreamer [video]` / `Libretro System A/V` lines in the live worker
logs (2026-07-07/08):

| System | Games | Native | **Encoded today** | Verdict |
|---|---:|---|---|---|
| `arcade` (fbneo/`mame`) | 6,214 | 304×224 | **304×224** (`scale` unset) | ✗ chroma below native |
| `ps1` (pcsx_rearmed) | 1,693 | 256×240 ↔ 640×480 | **256×240 / 640×480** (`scale` unset) | ✗ chroma below native |
| `ps2` (LRPS2) | 1,868 | 640×448 | **640×448** (`upscale_multiplier` unset = 1×) | ✗ no upscale |
| `n64` (mupen64plus-next) | 922 | 320×240 | **640×480** (`43screensize`) | ~ 2× only |
| `gc` (dolphin) | 598 | 640×528 | **1280×1056** *(2×; confirmed live — §10 supersedes §6)* | ✓ but bitrate-starved |
| `dc` (flycast) | 297 | 640×480 | **640×480** (no internal-res option set) | ✗ no upscale |
| `psp` (ppsspp) | 473 | 480×272 | **960×544** (2×) | ✓ the only core doing it |
| 2D consoles/handhelds | ~12k | varies | `scale: 3`/`4` | ✓ fine |
| `genesis`/`segacd`/`sega32x`/`neogeo` | ~1.7k | 320×224 | `scale: 2` | ~ minimum |

Encoder: **flat CBR, `preset=p4 tune=ultra-low-latency`, `bitrate` 5–8 Mbps** — identical whether the
frame is 256×224 or 960×544. No per-system logic exists anywhere
(`ArcadeController.cs:371-378` is the only producer, and it just clamps the user's dropdown).

**Headroom:** NVENC is idle. Encoder utilisation 0%, 0 active sessions, ≤2 concurrent rooms against a
driver cap of 8. **The binding constraint is core stability, not the GPU or the encoder.** The logs
show 8 × `0xc0000005` (7 of them GameCube/F-Zero GX, 1 PPSSPP), 1 × `0xc0000028` (PPSSPP), and
13 watchdog kills of wedged workers on 07-07/08. Dolphin and PPSSPP are the fragile ones.

---

## 3. Two bugs found while measuring

### 3.1 Flycast's option prefix is `reicast_`, not `flycast_`

The flycast DLL contains **140 `reicast_*` option keys and zero real `flycast_*` keys**. Every
flycast option named in `docs/arcade-per-game-config.md` — `flycast_widescreen_hack`,
`flycast_internal_resolution`, `flycast_brodcast` — **does not exist**. libretro silently ignores
unknown option keys, so those overrides would have been no-ops with no error.

Nothing is broken *yet* (`ArcadeGameProfile` has 0 rows, `game-overrides.json` is `{}`), but the very
first Dreamcast fix anyone wrote from that doc would have silently done nothing. Correct names:
`reicast_internal_resolution`, `reicast_widescreen_hack`, `reicast_broadcast`, `reicast_region`,
`reicast_alpha_sorting`, `reicast_anisotropic_filtering`, `reicast_texture_filtering`.

### 3.2 The client throws away the core-reported aspect ratio — FIXED (see §11, §13)

Every GL core sets `coreAspectRatio: true` **specifically so the worker populates `av.a`** with the
true (often per-game) aspect ratio. `cloudRetroClient.js:472-478` reads only `flip` and `rot` and
discards `a`, `w`, `h`. `ArcadeRoomPage.js:323` then hardcodes:

```js
const ar = ({ gb: 10/9, gbc: 10/9, gba: 3/2 })[system] || 4/3;
```

with `objectFit: "fill"`, so a wrong ratio **distorts** rather than letterboxes. Currently wrong:

- **`psp` (473 games): 16:9 squeezed into 4:3.** The worst one.
- `gg` (500), `wsc` (94), `lynx` (82), `ngpc` (81, ~1:1 rendered at 1.33), `vb` (24).
- `ps2` / `gc` / `dc` widescreen titles, and vertical arcade boards.

This is a geometry bug, not a resolution one, and it is probably the most *visible* item in this
whole document.

### 3.3 Stock-default cruft on PS1

CloudRetro's embedded default (`pkg/config/config.yaml:284`) sets `pcsx_rearmed_frameskip_type: auto`
for `pcsx`, and our config never overrides it — so PS1 is allowed to **drop frames** on a 13700K that
never needs to. `pcsx_rearmed_dithering` also defaults on; PS1 dither is high-frequency noise that
both looks worse after 4:2:0 and *costs bitrate*.

---

## 4. What is safe per-emulator vs. what must be per-ROM

This is the question that motivated the plan.

**Safe as a global per-core default** — failure mode is *performance*, not correctness, and we have
enormous GPU headroom:

- Internal / render resolution (`reicast_internal_resolution`, `pcsx2_upscale_multiplier`,
  `mupen64plus-43screensize`, `dolphin_efb_scale`, `ppsspp_internal_resolution`).
- Anti-aliasing (`mupen64plus-MultiSampling`, `dolphin_anti_aliasing`, `ppsspp_mulitsample_level`).
- Anisotropic filtering (all four 3D cores).
- Transparency accuracy (`reicast_alpha_sorting: per-pixel`).
- Colour depth (`fbneo-allow-depth-32`, `dolphin_force_true_color`).
- CloudRetro `scale` / `scaleMethod`.
- Encoder preset / AQ / profile.

**Must be per-ROM** (`ArcadeGameProfile.CoreOptionsJson`) — these *break specific games*:

- **PS2 upscaling artifacts.** Upscaling PS2 exposes half-pixel/sprite misalignment that is fixed
  per-title via `pcsx2_half_pixel_offset`, `pcsx2_round_sprite`, `pcsx2_align_sprite`,
  `pcsx2_merge_sprite` — all gated behind `pcsx2_enable_hw_hacks: enabled`, which itself should
  **never** be a global default.
- **Widescreen hacks** (`pcsx2_widescreen_hint`, `dolphin_widescreen_hack`, `reicast_widescreen_hack`)
  — they stretch or break HUDs on native-4:3 games.
- **N64 framebuffer exceptions** — `mupen64plus-EnableNativeResTexrects` is *required* once you
  upscale (otherwise 2D sprites seam), but a few titles regress with it.
- **Forced fps** and **PSP `ppsspp_locked_cpu_speed`** for frame-locked / slowdown-prone titles.
- **Per-game downgrades** for any title too heavy at the global internal resolution.

The mechanism already exists and is unused: `ArcadeGameProfile (System, TitleKey)` →
`arcade-gameconfig-export` → `game-overrides.json` → nanoarch merges before `retro_load_game`
(patch 0009). Identity-keyed, so one row covers every region/revision.

---

## 5. Target settings

Bitrate is derived, not guessed: `kbps ≈ bpp × width × height × fps / 1000`, with `bpp ≈ 0.10` for
2D (highly compressible, flat colour) and `≈ 0.16` for 3D. Clamped to the worker's 500–20000 range.

### 2D cores — lever B only

| Core | Change | Encoded | Default kbps |
|---|---|---|---|
| `mame` (arcade) | `scale: 3` **+ `scaleMethod: nearest-neighbour`** | 912×672 | 5,000 |
| `pcsx` (ps1) | `scale: 2`, `frameskip_type: disabled`, `dithering: disabled` | 512×480 / 1280×960 | 6,000 |
| `gen`,`segacd`,`sega32x`,`neogeo` | `scale: 2 → 3` | 960×672 | 5,000 |
| everything else | unchanged (already 3×/4×) | — | 5,000 |

> ⚠ **Landmine:** stock `mame` ships `scaleMethod: bilinear2`
> (`pkg/config/config.yaml:295`). Setting `scale: 3` without also overriding `scaleMethod` gives a
> *blurry multi-tap upscale* — worse than today. This is exactly the kind of change that looks like
> it worked and didn't.

### 3D cores — lever A

| Core | Change | Encoded | Default kbps |
|---|---|---|---|
| `dc` | `reicast_internal_resolution: 1280x960`, `reicast_anisotropic_filtering: 8`, `reicast_alpha_sorting: per-pixel (accurate)` | 1280×960 | 12,000 |
| `n64` | `43screensize: 960x720`, `MultiSampling: 4`, `EnableNativeResTexrects: Optimized`, `BilinearMode: 3point` | 960×720 | 6,500 |
| `ps2` | `pcsx2_upscale_multiplier: 2x Native (~720p)`, `pcsx2_anisotropic_filtering: 8x`, `pcsx2_native_scaling: Normal` | 1280×896 | 11,000 |
| `psp` | `ppsspp_mulitsample_level: x4`, `ppsspp_smart_2d_texture_filtering: enabled` (hold res at 960×544) | 960×544 | 8,000 |
| `gc` | verify `efb_scale` first (§6); then `dolphin_anti_aliasing`, `dolphin_max_anisotropy: 4` | TBD | 13,000 |

All values above are **exact value tokens read out of the core DLLs**, not display labels.
`pcsx2_upscale_multiplier` really does take the whole string `"2x Native (~720p)"`;
`dolphin_efb_scale` really does take `"2"`; PPSSPP's MSAA key really is misspelled
`ppsspp_mulitsample_level`.

### Encoder

Current: `preset=p4 tune=ultra-low-latency rc-mode=cbr bitrate=N gop-size=100000 repeat-sequence-header=true zerolatency=true`

Proposed (all properties confirmed present on this build's `nvh264enc`):

```
preset=p6 tune=low-latency rc-mode=cbr multi-pass=two-pass-quarter
spatial-aq=true aq-strength=8 bitrate=<per-system> gop-size=100000
repeat-sequence-header=true zerolatency=true
```
plus `profile=high` on the caps (enables CABAC, ~10% bitrate saving).

`zerolatency=true` still forbids frame reordering, so this costs no latency in principle — it must
still be A/B'd. **Instant rollback for the whole encoder: `codec: vp8`.**

---

## 6. The GameCube question — ANSWERED (see §10): `efb_scale: "2"` works, gc streams 1280×1056

Our config comment claims `efb_scale: "2"` = "1280×1056 internal render/stream". The last GameCube
room we have logs for (2026-07-07 21:02) encoded at **640×528**, and that room predates the current
config — so the claim is **untested, not disproven**.

There are two possible worlds and they need different fixes:

1. `efb_scale` raises the libretro framebuffer → the next GC room logs `System A/V >>> 1280x1056`
   and we already ship 2× GameCube. Then only AA/AF remain.
2. `efb_scale` raises only Dolphin's *internal* EFB and it is downsampled back to 640×528 before
   libretro sees it → we have been getting **free supersampled AA** and **zero extra delivered
   pixels**, and the real lever is `dolphin_anti_aliasing` + a `scale:`/output change.

**Resolve this with one room** before touching anything else on `gc`: start F-Zero GX and read the
`Libretro System A/V >>>` line. Dolphin is also our crashiest core, so it goes last regardless.

---

## 7. Adaptive bitrate (ABR)

**It would not drop the game.** `nvh264enc`'s `bitrate` property is flagged
`changeable in ... PLAYING state` — NVENC reconfigures its rate controller in place. No pipeline
rebuild, no renegotiation, no reconnect. This is the *opposite* of the Jellyfin ABR restart storm,
where each rung meant a new transcode.

Two constraints:

- **Only bitrate is free.** Changing resolution mid-room re-inits the pipeline (patch 0015's caps
  renegotiation) — visible glitch. ABR must ride bitrate alone.
- **One encoder per room.** ABR must target the **worst** receiver, not the local one.

Implementation: CloudRetro's Pion factory (`pkg/network/webrtc/factory.go:22`) exposes an
`interceptor.Registry` hook and already calls `RegisterDefaultInterceptors` (which brings TWCC). Add
`pion/interceptor`'s send-side bandwidth estimator (already a dependency, v0.1.45), take the min
estimate across the room's peer connections, and `g_object_set` the encoder's `bitrate`, bounded by
the per-system default as the ceiling and ~1.5 Mbps as the floor.

This *inverts* the current design in a good way: today the user picks a timid 5 Mbps up front. With
ABR we can default to the per-system ceiling and let the network find the floor.

---

## 8. Sequenced work

Every phase is independently shippable, verified with the **test-roms** skill on a real display
(never headless — it fabricates judder), and rolls back by reverting one config key + a worker
restart (workers self-heal via the runner loop).

**One core at a time. Never flip all cores in one restart** — a regression must be attributable.

| Phase | Scope | Rebuild? | Reach |
|---|---|---|---|
| **0** | ✅ **SHIPPED 2026-07-08** — repo `config.worker-gl.yaml` reconciled with the live worker (+ mojibake repaired, deploy direction documented); `reicast_` prefix corrected in `docs/arcade-per-game-config.md` and patch 0009's example | no | — |
| **1** | ✅ **SHIPPED 2026-07-08** — see §9 | config only | **7,907 games (~35%)** |
| **2** | ✅ **SHIPPED + VERIFIED ON PROD 2026-07-08/09** — see §11, §13 | UI build | 6 systems + all widescreen 3D + vertical arcade cabs |
| **3** | ✅ **n64 + ps2 + dc + gc-AA SHIPPED** (§10, §14, §15). §10's "dc blocked" verdict was WRONG — see §15 | config only | 3,087 games |
| **4** | ✅ **SHIPPED 2026-07-09** — `p6` + full-range colour; `profile=high` shipped then REVERTED (breaks Firefox); two-pass-quarter/spatial-AQ rejected. §15, §16 | config only | all |
| **5** | ✅ **SHIPPED + VERIFIED ON PROD 2026-07-08/09** — `CloudRetroHost.DefaultVideoBitrateKbps`; lobby gains "Auto". See §12, §13 | API build | all |
| **6** | ✅ **SHIPPED + VERIFIED 2026-07-09** — patch 0021. See §14 | worker rebuild | all |
| **7** | Seed `ArcadeGameProfile`: PS2 hw-hacks for upscale-sensitive titles, widescreen opt-ins, heavy-title downgrades | data | per-title |

### Deferred / researched-and-parked

- **PS1 real upscaling** needs swapping `pcsx_rearmed` → `beetle_psx_hw` (GL, internal 2–8×, PGXP).
  Blocked on proving memory-card compatibility: our `.srm` harvest depends on
  `pcsx_rearmed_memcard1: libretro` exposing card 1 via `RETRO_MEMORY_SAVE_RAM`. **Do not attempt
  before verifying beetle's save path**, or every PS1 save silently stops being harvested.
- **Feed NVENC RGBA directly.** `nvh264enc` on this build accepts `video/x-raw, format=RGBA` (system
  memory), so the pipeline's forced CPU `I420` capsfilter + `videoconvert` bridge is avoidable —
  NVENC would do RGB→NV12 on-GPU. Saves CPU and removes one conversion. Small patch to
  `buildVideoPipeline`; no user-visible resolution change.
- **Supersampling in the pipeline** (render 2–3×, downscale to a fixed encode size with
  `method=lanczos`). Would give true AA on cores that lack MSAA, at a constant bitrate. Blocked only
  by the `if conf.Scale > 1` gate at `frontend.go:194`; `round()` already handles fractional scale.
  Worth it only if per-core MSAA proves insufficient.

---

## 9. Phase 1 — shipped and verified (2026-07-08)

Applied to `D:\ArcadeStorage\worker-gl\config.yaml` (live; backup
`config.yaml.bak-prequality-20260708-223908`) and mirrored into `docker/arcade/config.yaml`:

- `mame` — **new block**: `scale: 3` + `scaleMethod: nearest-neighbour` (the stock entry pins
  `bilinear2`; without the override this change would have made arcade *blurrier*).
- `pcsx` — `scale: 2` + `scaleMethod: nearest-neighbour` + `pcsx_rearmed_frameskip_type: disabled`
  (CloudRetro's embedded default was `auto`, letting PS1 drop frames on a CPU that never needs to).
- `gen`, `segacd`, `sega32x`, `neogeo` — `scale: 2 → 3`.

Verified through the real product path (test-roms harness, prod origin, real WebRTC):

| Game | System | Encoded before → after | fps | freezes | dropped | decode |
|---|---|---|---|---:|---:|---|
| Metal Slug - Super Vehicle-001 | arcade | 304×224 → **912×672** | 59–60 | 0 | 0 | 0.4 ms |
| Castlevania: SotN (hi-res mode) | ps1 | 640×480 → **1280×960** | 59–60 | 0 | 0 | 0.6 ms |
| Sonic the Hedgehog 2 | genesis | 640×448 → **960×672** | 60 | 0 | 0 | — |

**Nearest-neighbour proven, not assumed.** A canvas probe of the live `<video>` at its intrinsic
resolution measured per-block luma variance: arcade 3×3 blocks **100.0% uniform** (99.5% even among
blocks straddling a source-pixel edge, mean variance 0.008); ps1 2×2 **98.7%**; genesis 3×3
**99.9%**. A multi-tap filter would have left gradients inside every block. Harness:
`scalecheck.mjs` (pattern worth keeping — resolution alone cannot distinguish nearest from blur).

**Bitrate cost was ~nil, as predicted.** PS1 quadrupled its pixel count (640×480 → 1280×960) at the
unchanged flat 5 Mbps and still held 59–60 fps with 0 freezes and 0 dropped frames: an integer
nearest upscale adds no high-frequency detail, so the encoder sees flat N×N blocks. Five rooms ran
with **no exceptions, no watchdog kills, and no worker respawns**.

**Deliberately dropped from Phase 1** (would have been unverified assertions):

- `fbneo-allow-depth-32` — could not confirm from the binary that its default isn't already
  `enabled`; may be a no-op.
- `pcsx_rearmed_dithering: disabled` — dithering trades PS1 15-bit banding against high-frequency
  noise that costs bitrate and survives 4:2:0 badly. The two effects point opposite ways; this needs
  a side-by-side on a real display, not a config diff.

**Open debt this exposed:** the repo still has no accurate copy of the live GL config, so a redeploy
from `config.worker-gl.yaml` would revert all of the above. The live file also carries 27 mojibake
sequences in its comments, so it cannot simply be copied back. Fix in Phase 0 before Phase 3 puts
more tuning into it.

---

## 10. Phase 3 — the load-bearing discovery, and what it blocks (2026-07-08)

### CloudRetro pins the encode size to the core's BASE geometry

`Frontend.FrameSize()` returns `nano.BaseWidth()/BaseHeight()` (`frontend.go:424`) — i.e.
`retro_get_system_av_info`'s `geometry.base_width/height`. The per-frame size a core passes to
`video_cb` is **ignored** for sizing. So raising a core's internal resolution only raises the
*delivered* resolution if that core also scales its **base** geometry.

Measured, one room each:

| Core | Raises base geometry? | Evidence |
|---|---|---|
| `dolphin` (gc) | **yes** | `System A/V >>> 1280x1056 (1280x1056)` with `efb_scale: "2"` |
| `mupen64plus` (n64) | **yes** | `43screensize: 960x720` → client `960x720` |
| `ppsspp` (psp) | **yes** | `960x540 (960x544)`, AR 1.7777778 |
| `flycast` (dc) | **NO** | `640x480 (1706x1706)` — internal res lands in `max_width` |

This settles §6: **`dolphin_efb_scale: "2"` works**; the old 640×528 log line predated it.

### What shipped

`n64`: `43screensize: 640x480 → 960x720` (+2.25× delivered pixels), plus
`EnableNativeResTexrects: Optimized` (native-res 2D HUD/text, required once upscaled) and
`BilinearMode: 3point` (the N64's real texture filter). Verified: **960×720, 60 fps, 0 freezes,
0 dropped, decode 0.7 ms** (Mario Kart 64).

### What was reverted, and why (both negative results — record them)

**1. N64 MSAA showed no measurable anti-aliasing.** `MultiSampling: "4"` vs `"0"` on Mario Kart 64
at 960×720: `hardEdgeFrac` **1.033% vs 1.028%** — a null result. Not shipped: a GPU cost with no
demonstrated benefit. It did *not* break framebuffer effects — Bomberman 64's flat-blue story
cinematic renders identically at MSAA 0 and 4, i.e. it is the **pre-existing** gliden64 limit, not a
regression. (That flat-blue frame looked exactly like an MSAA regression; the A/B is what proved it
wasn't. Do not "fix" it by reverting MSAA next time.)

**2. Dreamcast internal resolution cannot help.** `reicast_internal_resolution: "1280x960"` was
*accepted* (the core logs `retro_get_system_av_info: Res=960`) and still delivered 640×480, per the
base-geometry rule above. It could still supersample — but that could **not be verified**, because
the probe reads the **H.264-decoded** stream and 5 Mbps quantization erases exactly the sub-pixel
gradients AA creates (`hardEdgeFrac` 1.986% @2× vs 1.910% @1× on Crazy Taxi's static title screen —
no smoothing, wrong direction). Reverted to core defaults.

> **Methodological limit worth remembering:** any AA/filtering change must be verified on **raw
> frames at the worker, pre-encode**. Measuring the browser's decoded video cannot see it. Resolution
> changes *are* verifiable client-side (`frameWidth/Height`), which is why Phases 1 and the n64 bump
> could be proven and the AA work could not.

### Deferred, with reasons

- **ps2** (`pcsx2_upscale_multiplier`) and **gc** (`dolphin_anti_aliasing`, `max_anisotropy`): both
  raise real detail, and both are **bitrate-starved today**. GC already ships 1280×1056 = 1.35 Mpx at
  a flat 5 Mbps ≈ **0.06 bits/pixel/frame**. Raising PS2 to 2× would land in the same hole. These are
  gated on **Phase 5** (per-system default bitrate), not on the cores.
- **psp**: already 2× (960×544); MSAA (`ppsspp_mulitsample_level`, sic) hits the same
  can't-verify-through-the-encoder wall.
- **gc** is also our crashiest core (7 of 8 access violations in the logs) — change it last, alone.

### Regression sweep after all Phase 1 + 3 changes

| System | Delivered | fps | freezes | dropped |
|---|---|---:|---:|---:|
| arcade (Metal Slug) | 912×672 | 59 | 0 | 0 |
| genesis (Sonic 2) | 960×672 | 60 | 0 | 0 |
| n64 (Mario Kart 64) | 960×720 | 60 | 0 | 0 |
| gc (F-Zero GX) | 1280×1056 | 60 | 0 | 0 |
| dc (Crazy Taxi) | 640×480 | 59 | 1 | 0 |

Zero exceptions, zero watchdog kills across the whole session.

---

## 11. Phase 2 — client aspect fix (code complete, awaits deploy)

Prod serves a CI-built image, so this cannot be verified end-to-end without a push. Verified in the
two halves that *are* checkable:

- **Server side, measured:** psp reports `AR [1.7777778]` (= 16/9) — the client was forcing 4/3 on
  473 games. fbneo's 1942 reports `AR [0.75]` **with** `rot=90`, i.e. the core already gives the
  *post-rotation* display aspect, and CloudRetro transposes the frame (it arrives 672×768).
- **Client side, unit-tested:** `displayAspect()` extracted as a pure function
  (`cloudRetroClient.js`), 5 tests in `cloudRetroClient.test.js` pinning those measured values.

An early draft inverted the ratio (`1/a`) for rotated boards — which would have flipped every
vertical shooter back to landscape. Stock CloudRetro (`web/js/stream.js` `resize()`) assigns
`style.aspectRatio = a` verbatim and applies `rotate(-rot)` independently; the test now fences this.

`ArcadeRoomPage` prefers `av.a` and falls back to a per-system table only when the core reports
`<= 0` (libretro's "unspecified"). Fallbacks added: `gg`, `psp`, `wsc`, `ngpc`, `lynx`, `vb`.

---

## 12. Phase 5 — per-system default bitrate (2026-07-08)

A flat 5 Mbps was serving encoded frames that differ **~4.6× in pixel count**:

| System | Encoded | Mpx/frame | bits/pixel/frame @ 5 Mbps |
|---|---|---:|---:|
| gc | 1280×1056 | 1.35 | **0.062** ← starved |
| ps1 (hi-res) | 1280×960 | 1.23 | 0.068 (but nearest-upscaled ⇒ cheap) |
| n64 | 960×720 | 0.69 | 0.121 |
| psp | 960×544 | 0.52 | 0.160 |
| arcade | 912×672 | 0.61 | 0.136 (nearest-upscaled ⇒ cheap) |
| dc / ps2 | ~640×460 | 0.29 | ~0.28 |

`CloudRetroHost.DefaultVideoBitrateKbps(system)` now supplies a default when the creator leaves the
lobby's stream-quality on the new **"Auto · match the system"** option (value `0`); an explicit choice
still wins. `ArcadeController.CreateRoom` uses it, then the same `?vbr=` flag path as before.

Two bounds make this safe to ship **without** ABR, and they are what the tests actually pin
(`ArcadeDefaultBitrateTests`, 26 cases):

- **Floor 5000** — the previous flat default, so Auto can never be *worse* than what shipped.
- **Cap 10000** — the lobby's existing "Max" preset, so Auto can never exceed a value the user could
  already have chosen. This matters because **CloudRetro performs no congestion control** (its encoder
  ignores REMB/TWCC): an over-high bitrate silently punishes remote players. Lifting the cap is
  precisely what Phase 6 (ABR) is for.

Values: `gc` 10000, `n64` 8000, `psp` 7000, `ps2`/`dc`/`ps1` 6000, everything 2D 5000.

**Existing settings are NOT migrated.** Someone who deliberately picked "Balanced · 5 Mbps" on a thin
uplink must not be silently moved to Auto (which reaches 10 Mbps on GameCube). They opt in by choosing
Auto once. New users get Auto by default.

---

## 13. Prod verification, and a bug it uncovered (2026-07-08/09)

Phases 2 and 5 could only be verified after deploy. Measured against prod with a fresh browser
profile (so the lobby's new "Auto" default applies), reading the room-create response for the
server-chosen `?vbr=` and the rendered box geometry:

| Game | System | Box aspect before → after | Stream | server `vbr` |
|---|---|---|---|---|
| Daxter | psp | **1.3333 → 1.7778** | 960×540 | **7000** |
| Metal Slug | arcade | 1.3333 (unchanged, correct) | 912×672 | **5000** |
| 1942 | arcade | **4:3 sideways → 0.75 upright** | 672×768 | 5000 |

### The bug: vertical arcade cabinets rendered sideways

Verifying the aspect fix on a vertical board exposed a **pre-existing** defect — 1942's "1UP",
"HIGH SCORE" and "INSERT COIN" were all rotated 90° and squashed into a 4:3 box.

The worker only emits the GAME_START `av` payload **when the core sets `coreAspectRatio`**:

```go
if r.App().AspectEnabled() {   // pkg/worker/coordinatorhandlers.go
    response.AV = &api.AppVideoInfo{ W, H, A: AspectRatio(), Flip: Flipped(), Rot: Rotation() }
}
```

fbneo never set it, so the client learned neither the aspect (0.75 on a vertical cab) nor the
rotation (90). Setting `coreAspectRatio: true` on the `mame` core is safe — `Flipped()` is
`nano.IsGL()`, false for fbneo, so it cannot introduce the upside-down-GL bug that flag exists to
fix. Horizontal boards are unaffected (`a=1.333, rot=0`).

That rotated the picture upright but made it **overflow its box**: a quarter-turn swaps the element's
axes. `rotatedVideoSize(ar, rot)` now swaps width/height for `rot` 90/270 —

```
width  = boxH = boxW / ar  ->  calc(100% / ar)
height = boxW = boxH * ar  ->  calc(100% * ar)
```

— and the transform centres on the box (`translate(-50%,-50%)` before `rotate`), so the rotated frame
fills it exactly. Three tests pin the contract, including the `calc(100% / 0)` guard.

> Note the trap: `av.a` is used **verbatim** (0.75 for a vertical cab — already the post-rotation
> display aspect), while the *element* is what needs its axes swapped. Inverting `a` instead would
> flip vertical shooters back to landscape. Stock CloudRetro does the same (`web/js/stream.js`).

### Latent trap flagged (comment only, no behaviour change)

`CloudRetroHost.ZoneForSystem` still routes `psp/dc/naomi/atomiswave` to a `"gl"` zone. Since the
docker/WSL pool retired, **both** Windows workers register `CLOUD_GAME_WORKER_NETWORK_ZONE=main`, so
no worker serves `"gl"` at all — enabling `ArcadeZoningEnabled` today would route those systems at a
pool that does not exist. `ps2` and `gc` are GL cores and were never added to the list either. It is
harmless only because zoning is off (the descriptor then sends `zone=""`, a wildcard). Delete zoning,
or make every system return `"main"`, before anyone flips that flag.

---

## 14. Phase 6 (ABR) + PS2 — and the silent bug they uncovered (2026-07-09)

### The bug: per-room bitrate never reached the encoder

Patch 0018 (per-room bitrate/FEC) also patches `pkg/coordinator/workerapi.go`. The **coordinator
container image was built 2026-07-06**; 0018 landed 07-08. Its `StartGameRequest` struct therefore had
no `video_bitrate`/`audio_fec` fields, and Go's JSON decoder **silently dropped them** when relaying
`GAME_START` to the worker. Nothing errored anywhere.

So every room — including the per-system "Auto" bitrates shipped in §12 — ran at the worker config's
flat 5 Mbps. Proof: the gateway forwarded `vbr=10000` (it is a dumb WS proxy and never parses
packets), and the worker reported `abr: start … ceiling 5000` and never logged `Per-room encoder
overrides`. The coordinator is the only hop between.

> **Lesson.** §13 verified the bitrate reached the join **URL** and stopped there. A value in a URL is
> not a value at the encoder. Verify at the last hop that can drop it — here, the worker's own log.

**Fix: the coordinator now runs natively on Windows** (`scripts/run-arcade-coordinator.ps1`,
`register-arcade-coordinator-task.ps1`). It does signaling/relay only — no GPU, no GStreamer, builds
with `CGO_ENABLED=0`. This also retires the WSL/docker stack entirely (it was the last container), and
with it the WSL-idle failure mode ("arcade randomly died"). `docker-compose.gpu.yml`'s coordinator
service is commented out, not deleted, as evidence.

### ABR (patch 0021)

Drives the encoder from the **worst peer's** send-side estimate — one encoder per room, so a friend on
hotel wifi throttles everyone. That is inherent to shared-room emulation.

Three things that are easy to get wrong, all in `docker/arcade/patches/README.md`:

- Pion's default codecs carry **no `transport-cc`**, and `RegisterDefaultInterceptors` only installs
  the TWCC report *generator* for inbound streams. Outgoing RTP must be stamped with the TWCC sequence
  number (`ConfigureTWCCHeaderExtensionSender`) or the browser has nothing to report on.
- A send-side estimate **can never exceed what you send**. Following it would pin the room at its
  opening bitrate forever, so ABR probes upward (+15%/tick while the estimate keeps up ≥95%) and drops
  to the estimate below 85%.
- `SetVideoBitrate` must be **re-applied after `Reinit`**: a geometry change rebuilds the pipeline and
  the encoder element, silently reverting to the config bitrate.

**Measured** (GameCube, F-Zero GX, LAN):

| Ceiling | ABR trace | Browser `inbound-rtp` |
|---|---|---|
| 5 Mbps | pinned (start == ceiling) | 4.5 – 5.7 Mbps |
| 14 Mbps | `6000 → 6900 → 7935 → 9125 → 10493 → 12066 → 13875` | 12.0 – 15.9 Mbps |

60 fps, 0 freezes, 0 dropped in both. A real back-off occurred unprompted: `13875 → 11011` when the
estimate fell, recovering to `12662`. **A rung costs one GObject property set** — no rebuild, no
renegotiation, nothing visible.

### PS2 2× upscale

LRPS2 **does** scale its base geometry (unlike flycast), so `pcsx2_upscale_multiplier: "2x Native
(~720p)"` really delivers **640×448 → 1280×896**. Plus `pcsx2_anisotropic_filtering: "8x"`.

It could not have shipped before the bitrate path was fixed:

| Ceiling | jitter buffer | fps |
|---|---|---|
| 6 Mbps | 92–97 ms | 57–73, erratic |
| 12 Mbps | 13–14 ms | locked 60 |

Upscale and bitrate ship together or not at all. Per-game upscaling artifacts (half-pixel offset,
sprite rounding) stay per-title behind `pcsx2_enable_hw_hacks` — never a global default.

### New "Auto" ceilings

ABR makes a generous ceiling free: it walks back down within a second when a peer's link can't carry
it. So the cap now sits **above** the lobby's manual presets.

`gc` 14000 · `ps2` 12000 · `n64` 9000 · `psp` 7000 · `dc` 6000 · `ps1` 6000 · everything 2D 5000.
Floor stays 5000 (never worse than the old flat default). A new manual **LAN · 16 Mbps** preset exists
for a fat pipe; ABR still protects remote players from it.

---

## 15. Raw frames change the answers (2026-07-09)

§10 said AA was unverifiable and Dreamcast was blocked. Both were wrong, and both were wrong for the
same reason: **we were only ever looking at the decoded stream.** Worker patch 0022 dumps the raw frame
at `video_cb` — before scaling, before 4:2:0, before NVENC — and every open question fell out.

### Dreamcast was throwing away 3 of every 4 samples

flycast's `video_cb` hands us **1280×960** when `reicast_internal_resolution` says so. CloudRetro sized
the capsfilter *output* from the core's base geometry (640×480), so `videoconvertscale` downscaled 2:1
with the **default nearest filter** — point-sampling. Nearest downscale doesn't anti-alias, it *aliases*:
strictly worse than rendering at native.

**No worker patch was needed.** `scale` multiplies the base, and patch 0015 renegotiates the *source*
caps to the core's real frame size, so `base(640×480) × scale(2) == source(1280×960)` makes the scaler a
passthrough. DC now delivers **1280×960**, 60 fps, 0 dropped. Its Auto ceiling rose 6000 → 11000.

### The AA verdicts, on raw frames

Absolute edge counts are useless (the same config twice: hardEdge 0.157% vs 0.576%). What AA does is
convert **hard** edges into **mid** ones, so the scene-robust metric is the *share* of edges that are
hard.

| Core | MSAA option | Verdict |
|---|---|---|
| **gc** (dolphin) | `dolphin_anti_aliasing: "2"` (4× MSAA) | **works** — hardShare 6.29% → 4.08%, no overlap over 3 runs each. **Shipped.** |
| n64 (gliden64) | `mupen64plus-MultiSampling` | **inert** — BIT-IDENTICAL raw frames at 0/4/8 |
| psp (ppsspp) | `ppsspp_mulitsample_level` | **inert** — BIT-IDENTICAL |

libretro's `hw_render` FBO isn't multisampled; only Dolphin's OGL backend owns its framebuffers. §10's
"n64 MSAA does nothing" was *right*, but for the wrong reason — now it's proven at the source.

### Audit: does any other core hand us more than we deliver?

| System | raw | delivered | |
|---|---|---|---|
| n64 / gc / psp | 960×720 / 1280×1056 / 960×540 | same | clean |
| **dc** | 1280×960 | 640×480 | **fixed** |
| **ps2** | 1024×896 | 1280×896 | non-integer 1.25× **nearest** stretch → `scaleMethod: bilinear2` |
| snes | 256×224 | 768×672 | intended integer nearest upscale |

### Phase 4: encoder tuning, measured properly

QP cannot rank encoder settings — a better preset makes smarter mode decisions and reaches the same
quality at a *coarser* QP (measured: p6 raised mean QP while spending more bits). The right test is
reference-based. So: dump 120 deterministic Mario Kart 64 frames, encode/decode them offline through
`gst-launch` + `nvh264dec`, PSNR against the source.

| Setting @ 960×720 | 8 Mbps | 4 Mbps |
|---|---|---|
| `p4 tune=ultra-low-latency` (baseline) | 38.14 dB | 33.85 dB |
| **+ `profile=high`** | **39.16** | **34.49** |
| `p6` + `profile=high` | 39.18 | 34.62 |
| `p7` + `profile=high` | 39.18 | 34.78 |
| `p6` + `two-pass-quarter` | **37.51** ✗ | — |
| `p6` + `2pq` + `spatial-aq` | **36.98** ✗ | — |

**`profile=high` (CABAC + 8×8 transform) is worth ~+1.0 dB and is free.** The preset barely matters next
to it. `multi-pass=two-pass-quarter` measured *worse*; `spatial-aq` lowers PSNR by construction (it moves
bits to where the eye looks) — it may be perceptually better, but nothing available can prove that, so it
stays off. Shipped: `preset=p6` + `profile=high`.

Chrome decodes High profile even though Pion advertises a baseline `profile-level-id` (verified live on
gc/snes/ps1). **Not yet verified on Firefox** — rollback is deleting `,profile=high` from the caps.

> **The general lesson.** Anything the encoder can erase (AA, filtering, presets) must be measured
> before the encoder. Anything the encoder cannot erase (resolution, geometry, bitrate) can be measured
> in the browser. Two of this plan's "unverifiable" verdicts were just the wrong instrument.

---

## 16. The overnight sweep: every remaining item, checked (2026-07-09)

### Colour range — was silently costing every system

The cores emit RGB with luma **0..255**. The pipeline converted to **limited** range (16..235) and
correctly signalled limited, discarding ~14% of the code values. CloudRetro's capsfilter says
`color-range=0_255` — **that is not a `video/x-raw` caps field in GStreamer** and was silently ignored.
The field that matters is `colorimetry`.

Fixed with `encoder.video.colorimetry: "1:3:5:1"` (full range, BT.709). Measured end-to-end against the
raw source: limited (viewer-expanded) **37.751 dB** vs full **37.872 dB**. The SPS now carries
`video_full_range_flag=1` (it was 0) and Chrome reports `VideoFrame.colorSpace.fullRange = true`.

### Supersampling — the AA that N64/PSP could always have had (patch 0023)

`if conf.Scale > 1` → `> 0 && != 1`. That is the whole patch; `round()` already handled fractions.
On identical source frames, 1920×1440 → 960×720: hard edges **2.112% (nearest) → 1.792% (bilinear2)**.

| system | renders | delivers | CPU/room |
|---|---|---|---|
| n64 | 1920×1440 | **1280×960** | 1.02 cores (0.37 before) |
| psp | 1920×1088 | 960×544 | 0.64 |
| gc | 1920×1584 | 1280×1056 (+4× MSAA) | 1.38 |
| dc / naomi / atomiswave | 1920×1440 | 1280×960 | 1.09 |

> A fractional scale **must** be paired with a smooth `scaleMethod`. Nearest downscale point-samples: it
> aliases rather than anti-aliases. That was exactly DC's bug.

`naomi`/`atomiswave` carried the identical flycast trap, dormant only because no games are enabled.

### Anisotropic filtering — shipped, and it is not an AA operation

`hardShare` cannot score it, and shouldn't: on a deterministic DC frame, 16× moved **mid-frequency edges
+7%** with no meaningful hard-edge change. That is *retained texture detail*, which is what aniso does.
dc's `off` measured byte-identical to its stated default, so the default was effectively off. Shipped:
dc 16×, gc 16×, ps2 8×.

### Accuracy options

- **ps2 `blending_accuracy: Medium`** — shipped, measured free (60fps, 0 freezes).
- **dc `alpha_sorting: per-pixel`** — **rejected**: 58fps and 3 freezes vs 60/0 at the per-triangle
  default. Per-pixel OIT is not worth it here.

### RGB straight into NVENC — rejected

`nvh264enc` accepts RGB, so the CPU I420 conversion looked skippable. It measures **37.274 dB vs
37.872** *and* NVENC forces limited range when it does the conversion itself, throwing away the
full-range win. CPU saving was negligible (0.70 s vs 0.75 s wall). Rejected on evidence.

### `profile=high` — shipped, then reverted

+1.02 dB at 8 Mbps, and Chrome decodes it. But without a profile in the caps `nvh264enc` emits
`profile_idc=66` (Constrained Baseline), which is exactly what Pion advertises (`42001f`). Forcing High
emits `100` while the SDP still promises baseline. Firefox's WebRTC decoder is OpenH264, which decodes
only *some* subsets of High — a friend on Firefox gets **no video, silently**. Reverted.

### AV1 — works, quantified, opt-in (patch 0024)

At **matched actual bitrate** (nvav1enc overshoots CBR by ~19%, so the naive comparison flatters it by
2.4 dB): **+0.884 dB for 1.6% fewer bits**, ≈15% bitrate saving. Verified live end-to-end
(`mimeType: video/AV1`, full range, 60fps). Chrome **and Firefox** advertise AV1 receive; **Safari does
not**, and CloudRetro negotiates one codec per room — so it stays opt-in via `encoder.video.codec: av1`.

### PS1 upscaling — the blocker is NOT saves

Beetle PSX HW defaults memory card 0 to libretro `.srm` (the same path our harvest uses; the formats are
"internally identical"). It boots and streams on our worker. But it **falls back to software rendering**:
the log shows `[rhi_gl_open] requesting hardware render context: OpenGL Core 3.3+`, and nanoarch's WGL
path hands out a *compatibility* profile. Delivered 320×240 regardless of
`beetle_psx_hw_internal_resolution`. **The blocker is the GL context profile, not memory cards.** Fixing
it means teaching nanoarch to honour a core-profile hw_render request.

### Still open

- **`spatial-aq`** — PSNR says worse *by construction* (it moves bits to where the eye looks). Needs a
  subjective A/B on a real display; nothing here can settle it.
- **N64/PSP MSAA** remains inert (proven bit-identical). SSAA replaced it.
- **`profile=high` / AV1 by default** both hinge on which browsers the friend group actually uses.
