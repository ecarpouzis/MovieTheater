# Arcade Vulkan quality & expansion plan (post-M2)

**Context (2026-07-16).** The Vulkan capture path is DONE and LIVE: libretro Vulkan negotiation
(v1+v2), hw-render interface v5, and M2 Vk→GL external-memory zero-copy (semaphore sync, frames
never visit system memory). Proven on LRPS2/paraLLEl-GS: Stuntman ships at 2× high-res scanout,
"incredibly smooth", `vidpush=0`. This unlocks Vulkan renderers across every 3D core we run —
paraLLEl-RDP (N64), PPSSPP-Vk (psp), flycast-Vk (dc), Dolphin-Vk (gc) — plus deeper PS2 visual
options. This doc is the umbrella plan; each workstream runs the proven loop: **spec → Opus
implements → authoring-session review → gated live validation (pool-idle, driven bench,
pace-diag/perf-attr A/B) → Eric verdict → merge + profile flip + memory.**

**Hard constraints carried over (non-negotiable):**
- Pool-idle gates on every deploy/recycle/bench. Never `Stop-Process` a worker (graceful recycle
  script only). Own-tooling contention fabricates stalls (benchmark-isolation memory).
- `WaitGS` is PS2's frame-pacing; GS-side cost lands in tick time on EVERY core with a
  synchronous video path — measure with pace-diag + perf-attr, never by feel.
- PSP has NO save-states (both directions); the memstick card is the progress. Renderer changes
  don't touch that, but every other core must prove serialize/unserialize on its NEW context
  (patch 0030 same-thread rules; the deferred boot-restore dance).
- Per-game fixes live in the emulators' OWN DBs/INIs (hw_hacks off or PS2 auto-fixes die;
  Dolphin game INIs override options).
- Profiles are DB rows (`ArcadeGameProfile`) exported by `arcade-gameconfig-export` — never
  hand-edit game-overrides.json durably.
- `WantsVulkanRenderer` (overrides.go) currently infers ONLY from `pcsx2_renderer` — every new
  Vulkan core needs its inference key added (or a `hwContext` per-game override field).

---

## W1 — PS2 visual ceiling (config-only; no code) — FIRST
The core already exposes: `pcsx2_pgs_ssaa` (paraLLEl-GS super-sampling), `pcsx2_pgs_high_res_scanout`
(2×, live), `pcsx2_upscale_multiplier` (HW renderers), `pcsx2_renderer`.
- Probe arm: dump `pcsx2_pgs_ssaa` values (bench room, log shows option values), A/B Stuntman
  at each SSAA level on the 4070 Ti: pace-diag ticks/s + maxTick under driven load, screenshots.
  SSAA smooths edges WITHOUT changing scanout geometry (encode size stays 1280×896 — no new
  encode cost; the "encode size = core base geometry" rule).
- If SSAA holds 60: flip Stuntman's profile (+ssaa), then evaluate the other 16 cached PS2
  titles for paraLLEl-GS profiles (start with the fast ones; heavier titles get probe arms).
- Also try LRPS2's plain Vulkan HW renderer + `pcsx2_upscale_multiplier` ≥2 for titles where
  paraLLEl-GS isn't the right fit (GameDB gsHWFixes apply there).
- Deliverable: per-title `ArcadeGameProfile` rows; a short results table in this doc.

## W2 — N64 paraLLEl-RDP (the marquee unlock)
mupen64plus-next carries paraLLEl-RDP (Vulkan-only; accuracy + internal upscaling + SSAA-style
resolve). Today N64 runs GL (glide/parallel-GL path?) with GL zero-copy.
- Wiring: generalize `WantsVulkanRenderer` — key off the N64 RDP plugin option value
  (e.g. `mupen64plus-rdp-plugin=parallel`) the way it keys off `pcsx2_renderer` today.
- Spec must cover: negotiation version this core uses (v1 expected — our frontend handles both),
  LibCo same-thread serialize on the Vulkan context, geometry flips (N64 interlace/hi-res modes),
  SAVE_RAM harvest unchanged, `skip_hw_context_destroy` need (probe close ×5).
- Options pass: upscaling factor, deinterlacing, native-res downsampling — A/B on Mario Kart /
  Zelda class titles from the library.
- Risk: paraLLEl-RDP wants Vulkan compute queues — our create_device wrapper injects extensions
  but must not fight the core's own queue selection (same lesson as LRPS2 v2 create_device2).

## W3 — PSP PPSSPP-Vulkan
PPSSPP's Vk backend has better frame pacing + lower driver overhead than GL — likely retires the
crackle-headroom concern (crackle = pacer forgiveness under contention, fixed by REPAY; Vk adds
headroom on top).
- Wiring: psp per-game or system-wide `hwContext: vulkan` once validated (PPSSPP is one of the
  best-tested libretro Vk cores).
- Keep: psp-scoped audio jitter buffer, memstick card seed/harvest (renderer-agnostic), the
  200ms savedata delay (deliberate).
- Validate: the PSP boot chain on Vk (SCENET/lang errors are cosmetic today — confirm no new
  ones), GL-zc→Vk-zc switch, close ×5, mixed-core sequences on one worker process.

## W4 — Dreamcast flycast-Vulkan
flycast Vk (incl. per-pixel alpha/OIT) fixes DC's chronic alpha-sorting artifacts.
- Keep: VMU GLOB harvest (BIOS-dir VMUs), per-worker ConfDir, 16x aniso equivalent on Vk.
- flycast corrupts the heap on teardown (patch 0041 note) — the harvest-first ordering already
  protects saves; verify it still holds on the Vk context.

## W5 — GameCube Dolphin-Vulkan (probe-only; lowest priority)
Dolphin "runs good as-is" (stock GL core = full 60 after the custom-core retirement). Only flip
if a probe shows a measurable win (e.g. Vk ubershaders vs GL shader-compile hitches). Remember:
option VALUES not labels (patch 0016 lesson); game INIs override.

## W6 — Stream/encode quality pass (small fork feature)
With cores rendering at 2×+ internally, the stream is the next bottleneck:
- Per-game encode-scale (~60-line fork feature, previously deferred): let capable titles encode
  ABOVE base geometry (or confirm high-res-scanout titles already encode at the doubled size —
  Stuntman does: encode size = core base geometry, which scanout doubles).
- Re-derive bitrates at the new sizes (VBV = kbps/20 rule), AV1-vs-codec-pill per room, ABR
  (worst-peer TWCC) sanity at higher rates, LAN + remote leg.
- Chroma: encode scale ≥2 requirement (stream-quality memory) — check each Vk core's geometry.

## W7 — Capture worker / heavy lane (evaluate only — likely N/A)
The heavy lane streams via Moonlight/Apollo (not our encoder) and the capture lane uses the
patched gst d3d12 screen-capture — the libretro Vulkan work doesn't apply directly. One probe
note: the capture worker binary on 8448 is still pre-srflx/pre-M2; update it during the next
capture-lane window regardless. Anything deeper (Vulkan-based capture of caged apps) is a
separate future design, not this effort.

---

## Patch-compat matrix (verify per workstream, don't assume)
| Fork patch / behavior | ps2-Vk (done) | n64-Vk | psp-Vk | dc-Vk | gc-Vk |
|---|---|---|---|---|---|
| skip_hw_context_destroy | REQUIRED | probe | probe | probe | probe |
| 0030 same-thread serialize | n/a (works) | must-test | n/a (no states) | must-test | must-test |
| Vk-zc pool + geometry flips | proven | must-test | must-test | must-test | must-test |
| Save/card seed+harvest | proven | SAVE_RAM | memstick | VMU GLOB | card dirs |
| Cheats (0027) | works | probe | probe | probe | probe |
| Aniso/option names | pgs opts | rdp opts | ppsspp opts | flycast opts | dolphin opts |

## Execution order & gating
1. **W1 now** (config-only, biggest visible win per effort — includes "Stuntman even better").
2. **W2 spec → Opus** (flagship; most new wiring).
3. **W3**, then **W4**; **W5** probe only; **W6** after W1-W3 land (its sizes depend on them);
   **W7** note-only.
Each workstream gets its own spec doc (`arcade-vulkan-w<N>-spec.md`) when it starts; RESULTS file
in the fork; validation criteria written BEFORE the arm runs.
