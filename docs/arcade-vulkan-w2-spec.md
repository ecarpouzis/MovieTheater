# Vulkan W2 — N64 paraLLEl-RDP (spec for the implementing agent)

**Mission**: bring N64 onto the Vulkan capture path with paraLLEl-RDP — per-GAME, like PS2's
paraLLEl-GS — and prove it end-to-end (accuracy + optional internal upscaling), without disturbing
the GL default (GLideN64) that every other N64 title keeps using.

**You are building on a DONE foundation** — read these first, in order:
1. `docs/arcade-vulkan-capture-spec.md` (M1: the frontend — negotiation v1+v2, interface v5)
2. `docs/arcade-vulkan-capture-m2-spec.md` (M2: Vk→GL zero-copy — semaphore sync, pool realloc)
3. `RESULTS-vulkan-m1.md` + `RESULTS-vulkan-m2.md` in the fork (what was validated, and how)
4. `pkg/worker/caged/libretro/nanoarch/nanoarch_vulkan.c`, `nanoarch.go` (vk plumbing),
   `overrides.go` (`WantsVulkanRenderer`), `coordinatorhandlers.go` (room-level decision)

Repo: `D:\Arcade\build\cloud-game-gl`, branch `vulkan-w2` off `movietheater-fork` (HEAD f4073b1).
Worker build: same UCRT64 go build the fork always uses (see the repo's build scripts / prior
RESULTS). Do not push; do not touch the shipped `fork.patch`.

## 1. What to build

### 1.1 Generalize the per-game Vulkan inference
`WantsVulkanRenderer(gameKey)` (overrides.go) currently answers true only for
`pcsx2_renderer ∈ {paraLLEl-GS, Vulkan}`. Generalize it into a small table keyed by option
name/values so N64 joins:
- mupen64plus-next requests paraLLEl-RDP via its core options — discover the EXACT option key and
  value strings from the core itself (dump its option list in a bench room log, or read the core's
  libretro code; expected shape: `mupen64plus-rdp-plugin=parallel`, plus
  `mupen64plus-rsp-plugin=parallel` which paraLLEl-RDP REQUIRES — verify both names, don't trust
  this spec). Inference: rdp-plugin option value == "parallel" → Vulkan room.
- Keep the shape data-driven enough that W3/W4 cores (flycast/dolphin/ppsspp) can be added as
  single entries later, but DO NOT wire those systems now.

### 1.2 Nothing else in the vk frontend should need changes — verify, don't assume
- mupen64plus-next's Vulkan path is expected to use negotiation **v1** (no create_instance/
  create_device2). Our frontend supports v1+v2 (M1). If the core surprises you (v2, or interface
  version ≠ 5), document and adapt.
- paraLLEl-RDP is a Vulkan COMPUTE renderer — it may request device features/queues in its own
  device creation (v1 negotiation means WE create the device via the wrapper): make sure the
  extension-injection wrapper (M2 §1.2) doesn't fight its requirements; log what it asks for.
- N64 is a **LibCo** core: serialize/unserialize must ride the same_thread path (patch 0030).
  Save/Load state round-trip is a MANDATORY validation arm (PS2 skips this; N64 cannot).
- `skip_hw_context_destroy`: probe clean close ×5 WITHOUT the skip first; add the core to the
  skip-list only if it wedges (document either way).

### 1.3 Zero-copy + geometry
The M2 pool path should carry N64 frames unchanged. N64 flips geometry (240p/480i, per-game hi-res
modes, paraLLEl-RDP upscaling changes the FRAME size the core reports via av_info/video_cb — the
libretro sizing contract from the M1 F1 review lesson). Exercise realloc: boot a title, change
upscaling mid-session if the core applies it live, or across two rooms.

## 2. Validation arms (all pool-idle gated; bench user is ArcadePlayer2/user 33)
Titles: pick 3 from the library with different renderer stress: Mario Kart 64 (baseline-known),
Zelda OoT class (hi-res + interlace), Bomberman 64 (the drive harness knows its menus —
`.claude/skills/test-roms/arcade-drive.mjs` / `ps2-bench.mjs` in the MAIN repo; N64 keys: X=A
confirm, Z=B, Enter=Start, arrows=stick).
1. **A/B per title**: GL/GLideN64 (current default) vs Vk/paraLLEl-RDP @1x: pace-diag
   (ticks/s=60, audio 48000, forgiven=0), screenshots for correctness (colors, no tearing, UI
   intact), close clean.
2. **Upscaling arm**: paraLLEl-RDP upscale 2x (and 4x if 2x is free): same bars. Note encode size
   stays at base geometry unless av_info doubles — record what actually happens (this feeds W6).
3. **Save/Load state round-trip** on Vk (t=106/107 equivalents via the site buttons or the
   frontend API): state saves, loads, and the game continues; no wedge.
4. **Mixed sequence**: vk-N64 room → GL room (any 2D core) → vk-PS2 Stuntman room on ONE worker
   process; then close ×5.
5. **Fallback**: `CLOUD_GAME_VK_ZEROCOPY=0` room boots and plays via M1 readback.

## 3. Deliverables
- Branch `vulkan-w2` (clean commits), `RESULTS-vulkan-w2.md` (evidence per arm, deviations, open
  questions), compile-proven patch saved to the session scratchpad, shipped `fork.patch` untouched,
  live pool restored to shipped state (binary + configs) after your windows.
- A recommended per-title `ArcadeGameProfile` option set for the N64 titles that passed (rows are
  authored by the reviewing session, not you).
- **No deploy, no merge** — review gate is the authoring session, same as M1/M2.

## 4. Footguns inherited (M1 §6 + M2 §3 all apply)
- Pool-idle before EVERY recycle/bench; the user may open a room at any time.
- Workers read `game-overrides.json` from their ConfDir — it is GENERATED (arcade-gameconfig-export
  from DB profiles); hand-edits are temp-arm-only and MUST be restored (and are BOM-sensitive:
  write BOM-less UTF-8 or the worker silently drops ALL overrides — burned us 2026-07-16).
- Room ids are deterministic per (user,game) — a recycle guard can refuse for ANOTHER worker's
  room; identify workers by UDP mux port (8446/8447), never by coordinator /status order.
- Never `Stop-Process` a worker; `scripts/recycle-arcade-glworker.ps1` only.
- Headless Chrome fabricates judder — pacing verdicts come from pace-diag, not the browser.
