# Stuntman round 2 — frame jitter + level-4 AI turn bug (OVERNIGHT 2026-08-08)

## ❌ ~23:30 STRICT-REFS DID NOT FIX THE STALE FRAME — the encoder DPB was NOT the mechanism
Room MRJ6EQ (worker-gl-2, 9 min idle on attract/menus, spectator reel **22,965 frames**,
`reel-strictrefs-run1/`). Scanner: **4 A-B-A detections, and looking at them settles it** —
exhibit `anomaly-sheet-strictrefs-run1.png`:
- **pf0004514 mt=91.977, ratio 0.004 — A REAL STALE FRAME.** Neighbours A and C are both the
  static STUNTMAN title card (dAC=0.36); B is a car-crash frame whose nearest twin is
  **pf0004046 at mt=81.943 — +10.034 s / +464 frames back, pixel diff 0.21.**
- pf0015198 ratio 0.000: the title card spliced between two *identical* black frames, twin
  4.6 s away — same family, probable second instance.
- pf0018479 (0.409) and pf0019721 (0.366) are a white scene-cut flash and a muzzle-flash —
  the legitimate one-frame-flash false positives the skill warns about. NOT defects.
⇒ **VERDICT: FAIL.** ~1-2 genuine insertions / 23k frames (was 9 / ~24k pre-fix — lower, but the
mechanism is alive). The plugin was definitely in force: `gst-inspect nvh264enc/nvav1enc` shows
`strict-refs` (default true) and the room's worker (pid 45228) had
`D:\msys64\ucrt64\lib\gstreamer-1.0\libgstnvcodec.dll` (1,479,565 B, mtime 23:11:19) mapped —
workers connected at 23:11:24, *after* the DLL landed. Nothing in the fork sets the property, so
there is no `strict-refs` line to grep; the module check above is the proof.
⇒ **NEXT SUSPECT (as the plan already predicted): the zc slot handed to the encoder.** A 464-frame-old
picture cannot be a legal reference under a bounded DPB, so the encoder was most likely handed a
*recycled image* and encoded it correctly. Room evidence to chase: `[zc-stat] dup(pin=123)` with
`reallocs=0`, `inflight` briefly 5/8 at 23:19:15 (`serials[5396..5400 span=4]`) — the dup-pin path
is where a slot can be re-presented. Room health was otherwise clean: geom one line all room
(1280x896), ceiling 12373, `[ts-mono] violations=0`, no crash, `abr: summary` normal.
⚠ Caveat for the next run: with two browsers on the box the spectator flapped `abr: peer layer`
1↔0 repeatedly, so much of the back half of this reel was recorded at layer 0 (30 fps base only).
A cleaner run wants the reel client alone on the room.

## ✅ ~23:39 N64 PIPELINE-CACHE PERSISTENCE: BOTH CORES PASS (full write→load cycle, live)
- **parallel_n64 (b4bd60/b4dfc60) PASS** — room 23:32:48 on worker-gl-2:
  `paraLLEl-RDP: loading 4 MB pipeline cache from ...\parallel_n64\cache\parallel_rdp_pipeline_cache.bin
  (5015239 bytes)` → `Initializing pipeline cache` → streamed 1280x960 → at close
  `wrote 10 MB pipeline cache. (10678996 bytes)`. Cache grows and survives. `abr: summary`
  atCeilPct=99 cuts=0 starves=0, no crash.
- **mupen64plus_next (15ad6c3) PASS** — proven as a two-room cycle on worker-gl-2 (its mupen cache
  dir was empty; worker-gl's 4.5 MB from 21:06 was on the other box): room 23:37:21
  `no pipeline cache file at ...\mupen64plus_next\cache\... -- starting fresh` → close
  `wrote 8 MB pipeline cache. (9156100 bytes)`; room 23:39:20 `loading 8 MB pipeline cache ...
  (9156100 bytes)` + reached Playing. `[WARN]: Disabling pipeline cache control.` prints on every
  parallel-RDP boot and is unrelated (VK cache-control ext, pre-existing).
- Housekeeping done: `ArcadeGameProfile` row 33 (`TEMP pipeline-cache verify`) DELETED (guarded on
  the Notes text), `ArcadeGame` 60439 `MaxPlayers` back to **1**.
- ⚠ **"Diddy Kong Racing" is a 5-way lobby collision** and the first card is a dud. All five region
  variants carry the identical title, and `.arcade-card` first = **id 18243 (USA non-Rev-A)**, which
  dies with `error="couldn't find game Diddy Kong Racing (USA) (En,Fr) in system n64"` →
  coordinator `malformed game start response; slot released` → the room never binds a worker and the
  harness reads it as a **Playing-tag timeout**. That is the same failure as the earlier attempt this
  session — it is NOT a boot hang. The verified-good entry is **id 3, `(USA) (En,Fr) (Rev A)`**; the
  ROM for 18243 does exist on R: so this is a JIT-staging/library gap worth a separate look
  (the gateway also logged `RomCache evicted game 3 (cold >7d)` minutes earlier).

## ⏸ ~23:50 TASK 7 (Stuntman no-interlacing patch): AUTHORED + DELIVERY PROVEN, PATCH CONTENT WRONG
**Delivery path is SOLVED and needs no rebuild.** `pcsx2_enable_cheats=enabled` (profile
CoreOptionsJson) + `<worker>\libretro\system\pcsx2\cheats\76CBC428.pnach` works end-to-end in this
fork — worker log: `Found Cheats file: '76CBC428.pnach'` → `Loaded 8 Cheats` → `8 cheat patches are
active.` (`VMManager::LoadPatches` → `LoadPatchesFromDir(crc, EmuFolders::Cheats, "Cheats")`,
`EnableCheats` set at main.cpp:1133). The `cheats_ni` folder path exists too but
`EnableNoInterlacingPatches` is never set anywhere in the libretro port, so that branch is dead;
`pcsx2_nointerlacing_hint` only gates the 52-serial hardcoded table in `libretro/patches.cpp`.

**Where the patch came from.** No community NI patch exists for this title (PCSX2/pcsx2_patches
ships only a widescreen `SLUS-20250_76CBC428.pnach`). So it was derived from the ELF: extracted
`SLUS_202.50` straight out of the CSO without decompressing the disc
(`.claude/skills/test-roms/cso-extract.py` — CISO index + ISO9660 walk; the disc's block size is
16 KB, not 2 KB), confirmed against the boot log (`Game CRC = 0x76CBC428, EntryPoint = 0x00100008`,
ELF loads at 0x100000 / file offset 0x1000). The game reaches `SetGsCrt` only via the libgraph
`sceGsResetGraph` at **0x0023F4F8**, which tail-jumps (`j`, not `jal` — that is why a jal search
finds nothing) to the syscall-2 stub at **0x002583A0** with `a0=inter, a1=omode, a2=ffmd` taken from
its own `a1/a2/a3`. Five call sites, all with `a0(mode)=0`:

| call site | inter (a1) | omode (a2) | ffmd (a3) |
|---|---|---|---|
| 0x0011FD5C | 0 | 2 (NTSC) | 1 |
| 0x001D01FC | **1** | 2 | 1 |
| 0x00202DD0 | **1** | var | 0 |
| 0x00220CFC | **1** | 3 | 1 |
| 0x00220D1C | **1** | 2 | 1 |

**Result: both variants BLACK-SCREEN the game.** Variant A (force `a1=0` *and* `a3=0`, 8 patches)
and Variant B (force `a1=0` only, 4 patches, the classic asasega form) each boot to luma **0** and
stay there — v1..b3 snaps at t=40..100 s all luma 0, `shots3/`, `shots4/`. The control is
unambiguous: the *unpatched* clean boot of the same room type had luma 69 / 128 / 70 / 100 at
t=15/30/45/60 s (`/tmp/hold1.log`, room MRJ6EQ). So this is not "black is usually not a hang".
**But the patch IS landing and IS doing what it says:** with `field_fullres` off, gameplay is
1280x**447** un-patched (control run, room HWW7UD, ceiling 6173) and the patched clean boot reports
one stable 1280x**448** (ceiling 6186) — 448 = 447 without `field_aware_rendering`'s `height--`,
i.e. `FFMD && INT` went false. The interlace flag is being cleared; what breaks is everything
downstream of it (the game's DISPLAY DH/MAGV and dispenv are still set for the interlaced raster,
so the scanout has nothing valid to read).
**Conclusion / next step for Eric's eyes:** the SetGsCrt argument is the wrong lever for this title.
A working NI patch here has to patch the game's *own* video-mode setup (its dispenv / DISPLAY1-2
writes) alongside the interlace bit, or target the game's mode variable rather than the libgraph
call. Everything needed to continue is in `D:\ArcadeStorage\scratch\stuntman\`: the extracted ELF,
`mipsdis.py` (EE disassembler), both pnach variants under `pnach-not-deployed\`, and
`prof7_test.sql` / `prof7_restore.sql`.
**Nothing was left armed.** The pnach files are OUT of both workers' `cheats` dirs, profile row 7 is
back to exactly `{"pcsx2_softfloat":"enabled","pcsx2_pgs_field_fullres":"enabled",
"pcsx2_pgs_ssaa":"16x SSAA (can high-res)","pcsx2_pgs_ss_tex":"enabled"}`, MaxPlayers=1, no live
rooms, no Playwright processes. `pcsx2_pgs_field_fullres` was NOT changed — it stays, and it should:
with no working NI patch there is no evidence to drop it.

## 🎯 ~20:30 PROFILE VERDICT (Eric drove the loop 3x on instrumented core e272813): UPLOAD/DISPATCH STORM CONFIRMED
pgs-prof caught every crossing. THE stall frame (run 1, 20:26:22): **total=356.6ms =
upload 163.1ms (1494 ops / 194.2 MB) + binning 81.2 + shading 55.5 + texcache 40.1**,
disp=6158, sub=130 (a normal frame: disp≤1k, sub 4). Warm crossings (runs 2-3): same
queued volume (1928 ops / 244.9 MB, disp=8316, sub=130) but CPU cheap (~3.6ms upload) —
frames sit at 40-50ms dominated by **wait=23ms** (GPU absorbing the dispatch storm), plus
60-140 slowTicks(>20ms) per crossing window (pace-diag maxTick 531.8ms run 1, ~25-45ms warm).
Also one first-sight frame 119.4ms = alloc 49.1 + binning 42.4 (slab/scratch first-touch).
⚠ Counter caveat for interpretation: ops/bytes are counted at QUEUE time, section time at
flush — light frames can show huge byte counts (e.g. 12ms/132MB) that actually cost the
NEXT flush. The verdict is unaffected: crossing the corner queues a couple hundred MB of
GIF-path transfer re-uploads (the GSdx 452k-xfer storm, PGS cost model) + 6-8k compute
dispatches in ~1-2 frames. Mitigation directions (NOT yet built): spread/async the upload
burst, pre-warm slab allocs, or accept (warm cost is 40-50ms frames for ~1s).
**SEPARATE FINDING: Eric sees out-of-sequence frames ALL THROUGHOUT (not just the turn) —
NOT pace-explained: vcoal/s=0.0 the whole session, video/s==ticks/s (one emit per tick).
Server pacing is clean ⇒ suspicion moves to the zc DUP/stale-slot path (task 5 audit) or
client-side AV1/SVC. Eric offered spectate+burst-screenshot testing — take him up on it
AFTER the zc audit names a mechanism.
~20:45 ERIC A/B: SAME frame issues under H.264 ⇒ CODEC-INDEPENDENT (matches the 15:50
h264 A/B on the turn-garbage). Client codec decode/reorder ruled out; defect is in the
shared path: worker zc slot lifecycle → encoder input, RTP timestamping, or client render
loop. zc audit promoted ahead of the audio-adoption task.**

## ~17:15 GATE 4 RESOLVED: the 228MB cache LOAD **CRASHES the worker** (answer = none of a/b/c — it dies)
Both workers crash-looped through the afternoon: "loading 228 MB pipeline cache from ..." →
instant 0xC0000005 READ of 0x824 (null base + member offset) at pcsx2_custom_libretro.dll
+0x4B7816 — adjacent to the e9e4c7e-era +0x4B7736 (supports_subgroup_size_log2) = pipeline
code against an uninitialized/dead Device, now on the LOAD path. Proven at 16:36:46 (worker 1)
and 17:00:47 (worker 2, the OFCOM4-era file); more 0xC0000005 exits at 16:21/16:32/16:54/16:57.
The earlier "Creating a fresh pipeline cache." line after OFCOM4's load line was this same
death + runner respawn. MITIGATED 17:14: both caches quarantined as *.crash-7bd135e (worker-gl
had re-written a fresh 228MB at 17:08:49 that would have crash-looped the next ps2 room).
Fix assigned to core-builder agent alongside the stall instrumentation (JOB 2).

## 🎯 ~17:15 DECISIVE (Eric's experiment): THE TURN STALL IS NOT COMPILES.
Eric ran the turn TWICE in ONE room (Restart Stunt between) — identical slowdown + broken
frames both attempts. The in-memory VkPipelineCache serves attempt 2, so compiles are
EXONERATED as the driver (they were real passengers). The stall is CONTENT-DRIVEN GS work
fired every time that corner is crossed — prime hypothesis: the level-chunk texture upload
burst through PGS's compute upload path (upload.comp/vram ops), the paraLLEl-GS analog of the
July GSdx xfer-storm verdict (same game behavior, different renderer cost model). The broken
frames ride the stall cadence (established). NEXT SESSION'S MISSION: instrument/profile PGS at
the turn — Granite timestamp markers or in-core timers around upload/binning/ubershader
dispatch at that moment (the perf-attr pattern from July, now for PGS internals: which
dispatch eats the 120-400ms), then fix per findings (async upload processing, splitting the
burst across frames, or accepting-with-mitigation). Pipeline-cache persistence (gates below)
is now a SECONDARY nice-to-have — do not let it distract from the upload-burst hunt.
NOTE: Eric's repro protocol = lobby room → in-room Load (slot 99 = his Level 4 state) →
drive to THE turn ~TC 10-15s in, restart-stunt to repeat. He tests on demand — ask him.

## ⚡ HANDOFF ~17:10 (context exhausted) — persistence 3.5/4 gates green, ONE open question
Core 7bd135e DEPLOYED both workers (write-at-CloseGSRenderer). PROVEN this hour on clean state:
boots ✓, explicit no-file log ✓, "wrote 228 MB pipeline cache." at close ✓, worker SURVIVES
close ✓ (zero crashes since 16:36 — those were e9e4c7e-era). GATE 4 IN FLIGHT: room OFCOM4
printed "loading 228 MB pipeline cache from ..." (FIRST load in system history) but after ~2min
had not reached Playing; last Granite line "Creating a fresh pipeline cache." (timestamp
unverified — may be the PRIOR room's). OPEN: does the 228MB load (a) complete slowly (the
flagged boot-cost concern → lower PIPELINE_CACHE_MAX_BYTES, one constant), (b) get REJECTED
(UUID/hash → fresh; then why), or (c) hang? NEXT SESSION: check room OFCOM4's outcome in
glworker-2.log (did it reach a game? worker exit?), read timestamps on loading-vs-fresh lines,
then either cap-tune or send the finding to the core builder (agent context in this session's
transcript; SHADER-TOOLCHAIN-NOTES.md + SOFTFLOAT-PORT-NOTES.md in the lrps2 tree carry all
design history). STALE e9e4c7e-era caches quarantined as *.suspect-e9e4c7e (they KILLED boots
— the crash was a pipeline compile against a dead Device, +0x4B7736 = supports_subgroup_size_
log2, NOT the write path). Also outstanding: commit/push lrps2 patch artifact regen for
7bd135e + guard -Snapshot (deployed DLL changed), Eric's turn test once gate 4 resolves.
ERIC DIRECTIVE STANDS: no rollbacks, forward only, he TESTS (doesn't play).

## ~16:55 ERIC DIRECTIVE: NO MORE ROLLBACKS, forward only until Stuntman done; he TESTS not
## plays. e9e4c7e crashed 0xC0000005 at room close (cache write in destructor chain vs
## pgs_destroy_device ordering — dev internals torn down); rolled back ONCE to 34a5cc1
## (deployed, stable) before the directive. Builder implementing the FINAL persistence:
## write at retro_unload_game (alive), load AFTER EmuFolders, cap+logging. Deploy IMMEDIATELY
## on report (window pattern), verify in ONE scripted cycle: boot LOAD line + close WRITE line
## + no crash + second boot loads grown file. Then Eric turn-tests. 240MB caches still on
## disk both workers (34a5cc1 ignores them; the new build must load them).

## ~16:45 CRANK N: teardown-write core (e9e4c7e) DEPLOYED; LOAD is init-order broken (fix in flight)
Write moved to teardown-only + 384MB cap (async-write rejected: no lock for concurrent
getData; teardown CONFIRMED to run past skip_hw_context_destroy). BUT the LOAD never fires:
boot logs "Creating a fresh pipeline cache." with the 240MB file present and neither explicit
load-log line prints ⇒ the load executes at device-init BEFORE EmuFolders::Cache resolves
(empty-path case skipped SILENTLY — now to be logged). Fix in flight: defer/lazy the load
past folders-init (merge or pre-first-frame recreate). Builder also confirmed: the two
"mystery" cache files were Dolphin's + GSdx's own (unrelated); 240MB size real (ubershader
spec-constant variants), per-worker. ACCEPTANCE unchanged: boot logs "loading NNN MB pipeline
cache" + "Initializing pipeline cache.", room close logs "wrote NNN MB", Eric's turn clean on
run 2 with NO 400ms-class maxTicks. Watch boot time on the 240MB read (cap is one constant).

## ~16:30 CURRENT: cache killed the compiles; its WRITE is the new 400ms stall (fix in flight)
Eric's room post-49ab178: Stalled-compile = ZERO (cache works) but maxTick 402/413/427ms on
~5s cadence at the turn = the periodic persistence serializing the 240MB cache
(vkGetPipelineCacheData + write) ON THE GS THREAD. Builder fixing: move write off frame path
(background writer or room-close write), verify getData thread-safety vs pipeline creation,
investigate 240MB-after-one-session (variant explosion? prune policy?) AND the second smaller
pipeline-cache file on worker-gl (2.4MB, newer — if Granite is REJECTING the big file each
boot and rebuilding, load-rejection churn is part of the story — check boot lines
"Initializing pipeline cache" vs "creating a fresh"). DEPLOY PENDING = the closing fix.
NOTE for resume-if-context-lost: deploy = disable-first window pattern; verify = Eric's turn
twice (run1 may still stall once per NEW variant; run2 clean) + zero 400ms-class maxTicks.

## ~16:10 THE TURN, FINAL ROOT CAUSE: PGS SYNC PIPELINE COMPILES (fix in flight)
Eric's persistent same-turn slowdown+out-of-sequence frames (surviving coalescer: vcoal=0,
video/s==ticks/s, all counters clean) = paraLLEl-GS/Granite SYNCHRONOUS compute-pipeline
compiles at first sight of the turn's effects: "Stalled compile (compute, 816e78...): 120589us
(mode: sync)" + maxTick 123-138ms windows, 45 slowTicks — EVERY room (pipelines don't persist
across rooms). The stall-burst cadence is what reads as out-of-sequence; earlier mechanisms
(double-present, pool staleness, SVC-dropped keyframes, AV1 no-resync) were each real and are
each fixed, but this is the remaining trigger. Core builder investigating Granite pipeline-
cache persistence (Fossilize/VkPipelineCache — likely an unwired cache dir) + async compile
("mode: sync" implies async exists). Persistence = turn stalls once EVER; async = never.
Cache design mind: GL cache handle-leak lesson, skip_hw_context_destroy (write-at-compile,
not at-teardown), driver-keyed, corrupt-cache must rebuild not crash. DEPLOY PENDING.
Verification once deployed: room 1 = turn stalls once (compiles logged, cache writes); room 2+
= ZERO Stalled-compile at the turn, maxTick <25ms, Eric sees clean cadence.

## ~15:50 THE OUT-OF-ORDER FRAMES: ROOT-CAUSED (catch-up double-present) + TAILSCALE POLICY
- Eric's h264 A/B killed the AV1-concealment theory: same garbage frames, SAME TURN as the
  slowdown every run = content-locked, server-side. Smoking gun in his room's pace-diag:
  **ticks/s=57.8 video/s=108.8 maxTick=131ms** — the core presents TWO vsyncs per retro_run
  when catching up after the streaming-stall at that turn (~49 extra frames/5s window); worker
  encodes both → timestamp-crowded bursts → client renders out of order. Codec-independent.
  FIX (queued, fork): coalesce to ONE frame per tick at the video sink (keep last; count via
  pace-diag; mind zc slot/semaphore leak for uncommitted set_images). The slowdown itself =
  the known deterministic streaming-burst class (July round-3), now minor (~57 t/s dips).
- **TAILSCALE POLICY (Eric): tailscale = phone-RDP ONLY, nothing site-wide may traverse it.**
  His room had paired samehost via Ziggy's OWN tailscale addr (100.100.38.103, prflx — formed
  from probe SOURCE addresses, so client candidate filtering can't fully prevent it).
  SHIPPED: 3 firewall rules (elevated, Eric-approved UAC): Tailscale iface = block inbound TCP
  except 3389, block ALL inbound UDP, block ALL outbound (RDP replies are stateful; block
  beats allow in WFP — never block-all+allow-RDP). IN FLIGHT (fork): pion SetIPFilter/
  SetInterfaceFilter excluding Tailscale/100.64.0.0/10 + remote-pair rejection if the API
  allows. TODO: watchdog check greping worker logs for dev=100.64-127.x peers → loud alert.

## ~15:20 OPEN CYCLE (deploy pending: accumulated core+worker set)
- Textures FIXED via config: Stuntman profile = softfloat + field_fullres + 16x SSAA + ss_tex
  (plate legible, locked 60; ss_tex is the bigger texture lever — 16x still averages pairs
  vertically). Motion "re-blur breathing" = adaptive-deinterlace bob softening, EXPECTED,
  tunable via MOTION_LO/HI in field_merge.frag (rebake).
- Mid-play RESET bug root-caused: boot-restore attempt 2 (22s) fires over live gameplay when
  attempt 1 landed early (warm worker) — restore agent implementing INPUT-CANCEL (player input
  since attempt 1 ⇒ skip attempt 2) + weighing removal of the in-room-load re-fire.
- Stale loading-frame: core builder's ping-pong fix (8eebae8) removes the demonstrated
  mechanism (recycled-pool memory presented before merge write lands); the fork's Round-4
  serial instrumentation proves emit order is CORRECT → residual suspicion is DOWNSTREAM:
  **the DUP path (handleDup re-pushing a slot whose serial advanced; DUP-PIN guard
  nano_vk_zc_last_serial :1515 exists for the gc idle case) — ASSIGN TO FORK OWNER (restore
  agent) after its input-cancel lands.** Completion semaphore being implemented as robustness
  (safe design: fresh Granite semaphore/frame, deferred destroy; worker wait path verified
  correct and needs NO change; NOT the demonstrated cause — serials ordered).
- Deploy set when ready: core (ping-pong + semaphore + everything) + worker (input-cancel).

## ✅ ~14:45 PHASE 3 SHIPPED — THE FULL-FIDELITY ENDGAME (core v2.0.0-e3b9c0a, deployed both
## workers, guard snapshotted, patch artifact regenerated, pushed 90734b0)
## Motion-adaptive temporal merge of the aligned 4×-resolved fields: same-phase motion detection
## (f0v f2, f1v f3 — comparing phases directly would flag real inter-field detail as motion; 4
## history entries, RESOLVED fields retained never merged output to avoid IIR smear), static =
## 0.5*(f0+f1) weave, motion = newest field, smoothstepped (fastmad's 0.04/0.06 thresholds);
## scene cuts saturate the motion term → bob (no special case); history dropped on geometry
## change/mode exit; no GS-thread serialization (history images already READ_ONLY_OPTIMAL via
## the always-true intermediate pass; ImageHandle retention = the anti-recycle mechanism).
## ACCEPTANCE: static screen meanFrameDiff 0.007, 0 shifts/571 frames (baseline era 0.28-0.5,
## defect era 0.5-0.9), full sharpness retained, no combing post-crash frame. Tuning lever if
## slow pans ever soften: MOTION_LO/HI in field_merge.frag (rebake). AWAITING ERIC'S EYES.

## ~14:35 SHADER PROJECT: Phase 1 ✓ (toolchain byte-identical repro; parallel-gs is PURE
## upstream df08ccf — always diff --strip-trailing-cr, CRLF fakes a 10k-line fork; spirv-tools
## pin = the fragile link; PARALLEL_GS_STANDALONE=ON means LIBRARY mode, slangmosh needs OFF).
## Phase 2 ✓ BUILT+DEPLOYED (core v2.0.0-2589468, 11,618,816 B): true 4× resolve — 8x SSAA
## slice table derived+cross-validated (each output pixel = exactly ONE sample; slice =
## BASE+(sy&1)+2*sx+4*(sy>>1)); field offset = EXACT 2 texels; 16x = 2 samples/texel headroom;
## shader edit as shader-patches/0001-*.patch in the lrps2 tree, rebake offline-reproducible.
## ACCEPTANCE: SHARPEST image yet (real detail gain), position+grid clean — but probe reads
## HIGHER alternation (0.805/0.884, ±1 both-signed): the exact resolve exposes TRUE field
## content twitter that the 2:1 blend was low-passing. Phase 3 COMMISSIONED (the convergence):
## motion-adaptive temporal merge (fastmad approach) of consecutive 4×-resolved fields —
## static=weave (GSdx-reference stability + full detail), moving=newest field; scene-cut +
## first-frame fallback to bob; watch recycled_image_pool lifetime for the field-history image.

## ~13:30 STATE — ONE PATH REMAINS (Eric mandate: FULL fidelity, no fallbacks)
- ✅ ONE-CLICK RESUME SHIPPED+VERIFIED: worker 4aaba95 = boot-restore attempt 2 at 22s + Save()
  held while restore pending (the site's 2s pre-warm save was overwriting the seeded .dat AND
  vetoing the retry — the entire "load it twice" era). Live proof: lobby resume → Level 4
  in-engine, log shows "save skipped: boot-restore pending" + both attempts done. Covers plain
  Continue too. Trade-off: human saves in first 22s of resumed ps2 rooms are held (logged).
- ⚠ TASK-SCHEDULER RESTART RACE (deploy trap, hit ~13:20): RestartCount=999/PT1M auto-restarts
  the runner ~1min after schtasks /End — it can respawn workers onto the OLD binary before a
  slow build finishes. After any /End→build→/Run window, VERIFY worker StartTime > binary
  mtime; recovery = sentinel recycle (runners alive, they respawn onto the new exe).
- 🔨 IN PROGRESS (the single remaining fidelity path): parallel-gs shader toolchain lift —
  vendor upstream shader sources + slangmosh bake (D:\Arcade\build\parallel-gs-upstream),
  toolchain-proof gate (reproduce slangmosh.hpp at df08ccf), then TRUE 4× vertical resolve in
  sample_circuit from the real 8x-SSAA samples (y_log2=2 CONFIRMED at our live setting) →
  exact texel field placement, no compensation, no 2:1 blend shimmer. Eric's GSdx observation
  = the fidelity reference (GSdx deinterlacer = stable full-height; GSdx speed = unusable,
  xfer storm). NO more constant sweeps (comp0/1/2 all fail differently — 2:1 blit makes a
  clean constant impossible; sweep tokens to be removed by the shader work). Notes:
  D:\Arcade\build\lrps2\SHADER-TOOLCHAIN-NOTES.md.

## BLUR ROOT CAUSE FOUND ~12:30 (investigation agent, full report in transcript)
- **Gameplay = FFMD=1 field rendering → 1280×447 output** (odd height, chroma half-shift);
  menus/FMV = FFMD=0 → promoted progressive 896. force_progressive only cancels interlace when
  FFMD=0 (gs_renderer.cpp:4123/:4179 guarded by alternative_sampling). field_aware_rendering
  (FFMD&&INT) does image_info.height-- (:4745) = the 447 fingerprint; ceiling 6173=447-class,
  12373=896 — the log tells which mode a room ran.
- **The vibration = vp.y -= 1.0f when !info.phase** (gs_renderer.cpp:4819/:4852) = whole frame
  moves 1 line of 447 EVERY frame at 60Hz ≈ 2.4 physical px — Eric's perception exactly. It's
  COMPENSATION for a game-side half-pixel field jitter that may not exist here (parity A/B
  running: shimmer differs between Automatic and Bob BFF ⇒ game jitters/parity bug; identical ⇒
  compensation spurious).
- **FIX CHOSEN: core option (option 3+2): blit field to stable even 1280×896 at scanout**
  (image_info.height = mode_height << 2 at :4742, viewports ×4 at :4812/:4845, drop the phase
  shift + height--). ~5-6 lines, gated, per-game via profile row. Bonus: kills 896↔447 mode
  flips → fewer zero-copy REALLOCs with frames in flight = the STRONGEST white-flash candidate
  (log shows 213 reallocs, [zc-instr] REALLOC lines with slots in flight; GSRendererPGS.cpp:598
  keeps only last_vsync_image alive — recycled while encoder reads = flash).
- Config landmines CONFIRMED: pcrtc_antiblur must stay ENABLED (feeds horizontal-res adaptation
  + raw scanout fast path; inert for the vertical issue); **pcsx2_pgs_ssaa BELOW 8x silently
  kills high_res_scanout entirely** (needs both sampling_rate log2s nonzero; 2x=(0,1), 4x
  non-ordered=(0,2) fail the gate at :4227). Deinterlace-mode under PGS = parity XOR only;
  "Off" INVERTS phase (does not disable). Upstream parallel-gs (ee049c6) has NOTHING waiting —
  we vendor df08ccf, 3 cosmetic commits behind.
- EE no-interlacing patch: would have to be authored from scratch (not in the 64-serial
  hardcoded table; cheats_ni path dead; pnach/external-GameIndex delivery paths DO exist
  no-rebuild: pcsx2_enable_cheats + <DataRoot>/cheats/76CBC428.pnach, or use_external_gameindex
  which IS wired in this port at main.cpp:2643 with EnablePatches default true — contradicts
  the July "dead code" note, re-verify before relying). Not pursued: doubles GS fill.

## ✅ BLUR FIX SHIPPED + VERIFIED 6/6 (~12:35, room PPFWCQ, core v2.0.0-ee1cbd0)
pcsx2_pgs_field_fullres enabled for Stuntman (profile row 7 + manifests): field gameplay now a
STABLE EVEN 1280x896 — one geom line all room (0x0→896), ZERO 896↔447 flips, ZERO zero-copy
REALLOCs (was 213 in the 447 era — the lead white-flash suspect structurally eliminated),
ceiling 12373, probes bob-free (alternations=0; weak one-sided -1s = the scaled compensation's
sub-line bias, expected), sharpness plainly visible (TC digits razor, speedo ticks resolved),
pace at baseline (59.9-60.0, 0 slowTicks, meanTick 6.97ms), PROVEN audio latch, clean close.
Bounded known edge: bottom ~2-3 native rows black (vacated-rows clamp, reads as letterbox).
⚠ meanFrameDiff baseline at 896 = ~0.5-0.55 (finer row sampling), don't read as drift.
Bench-probe agent note: two node PIDs from 02:50 predate the run (the MCP servers) — not strays.

## PARITY A/B VERDICT (~12:15, 5 arms, cross-worker confound closed)
**The game FIELD-JITTERS and PGS's default-parity compensation is CORRECT — the shipped stream
is perfectly stable** (as-is: 0 integer shifts / ~1400 frames, meanFrameDiff 0.28-0.50; Bob BFF
= instant alternating ±2-row bob, meanFrameDiff 1.34+, ghosted stills = POSITIVE CONTROL proving
the probe catches a real bob). ⇒ NEVER ship a pcsx2_deinterlace_mode value for Stuntman ("Off"
and *BFF INTRODUCE the bob — parity XOR is LIVE under PGS, correcting the earlier "inert"
verdict). ⇒ The core patch must KEEP the phase compensation (scaled): option 2 is DEAD, option 3
amended — pcsx2_pgs_field_fullres being built (blit field scanout to stable even 1280x896,
compensation preserved at scaled magnitude, menus un-affected via field_aware_rendering gate).
Eric's residual blur = 447-line client upscale (fixed by the blit) ± interlace TWITTER
(alternate-scanline detail alternation — position-stable; if shimmer persists post-blit, that's
the remaining suspect; a temporal blend would need the slangmosh shader toolchain — later).

## ✅ MORNING FOLLOW-UP SHIPPED ~09:40 — pacer derivation fixes (fork 0de72c3, deployed)
Health-gate (rate windows only count at full pace) + proven-declaration immunity (a core that
ever delivered ≈declared at pace is immune to downward derivation for the session). Worker
rebuilt via dev.build-local recipe (go version -m stamps 0de72c37333f, clean), deployed to both
GL workers (backup bin\worker.pre-audiogate.exe), fork.patch re-exported + compile-proven,
branch pushed. Smoke room JGJG7N: PROVEN latch fired 7s in ("48000 Hz PROVEN, derivation
disabled"), a −1.9% scene-load dip did NOT re-pin (the old trap, exercised live), softfloat
delivery + paraLLEl-GS default + 60fps driving + clean close all re-verified on the new binary.
Defaults confirmed for Eric's launch: profile row 7 RenderProfile/HwContext NULL → system
default parallel_gs (paraLLEl-GS on Vulkan, first-listed/default in ArcadeRendererProfiles) +
CoreOptionsJson pcsx2_softfloat=enabled (scope All / vu0micro interpreter via core defaults).

## ✅ SHIPPED ~05:15 — summary for the morning
- **AI fix LIVE**: `pcsx2_softfloat` (scope All, VU0 interpreter) delivered per-game to every
  Stuntman room via ArcadeGameProfile row 7 + regenerated game-overrides.json. Core
  v2.0.0-c98d183 (soft-float PS2Float + int_fallback routing) on BOTH GL workers. Verified in a
  real room: `[game-override] option pcsx2_softfloat=enabled`, reconcile READ, driving **59.9
  ticks/s, 0 slowTicks, meanTick ~9.4ms** (43% headroom), no audio derives, clean teardown.
- **ERIC'S TEST**: open Stuntman normally from your account, load your "Level 4" snapshot
  in-room (📂 Load snapshot — lobby Resume is a known-broken no-op for PS2, see below), and run
  the chase corner. The option arrives automatically; nothing to configure. If the lead car
  still deviates, grab the room code + time so the log can be checked for the DERIVED re-pin.
- Jitter: no vertical bob exists in the delivered paraLLEl-GS stream (probed 5×, incl. driving).
  If you still see the frame jump, tell me the exact moment/screen — next probe is client frame
  PACING, not vertical offset. The 08-05 "runs slow" rooms were your GSdx renderer arms — GSdx
  is structurally wrong for this title (xfer packet storm, ~452k transfers/5s, API-independent).
- Rollback if anything is off: cores dirs have `pcsx2_custom_libretro.dll.pre-softfloat-20260808`;
  profile row 7 CoreOptionsJson back to NULL; recycle workers via .stop sentinel.
- Fleet state: 3 workers up + supervised (orphans replaced), watchdog on, /status 3 free.
- Open follow-ups (tasked): sticky one-sided audio re-pin (worker), PS2 lobby-resume 5s restore
  (worker), catalog regen for the ⚙ UI (extractor harness regression), upstream-report the
  MSVC bitfield bug found in PR #12001.

**Mission (Eric, overnight, autonomous):** Stuntman (SLUS-20250, ArcadeGame 60439) still runs poorly:
(1) frame jitter — frame visibly jumps up and down (classic PS2 interlace bob; "known PS2 config
issue we're just starting to be able to modify"), (2) level-4 AI can't take a turn (PCSX2 #2990,
FP precision). Eric approves core customization. Verify perf + logical fix myself from his save;
he tests the AI corner himself after. Avoid stopping points that wait on him unless state is better
than now.

## WORKLOG — read this first on context loss

### Facts established (2026-08-08 early AM)
- **Eric's Level 4 save**: ArcadeSave Id 293, UserId 1, game 60439, slot **100**, label "Level 4",
  file `D:\ArcadeStorage\savestore\1\60439\slot-100.dat` (50,599,322 B, 08-05). System=ps2 (default
  core) so it seeds cleanly. Do NOT touch his rows/files — copy to ArcadePlayer2 (user **33**) as a
  NEW slot (33/60439/slot-101 + ArcadeSave row, label "Level 4 bench") for benching. July repro
  precedent used user 33 the same way.
- **Current Stuntman config**: ArcadeGameProfile Id 7 (ps2/stuntman) is EMPTY (renderer sweep
  2026-08-02 dropped pcsx2_renderer). Runs system default = **paraLLEl-GS (Vulkan)** profile
  `parallel_gs`; yaml Ultra baseline = pgs_ssaa "8x SSAA (can high-res)" + pgs_high_res_scanout
  enabled + pgs_disable_mipmaps enabled. No game-overrides.json entry for Stuntman (only Ignition).
- **GameDB at source has clampModes REMOVED** (D:\Arcade\build\lrps2 branch movietheater-lrps2,
  HEAD 2cf344b; GameIndex.yaml SLUS-20250 = BlitInternalFPSHack + cpuSpriteRenderBW:4 +
  halfPixelOffset:4 only). So live core has NO FP-accuracy fix → AI bug fully live. The removal
  was justified on Software-renderer-era perf math (~5-9ms/frame EE cost vs 16.6 budget), PREDATES
  the paraLLEl-GS/Vulkan switch.
- **Live core** = pcsx2_custom_libretro.dll "LRPS2 (v2.0.0-fe939ae)" per worker log (build of the
  perf-attr instrumented branch; 2cf344b magic-byte disc fix may NOT be in the deployed DLL —
  version string says fe939ae. Check before rebuild).
- **Eric's 08-05 session (glworker.log from line 193536)**: he tried renderer arms himself:
  17:40 paraLLEl-GS room, 17:42 room-cheat pcsx2_renderer=**Vulkan** (GSdx) room, 17:45+ more.
  Combined pace over that stretch: median ticks/s 51, 82/97 windows had slowTicks, total 7230
  slowTicks, worst maxTick 220ms — BUT this mixes rooms/renderers; need per-room split (TODO).
  Boot-time GameDB lines confirm only BlitInternalFPS + cpuSpriteRenderBW + halfPixelOffset fire.
  RA note: save-state load tags run save-scummed — fine for bench.
- All 3 workers free right now; coordinator healthy on :8000. Level 4 save file verified present.
- **Deinterlace levers ARE core options** (hot-reload, no rebuild): pcsx2_deinterlace_mode
  (Automatic default; Off/Weave/Bob/Blend/Adaptive × TFF/BFF), pcsx2_nointerlacing_hint (enabled
  default — applies no-interlacing patch IF game has one in internal DB; Stuntman "No CRC-specific
  patch or default patch found" in log — VERIFY that line refers to this db), pcsx2_pcrtc_antiblur
  (enabled default), pcsx2_disable_interlace_offset (disabled default).
- **Eric downloaded a desktop PCSX2 build of branch `int_fallback_fpu_cop2`** to
  `C:\Users\Atoramos\Downloads\int_fallback_fpu_cop2\` (pcsx2-qtx64.exe, Qt6). Users in #2990 say
  it's a big help for the AI bug. Version resource is 0.0.0.0 — identify commit via strings
  (UTF-16 scan TODO) or docs. This is (a) ground-truth tester, (b) the SOURCE of the fix to
  potentially backport into LRPS2.
- Opus research agent running in background: upstream GameIndex entry, #2990/#12001 state,
  deinterlace-vs-PGS wiring in LRPS2 source, no-interlacing patch db coverage, clamp modes.

### Key constraints (from memory, non-negotiable)
- Benchmark isolation: deregister workers to ONE for arms (schtasks /End on "MovieTheater - Arcade
  GL Worker 2" + Watchdog; NOT Stop-Process — classifier blocks it; sanctioned recycle =
  schtasks /End + /Run). Hands off box during measurement. Restore + verify /status 3 free after.
- game-overrides.json is GENERATED (arcade-gameconfig-export) — never hand-edit; per-game options
  go in ArcadeGameProfile row 7 then export, OR per-room via bench (config tool delivers per-room).
- Worker DLL is locked while worker runs; core DLL swap needs worker task recycle. go build rename
  trap if rebuilding worker (not planned). Coordinator rebuild only if API changes (not planned).
- PS2 saves: skip_boot_restore + deferred warm-VM restore (0032b). Manual Save/Load works.
  Never hand a core a foreign save-state: states are per-CORE; our .dat = LRPS2 state, NOT
  loadable in desktop PCSX2. Desktop testing would use the memcard
  (D:\ArcadeStorage\cards\1\ps2\) — copy, never move.
- pcsx2_enable_hw_hacks kills ALL GameDB auto-fixes — never set it.
- Stuntman ISO: JIT-copied at D:\ArcadeStorage\roms\ps2\Stuntman (USA).cso (verify present).

### Plan
1. [in progress] Recon (this section).
2. Research agent report → pick levers.
3. Baseline bench: seed Level 4 as user 33 slot 101; ps2-bench variant using chooseStartSlot
   ("Level 4 bench"); measure pace-diag + burst screenshots (vertical bob evidence = compare
   consecutive frames' vertical offset). Consider restart-stunt via pause menu for clean window.
4. Fixes: (a) jitter: deinterlace/PGS option arm (per-room via bench first, then persist in
   profile row 7 + export); (b) AI: GameDB clampModes at source (eeClampMode:3 and/or
   vuClampMode) → rebuild core → measure perf cost from Level 4 save; if int_fallback branch
   is backportable and clamp insufficient evidence-wise, evaluate backport (bigger lift).
5. Verify: perf ≥ baseline within budget at 60fps, bob gone in screenshots, GameDB log lines
   prove clamp applied. Restore workers, write memory, leave Eric test notes.

### SESSION STATE 2026-08-08 ~03:45 (context insurance — read on resume)
- RESEARCH VERDICTS (Opus agent, full report in transcript; key facts): (1) deinterlace_mode/
  disable_interlace_offset/nointerlacing_hint are ALL effectively inert under paraLLEl-GS
  (PGS has ONE deinterlacer + force_progressive; hint table lacks SLUS-20250; the 10 modes
  collapse to a field-phase XOR — only TFF-vs-BFF matters). pgs_high_res_scanout=enabled
  (our live config) DISABLES deinterlacing entirely. (2) #2990: chases are RECORDED INPUT
  playback (dev confirmed) — only bit-accurate FP fixes it. PR #12001 (PS2Float soft-float,
  open draft, updated 08-05) + Goatman13's int_fallback (route ONLY FPU/COP2 ops to interpreter,
  keep EE rec, VU0 interpreter) = video-confirmed 100% completion, runs on a 2013 i5. Eric's
  Downloads exe = that build (Oct 26 2025). Branch NOT public — port from PR #12001 diff +
  re-derive routing. (3) Our tree HAS the interpreter compiled in + both fallback macros
  (iFPU.cpp:94 REC_FPUFUNC, microVU_Macro.inl:150 INTERPRETATE_COP2_FUNC). vuClampMode has NO
  fast-path cost (inline SSE clamps; mode 3 cheaper than 2); eeClampMode:3 cost = iFPUd double
  path (the 5-9ms). roundMode cost = FPCR MISMATCH (LDMXCSR pairs), not the mode itself.
- PORT AGENT RUNNING (Opus): branch stuntman-softfloat in D:\Arcade\build\lrps2 — new core
  option pcsx2_softfloat (default disabled) + PS2Float port + block-compile-time fallback
  routing + VU0-interpreter switch; builds via build-core.bat; does NOT deploy. Its log:
  D:\Arcade\build\lrps2\SOFTFLOAT-PORT-NOTES.md.
- BENCH SEEDS READY (user 33 = ArcadePlayer2): slot 101 "Level 4 bench" (row 305) + slot 99
  QUICKSAVE overwritten with Eric's Level 4 state (row 65 relabeled "Quicksave (Level 4)";
  originals backed up as *.bak-20260808). Eric's slot-100 = START OF THE LEVEL 4 RACE.
- ⚠ BOOT-SEED RESTORE IS SILENTLY NOT LANDING: gateway logs "Arcade save seeded ... slot 101
  (chosen resume)", worker logs "deferred boot-restore done (warm-VM resume)" — but screen
  shows normal boot/attract (NOT the race). Unresolved; WORKAROUND = in-room quickload button
  ("Load", exact text) loads slot 99 (human-proven path). Diagnose the deferred-restore lie
  later (fork frontend.go warm-VM path).
- Driver tool built: .claude/skills/test-roms/ps2-live-drive.mjs (--cmd file commands: press/
  hold/snap/probe/wait/loadquick/clickbtn/quit; --slot picks named snapshot at start modal).
  ps2-jitter-bench.mjs = one-shot variant with bob probe. Both log in as ArcadePlayer2.
- Isolation state: GL Worker 2/3 + watchdog TASKS ended; their worker.exe processes run
  ORPHANED (idle, High prio, affinity ok) — classifier blocks Stop-Process; restore =
  schtasks /Run watchdog (+workers if their orphans die). Worker 1 task+runner intact.
- Room attempt DJZDRY (clean start) timed out waiting Playing 60s — cause unknown (port-9000
  worker possibly still closing prior room; or build contention). Retry with /status check first.
- Workers' Stuntman perf (Eric 08-05, per-room): paraLLEl-GS ~59.9 t/s GOOD; Vulkan-GSdx 45-51
  BAD; OpenGL-GSdx 54 BAD. Ship nothing GSdx.

### CHECKPOINT ~04:40 — port DONE, baseline DONE, soft-float DEPLOYED, A-arm RUNNING
- **PORT COMPLETE** (Opus agent): branch `stuntman-softfloat` (5 commits, HEAD 5ff4f9c) in
  D:\Arcade\build\lrps2. Options: `pcsx2_softfloat` (disabled default) + `pcsx2_softfloat_scope`
  (All / Add-Sub+Mul / Add-Sub only / Div-Sqrt only). When on: 13 COP1 ops + ALL 75 COP2 macro
  ops route to interpreter at block-compile time (plain if — bytecode identical when off), VU0 →
  CpuIntVU0, VU1 recompiled. PS2Float vendored from PR #12001 (3 mechanical adaptations; agent
  FIXED an MSVC bitfield-union bug present in the upstream PR — bitfields moved out of the
  bitset union to plain u8s). VUops.cpp half fully re-derived (this fork predates upstream's
  template refactor): VU_MAC_UPDATE PS2Float overload routes ~300 sites; 70 FMAC ops got guard
  lines; hand-written soft branches for OPMULA/OPMSUB/DIV/SQRT/RSQRT/E-ops. ⚠ COP2 fallback must
  stay ALL-or-nothing (denormalized status flag lives in VI[REG_STATUS_FLAG] between macro ops).
  Full notes: D:\Arcade\build\lrps2\SOFTFLOAT-PORT-NOTES.md. Built clean /O2 (620 TUs proven):
  bld\libretro\pcsx2_libretro.dll 11,611,136 B (sha256 8BFFA576...).
- **BASELINE DONE** (bench agent, rooms N4RITE + M6HTWH, full report in transcript; artifacts
  scratchpad\benchA\, pace*.txt): Level-4 start-line driving on paraLLEl-GS = **median 60.0
  t/s, min 59.9, 2 slowTicks total, worst maxTick 34.3ms, meanTick 6.92ms** (~58% headroom).
  **ZERO vertical bob** in 5 probes (incl. 2 while driving) — consistent with hi-res scanout
  disabling deinterlacing; if Eric still sees jumping, probe FRAME PACING client-side next, or
  it was the GSdx arms he tried on 08-05. QUIRKS: quickload of his snapshot lands on "SCENE
  FAILED: TOO SLOW" (the wait between load and drive burns the 5s no-move budget) → recovery:
  KeyZ → wait 3 → KeyZ(Restart Stunt) → wait 10 → race live. Menus are edge-triggered (press,
  never hold). Pause→Restart = Enter, ArrowDown, KeyZ, NO confirm dialog. Room-1 "heavy blocks"
  (meanTick 14ms, noSleep 94%) correlate with results-screen auto-replay + off-road warehouse
  yard — NOT start-line driving; identify later if it matters.
- **DEPLOYED for the A-arm**: new DLL → BOTH workers' assets\cores\pcsx2_custom_libretro.dll
  (backups *.pre-softfloat-20260808). worker-gl yaml has TEMP `pcsx2_softfloat: "enabled"`
  (REMOVE post-bench; marked in-file). worker-gl-2 taken DOWN via .stop sentinel (isolation;
  bring back later with schtasks /Run "MovieTheater - Arcade GL Worker 2"). Fresh worker-gl
  PID 14196. ⚠ CLASSIFIER BLOCKS: Stop-Process on workers AND recycle-arcade-glworker.ps1
  (contains force fallback) AND edits to worker-gl-2\config.yaml — the pure .stop sentinel
  drop + wait + Remove-Item works and is the graceful path (worker exits in ~3s, flushes).
- **A-ARM RUNNING** (fresh Opus agent, benchB\): same 3-run protocol; verifies core version
  suffix ≠ fe939ae, pcsx2_softfloat READ in reconcile, old-build state loads OK, then paces.
  PASS bar: median ≥ ~59, slowTicks ~0 (meanTick rise expected, budget 16.6ms). FAIL → try
  scope arm. ROLLBACK: restore .pre-softfloat DLLs + drop the yaml key + recycle.
- **A-ARM RESULT (~04:15 room O4ZPPL): FAIL perf gate.** Core v5ff4f9c live, option READ,
  old-build state loads fine, room clean — mechanism 100% works. But driving = median 54.3 t/s
  (min 53.0), 349 slowTicks, meanTick 12.22ms, audio derived 43509 Hz (−9.4% ≡ tick deficit).
  Cost entirely in-engine; menus/boot/attract full speed. Full-scope soft-float ≈ −10% realtime.
- **ARM B1 RUNNING (~04:50)**: pcsx2_softfloat_scope: "Add/Sub + Mul" added to worker-gl yaml
  (tokens verified from source), worker recycled (PID 6800), fresh Opus bench agent on the same
  3-run protocol (benchC\). Port agent simultaneously analyzing: what scope actually gates
  (COP2 routing is all-or-nothing!), cost decomposition (per-op flush+call vs VU0-micro
  interpreter vs PS2Float math), and a VU0-micro-stays-recompiled variant. Next arms if B1
  fails: Add/Sub only; then targeted variant per port-agent analysis (incremental rebuild).
- **PORT-AGENT ANALYSIS (~05:00)**: scope narrows COP1 ONLY (all 75 COP2 ops still route;
  VU0 executor switch unaffected by scope — any nonzero mask forces CpuIntVU0). Cost ranking:
  (a) per-op FLUSH_INTERPRETER (0xfff — destroys EE reg alloc + const prop around EVERY
  diverted op) = primary suspect; (c) PS2Float math = second; (b) VU0-micro-interpreted =
  wildcard (≈0 if physics is macro-only). NOTE meanTick 12.22 < 16.68 budget → the 54.3 cap
  may be partly contention/scheduling — CHECK perf-attr wall/ee_cpu/gs_cpu split for the A-arm
  windows (already in glworker.log) before optimizing blind. VU0-micro-recompiled + macro-
  interpreted split IS state-coherent (all VU0 state in vuRegs[0] memory at boundaries;
  full-diversion means the denorm-flag protocol never fires; Goatman's "VU0 interpreter" =
  accuracy statement, micro programs just get hard-float — measurable risk, not crash risk).
  BUILD PLAN (agent holding for my go until B1 bench finishes): V1 lighter flush (COP2:
  FLUSH_FREE_XMM|FREE_VU0|CODE=0x904; COP1: 0x804; xFastCall direct, drop per-op cycle-add;
  pre-flight: verify FLUSH_NONE frees caller-saved in iCore-32.cpp) + V2 separate option bit
  for VU0-micro executor — ONE compile, then arms: (i) V1 full-soft, (ii) V1+VU0-micro-rec.
  Optional decomposition arm: scope "Div/Sqrt only" ≈ isolates routing+VU0 cost from math cost.
  Real ceiling-raiser if V1+V2 fall short: soft-float RECOMPILED path (emit PS2Float calls,
  no flush) — bigger lift, later.
- **B1 RESULT (~05:05, room H7VPR6): NEAR-PASS.** Scope "Add/Sub + Mul" = driving 59.9 flat
  slowTicks 0 on true clock, +1.44ms/frame (vs +5.30 full). CAVEAT: results-screen auto-replay
  is heavy enough with +1.44 to trip the worker's 2% audio derivation once (48000→46749; then
  the PACER pins the room at 58.4 = the B/C windows' 58.4 — a clock artifact, not compute).
  DECOMPOSITION VERDICT: div/sqrt (COP1 routing + PS2Float iterative divider math) = the ~3.9ms;
  flush overhead NOT dominant. Restart from results menu = ONE KeyZ (then wait 10).
  Worker-side follow-up gap: pipeline built at 48000 with NO resampler → derivation re-pin is
  one-sided (audible pitch shift risk) — pre-existing, fix later.
- **V1+V2 BUILT (commits a8af345/4a98297 + notes on stuntman-softfloat)**: V1 = narrow flush
  masks (FLUSH_SOFTFLOAT_COP1/COP2, xFastCall direct, no per-op cycle store — ⚠ FLUSH_CODE is
  mandatory, not in FLUSH_FOR_POSSIBLE_MICRO_EXEC); V2 = new option pcsx2_softfloat_vu0micro
  (interpreter default / recompiler) — gates ONLY CALLMS micro programs; COP2 macro stays soft
  either way. DLL 11,606,528 B (verify by LENGTH not hash — MSVC timestamps make sha differ
  across identical builds). Deployed to BOTH workers' cores dirs ~05:45.
- **ARM C1 RUNNING** (benchD\): scope All + vu0micro interpreter on V1 core. Then C3 =
  scope "Add/Sub + Mul" + vu0micro "recompiler" (cheapest bit-exact-COP2-macro arm, the ship
  favorite if C1 fails). C2 (All + recompiler) only if needed to isolate (b).
- **DELIVERY FACT: no site deploy needed** — ArcadeRoomOptionDelivery.FilterForBootingCore
  passes keys unknown to every catalog (Advanced-key path). Profile row 7 delivers per-room
  NOW. Catalog regen (scripts/extract-core-options.ps1) + CI push only for the ⚙ UI display.
- REMAINING for ship after A-arm passes: (1) merge branch → movietheater-lrps2; (2) regen
  docker/arcade/lrps2 patch artifact if that's the convention (check arcade-lrps2-build);
  (3) durable per-game delivery: scripts/extract-core-options.ps1 regen of
  core-options-catalog.json from the NEW deployed DLL + ArcadeGameProfile row 7 (ps2/stuntman)
  CoreOptionsJson={"pcsx2_softfloat":"enabled"} + arcade-gameconfig-export to BOTH ConfDirs +
  site commit/push (CI deploys; Eric authorized prod impact) — REMOVE the temp yaml key at
  this point; (4) restore worker fleet (schtasks /Run for Worker 2 + Watchdog; capture worker
  3 orphan: .stop its ConfDir D:\ArcadeStorage\worker-capture then /Run task 3); (5) verify
  /status 3 free; (6) memory updates + Eric test notes.

### DIAGNOSED (not yet shipped): PS2 lobby-Resume is a silent no-op
- Fork frontend.go:603-615 — skip_boot_restore ⇒ deferred restore at FIXED 5s. PS2 fastboot is
  still in BIOS/ELF-load at t+5s (Atari logo ~25s in), so the state restores into a pre-game VM
  and the boot stomps it; RestoreGameState returns nil so the log says "deferred boot-restore
  done (warm-VM resume)" regardless. Eric's 08-05 17:40 45s room = him hitting this.
  FIX LATER (worker change): per-core configurable BootRestoreDelaySec (ps2 ~25s) or a
  game-running signal; verify restore actually landed (frame content/serialize compare) before
  logging success. NOT shipped tonight — quickload covers testing; Eric can use 📂 Load snapshot
  in-room. Ship with next worker rebuild.

### Bench interface notes
- ps2-bench.mjs: logs in ArcadePlayer2/ArcadeTest!2026, --resume takes Continue Auto-Save;
  use startModal.mjs `chooseStartSlot(page, /Level 4 bench/)` for the named slot.
  KeyZ=PAD.B=Cross=accelerate; arrows=left stick; Enter=START (pause menu).
- pace-diag + [perf-attr] lines land in D:\ArcadeStorage\logs\glworker*.log per 5s window.
- Screenshots land in CWD (bench-*.png) — run from scratchpad dir.

### Findings log (append as discovered)
- (08-05 log) Vulkan-GSdx room reconcile: 7/9 keys read, pgs_high_res_scanout DEAD under GSdx
  (expected); paraLLEl-GS room: 6/9 read, aniso DEAD (expected — GSdx key).
- **PER-ROOM SPLIT of Eric's 08-05 session (glworker.log 193530+): the slowness is GSdx, not PGS.**
  room1 17:40 pGS: median 59.9; room2 17:41 pGS: median 59.9 (min 57) — GOOD.
  room3 17:42 Vulkan-GSdx: median 51, 3043 slowTicks — BAD. room4 17:45 Vulkan-GSdx: median 45.5.
  room5 17:45 OpenGL-GSdx: median 54.1, 3850 slowTicks — BAD. Eric was renderer-hunting (probably
  chasing the bob); GSdx renderers are the "runs slow" experience. VERDICT: keep paraLLEl-GS,
  fix the bob there. Do NOT ship a GSdx renderer for this title.
- int_fallback_fpu_cop2 download = desktop PCSX2 Qt build (pcsx2-qtx64.exe), version resource
  blank; its GameIndex.yaml Stuntman entry: BlitInternalFPSHack only, cpuSpriteRenderBW commented
  out, NO clampModes (suggests branch fixes accuracy in code, not per-game data). Research agent
  briefed to identify branch/PR + backport feasibility (supersedes old "soft-float rejected" verdict
  if evidence changed).
- Deployed core = v2.0.0-fe939ae; source HEAD = 2cf344b (magic-byte disc-reader fix, one commit
  ahead, apparently never deployed). A rebuild ships it too — validate boot in bench.
- Jitter fix candidate ranking (pre-research): (1) no-interlacing patch for SLUS-20250 (game
  renders progressive — kills bob at source; nointerlacing_hint is already default-on but log
  says no patch found for CRC 76CBC428 — verify which db that line refers to); (2)
  pcsx2_deinterlace_mode Adaptive/Blend IF it applies to PGS path; (3) PGS scanout/field options.

---

## 2026-08-08 SESSION CLOSE — the stale-frame hunt (spectator reels) and the fix built for it

### What was actually wrong, in one line
Single frames of **pristine, seconds-to-a-minute-old content** are spliced into an otherwise
perfect stream, with the sender's clock provably monotonic. It is a **reference-slot** artefact at
the codec layer, and it has **nothing to do with per-peer temporal-layer dropping**.

### The instrument: spectator reels
`spectate-probe.mjs --reel-all` joined each live room as a second seat and wrote **every presented
frame** (`requestVideoFrameCallback`) to disk as `pf<7-digit presentedFrames>-mt<mediaTime>.jpg`;
filename sort order IS presentation order. Two runs of Eric playing Stuntman (PS2, paraLLEl-GS,
AV1 3 temporal layers over WebRTC):

| reel | frames | mediaTime | room | worker log | wall clock |
|---|---|---|---|---|---|
| `reel-run1` | 6,315 | 0.056 - 118.590 s | QRPH4K | `glworker-2.log` from line 183590, `cid=d9r.ung` | room 21:50:50 -> 21:55:13; **reel t0 ~ 21:53:15.29** (3rd spectator peer) |
| `reel-run2` | 12,412 | 0.046 - 228.401 s | 6Q7XCX | `glworker.log` from line 216991, `cid=d9r.7q0` | room 21:57:12 -> open; **reel t0 ~ 21:57:28.88** (2nd peer, `[ts-mono]` close 22:01:17.28) |

Both t0 anchors are end-anchored off that peer's `[ts-mono] summary` line and agree with its
`rtc (connected)` line to ~1.5 s.

### Detector (`.claude/skills/test-roms/reel-anomaly-scan.py`)
A-B-A at a 64x48 luma downscale: flag B when `dAB > 12` **and** `dBC > 12` **and**
`dAC < 0.45 * min(dAB,dBC)` — B disagrees violently with both neighbours while the neighbours agree
with each other. The separation is not marginal, it is **bimodal**: every true anomaly scores
`dAC/min <= 0.165`, the strongest non-anomaly in either reel scores **1.33**. There is no threshold
to tune; anything from 0.2 to 1.2 gives the same nine frames. Playwright was abandoned for this
(it kept dying with "context destroyed by navigation" on `file://`); Python + PIL + numpy, with the
decoded luma cached to `_luma64x48.npy`, scans 12k frames in ~40 s and re-scores instantly.

**Sanity gate passed:** both user-confirmed exhibits (`pf0006112`, `pf0011214`) are flagged, and
only 3 other frames in 12,412 join them.

### FULL ANOMALY LISTS — 9 total, every one visually confirmed as a true positive

**reel-run2 — 5 of 12,412 (1 in 2,482)**

| pf | mediaTime | wall | ratio | what the frame shows | content age |
|---|---|---|---|---|---|
| 0000347 | 10.655 | 21:57:39.5 | 0.050 | **fully black** between two identical gameplay frames (TC 00:20:46 / 00:20:50) | pre-gameplay black, >=27 s |
| 0006112 | 112.190 | 21:59:21.1 | 0.100 | gameplay TC **00:16:73** between 00:22:73 / 00:22:76 — *the user's exhibit* | +6.013 s |
| 0008162 | 148.123 | 21:59:57.0 | 0.036 | the **PAUSE menu**, mid-gameplay (TC 00:23:53 / 00:23:56) | +35.132 s (pixel-identical to pf0006160) |
| 0011214 | 203.465 | 22:00:52.3 | 0.153 | the **PAUSE menu**, mid-gameplay (TC 00:41:96 / 00:42:03) — *the user's 2nd exhibit* | +53.640 s (pixel-identical to pf0008261) |
| 0011446 | 207.668 | 22:00:56.6 | 0.165 | gameplay TC **00:42:00** between 00:46:13 / 00:46:16 | +4.169 s |

**reel-run1 — 4 of 6,315 (1 in 1,579)**

| pf | mediaTime | wall | ratio | what the frame shows | content age |
|---|---|---|---|---|---|
| 0000801 | 19.211 | 21:53:34.5 | 0.142 | the **PAUSE menu**, mid-gameplay (TC 00:08:60 / 00:08:63) | older than the reel — from before this peer joined |
| 0000894 | 21.127 | 21:53:36.4 | 0.134 | gameplay TC **00:08:63** between 00:10:40 / 00:10:43 | +1.899 s (pixel-identical to pf0000802) |
| 0002929 | 58.160 | 21:54:13.5 | 0.025 | the **PAUSE menu**, mid-gameplay (TC 00:24:56 / 00:24:60) | +36.243 s |
| 0004129 | 79.231 | 21:54:34.5 | 0.124 | the **PAUSE menu**, mid-gameplay (TC 00:08:73 / 00:08:76) | +20.188 s |

Contact sheets: `.claude/skills/test-roms/anomaly-sheet-reel-run1.png` and `-run2.png` (A/B/C
triples). One multi-frame run exists (`k=4`, pf0008163..66) and it is simply the real pause
transition beginning 8 frames after the stale pause frame at pf0008162.

**Shape of the population.** 6 of 9 are a *pause menu* or *black* — screens that are STATIC for
many frames. 3 of 9 are ordinary gameplay from 1.9-6.0 s earlier. Half the anomalies are
pixel-identical (`diff <= 0.22` at 64x48) to a specific earlier frame in the same reel. Nothing but
a retained decoded picture can produce that.

### CORRELATION VERDICT — the temporal-layer-dropping theory is REFUTED

Layer decisions in the two rooms (`abr: peer layer X -> Y`, the ONLY layer log there is):

```
run2  21:57:14.66  peer 7 -> 2 (solo room)        21:57:29.66  peer 7 -> 1
      21:57:33.66  peer 2 -> 1                    21:57:40.66  peer 1 -> 2
      21:57:41.66  peer 1 -> 2      ... and NOTHING for the remaining 3.5 minutes
run1  21:50:52.96  peer 7 -> 2      21:51:15.95 7->1   21:51:26.95 1->2
      21:52:26.95  7 -> 1           21:52:37.95 1->2   21:52:39.95 2->1
      21:52:47.95  1 -> 2           21:53:14.95 7->1   21:53:25.95 1->2   ... then nothing
```

- **8 of the 9 anomalies land in windows where every peer sat at the TOP layer** — no frame was
  being skipped for anybody, anywhere. Only run2's black frame (21:57:39.5) falls inside a
  layer-1 window, and that window closed 1.1 s later; with 9 events and ~8 % of the session spent
  capped, one hit is what chance looks like.
- **No congestion at all.** `abr: summary` for run1: `open=6000 ceil=12373 layers=3 ticks=261
  rampTicks=2 atCeilPct=99 cuts=0`. Every `summary-peer` is `path=samehost sustained=12373`,
  `rttMean <= 0.56 ms`. The room sat pinned at its ceiling for its whole life.
- **No loss-repair activity.** Exactly ONE `video: PLI honored` per peer join in each room and
  none in between — the decoders never asked for help, because the frames they got were *valid*,
  just *wrong*.
- **Nothing in the emulator.** `pace-diag` across both sessions: `ticks/s` 58-60, `vcoal/s = 0.0`
  everywhere after boot, no `maxTick` spike near any anomaly. `[zc-stat]`: `inflight 1-2/8`,
  `refs 1-2`, `dup(pin=2 skip=0)` for a whole 27-minute worker lifetime, `reallocs=0`.
- **Intra-refresh phase cannot be tested from this data and was not.** The mediaTime->wall mapping
  is good to ~+/-0.5 s = +/-30 encoder frames, which is wider than half the 120-frame refresh
  period. Saying "it does/doesn't align with the wave" from these reels would be arithmetic theatre.

**Therefore:** per-peer `WriteSample` skipping is not the cause, and rolling the SVC ladder back
would not have fixed anything. Good — the directive was to keep it, and the evidence agrees.

### What IS left, and the measurement that pointed at it
A pristine picture from 53 seconds ago, decoded correctly, displayed with a fresh timestamp, in
BOTH codecs, with no loss and no drops, can only come from a **decoded picture the encoder still
had in its reference list**. Reading our own patched plugin:

```c
av1_config->maxNumRefFramesInDPB = 0;                        /* 0 = "driver default" = full 8-slot DPB */
av1_config->numFwdRefs = NV_ENC_NUM_REF_FRAMES_AUTOSELECT;   /* up to 4: LAST, LAST2, LAST3, GOLDEN */
/* h264: maxNumRefFrames and numRefL0 were never set at all */
```

With `gop-size=-1` there is **no periodic IDR to ever flush that DPB**. A slot — AV1's GOLDEN in
particular, which is exactly the "long-lived anchor" slot — can hold one picture for the entire
life of a room, and a frame predicted from it with a null residual decodes to that picture,
pristine. That is a complete account of every observation, including why STATIC screens (pause
menu, black) are over-represented: a static screen is precisely what a long-lived anchor gets set to.

**MEASURED tonight, on the GPU, with the new build (this kills the LTR sub-theory):**
```
nvav1enc : strict-refs: preset gave enableLTR=0 ltrNumFrames=0 maxNumRefFramesInDPB=0
nvh264enc: strict-refs: preset gave enableLTR=0 ltrTrustMode=0 ltrNumFrames=0 maxNumRefFrames=0
```
So it is NOT long-term-reference marking. It is the **unbounded DPB + multi-forward-ref** default.

### FIX BUILT (nothing deployed)

**1. `docker/arcade/patches/gst/0003-nvcodec-strict-refs.patch`** — new, applies on top of 0002.
Adds a `strict-refs` boolean property (**default TRUE**) to BOTH `nvav1enc` and `nvh264enc`:

| | AV1 (3 layers) | H.264 (2 layers) |
|---|---|---|
| DPB | `maxNumRefFramesInDPB = numTemporalLayers` (3) | `maxNumRefFrames = numTemporalLayers` (2) |
| forward refs | `numFwdRefs = NV_ENC_NUM_REF_FRAMES_1` | `numRefL0 = NV_ENC_NUM_REF_FRAMES_1` |
| long-term | `enableLTR = 0, ltrNumFrames = 0` | `enableLTR = ltrNumFrames = ltrTrustMode = 0` |

The oldest picture anything can reference becomes the previous base-layer frame (<=2 frames back).
Upper-layer drops are reference-safe **by construction**, and a stale-reference re-emission, if one
still happened, would reproduce a ~33 ms-old picture — invisible — instead of a six-second time warp.
It also logs what the preset handed it before overriding, so the next session can re-read the facts
with one grep. `strict-refs=false` in the params string restores today's behaviour for an A/B.

**Built and verified (worker.exe was NOT stopped; nothing was installed):**
- artifact `D:\Arcade\build\gst-refsafe\gst-plugins-bad-1.28.4\bld2\sys\nvcodec\libgstnvcodec.dll`
  — **1,479,565 bytes** (live/installed one is 1,478,029 and has zero occurrences of `strict-refs`).
- `gst-inspect-1.0` against a private `GST_REGISTRY` + `GST_PLUGIN_PATH`: `strict-refs` present on
  **both** elements, `Boolean. Default: true`, alongside the existing `temporal-layers` /
  `intra-refresh-period` / `intra-refresh-count`.
- **Live encodes with the exact production params succeeded (exit 0) and both ladders survive:**

| | strict-refs=false | strict-refs=true |
|---|---|---|
| AV1 300 frames | 164,802 B, tid histogram `{0:75, 1:75, 2:150}` | 164,802 B, tid histogram `{0:75, 1:75, 2:150}` — **different md5**, same size |
| H.264 300 frames | 135,590 B, `nal_ref_idc` 150 ref / 150 non-ref, strict `3,0,3,0...` | 131,135 B (**-3.3 %**), 150/150, strict `3,0,3,0...` |

The H.264 sender identifies droppable frames BY `nal_ref_idc`, and the AV1 sender by the OBU
extension `temporal_id`; both are untouched, so **the ladder is not harmed**.

**2. `scripts/build-gst-nvcodec-patched.ps1`** — now applies 0002 **then** 0003 (order matters,
0003 extends 0002's property tables), verifies `strict-refs` alongside the other three properties,
and takes a new `-BuildOnly` switch that stops before touching the installed DLL. That switch is
how tonight's artifact was produced with workers up; the install path still refuses to run against
a live `worker.exe`, as it should.

**3. Worker interim lever — `CLOUD_GAME_SVC_NO_PEER_DROP`** (fork commit on `movietheater-fork`,
`pkg/network/webrtc/svc.go` + `webrtc.go` + `pkg/worker/coordinatorhandlers.go`). Set it and the
encoder still produces the pyramid but the sender never skips a frame for anyone; ABR falls back to
its bitrate lever. **DEFAULT OFF — it is an instrument, not a fix**, and tonight's correlation says
it is not the cause. It exists so the next person can bisect that class of bug with one restart
instead of a rebuild, and the worker WARNs at room open when it is set so nobody discovers it by
staring at fps. Binary: `D:\Arcade\build\cloud-game-gl\bin\worker.candidate-refsafe.exe`,
**38,988,679 bytes** (`go vet` clean, `-pgo=auto`, CGO `-g -O3`).

### Other verdicts closed out tonight
- **`[ts-mono]` is CLEAN.** `violations=0 maxRegression=0ns` over `samples=13376` (run2) and 2510 /
  5036 / 6883 / 13338 (run1's four peers). The sender's RTP clock is not the problem, full stop.
  The instrumentation stays — it is what makes "the timestamp is fine, the *picture* is wrong" a
  fact rather than an opinion.
- **The upload-redundancy theory is DEAD.** `pgs-prof` on the heaviest crossings:
  `upload=6.4ms(1480ops/187.7MB dup=1/0.0MB)`. One duplicated upload out of 1,480, zero MB. A
  per-page upload-skip cache (the one sketched in lrps2's `SHADER-TOOLCHAIN-NOTES.md`) has nothing
  to win and a corruption risk to lose. **Do not build it.**
- **Warm crossings are now 46-93 ms** (46.4 / 53.8 / 92.9 ms observed; one 155.3 ms cold) after the
  pipeline-cache fix + scratch pre-warm. `disp~6320, sub~163` on those frames. The remaining cost
  is spread across `shading`/`texcache`/`other`, not one hotspot.
- The reels also measured something worth remembering: **~53 % (run1) / ~25 % (run2) of presented
  frames are pixel-duplicates of their predecessor.** That is the 30 fps interlaced source arriving
  through the field merge at 60 — normal, and it is why a naive "duplicate frame" metric can never
  find this bug.

### NEXT-SESSION CHECKLIST (nothing below was done tonight)

**A. Deploy the encoder fix** — this is the one that matters.
1. Stop BOTH worker tasks and confirm `localhost:8000/status` shows no live rooms. The DLL is
   loaded by every running worker and cannot be replaced while one is up.
2. `pwsh scripts/build-gst-nvcodec-patched.ps1` (no `-BuildOnly`). It rebuilds from a pristine tree,
   re-vendors the SDK 13 header, applies 0002+0003, installs, and verifies all four properties.
   Tonight's artifact can be copied instead, but re-running the script is the supported path.
3. Restart the workers, open a Stuntman room, and grep the worker log for
   `strict-refs: preset gave ...` — its absence means a stock DLL got restored and the whole thing
   (SVC + intra-refresh included) is silently off.
4. **Re-run the spectator reel** (`spectate-probe.mjs --code <room> --reel-all`, then
   `reel-anomaly-scan.py`). ~2,000 presented frames per anomaly is the observed rate, so a
   3-4 minute run with heavy spots (restart reload, warehouse content storm, pause/unpause) is the
   right length to expect 4-6 events if the bug survives. **Zero anomalies over >=12k frames is the
   pass criterion.** If it survives, the next suspect is the zero-copy slot handed to the encoder,
   not the encoder's own DPB — instrument `glzerocopy.go` / `nanoarch_vulkan.c` to stamp each
   emitted slot's serial into the frame and read it back off the reel.

**B. Worker binary** — only if the encoder fix alone is not conclusive; the flag is diagnostic.
`worker.candidate-refsafe.exe` deploys the usual way: `mv bin\worker.exe` aside, copy the candidate
in as `bin\worker.exe`, then `touch` the `.stop` sentinel in **both** `D:\ArcadeStorage\worker-gl`
and `worker-gl-2`. Workers exit consuming the sentinel and the runners respawn on the new exe in
~4 s. Verify by HASH, not by timestamp.

**C. Loose ends left open on purpose tonight**
- **`ArcadeGame` Id 60439: revert `MaxPlayers` 2 -> 1.** It was raised so the spectator probe could
  take a second seat. Stuntman is a 1-player title.
- **Delete `ArcadeGameProfile` row Id 33** (`TEMP pipeline-cache verify`).
- **N64 pipeline-cache live verification is still pending.** The cores are deployed to BOTH
  workers' `assets\cores`; grep a live N64 room's log for the paraLLEl-RDP cache lines to confirm
  the cache is being loaded and written.
- **Live `worker.exe` is the `5e0898f` build** (38,987,569 B) — it already contains the zc-dup
  refcount fix (10ff67a), the two-sided audio-rate fix (94f2324) and the `[ts-mono]` probe. The
  audio fix IS live. `worker.candidate-refsafe.exe` is 5e0898f + tonight's SVC flag only.
- Core DLLs (lrps2 `2fe1510`) are already deployed to both workers.
