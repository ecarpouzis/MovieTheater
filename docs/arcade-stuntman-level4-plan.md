# Stuntman round 2 — frame jitter + level-4 AI turn bug (OVERNIGHT 2026-08-08)

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
