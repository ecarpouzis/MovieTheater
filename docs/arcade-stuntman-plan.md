# Stuntman (PS2) — diagnosis & fix plan (2026-07-15)

## RESOLVED — final state (2026-07-15 evening; kept for the method + the retractions)

Three stacked root causes, all found by instrumentation, two fixed and one correctly worked around:
1. **Workers ran at Normal priority** (raised classes don't inherit to children) → Win11 E-core
   steering → the 60↔21fps oscillation. Fixed self-healing in run-arcade-glworker.ps1
   (High + 0x5555 one-logical-per-physical-P-core affinity).
2. **LAN ICE hairpin** (equal-priority host candidates) → client rationed to 24fps/3Mbps. Interim
   LAN-only; durable srflx fix in docs/arcade-ice-priority-plan.md (other session).
3. **Level-start audio chop = GSState::Transfer packet storm** (GIF path-3 texture streaming,
   99.3% of the burst's GS-thread CPU, 664k calls/window) — NOT shader compiles, NOT binary loads,
   NOT readbacks, NOT GL texture uploads (all measured ~0), NOT the encoder. The HW renderer's
   per-write invalidation + packet processing is intrinsic; **the Software (HW) renderer is the
   architecturally correct per-title fix** — shipped via ArcadeGameProfile Id 7 → gameconfig-export.
   The async-upload custom-core project was REFUTED by rounds 4-5 and closed.

The instrumented core (perf-attr rounds 1-5, branch movietheater-lrps2, patch in docker/arcade/)
remains deployed as the permanent PS2 profiler. Still open: the level-4 AI test (clamp:3 re-add now
that the EE idles at 13%), and 2x-native sharpness stays unavailable on SW (acceptable at 1x).

Two distinct user-visible problems, one game (SLUS-20250, LRPS2 `pcsx2_custom_libretro`):

1. **Audio stutter** during mission 1 — "stalls at the same points every replay"; historically also
   described as "regular, every few seconds." Persists across every config arm tried so far.
2. **AI softlock** — the lead car fails a corner on level 4 (PCSX2 #2990, open upstream). Untested on
   our stack since the clamp patch.

They are independent. Do not let a fix for one masquerade as progress on the other.

---

## Part 1 — the audio stutter

### What is PROVEN (do not re-litigate)

- **The skip is missing samples, not transport.** `audio/s = ticks/s × 800.8` holds exactly in every
  pace-diag window of every real session. When Eric hears a skip, the core did not produce the samples.
  Encoder/IDR/ABR/caps suspects are dead twice over: audio rides its own PeerConnection (patch 0020),
  and AV1 runs infinite-GOP intra-refresh (zero periodic IDRs). The old "IDR every 2s" hitch was a
  real bug — fixed in 2026-07-08's session (docs/arcade-audio-nextsteps.md) — and does not exist today.
- **GPU exonerated**: dips identical at 1x native vs 2x. **FP-accuracy exonerated as the dip cause**:
  clamp:2 ≡ clamp:3 at the dips. **Readback sync mode exonerated as sufficient fix**:
  `hw_download_mode: Unsynchronized` rejected twice (still ~47fps, added a 129ms spike).
- **Cold-vs-warm disk shader cache exonerated as sufficient fix**: a valid warm 568-entry cache loaded
  (`Read 568 entries`) and the stutter persisted (13:05 session).
- Two components visible in Eric's sessions:
  (i) **bursty spikes** at deterministic content points (maxTick 40–129ms), and
  (ii) **sustained sags** in heavy driving (ticks/s 39–47, meanTick 20–23ms, up to ~25s) — worst at
  the end of a long take.

### What today's source reading CORRECTED (three wrong beliefs)

Read from `github.com/libretro/ps2` (LRPS2, branch `libretroization`; our build = `v2.0.0-b03969a`):

1. **The GL shader cache writes IMMEDIATELY on compile** (`CompileAndAddProgram → WriteToBlobFile`),
   not at close/teardown. ⇒ *"force-kill dumps the shader cache"* — the premise recorded in
   config comments, the recycle script, and the worker-pool-wedge memory — **is wrong**. (The graceful
   recycle remains correct for the zombie/wedge reasons; the cache rationale is retracted.)
2. **There is no size cap or eviction** in the cache. The "~568-entry ceiling" was a misread: 568 ≈
   full coverage of the content that had been played; the byte-identical 8.5MB bins on both workers =
   identical deterministic content, not a limit.
3. **The freeze mechanism is a HANDLE LEAK, not a missing close-flush.** `ShaderCache::Close()` runs
   only in `GSDeviceOGL::DestroyResources()` ← `Destroy()` ← `context_destroy` — which we SKIP
   (`skip_hw_context_destroy`, required: running it deadlocks → watchdog exit-70, reproduced today).
   So the first PS2 room in a worker process appends fine and **leaks the open idx/bin handles**;
   every LATER PS2 room in that process hits the cache's sharing-violation path, which *silently*
   "continue[s] without a cache" → memory-only → nothing persists. Matches all observed growth/freeze
   patterns (fresh process 58→92 live; long-lived process frozen).

Also newly understood, and it **weakens the compile theory**:

4. **`Compiling new vertex/pixel shader with selector …` prints at SOURCE GENERATION** (GetVSSource/
   GetPSSource), which happens once per selector per room on **both** paths — a true driver compile
   (10–100ms) *and* a fast disk-binary load (~1–3ms). Per-session "compile" counts therefore do NOT
   measure slow compiles. Every compile-correlation argument made from those lines is ambiguous.
5. **MTGS is threaded in this port** (real GS thread; EE blocks via `GenericStall`/vsync-queue waits).
   A slow GS-side operation still stalls `retro_run` (the frame can't complete), but attribution
   needs timers, not thread reasoning.
6. The in-memory program map (`m_programs`) is **per-room**. Even with a complete disk cache, every
   new room lazily re-loads program binaries at first use — **batched at scene transitions** (50 in
   one frame ≈ 50–150ms stall). This is a candidate unified explanation for "same points, every
   session, even warm" that no prior arm could distinguish from true compiles.

### The honest state: attribution is UNRESOLVED

Four candidate sources of slow ticks, none currently separable from the logs we have:

| # | Candidate | Signature it would leave | Current evidence |
|---|-----------|--------------------------|------------------|
| 1 | True GLSL compiles (first-ever content) | bursty, fades permanently once cached | ambiguous (log line can't distinguish) |
| 2 | Batched program-binary loads (every room, same points) | bursty, same points, never fades | consistent with all observations |
| 3 | EE saturation (game load + clamp + MTVU limits) | sustained meanTick > 16.6ms, uniform | the ~25s sags fit this |
| 4 | Readback/MTGS waits (cpuSpriteRenderBW=4) | load-correlated waits | Unsync arm says not sufficient alone |

### The plan

**Step 1 — cheap config arm: `pcsx2_ee_cycle_rate: "50% (Underclock)"` — RAN 2026-07-15, REJECTED.**
Measured on Eric's live session (override confirmed applied in the log): ticks/s **27–39 sustained,
meanTick 23–30ms, forgiven 25–40/window** — decisively WORSE than the no-arm baseline (39–60 with
39–47 sags), on a fresh worker process (process-age confound eliminated: the runner had respawned
worker-gl 5s before the room). Eric: "no improvement." Likely interaction with Stuntman's GameDB
`BlitInternalFPSHack` (internal-FPS detection) — mechanism unproven, verdict measured. REVERTED to
1x-native-only. Do not re-try 50%; if EE saturation is later confirmed by Phase A timers, titrate
from 75% instead, or accept.
*Note: `audioMaster` (the PSP repay-pacer lever Eric asked about) is NOT a lever here — PS2's audio
clock is honest, and the deficit is real missing samples; a repay policy can't repay what a
below-60fps core never produces.*
*With this rejection, EVERY cheap knob is exhausted: native res ✗, Unsync ✗, clamp:2≡3 ✗, warm
cache ✗, cycle-rate ✗. Phase A instrumentation (Step 3) is the only remaining path — no more blind arms.*

**Step 2 — ops stopgap until the core fix: recycle GL workers before Stuntman sessions.**
Fresh process = first-room cache appends work (leak not yet hit). Use
`scripts/recycle-arcade-glworker.ps1` (graceful path is live). This also converges the disk cache
toward full mission coverage over Eric's plays.

**Step 3 — THE custom LRPS2 core (the Dolphin-style lift; Eric approved).**
Build from `libretro/ps2` @ `b03969a`, branch `libretroization`. The core Makefile supports
`platform=windows_msvc2017_desktop` (cl.exe) — same MSVC toolchain family as our Dolphin/PPSSPP
custom cores (build scripts precedent: docker/arcade/dolphin-build-core.bat, ppsspp-build-core.bat).
Validate a STOCK build for behavioral parity (GameDB count 12806, Stuntman boots, fps parity) before
any edits. ⚠ Build only while the pool is idle — the build box is the game server
(arcade-benchmark-isolation).
Two phases:

- **Phase A — instrumentation, then ONE attribution session.** Add per-5s-window timers, logged
  beside pace-diag: `true_compile_ms` (ShaderCache miss path), `binary_load_ms + count`
  (CreateFromBinary), `mtgs_wait_ms` (GenericStall/WaitGS), `readback_ms` (hw_download syncs).
  Eric plays mission 1 once. The table above collapses to a measured answer. **No fix ships blind.**
- **Phase B — fixes chosen by the Phase A data:**
  - Candidate 2 confirmed → **preload the disk cache at GSopen** (eagerly populate `m_programs`
    from all cached binaries; ~568 × 1–3ms ≈ +1–2s room boot, zero in-game loads). Likely the
    single highest-value patch.
  - Candidate 1 confirmed → **fix the handle leak** (open-append-close per write, or share-mode
    open, or a reachable Close hook at room end) so the cache converges permanently; optionally
    pre-bake by driving mission 1 once per fresh cache.
  - Candidate 3 confirmed → ship the Step-1 cycle-rate value per-game; revisit clamp necessity
    after the Part-2 verdict.
  - Candidate 4 confirmed → A/B `cpuSpriteRenderBW` via the (now native) GameDB entry, watching
    for the texture corruption it exists to fix.
  - Regardless: move the Stuntman GameDB entry (clamp + halfPixelOffset) into source — retires the
    byte-patch and its 189-byte length gymnastics.

### Verification protocol (unchanged, load-bearing)
Eric plays on his remote browser; we read ONLY worker-side pace-diag (+ new timers). No local
Playwright arms for perf judgments; no box activity during an arm (arcade-benchmark-isolation).
One variable per arm. Bench harness is for mechanism checks only.

---

## Part 2 — the AI softlock (level 4)

**Soft-float is REJECTED, custom core or not — verdict upgraded with fresh evidence (2026-07-15):**
upstream PR #12001 is **still an open DRAFT** ("Hold Merge", milestone 2.X, last activity June 2026),
covers the **EE FPU interpreter only** (no VU0, no recompiler integration), and its own thread reports
**Stuntman "still can 'slightly' deviate"** even with it. Our LRPS2 has **no interpreter path compiled
in at all** (binary-confirmed), so a backport = resurrect an interpreter + port a moving draft, to run
slower and still not fully fix the game. Do not spend the lift.

**What to actually do:**
1. **Test what's deployed.** eeClampMode:2 (worker-gl) / :3 (worker-gl-2) has never been tested against
   the actual level-4 corner. Reconcile both workers to :3 (per the handoff: perf-equal, more accurate)
   on the next DLL touch, then Eric plays to level 4. This is the only open question that matters.
2. **If clamp fails** → document "level 4 AI may softlock" as a known limitation on our stack, and
   watch PR #12001 for a merged, recompiler-integrated, VU0-covering future — revisit only then.

---

## Corrections to propagate (wrong claims currently written down)
- config.worker-gl.yaml deploy comment + recycle-arcade-glworker.ps1 header + arcade-worker-pool-wedge
  memory: "force-kill skips the shader-cache flush → cold cache" — **retracted** (immediate appends).
  Graceful recycle stays, for the zombie/wedge reasons only.
- arcade-stuntman-audio-skip memory: "~568 ceiling" and "writes on clean close via retro_unload_game"
  — **both corrected** (no cap; handle-leak → memory-only mode; appends are immediate).
- "Compiling new …" log lines ≠ slow compiles — treat all compile-count arguments as ambiguous.
