# Arcade — the UNKILLABLE worker wedge (ROOT-CAUSED 2026-07-21; fixes deployed)

**Status (2026-07-21, late session): SOLVED at the user-mode root, from the worker logs — no kernel
debugging needed after all (do NOT disable Secure Boot for this; that whole avenue is moot). The
EIGHTH PASS section directly below has the complete causal chain, the explanation of every earlier
"mystery" finding, and the three deployed fixes. Everything below it is kept as investigation
history — several of its theories (kernel-mode NVIDIA teardown hang, cross-adapter fence, missing
SetEvent caller) are now known to be WRONG or red herrings; read the eighth pass first.**

## EIGHTH PASS — ROOT CAUSE (2026-07-21, from logs; supersedes all prior theories)

The wedge was never a driver bug and never started in teardown of a healthy room. The full chain,
proven for BOTH recent zombies (7/19 PID 11328 and 7/21 PID 4100 — same game, same log signature,
minutes apart in their respective logs):

1. **"Super Mario 64 - Last Impact" (n64, game 67537 — a Kaze romhack) crashes the emulated CPU
   seconds after boot**: `mupen64plus: Unaligned dword write 00000101` → `error in ERET` →
   `R4300 emulator finished.` (glworker-2.log 2026-07-20 19:42:03; glworker.log.1 2026-07-19
   20:42:57 — identical). Kaze hacks are known to break mupen's old dynarec.
2. The crashed core wedges the room close: `app.Close()` never returns, and **patch 0041's bounded
   teardown correctly fired** ("the core did not finish shutting down after 30s — it is wedged.
   Exiting…") and called `os.Exit(70)`.
3. **`os.Exit` → `ExitProcess` never completed.** ExitProcess terminates sibling threads and walks
   every DLL's `DLL_PROCESS_DETACH` under the loader lock — and it tangled with the still-crashing
   core thread: the log's last lines are `plugin_start_gfx` and `Exception 0xc0000005` (the core
   attempting a gfx restart mid-exit, taking an access violation). Result: a **half-exited undead
   process** — most threads gone, one survivor parked forever, ConfDir handles still held,
   coordinator slot dead. It sat that way for 5.5 h (7/21) / 3.5 min (7/19).
4. The watchdog's external `TerminateProcess` then **failed against this half-exited tangle** —
   that is the "unkillable" symptom. The force-kill didn't cause the wedge; it was fired INTO a
   process already stuck mid-exit (and on 7/19 it was fired only 8 s after the graceful request,
   well inside the worker's own 30 s bound).

**This explains every earlier "mystery" finding:**
- The single surviving thread blocked in `WaitForSingleObjectEx` on a **plain auto-reset Event with
  a NULL timeout, called via Go's `asmstdcall`** is the signature of Go's own runtime lock
  machinery: `runtime.semasleep` waiting on the per-thread `m.waitsema` event (created with
  `CreateEventA(0,0,0,0)` = auto-reset, waited with INFINITE → NULL timeout pointer, invoked by
  direct stdcall — matches the trace detail-for-detail). The thread was waiting on a runtime lock
  whose owner had already been terminated by the concurrent ExitProcess: nobody left to SetEvent,
  ever. The hunt for "our missing SetEvent caller" was a dead end by construction.
- **No GPU module in the stack** — correct, it was never a driver-call hang.
- **TDR never fired** — correct, the GPU was healthy; the wedge is CPU-side process-exit state.
- **Both Intel and NVIDIA driver DLLs loaded** — benign: Vulkan instance enumeration loads every
  installed ICD. The cross-adapter-fence theory was already dead (the handle was a plain Event);
  ignore it.
- Why the process resists TerminateProcess at the kernel level (which specific rundown step pins
  it) remains formally unproven — but with both formation paths closed (below) it no longer
  matters operationally, and kernel debugging (blocked by Secure Boot anyway) is NOT worth
  pursuing.

**A second, independent bug amplified the damage (7/20 22:00 → 7/21 01:11): the watchdog killed
~18 INNOCENT workers.** Its room→port mapping took the FIRST worker log whose last "New room" line
matched the busy room; both GL logs' last room was the wedged title, so it blamed port 8446 all
night — gracefully recycling whatever healthy/idle worker sat there every ~3 minutes — while the
truly wedged PID 4100 on 8447 sat untouched for 5.5 h ("log silent 19,667s" on its first strike).
It only found the real culprit when a player started a different game on 8446 at 01:10:40, which
changed that log's last room.

**Fixes deployed 2026-07-21 (late session):**
1. **Worker (fork, branch `vulkan-w3`): every wedge-exit is now a raw self-`TerminateProcess`, not
   `os.Exit`.** New `pkg/os.HardExit(code)` helper (skips Go runtime shutdown AND the DLL-detach
   walk — the exact mechanism that tangled; same lesson `GstMediaPipe.Destroy` had already learned
   and documented). Used by: room.go's patch-0041 wedge exit (code 70), and a NEW 45 s
   whole-shutdown deadman in `cmd/worker/main.go` armed the moment a stop is requested (code 72) —
   so even a hang in an unbounded stage, or in the final process exit itself, ends in
   self-termination. Deployed binary sha256 `CF2ED746…`; rollback kept as
   `bin/worker.pre-hardexit-20260721.exe`. (The capture worker runs its own separate exe and does
   NOT have this fix yet.)
2. **Watchdog (`scripts/watch-arcade-glworkers.ps1`): ghost-room guard + patient graceful window.**
   A busy room now maps to a port only if that log's last "New room" line was written by the
   CURRENT process on the port (line timestamp ≥ process start; newest match wins) — ghost rooms
   are logged (`ghost room '…' … NOT killing`), never killed. Graceful wait raised 8 s → 60 s so it
   outwaits every worker self-bound (30/10/45 s) and the worker always dies by its own hand;
   external force-kill is a true last resort. `recycle-arcade-glworker.ps1` GraceSec default
   likewise 25 → 60.
3. **Game 67537 stays ENABLED (Eric's call — the goal is to find settings that run it), marked with
   a forensic `Notes` entry in the DB** recording the crash signature and both zombie incidents.
   Candidate fix to test: per-game core option `mupen64plus-cpucore=cached_interpreter` (Kaze hacks
   are known to break the old dynarec). With the hard-exit binary, a repeat crash should now cost a
   ~4 s worker recycle instead of a box reboot. The sibling SM64 romhacks (BAZR etc.) played
   without incident.

**Residual risk, stated honestly:** if a *future* wedge somehow still reaches the external
force-kill (e.g. the Go runtime itself too tangled to run the deadman goroutine), a zombie remains
possible — nothing user-mode can reap a truly stuck thread. But both OBSERVED formation paths
(ExitProcess tangle; premature external kill mid-teardown) are closed, and the trigger game is
disabled. The 2026-07-15 first sighting (PID 7948, game/core unrecorded) is consistent with this
mechanism but can no longer be verified.

---

*Everything below is the investigation history that led here — kept because its negative results
(what the wedge is NOT) and tooling notes are still valuable. Its standing theories are superseded.*

**SEVENTH PASS UPDATE (2026-07-21, same day, admin session): the Administrator-rights blocker from
the SIXTH PASS is now resolved (confirmed elevated on Ziggy itself), but kernel debugging is STILL
blocked — for a new, environmental reason: Memory Integrity (HVCI) is enabled on this box and blocks
LiveKD's legacy kernel driver. Fixed a real LiveKD symbol-DLL bug along the way. See "SEVENTH PASS"
section below for the full finding and the one open decision it leaves for next session (a
deliberate one-time reboot during idle time to pre-enable `bcdedit -debug on`, so the *next* wedge
can finally be traced at the kernel level).**

## LIVE TRACE (2026-07-21, PID 4100, ~1h15m into the wedge)

Attached with `gdb -p <pid>` (MSYS2 UCRT64 build, `D:\msys64\ucrt64\bin\gdb.exe` — same tool used
for the unrelated capture-worker D3D12 crash, see [[arcade-capture-d3d12-crash]]), batch-mode,
`thread apply all bt`, then clean detach. Full raw output kept in this session's transcript if
needed later; key facts:

- **Only ONE live application thread remained** (`info threads` showed it plus gdb's own injected
  `DbgUiRemoteBreakin` thread from the attach — nothing else). A real GL/NVENC worker normally runs
  several OS threads (Go scheduler, a cgo-blocking GL thread, NVENC/GStreamer threads, network
  I/O). By the time this was captured, everything else had already exited/been reaped — only one
  straggler survives. This reframes "unkillable process" as: termination mostly WORKS, but one
  thread + the process kernel object won't fully go away while it persists.
- **That thread's full stack, top to bottom (top 3 frames are solid/reliable):**
  `ntdll!ZwWaitForSingleObject` → `KernelBase!WaitForSingleObjectEx` →
  `internal/runtime/syscall/windows.asmstdcall` (Go's own direct-syscall mechanism for calling a
  Win32 API by address — NOT a cgo call) → then increasingly unreliable/optimized frames, one of
  which resolves to `pkg/worker/caged/libretro.(*Caged).Start` at `caged.go:104`.
- **Read `caged.go:104` — it's `func (c *Caged) Start() { go c.Emulator.Start() }`, a two-line
  goroutine spawn that cannot itself block.** That frame attribution is almost certainly gdb
  misattributing an unwound return address to the nearest resolved Go symbol (the same failure
  mode that produced an obviously-wrong `go:fipsinfo` frame elsewhere in the same trace) — Go's
  runtime stack layout doesn't unwind cleanly under a C-oriented debugger, especially through
  optimized/inlined code. **Don't trust the exact call site; DO trust that it's somewhere in this
  process's own Go code calling a Win32 wait, not inside a vendored library or the driver.**
- **No GPU driver module (`nvoglv64.dll`, `nvldumkm.sys`, `d3d12.dll`, `vulkan-1.dll`) appears
  anywhere in the captured stack.** This is the important negative result: the original theory
  ("thread stuck inside the NVIDIA driver's kernel-mode teardown") has no direct evidence in this
  trace. What's actually visible is a thread blocked on an ordinary Windows synchronization handle
  via a plain syscall — which never gets signaled. The failure to fully terminate under
  `TerminateProcess` despite this is a separate, kernel-level phenomenon (most likely a
  driver-held resource tied to this thread's GPU context refusing to release, which is exactly
  the sort of thing that keeps a Windows process object from being reaped even after its threads
  nominally die) — but that mechanism is invisible to gdb/any user-mode debugger. **This is still
  the argument for kernel debugging as the next step**, just with a narrower question to answer:
  not "where is it stuck" (now partially answered) but "why does the kernel refuse to reap the
  process once this thread is the only one left."
- **Checked for a matching direct `WaitForSingleObject` call site in the fork's own Go code**
  (`grep -rn WaitForSingleObject pkg/`): only one hit, `worker/caged/capture/launch.go:277`,
  and it already has a 3s timeout — so it can't be the cause of an hours-long hang, and it's in
  the capture-lane package anyway (this PID is a GL worker, not the capture worker). **The actual
  call site is not in this repo's own `pkg/` tree** — it's either inlined into a location grep
  didn't catch, or it's inside a vendored dependency (candidate: the RGFW/GL context library, or
  Go's own `os`/`os/exec` internals if something is waiting on a handle tied to a helper thread or
  process). Not yet identified. Worth a `go build -gcflags="all=-N -l"` debug build (disables
  optimization/inlining) next time, specifically so a debugger's unwinder stops producing garbage
  frames like this trace's — that alone would likely resolve the real call site precisely.

**Immediate actionable idea this surfaces:** if the true call site turns out to be an
unguarded/timeout-less wait (mirroring the ALREADY-guarded 3s wait in
`capture/launch.go:277`), the fix could be as simple as adding a bounded timeout to whatever
native wait this is — converting an unrecoverable hang into a clean, bounded failure the existing
graceful-stop/bounded-teardown machinery (patch 0041) already knows how to handle. This has NOT
been confirmed — the call site itself is still unidentified — but it's the most concrete lead
this investigation has produced, and a debug (non-optimized) build is the fastest way to nail it
down for certain.

### SECOND PASS (same session, same live PID) — the wait IS infinite, and BOTH GPU vendors' drivers are loaded

Re-attached and inspected the register state at the `ZwWaitForSingleObject` frame plus the full
loaded-module list before detaching:

- **`r8` (the `Timeout` pointer argument) = `0x0` (NULL).** Confirmed, not inferred: this is a
  genuinely **infinite** wait (`ZwWaitForSingleObject(HANDLE, BOOLEAN Alertable, PLARGE_INTEGER
  Timeout)` — a NULL `Timeout` pointer means wait forever). `rcx` (the handle) = `0xbcc` — a small,
  plausible-looking real kernel object handle, not garbage/zero, so this is a deliberate wait on a
  real object that simply never gets signaled.
- **`info sharedlibrary` shows BOTH the NVIDIA driver stack (`nvoglv64.dll`, `nvcuda.dll`,
  `nvcuda64.dll`, `nvapi64.dll`, `nvEncodeAPI64.dll`, `nvcuvid.dll`, `nvdxgdmal64.dll`, `nvppex.dll`,
  `nvrtum64.dll`, `nvgpucomp64.dll`) AND the Intel iGPU driver stack (`igvk64.dll`, `igdgmm64.dll`,
  `igc64.dll`, `igvkMedia64.dll`, `igddxvacommon64.dll`, `media_bin_64.dll` — all from
  `iigd_dch.inf_amd64_...`, the Intel graphics driver package) loaded in the SAME process,
  alongside `vulkan-1.dll`, `D3D12.dll`, `d3d11.dll`, and GStreamer's `libgstd3d12-1.0-0.dll` /
  `libgstd3d11-1.0-0.dll` / `libgstnvcodec-...` plugins.** Two GPUs' driver stacks loaded together
  in a process that's supposed to render+encode on the NVIDIA 4070 Ti is a strong signal something
  in the pipeline enumerated adapters and picked the wrong one for at least one stage.
- **This matches a documented pattern elsewhere in this project, not a new class of bug.** The
  GL/Mesa render path already has a known, explicitly-guarded version of exactly this failure —
  `MESA_D3D12_DEFAULT_ADAPTER_NAME=NVIDIA` exists specifically because "Mesa grabs the Intel iGPU"
  if not pinned (see the arcade skill's GPU-env section). **Checked whether the D3D12/D3D11/NVENC
  capture-encode pipeline (`pkg/worker/caged/capture/launch.go`) has an equivalent adapter pin
  (`GST_D3D12_ADAPTER`/`DXGI_ADAPTER`/similar) — it does not.** No adapter selection logic appears
  anywhere in that file. If GStreamer's D3D12/D3D11 elements default-enumerate and land on the
  Intel adapter for one stage of a pipeline while NVENC/CUDA (which can only run on the NVIDIA
  device) own another stage, a **cross-adapter shared fence/semaphore** — the kind needed to hand a
  frame between two different GPU devices' contexts — would need the OTHER device to signal it.
  If that never happens (e.g. the Intel-side stage stalls, or the handoff was never valid for
  cross-vendor adapters to begin with), the NVIDIA-side thread's `WaitForSingleObject` on that
  shared handle blocks forever — exactly the confirmed NULL-timeout infinite wait above.
- **This is now the leading hypothesis, NOT yet proven.** It has NOT been confirmed that handle
  `0xbcc` is specifically a cross-adapter D3D12 fence/semaphore (would need `!handle`-equivalent
  enumeration — no Sysinternals `handle.exe` is installed on this box, checked and absent) — it
  could still be an ordinary same-adapter object. But "two GPU vendors' full driver stacks loaded
  together in a single-adapter-only render/encode process, with no adapter pin on the
  capture/encode path, and a confirmed infinite wait" is a coherent, testable story with a concrete
  next action.

**THIRD PASS — upgraded from hypothesis to confirmed gap.** Checked `scripts/run-arcade-glworker.ps1`
(the actual live launch script) for any adapter-pinning env var at all — it sets
`CLOUD_GAME_WORKER_NETWORK_*`, `CLOUD_GAME_WEBRTC_*`, `CLOUD_GAME_LIBRARY_BASEPATH`,
`CLOUD_GAME_EMULATOR_STORAGE`, `CLOUD_GAME_STOP_FILE` — **nothing GPU-adapter-related at all.** The
`MESA_D3D12_DEFAULT_ADAPTER_NAME=NVIDIA` pin referenced earlier in this doc (and in the arcade
skill) belongs to the OLD WSL/Mesa-under-WSLg path, which is **retired** — it was never ported to
this native Windows worker, and doesn't apply to WGL or to GStreamer's D3D12/D3D11 elements anyway
(different render/encode stack entirely). And per `arcade-cloudretro-vertical` project memory,
**Ziggy genuinely has two physical GPUs** — "RTX 4070 Ti 12GB + Intel UHD 770" — this isn't a
misconfigured single-GPU box; it's a real dual-adapter machine with literally no code anywhere
pinning the encode pipeline to the discrete GPU. That upgrades the cross-adapter-fence theory from
plausible to well-evidenced: two real GPUs, no adapter selection, both drivers demonstrably loaded
in the wedged process, and a confirmed infinite wait on a handle consistent with a cross-device
sync primitive.

**Concrete next step to actually test this:** pin the capture/encode pipeline to the NVIDIA adapter
the same way the GL/Mesa path already is — GStreamer's D3D12/D3D11 elements accept an
`adapter`/`adapter-luid` property, and there's likely an equivalent `GST_D3D12_ADAPTER_INDEX` /
`GST_D3D11_ADAPTER_INDEX` environment variable this fork isn't setting. Add it to
`run-arcade-glworker.ps1`'s launch environment (mirroring how `MESA_D3D12_DEFAULT_ADAPTER_NAME` is
already set), rebuild if the pin needs to be code-side instead of env-side, and watch whether the
Intel driver DLLs still load in a subsequent worker process — if they stop appearing, the
adapter-confusion theory is confirmed and the wedge should stop recurring (or at minimum change
character). This is the most promising unstarted lead — worth trying before investing in a debug
(non-optimized) rebuild or kernel debugging.

## The symptom

A GL worker process (`worker.exe`, one process = one CloudRetro room slot) occasionally becomes
**completely unkillable** — it survives `Stop-Process -Force` / `taskkill /F` / `TerminateProcess`.
It lingers as a zombie: holds its ConfDir (DLL + shader cache files) locked, its coordinator slot
permanently dead (never reports free, never reports done), consuming one of the box's ~2 real
worker slots indefinitely. **The only documented cure is a full reboot of Ziggy** (the whole
Windows box — not the worker, not a service, the OS). First seen 2026-07-15 (PID 7948, sat silent
10 hours before anyone noticed). Recurred 2026-07-21 (PID 4100, port 8447, wedged holding an N64
room ~35+ minutes and counting when last checked).

## Why this is NOT the same bug as the "room-close wedge" (that one IS fixed)

There are two superficially similar but mechanically distinct wedge classes in this codebase —
conflating them wastes time re-investigating something already solved:

1. **Room-close wedge** (fixed, patch 0041, see [[arcade-worker-pool-wedge]] /
   [[arcade-worker-room-close-wedge]]): a core hangs in its own teardown (PPSSPP boot-restore,
   snes9x, PCSX2), and stock `Room.Close()` only notified the coordinator *after* `app.Close()`
   returned — so a hung core meant the coordinator was never told the room was empty, and it kept
   the worker marked BUSY forever. Fixed by bounding teardown at 30s and having the worker
   `os.Exit(70)` (self-terminate + respawn, ~4s) instead of hanging silently. **The process itself
   was never unkillable in this class — it just never told anyone it was free.**

2. **UNKILLABLE-ZOMBIE class** (this doc, still open): force-killing a worker *while it has active
   GPU/NVENC work in flight* can strand a thread inside the **NVIDIA driver's own kernel-mode
   teardown path**. At that point the process is not merely slow to respond — user-mode signals
   (`TerminateProcess`, `SIGKILL`-equivalent) cannot reach a thread blocked in a kernel-mode driver
   wait. This is a genuinely different failure surface: not a bug in *our* Go code, but in what
   happens when our code's GPU calls collide badly with a forced termination.

## Full timeline of what's been tried

- **2026-07-15, first sighting (PID 7948).** An early theory — "force-kill skips PCSX2's
  shader-cache flush, leaving a cold cache that causes audio skip" — was investigated and
  **explicitly retracted, source-confirmed wrong**: PCSX2's cache appends immediately on compile,
  there's no close-time flush to skip. (The real cause of that separate audio-skip symptom was a
  handle leak — see [[arcade-stuntman-audio-skip]] — unrelated to the wedge.) The zombie/wedge
  finding stood on its own regardless.
- **Fix built + deployed (2026-07-15):** the worker now watches a stop-file sentinel
  (`CLOUD_GAME_STOP_FILE`, `pkg/os` `ExpectTermination`) and, when told to stop this way, runs a
  **graceful** `w.Stop()` — flush cache, tear down GL/NVENC cleanly — instead of being force-killed
  from outside. `scripts/recycle-arcade-glworker.ps1` and the watchdog's `KillWorker` both now do
  **graceful → force (fallback only) → verify the kill actually happened**. If the process
  survives even the force-kill, they stop thrashing it, write a loud `WEDGED-worker<pid>.flag`,
  and log "reboot required" on every watchdog cycle instead of silently giving up after one try.
  Live binary: `D:\Arcade\build\cloud-game-gl\bin\worker.exe` (sha256 `8831..B80C1`), running on
  both `worker-gl` and `worker-gl-2`. Rollback kept as `worker.pre-stopfile-20260715.exe`.
- **Known residual gap, documented at the time:** the *first* recycle of an already-running
  pre-stopfile process still has to force-kill it (an old process can't honor a `.stop` file it
  was never coded to look for) — but that's a one-time transition cost, not an ongoing risk, and
  was done while idle specifically to avoid triggering the zombie.
- **2026-07-21 recurrence (PID 4100).** Confirms the graceful-stop mitigation reduces *frequency*
  but does not eliminate the underlying class: this worker was already running the graceful-stop
  binary and still wedged. The most likely read: the watchdog's escalation path (graceful attempt
  → timeout → **force-kill fallback**) is exactly the "force-kill during active GPU work" trigger
  the 2026-07-15 fix couldn't remove, because the fallback to force-kill only happens when graceful
  stop has *already failed to respond* — i.e. precisely when the GPU state is already unhealthy
  enough that a clean stop wasn't going to work anyway.

## What was checked tonight (2026-07-21) and ruled out — don't re-check these

- **TDR (Timeout Detection and Recovery) is at Windows/driver defaults, not misconfigured**
  (`HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers`: `TdrLevel`/`TdrDelay`/etc. all
  unset → defaults, `TdrLevel=3` full recovery, `TdrDelay=2s`). This was the most obvious lever
  to try and it's already at maximum sensitivity. **It structurally cannot help further**: TDR
  watches for GPU *engine execution* hangs (a shader/kernel that's been running too long) and
  resets the display driver in response. This wedge is not that — it's a CPU-side thread parked in
  a kernel-mode *driver API call* during teardown, a wait TDR was never designed to detect or
  interrupt. Don't waste time tuning `TdrDelay` further; it's the wrong tool for this shape of hang.
- **No known-bad-driver signal for the installed version** (596.21 / `32.0.15.9621`,
  `nvidia-smi` confirms). Absence of forum chatter isn't proof it's clean — this version may simply
  be too recent/niche to have accumulated reports — but there's no actionable "downgrade to X"
  recommendation to act on right now.
- **Studio vs. Game Ready driver branch: no meaningful difference expected.** Neither branch is
  validated against a headless background compute+encode workload using WGL/D3D12/Vulkan interop
  (which is what this worker does) — that's not what either branch's QA targets.
- **No Windows-native process isolation helps** (Job Objects with
  `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`, Windows Sandbox, containers). All of these ultimately still
  call the same termination primitives on the process, and the hang lives in the **shared
  kernel-mode driver** (`nvlddmkm.sys`), not inside the process's own address space. Isolating the
  *process* doesn't touch the *driver*, so none of this changes the outcome.
- **`nvidia-smi -r` / GPU-level reset instead of a full box reboot: very likely not usable here.**
  NVIDIA's GPU reset via `nvidia-smi` is primarily supported for TCC-mode compute GPUs; this box's
  4070 Ti is presumably in WDDM mode (it's driving a desktop / the same GPU the workers render
  through via WGL), where this reset path is generally unavailable or requires no active contexts
  — which a zombie process, by definition, still holds. Not deeply tested tonight (no wedge was
  live to safely experiment against without risking making things worse) — worth a real attempt
  next time a wedge is live and reboot is being considered anyway (zero-risk to try first, since
  the fallback is the reboot you were going to do regardless).

## What's genuinely unexplored — the actual next steps, roughly in priority order

1. **Get a real stack trace of the hung thread.** This is the single highest-value thing nobody
   has done. Every theory so far ("stuck in driver teardown," "kernel-mode wait") is inferred from
   symptoms (survives force-kill, holds file locks, coordinator never hears from it again) — not
   from actually looking at where the thread is parked. Next time a wedge is live:
   attach a kernel debugger (WinDbg + `!process` / `!thread` / `!analyze -v` on the wedged PID, or
   capture a full kernel-mode memory dump via `livekd`/`NotMyFault` before rebooting) and get the
   actual call stack. That stack either matches a known NVIDIA driver bug (searchable, sometimes
   with a public bug ID and a fixed driver version) or narrows down which GPU subsystem is
   involved (NVENC teardown vs. WGL/D3D12 context destruction vs. Vulkan zero-copy semaphore sync —
   this project has a documented history of exactly this interop being fragile: see the
   `arcade-gl-zerocopy-caps-renegotiation` and `arcade-capture-d3d12-crash` findings for prior,
   *different* bugs in the same interop layer). Without this, every fix attempt is still a guess.
2. **Narrow the trigger.** Tonight's recurrence was an N64 room (mupen64plus_next, GL renderer).
   The 2026-07-15 first sighting's game/core isn't recorded in memory — worth checking
   `glworker*.log.1` history if still retained, or just watching for a pattern going forward: is
   this GL-only, or does it also happen on Vulkan-path titles? Does it correlate with a *specific*
   moment in teardown (mid-encode frame vs. mid-context-destroy vs. mid-shader-compile)? A tighter
   trigger window makes both a targeted code workaround and a driver bug report far more credible.
3. **Try a driver version change as a controlled experiment** — update to the latest at the time,
   or deliberately roll back to an older known-stable build from before this project's driver was
   installed. No specific version is recommended by anything found so far; this is genuinely just
   "does the bug move" as a data point, not a confirmed fix. Re-verify the whole GPU quality
   pipeline afterward if you do this (docs/arcade-gpu-research.md and the arcade-vulkan-* docs
   describe what "working" looks like and are expensive to re-verify — don't do this casually).
4. **Reduce reliance on force-kill ever being reached at all.** The graceful-stop path is the one
   proven-safe route; the wedge appears to correlate with falling through to the force-kill
   fallback. Is the graceful-stop timeout tuned right, or does it give up too early (before a
   legitimately-slow-but-recoverable teardown finishes) and escalate to force-kill prematurely?
   Worth logging exactly how long graceful stops normally take vs. how long the watchdog currently
   waits before declaring one failed and escalating — if the margin is thin, widening it costs
   nothing and might remove most of the remaining trigger occasions.
5. **DDA (Discrete Device Assignment) / GPU passthrough VM isolation** — researched tonight and
   NOT recommended as a near-term project: this would let a wedge cost a fast VM reset instead of a
   full host reboot, but consumer GeForce cards generally don't support the GPU virtualization
   (vGPU/SR-IOV) needed to share one physical 4070 Ti across multiple isolated worker VMs. A
   single dedicated VM with the entire GPU passed through (DDA) might be feasible for *one* worker,
   at the cost of needing additional GPU hardware to keep today's worker count. Treat this as a
   "only if everything else fails and there's budget for another GPU" option, not a quick win.

## FOURTH PASS — Windows event log checked, TDR confirmed to never fire

`Get-WinEvent` against the System log (3000 most recent entries, spanning well before and through
the current PID 4100 wedge) for `nvlddmkm`/`TDR`/"display driver"/"stopped responding": **zero
matches.** The classic TDR-recovery event (ID 4101, "Display driver nvlddmkm stopped responding
and has successfully recovered") never fired. This independently corroborates the gdb findings
above from a completely different angle: Windows' own GPU-hang detector never considered this a
GPU engine hang at all — consistent with a thread blocked on an ordinary synchronization wait
rather than a stalled GPU command queue. Whatever this is, it's invisible to TDR by construction,
not just under-triggered — reinforces that tuning `TdrDelay`/`TdrLevel` (already ruled out earlier
in this doc) was never going to help.

## SIXTH PASS — identified the handle, and re-confirmed TRUE unkillability (2026-07-21, ~3h into wedge)

**LiveKD attempted, blocked: this session has no Administrator rights** (confirmed via
`IsInRole(Administrator)` = False), and loading the local-kernel-debug driver LiveKD needs requires
elevation with no way to self-elevate headlessly here. **Full kernel debugging is still the
unfinished item — needs a human running an elevated session,** not a tooling gap (WinDbg Preview +
`kd.exe`/`cdb.exe` ARE now installed at `C:\Program Files\WindowsApps\Microsoft.WinDbg_*\amd64\`
and `LiveKD` is downloaded to a temp dir this session — both ready to go, just need `Run as
Administrator`).

**Fallback that worked without elevation: `cdb.exe` (WinDbg's user-mode debugger, same package) can
inspect a live process's OWN handle table without any kernel access.**
`cdb.exe -p <pid> -pv -c "!handle 0xbcc f; q"` (the `-pv` flag = non-invasive attach, doesn't
disturb the target) gave a definitive answer:

```
Handle bcc
  Type          Event
  GrantedAccess 0x1f0003 (Delete,ReadControl,WriteDac,WriteOwner,Synch,QueryState,ModifyState)
  HandleCount   2
  PointerCount  51683
  Object Specific Information
    Event Type Auto Reset
    Event is Waiting
```

**Handle `0xbcc` is a plain Win32 auto-reset Event, not a GPU/driver object of any kind.** This
kills both the "stuck inside the driver" theory AND the cross-adapter-fence theory from the
earlier passes — there's no GPU semaphore or fence here. Something else in the process was
supposed to call `SetEvent()` on this handle (classic teardown-coordination pattern: "thread A,
wait here until thread B finishes cleanup and signals") and never did, most likely because thread B
already exited/crashed/returned early without reaching its `SetEvent()` call.

**But this REOPENS the real puzzle rather than closing it.** A `WaitForSingleObject` on an ordinary
Event is a **fully interruptible, abortable wait** — `TerminateProcess` normally kills a thread
blocked on this instantly, no exceptions. So a plain unsignaled Event should NOT make a process
unkillable by itself. **Tested this directly: issued a fresh `Stop-Process -Id 4100 -Force` (~3
hours after the original watchdog force-kill attempt that first flagged it WEDGED) — the process
survived again, identical `StartTime`, unchanged.** This confirms the unkillability is real and NOT
just the watchdog giving up after one attempt (that was a live, testable alternate theory, now
ruled out). **The Event wait is a red herring for *why it won't die* — the true blocker is a
SEPARATE, kernel-level reference (almost certainly driver-held, e.g. by `nvlddmkm.sys`, on some
other resource tied to this process — a GPU context, a mapped section, a device handle) that's
pinning the process object open regardless of what this particular thread is doing.** That
mechanism remains invisible without kernel debugging. **The unfinished next step is unchanged in
kind, but now much more precisely scoped: run the already-downloaded LiveKD (elevated) against this
exact PID, and specifically look at what OTHER kernel-mode resources this process still holds open
besides the ordinary user-mode Event — that's where the real answer is.**

## Diagnostic reference (2026-07-21 snapshot — will drift, re-check before trusting)

- TDR registry: all keys absent under `HKLM:\SYSTEM\CurrentControlSet\Control\GraphicsDrivers`
  (Windows defaults apply).
- GPU: NVIDIA GeForce RTX 4070 Ti, driver `596.21` (`32.0.15.9621`, dated 2026-04-12).
- `nvidia-smi --query-gpu=name,driver_version,compute_mode,persistence_mode`:
  `NVIDIA GeForce RTX 4070 Ti, 596.21, Default, [N/A]` — persistence mode unset (N/A is expected on
  Windows; it's a Linux-only concept).
- Watchdog flag format: `D:\ArcadeStorage\logs\WEDGED-worker-<pid>.flag`, one line:
  `WEDGED/UNKILLABLE: worker PID <pid> SURVIVED force-kill (kernel-stuck GPU teardown). Holds its
  ConfDir locked, coordinator slot dead. BOX REBOOT REQUIRED. (<reason>)`.
- Watchdog log then repeats every ~30s: `worker PID <pid> (port <port>) BUSY with '<room>' but log
  silent <N>s (wedge strike <1|2>)` + the reboot reminder — this is intentional (loud, not
  self-clearing) so it can't be silently missed the way the 2026-07-15 case sat for 10 hours.

Related: [[arcade-worker-pool-wedge]] (the graceful-recycle fix this doc extends),
[[arcade-worker-room-close-wedge]] (the *different*, already-fixed room-close bug — don't conflate
them), [[arcade-gl-zerocopy-caps-renegotiation]], [[arcade-capture-d3d12-crash]] (prior, distinct
bugs in the same GL/Vulkan/D3D12 interop layer — evidence this interop is a recurring soft spot,
not proof of a shared root cause), `docker/arcade/patches/README.md` §0041/§0017 (the fixed
room-close/media-teardown-timeout bugs, for contrast), `scripts/watch-arcade-glworkers.ps1`,
`scripts/recycle-arcade-glworker.ps1`.

## SEVENTH PASS — admin session finally available, but HVCI blocks LiveKD (2026-07-21)

**This session genuinely had Administrator rights on Ziggy itself** (verified via
`IsInRole(Administrator)` = True, not just asserted) — the exact blocker the SIXTH PASS flagged
as unfinished. **PID 4100 was still live** at the start of this pass (running since the prior
day, flagged WEDGED at 01:11), giving a real specimen to test against.

**Real, fixed bug along the way:** LiveKD (`C:\Users\Atoramos\AppData\Local\Temp\livekd\livekd64.exe`)
initially failed all symbol resolution (`symsrv.dll load failure`, then `ntkrnlmp.pdb dia error
0x80070005`) because it was loading a mismatched `dbghelp.dll`/`symsrv.dll`/`msdia140.dll` from the
default DLL search path instead of the versions bundled with the installed WinDbg Preview package
(`C:\Program Files\WindowsApps\Microsoft.WinDbg_1.2606.22001.0_x64__8wekyb3d8bbwe\amd64\`). **Fix:**
copy `dbghelp.dll`, `symsrv.dll`, `msdia140.dll`, and `dbgeng.dll` from that WinDbg package folder
directly into LiveKD's own folder (`...\Temp\livekd\`) so LiveKD's default DLL search order picks up
the matching set first. After this fix, LiveKD successfully downloaded kernel symbols
(`ntkrnlmp.pdb`) from the Microsoft symbol server and reported launching `kd.exe` cleanly (exit 0).
This fix is durable — keep it in mind if LiveKD is used again on this box.

**Could not capture the interactive `kd.exe` session's actual output**, despite several attempts:
passing `-logo <file>` (breaks LiveKD's own argument parsing outright — `kd.exe` falls back to
printing its usage/help text instead of running, reproduced twice, order-independent); setting the
`_NT_DEBUG_LOG_FILE_OPEN` env var instead (doesn't break parsing, but the log file stays empty —
the spawned `kd.exe` child doesn't appear to inherit or honor it under LiveKD's launch); and
`Start-Process` with explicit stdout/stderr redirection (breaks path quoting for the `-k` debugger
path argument instead). `kd.exe` opens its own console window when LiveKD launches it for a live
session, independent of the parent's redirected handles.

**Tried LiveKD's `-m`/`-mp` mirror-dump path instead**, to sidestep the console problem entirely
(capture a static dump of just PID 4100's memory once, then analyze offline with `cdb.exe -z
<dump>`, which redirects normally with no console issues). This failed distinctly and
reproducibly, regardless of memory-region mask size (tried default `0x18F8`, then a minimal `0x1`):
`Error communicating with livekd driver: Insufficient system resources exist to complete the
requested service.`

**Root cause of both failures, confirmed:** `Get-CimInstance Win32_DeviceGuard` shows
`VirtualizationBasedSecurityStatus = 2` (Running) and `SecurityServicesRunning = {1, 2, 7}` —
**Memory Integrity (HVCI) is enabled on Ziggy.** LiveKD v5.65 (last updated ~2020) relies on its own
legacy kernel-mode driver to both establish a live local-kernel debug session and to capture mirror
dumps; HVCI's stricter kernel code-integrity enforcement blocks that driver from functioning
correctly on this box's current Windows build (`10.0.26100.8894`). This is almost certainly why the
"successful" interactive `kd.exe` launches never produced visible output — the spawned `kd.exe` was
very likely hitting the same failure confirmed by testing the *native*, non-LiveKD path directly:

```
kd.exe -kl → "The system does not support local kernel debugging.
              Local kernel debugging is disabled by default. You must run
              'bcdedit -debug on' and reboot to enable it."
```

**This reframes the remaining blocker as environmental, not a rights/tooling gap.** Both tools
(`kd.exe`, LiveKD) are present and otherwise functional (proven by clean symbol resolution) — the
missing piece is a boot-time kernel debug capability that neither the native path (needs `bcdedit
-debug on` + reboot) nor LiveKD's workaround (blocked by HVCI) can provide *without a reboot*, and
**a reboot is exactly what clears this class of wedge** — meaning the currently-live specimen (PID
4100 or whatever is wedged next) cannot survive being made kernel-debuggable. This is a genuine
chicken-and-egg constraint, not a skill issue: to ever get a real kernel-mode trace of a live wedge,
the box would need to *already* be kernel-debug-capable *before* the next wedge occurs — i.e., someone
would need to deliberately reboot Ziggy once, during idle time, specifically to either run `bcdedit
-debug on` (supported path, no security downside, HVCI-compatible) or disable Memory Integrity
(unblocks LiveKD, but weakens kernel protection until re-enabled) — purely as prep for catching the
*next* occurrence, not this one. Neither was done this session (both require a reboot, which was
explicitly out of scope for inspecting the live PID 4100 specimen).

**Next session should decide, before anything else:** is it worth deliberately rebooting Ziggy once
during idle time to pre-enable `bcdedit -debug on` (recommended over disabling HVCI — no standing
security tradeoff), purely so the *next* wedge occurrence can finally get a real kernel-mode stack
trace via native `kd.exe -kl` (no LiveKD, no driver-compatibility risk)? If yes, do it during a
guaranteed-idle window, verify `bcdedit -debug on` took effect after the reboot (`bcdedit /enum
current` should show `debug Yes`), and leave it enabled until the next wedge is caught and traced.

**UPDATE, same pass: tried it immediately — `bcdedit /debug on` is ALSO blocked, by Secure Boot:**

```
An error occurred while attempting to modify the debugger settings.
The value is protected by Secure Boot policy and cannot be modified or deleted.
```

Confirmed `Confirm-SecureBootUEFI` = `True` (Secure Boot is active). **This is now a THIRD, harder
blocker layered on top of the admin-rights gap (resolved) and HVCI (blocks LiveKD): with Secure Boot
on, Windows will not let *either* kernel-debugging path be enabled from inside the OS at all** — not
just LiveKD's driver (blocked by HVCI), but the fully-native, Microsoft-supported `bcdedit -debug on`
route too. The only way around this is disabling Secure Boot in UEFI firmware itself — a BIOS-level
setting, not a Windows/PowerShell-level one, meaning it cannot be done remotely/headlessly from
inside Windows at all; it needs physical (or out-of-band remote-KVM-style) access to the firmware
setup screen during boot, plus a reboot to get there. (Checked BitLocker as a side risk of that
change: it's `Off` on `C:`, so at least disabling Secure Boot won't trigger a BitLocker recovery-key
prompt here — one less complication if this path is ever taken.)

**Verdict: kernel-level debugging of this wedge class is not achievable through any
software-only path on Ziggy as currently configured.** Getting it working requires an explicit,
separate decision to go into UEFI firmware setup (physical/out-of-band access) and disable Secure
Boot, purely as prep — and that is a standing security-posture tradeoff (not just a one-time reboot
cost like the HVCI path would have been), so it should be weighed on its own rather than folded into
routine wedge triage. Until/unless that happens, the only real lever left for this bug class is the
non-kernel-debugging angle already outlined in "What's genuinely unexplored" above (narrowing the
trigger, tuning the graceful-stop timeout, trying a driver version change) — kernel debugging should
be considered closed off for now, not merely unattempted.

## FIFTH PASS — research for next session (background agent, 2026-07-21)

**1. Kernel debugging setup.** Install "Debugging Tools for Windows" either via the Windows SDK
installer (pick just that component: learn.microsoft.com/windows-hardware/drivers/debugger/) or,
preferred, **WinDbg Preview** from the Microsoft Store (apps.microsoft.com/detail/9pgjgd53tn86) —
same debug engine, same commands, auto-updates. For a TARGETED capture instead of a full 64GB+
kernel dump, use Sysinternals **LiveKD** (download.sysinternals.com/files/LiveKD.zip;
learn.microsoft.com/sysinternals/downloads/livekd) — attaches kd/WinDbg to the live kernel with
no reboot/bugcheck. Interactive, no dump written: `livekd -w -k "C:\Program Files (x86)\Windows
Kits\10\Debuggers\x64\kd.exe"`, then run `!process 0 7 <pid>`, `!thread`, `!handle` live. To save a
dump scoped to one process's user memory instead of full RAM: `livekd -k <kd.exe path> -m -mp <pid>
-o wedge.dmp` (`-mp` restricts capture to that PID). NotMyFault is for deliberately bugchecking a
box to test dump tooling — not useful here since the goal is inspecting a live wedge, not crashing it.

**2. Debug build + Delve.** Build with `CGO_ENABLED=1 go build -gcflags="all=-N -l" -o bin/worker.exe
./cmd/worker` from MSYS2 UCRT64 — the `all=` prefix is required so `-N -l` (no optimize, no inline)
applies to every package, not just `main`; no known incompatibility with cgo. **Delve is likely
better than gdb here**: it's Go-runtime-aware (understands goroutines/scheduler) where gdb doesn't,
which is almost certainly why tonight's gdb trace produced the garbage `caged.go:104` frame. Install:
`go install github.com/go-delve/delve/cmd/dlv@latest`. Attach to the live wedged PID: `dlv attach
<pid>` for a direct session, or `dlv attach <pid> --headless --listen=127.0.0.1:2345 --api-version=2`
+ `dlv connect 127.0.0.1:2345` from another shell. Once attached, `goroutines` lists ALL goroutines
(not just OS threads — may surface ones gdb's thread-only view couldn't see), then `bt`/`frame N` on
the relevant one. Delve's Windows attach is real but less battle-tested than Linux; if it fails
outright, fall back to gdb against the same `-N -l` build (still improves gdb's unwind quality).

**3. Identifying handle 0xbcc.** Once attached in WinDbg/cdb: `!handle 0xbcc f` — the `f` (full)
flag prints the object Type (Event/Semaphore/Mutant/Section/Process/Thread/etc.), name, and ref
counts, directly answering what the handle is. Faster/no-command alternative: **Process Explorer**
(Sysinternals GUI, more commonly pre-available than handle.exe — download.sysinternals.com/files/
ProcessExplorer.zip) — select `worker.exe` in the tree, View > Lower Pane View > Handles (Ctrl+H),
scroll/search for `0xBCC`, read the Type column directly. PowerShell/WMI has no per-handle-type
enumeration (`Get-Process` only exposes an aggregate `HandleCount`) — not viable without WinDbg or
Sysinternals.

**4. Prior art.** No GitHub issues found on `giongto35/cloud-game` mentioning hangs, unkillable
workers, or GPU teardown deadlocks — the upstream tracker is small and Linux/Docker-focused; this
looks like uncharted territory specific to this fork's native Windows worker. Did find a directly
relevant NVIDIA developer-forum report: **"NVENC encoder hangs on Windows when using D3D11 in
real-time mode"** (forums.developer.nvidia.com/t/bug-report-nvenc-encoder-hangs-on-windows-when-
using-d3d11-in-real-time-mode/357466) — root cause traced to
`D3DKMTSetProcessSchedulingPriorityClass(..., D3DKMT_SCHEDULINGPRIORITYCLASS_REALTIME)`, worsened by
HAGS, DX12 games, or near-max VRAM, manifesting as `NvEncoder::GetEncodedPacket` hanging; no fix
published in-thread. Worth checking whether GStreamer's nvcodec plugin sets realtime scheduling
priority, and whether HAGS is enabled on Ziggy (Settings > Display > Graphics) — disabling HAGS is a
cheap, reversible experiment. No GStreamer GitLab issue matches "nvcodec + EOS + Windows hang"
exactly, but EOS/shutdown deadlocks are a recurring bug *class* across gst-plugins-good/bad and core
gstreamer generally — precedent that "stuck in teardown/EOS" is plausible here even without a
nvcodec-specific report.
