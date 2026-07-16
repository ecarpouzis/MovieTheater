# W3/W4/W5 spec — PPSSPP-Vulkan, flycast-Vulkan+OIT, Dolphin-Vk patch compat

**Authoring session, 2026-07-16.** Child spec of `docs/arcade-vulkan-quality-plan.md` (read it first).
Implementation = one Opus agent, compile-only; live validation is GATED and happens in the authoring
session with Eric. Fork base: `D:\Arcade\build\cloud-game-gl`, branch `vulkan-w2` @ `7f96784`
(= the live `worker.exe` / w2fix8). Work on a new branch `vulkan-w3` off that.

## State you inherit (do not re-derive)

- Vulkan capture is LIVE for PS2 (paraLLEl-GS) and N64 (paraLLEl-RDP): negotiation v1+v2,
  hw-render iface v5, M2 Vk→GL external-memory zero-copy, sub-16px warmup guard, caps reneg,
  per-game GL escape via `glRendererOptions`. "Vulkan by default" = core config `hwContext: "vulkan"`
  + `isGlAllowed: false` (`coordinatorhandlers.go`: `vkGame := coreCfg.HwContext=="vulkan" || WantsVulkanRenderer(...)`).
- **The W3 surface fix already shipped**: commit `4451d47` enables `VK_KHR_surface` +
  `VK_KHR_win32_surface` on the v1 headless instance (PPSSPP unconditionally loads
  `vkGetPhysicalDeviceSurface*KHR` and fastfailed 0xC0000409 without them). See the comment block
  at `nanoarch_vulkan.c:510`. So PPSSPP-Vk has no KNOWN code blocker — the work is wiring, escape
  hatch, teardown, and evidence.
- PPSSPP/flycast/dolphin have **no renderer core option** — they follow the frontend's hw context.
  So the `vulkanRendererOptions`/`glRendererOptions` inference cannot express a per-game escape for
  these systems. That is the main new fork feature (below).
- Live config file is `docker/arcade/config.worker-gl.yaml` ONLY (config.yaml is dead). psp block:
  stock `ppsspp_libretro`, IR JIT + fastmem off, `audioMaster: true`, `vfr: false`, `usesLibCo: true`,
  `hacks: [skip_hw_context_destroy]`, streams 960x544. dc/naomi/atomiswave: flycast, base 640x480
  ALWAYS (internal res goes to max_width), `scale: 2` + `scaleMethod: bilinear2`, internal res
  1920x1440, `reicast_` prefix (NOT `flycast_` — unknown keys are silently ignored). gc: custom
  dolphin core (`dolphin_custom_libretro`), mode "2" async ubershaders, efb 3 + scale 0.6667,
  `coreCache` stamping, `hacks: [skip_hw_context_destroy, skip_serialize_size_probe]`.
- PSP has NO save-states either direction (patch 0041 measured); the memstick card is the progress.
  The 200 ms savedata delay is DELIBERATE. The rare crackle is an audio UNDERRUN under contention
  (repay pacer fixed the sustained case); Vulkan's contribution is HEADROOM (lower/steadier tick).
- flycast corrupts the heap on teardown — save (VMU GLOB) harvest-first ordering already protects
  saves; must still hold on a Vk context. Dolphin game INIs override base options; core-option
  VALUES not display labels.

## F1 (fork) — explicit per-game `hwContext` override

Add `hwContext` to the game-overrides manifest schema (`GameOverride` in overrides.go):
allowed values `"gl"` / `"vulkan"` / absent. Precedence, evaluated in the room layer:

1. per-game `hwContext` (new field) — absolute;
2. renderer-option inference (`WantsVulkanRenderer` / `ForcesGlRenderer`) — unchanged, still
   serves ps2/n64 profiles already exported;
3. core config `HwContext` default.

Keep the inference tables; do NOT wire psp/dc/gc rows into them (the field replaces that need).
Honor the existing rule that gl.enabled must be true when the resolved context is "gl" even if
`isGlAllowed: false` (the escape-hatch fix in nanoarch.go). Unit-test the precedence if the pkg
has tests; otherwise a table in the results doc with the 6 combinations.

## W3 (fork + config) — PSP on Vulkan

1. Source-verify PPSSPP's libretro Vk contract from `D:\Arcade\build\ppsspp` (or the stock core's
   headers/strings if source absent): negotiation version (v1 expected — we handle both),
   create_device features/extensions it asks for, whether its delivered frame geometry equals
   av_info base (480x272 × internal-res multiplier) and whether it can CHANGE mid-session
   (internal-res option change, FMV) — our zc path handles caps reneg, but document what to expect.
2. Teardown: psp-GL needed `skip_hw_context_destroy` (deep NVIDIA GL destroy on the small libco
   stack). Check what the Vk teardown path (`nanoarch_vulkan.c` deinit) does for a libco core —
   does the hack apply/matter on Vk? Fix or document. Close ×5 is a validation gate.
3. Proposed config diff (edit `docker/arcade/config.worker-gl.yaml` in the F: working tree, leave
   UNCOMMITTED): psp block gains `hwContext: "vulkan"`, `isGlAllowed: false`; everything else
   (stock lib, IR JIT, fastmem off, audioMaster, vfr, usesLibCo, hacks, scale) UNCHANGED. Note in
   the results doc: cpu_core is orthogonal to the renderer; stock stays pinned to IR JIT either way.
4. Rollback = revert the two config lines (document as the one-liner).

## W4 (config + probe wiring) — Dreamcast flycast Vulkan + per-pixel OIT

1. Source-verify flycast's libretro Vk contract the same way (`D:\Arcade\build\flycast` if present,
   else DLL strings + libretro upstream source): negotiation version, extensions, and CRITICALLY —
   on the Vk interface, does it hand frames at BASE geometry (640x480) or at internal-res
   (1920x1440)? The GL path always delivered base; if Vk delivers internal-res, the dc `scale: 2`
   must be re-derived (encode = base × scale rule) — propose the corrected numbers, don't guess.
2. Proposed dc config diff (UNCOMMITTED): `hwContext: "vulkan"`, `isGlAllowed: false`,
   `reicast_alpha_sorting: "per-pixel (accurate)"`, `reicast_oit_abuffer_size: "512MB"` (verify the
   value token from the DLL's option strings first — VALUES not labels). naomi/atomiswave stay GL
   until dc validates; note the lockstep follow-up.
3. OIT was REJECTED on GL for perf (58fps + 3 freezes vs 60/0). The validation A/B (gated, not
   yours) re-tests it on Vk: OIT-on must hold 60/0-freeze on Crazy Taxi + Sonic Adventure + a
   known alpha-artifact title. If flycast-Vk can't zero-copy (missing external-memory ext on its
   device), it falls to the M1 readback path — which still has the mid-stream caps-change bug
   (gstreamer.go, "Internal data stream error -5"); check whether that latent bug becomes live for
   flycast and fix it if cheap (the guard/reneg already exists on the zc path).
4. VMU GLOB harvest-first ordering + `skip_hw_context_destroy` on the Vk teardown: same check as W3.2.

## W5 (source-audit only, NO deploy) — Dolphin custom-core patches vs Vulkan

Answer Eric's question with source evidence from `D:\Arcade\build\dolphin` (tag 2606.88-mt @287ab2d):

1. Does the dolphin libretro port support libretro Vulkan negotiation at all, and which version?
   How does it pick the backend (`dolphin_renderer` values? hw context type?)? Cite files/lines.
2. `dolphin-custom-core.patch` (CMakeLists CXX release-flags fix): backend-agnostic — confirm it
   still applies and that a Vk build would carry /O2 + NDEBUG (the 13 MB vs 21 MB tell).
3. `dolphin-createsharedcontext.patch` (WGL.cpp / GLContextLR): GL-only file. Confirm that under a
   Vulkan backend it compiles but is never exercised, and that Vulkan async ubershaders (mode "2")
   compile on their own threads WITHOUT shared GL contexts (i.e. the patch is unnecessary-but-inert
   on Vk). Cite the Vk shader-compile path.
4. Cache: do Vulkan pipeline caches land under `User/Cache` paths covered by our `coreCache.purge`
   globs (`Shaders`, `*.uidcache`)? Note that host-config keys include the backend → a GL→Vk flip
   means a COLD cache per game (mode-2 warm-up ~29 ticks/s for minutes on first boot).
5. Verdict: is a gc-Vk probe ever worth running, given stock-GL holds 60 flat? Recommendation only —
   gc does NOT flip in this workstream.

## Deliverables

- Fork branch `vulkan-w3`, committed (small commits, style of `2c2ec7a`/`1d84591`). Do NOT push.
- Staged binary `bin/worker.w3.exe` (build with the usual recipe; verify it compiles clean).
  NEVER touch `bin/worker.exe` or anything under `D:\ArcadeStorage`.
- F: working-tree edits, UNCOMMITTED: config.worker-gl.yaml (psp + dc blocks), regenerated
  `docker/arcade/fork.patch`, and — for the hwContext export — the minimal `ArcadeGameProfile` /
  `arcade-gameconfig-export` change. If that needs a DB column: write the ALTER script under
  `scripts/` and the C# code, but DO NOT run SQL against the database (shared prod/dev DB).
- `RESULTS-vulkan-w3.md` in the fork root: what changed, source-audit answers (W3.1, W4.1, all of
  W5 with citations), the precedence table, proposed config diffs, validation checklist for the
  gated arm, rollback one-liners, open risks.

## Hard guardrails (violating any of these is a failed run)

- COMPILE-ONLY. No live deploys, no `Stop-Process`, no scheduled-task restarts, no rooms, no
  browsers/benchmarks on this box (your own load fabricates emulator stalls — benchmark-isolation
  rule), no edits under `D:\ArcadeStorage`, no SQL execution, no pushes to any remote.
- PowerShell 5.1 `Out-File`/`Set-Content` default UTF-16/BOM — any file the worker or tooling reads
  must be written BOM-less UTF-8.
- Option VALUES are core-options-v2 value strings, never display labels; `reicast_` prefix on
  flycast options; unknown option keys fail SILENTLY.

## Validation plan (authoring session + Eric, AFTER review — not the agent)

1. Review fork diff; rebuild reproducibly; stage `worker.w3.exe`.
2. Pool-idle gate → graceful recycle ONE worker onto the W3 binary + psp/dc config arm.
3. Boot probes (w1-probe pattern): psp (LocoRoco, Daxter) and dc (Crazy Taxi, Sonic Adventure,
   Power Stone) → expect `hw render context: Vulkan` + `[vk] zero-copy: ACTIVE`, 60 t/s,
   forgiven=0, close ×5 clean, memstick/VMU seed+harvest intact.
4. OIT visual + perf A/B on dc (worker-side pace-diag; screenshots for alpha sorting).
5. Crackle verdict: Eric plays PSP remotely on the Vk arm; we read ONLY worker-side pace-diag II
   (meanTick / noSleep% / maxDeficit / forgiven) vs the GL baseline. Idle A/Bs cannot see it.
6. Eric verdict → commit config, merge fork branch, flip naomi/atomiswave, memory update.
