# Worker Vulkan capture — M1 implementation spec (for the implementing agent)

**Mission**: make the Windows CloudRetro worker host libretro cores that render via
`RETRO_HW_CONTEXT_VULKAN`, and deliver their frames through the EXISTING CPU video path. Proof
target: **LRPS2 `pcsx2_renderer: "paraLLEl-GS"` streams Stuntman in a live room.**

**Scope**: Milestone 1 ONLY (readback path). M2 (Vulkan→GL zero-copy) and M3 (fleet rollout) are
designed in `docs/arcade-vulkan-capture-plan.md` — read it first for the why; this doc is the how.

**Review gate**: implementation will be reviewed by the session that wrote this spec before any
deploy is considered done. Commit early and often on a branch (see Fork discipline); do not deploy
to the live pool without the runbook below.

---

## 0. Environment facts (do not rediscover these)

- Box: "Ziggy", Windows 11, i7-13700K (8 P-cores + 8 E-cores), RTX 4070 Ti. The worker MUST end up
  High priority + affinity 0x5555 — the runner does this automatically
  (`scripts/run-arcade-glworker.ps1`, watcher job); do not remove that.
- Worker source: `D:\Arcade\build\cloud-game-gl`, branch **`movietheater-fork`** (private
  `ecarpouzis/cloud-game-gl`). ⚠ The checkout may hold uncommitted `nanoarch.*` WIP (a dormant
  64 MiB same_thread stack + deinitVideo reorder) and a committed-but-possibly-undeployed ICE srflx
  change (`7af7f03`, another session's work — see `docs/arcade-ice-priority-plan.md`). **Branch from
  current `movietheater-fork` HEAD; do not revert either; coordinate deploys** (one worker.exe binary
  carries everything in the tree).
- Worker build (MSYS2 UCRT64, native Windows Go):
  `PATH=D:\msys64\ucrt64\bin` first, `CGO_ENABLED=1`, then in the repo root:
  `go build -o bin/worker.vk.exe ./cmd/worker`. cgo deps resolve via ucrt64 pkg-config
  (gstreamer-1.0 etc.). Never MinGW-from-elsewhere; never touch the Makefile flags.
- Deploy: the live binary is `D:\Arcade\build\cloud-game-gl\bin\worker.exe` (locked while running).
  Rename-swap (`Move-Item worker.exe worker.pre-vk.exe; Copy-Item worker.vk.exe worker.exe`), then
  recycle each worker with `scripts/recycle-arcade-glworker.ps1 -WorkerId N`. ⚠ NEVER
  `Stop-Process worker -Force` (zombie risk, documented in the script header). ⚠ Check
  `curl -s localhost:8000/status` first — the guard refuses live rooms; respect it (room ids are
  deterministic per user+game, so the same room id can appear on another worker — the guard may
  refuse for a DIFFERENT worker's live room; verify by log mtime before overriding with -Force).
- Test harness: `.claude/skills/test-roms/ps2-bench.mjs` (Playwright against prod
  `https://theater.carpouzis.com`, logs in as ArcadePlayer2/user 33). ⚠ Never run it while a real
  user has a room open (the local browser's decode contends on the box —
  `arcade-benchmark-isolation` memory). To test IN-LEVEL content: seed
  `D:\ArcadeStorage\saves\sv-33-60439-0-ps2___Stuntman (USA).dat` from the `sv-1-...` twin
  (back up first, restore after), then `--resume`.
- Per-game config: `D:\ArcadeStorage\worker-gl{,-2}\game-overrides.json` is **GENERATED** from the
  `ArcadeGameProfile` DB table via `dotnet run --project src/MovieTheater/MovieTheater.csproj --
  arcade-gameconfig-export -o <path>`. Hand-edits are allowed ONLY as temporary test arms and must
  be regenerated afterward. Stuntman's shipped profile = `Software (HW)` renderer (row Id 7) — for
  Vulkan testing, hand-edit to `"pcsx2_renderer": "paraLLEl-GS"` on the worker(s) under test,
  regenerate when done.
- Worker logs: `D:\ArcadeStorage\logs\glworker.log` / `glworker-2.log` (ANSI codes present — strip
  before parsing). Instrumentation: the deployed LRPS2 core logs `pace-diag` (ticks/audio) and
  `[perf-attr]` (per-window attribution incl. `vidpush`) — these work under ANY renderer and are
  your primary evidence.

## 1. Current architecture (what you are extending)

- `pkg/worker/caged/libretro/nanoarch/` — the libretro frontend. `nanoarch.go` handles env calls;
  `nanoarch.c` the C bridge; core lifecycle runs on a dedicated pthread (`same_thread`) for LibCo
  cores (LRPS2 IS LibCo: `usesLibCo: true`).
- GL hw-render today: `SET_HW_RENDER` (nanoarch.go ~line 1044) accepts ONLY
  `RETRO_HW_CONTEXT_OPENGL{,_CORE}` and explicitly warns+rejects Vulkan (patch 0006);
  `GET_PREFERRED_HW_RENDER` (~1033) answers OPENGL when `Nan0.Video.gl.enabled`.
- Frame ingestion: the core calls `video_cb` → `core_video_refresh_cgo` (nanoarch.c:204) →
  `coreVideoRefresh(data, width, height, pitch)` (nanoarch.go:799). Two cases today:
  `data == RETRO_HW_FRAME_BUFFER_VALID` (GL: frame is in the FBO; GL zero-copy or readback), else
  `data` = CPU pixel pointer (software cores) → flows to the media pipeline's CPU path
  (`pushVideoBuf`, `pkg/worker/media/gstreamer.go`), which ALREADY handles mid-run frame-size
  changes by renegotiating appsrc caps.
- Pixel formats: cores declare via `RETRO_ENVIRONMENT_SET_PIXEL_FORMAT`; the CPU path supports
  XRGB8888 (and RGB565). Your staged Vulkan readback must emit what the pipeline expects for the
  core's declared format (LRPS2 declares XRGB8888).

## 2. The libretro Vulkan contract (M1 must implement ALL of this)

**Vendor the header**: copy `libretro_vulkan.h` from the LRPS2 tree
(`D:\Arcade\build\lrps2\libretro\libretro-common\include\libretro_vulkan.h`, 494 lines) into
`pkg/worker/caged/libretro/nanoarch/`. This exact copy is what the core was compiled against —
struct layout agreement is guaranteed by using it verbatim. Constants there:
`RETRO_HW_RENDER_INTERFACE_VULKAN_VERSION 5`, negotiation `..._VERSION 2`.
Also vendor Vulkan API headers (copy `vulkan_core.h` etc. from LRPS2's
`3rdparty/vulkan-headers/include`) — do NOT link a Vulkan SDK: load `vulkan-1.dll` at runtime
(`LoadLibraryA`) and resolve everything from `vkGetInstanceProcAddr`.

**Environment calls to add/extend in nanoarch.go** (gate all of it behind a new per-core config
flag — see §4):

1. `RETRO_ENVIRONMENT_SET_HW_RENDER_CONTEXT_NEGOTIATION_INTERFACE` (env 47): currently unhandled.
   Store the pointer; validate `interface_type == RETRO_HW_RENDER_CONTEXT_NEGOTIATION_INTERFACE_VULKAN`.
   The struct (see vendored header) has: `get_application_info`, `create_device`, `destroy_device`,
   and — **v2 additions used by paraLLEl-GS** (LRPS2 main.cpp:2072) — `create_instance`,
   `create_device2`. Respect `interface_version`: v1 cores leave the v2 members null.
2. `GET_PREFERRED_HW_RENDER`: answer `RETRO_HW_CONTEXT_VULKAN` (=6) when the core's config says
   vulkan.
3. `SET_HW_RENDER`: accept `context_type == RETRO_HW_CONTEXT_VULKAN`. Do NOT install
   `get_current_framebuffer`/`get_proc_address` (GL-only concepts). Save the callback struct —
   `context_reset`/`context_destroy` still fire (see lifecycle below).
4. `RETRO_ENVIRONMENT_GET_HW_RENDER_INTERFACE` (env 41): return the
   `retro_hw_render_interface_vulkan` you construct (LRPS2 fetches it at context_reset time and
   checks `interface_version == 5`, main.cpp:1672).

**Device bring-up order** (mirror RetroArch `vulkan_common.c` — it is the normative reference for
every semantic below; when in doubt, do what it does):

1. Load vulkan-1.dll. If the negotiation iface has `get_application_info`, use it for
   `VkApplicationInfo` (else a default; apiVersion at least what the core asks — use 1.3 if free).
2. Instance: if negotiation v2 `create_instance` is set, call IT with a wrapper that does our
   `vkCreateInstance` (the `retro_vulkan_create_instance_wrapper_t` pattern in the header) — the
   core injects its required instance extensions. Else create ourselves (no surface/swapchain
   extensions needed — we never present).
3. Physical device: pick the discrete NVIDIA GPU (only one on the box).
4. Device: prefer v2 `create_device2`, else v1 `create_device`, else create ourselves with one
   graphics+compute queue. The core's create_device returns a `retro_vulkan_context` (device,
   queues, queue family indices) — SAVE IT ALL; those are the queues the core will submit on.
5. Build the `retro_hw_render_interface_vulkan` (all fields; `interface_version = 5`;
   `handle` = your context object) and keep it alive for the core's whole life.

**Interface callbacks — required semantics** (implement in C, `nanoarch_vulkan.c`):

- `set_image(handle, const struct retro_vulkan_image *image, num_semaphores, const VkSemaphore*,
  src_queue_family)`: the core hands the CURRENT frame as `image->image_view` /
  `image->image_layout` / `image->create_info` (contains the VkImage, format, extent). Store the
  pointer contents (copy the struct — its lifetime is only until the next set_image). If
  `num_semaphores > 0`, those semaphores must be waited on by YOUR first use of the image this
  frame. `src_queue_family` may differ from yours → your copy must handle a queue-family
  acquire if the core did a release (paraLLEl-GS passes 0/nullptr/queue_index — same family in
  practice for M1; assert and log if not).
- `get_sync_index(handle)` → current sync index (0..N-1); `get_sync_index_mask(handle)` → bitmask
  of valid indices (use N=2, mask 0b11 for M1). The protocol: the frontend advances the sync index
  once per displayed frame and guarantees that GPU work referencing sync index i has completed
  before index i comes around again — RetroArch does this with per-index fences around its
  consumption of the core's image. Your per-frame copy submit gets a fence; `wait_sync_index`
  semantics = wait those fences.
- `wait_sync_index(handle)`: block until the current sync index's prior work is done (fence wait).
- `lock_queue`/`unlock_queue(handle)`: a mutex around the shared VkQueue. The core calls these
  around ITS submits (GSRendererPGS.cpp:223/227); YOU must take the same lock around YOUR
  vkQueueSubmit/vkQueueWaitIdle.
- `set_command_buffers(handle, num, const VkCommandBuffer*)`: alternative frame-handover mode —
  paraLLEl-GS does NOT use it; implement as a stub that logs-once and no-ops (assert-fail loudly in
  DEBUG so a future core that needs it is visible).
- `set_signal_semaphore(handle, VkSemaphore)`: semaphore the frontend should signal when done with
  the frame; store it and signal on your copy-complete submit if set (paraLLEl-GS may not use it;
  handle null).

**Core lifecycle integration**:

- LRPS2 is LibCo: `context_reset` must run on the core's `same_thread` (existing
  `C.same_thread(...)` machinery — mirror the GL branch at nanoarch.go ~line 426).
- The core queries `GET_HW_RENDER_INTERFACE` during/after `context_reset` — the interface must be
  ready BEFORE context_reset fires.
- `context_destroy` + device teardown: destroy YOUR pool/staging/fences first, then let the core's
  context_destroy run, then vkDestroyDevice/Instance. ⚠ ps2 config currently carries
  `skip_hw_context_destroy` (a GL-deadlock workaround — see arcade-stuntman-audio-skip memory).
  For the Vulkan test arm, run WITHOUT that hack (hand-edit a test config copy) and verify clean
  close; if teardown wedges, the room-close watchdog exits the worker (exit 70) — that is a finding
  to report, not to paper over.

## 3. M1 frame path (the readback)

Per `video_cb` with `data == RETRO_HW_FRAME_BUFFER_VALID` on a Vulkan core:

1. `wait_sync_index` housekeeping; take `lock_queue`.
2. Record+submit a command buffer: layout-transition the core's image
   (`image->image_layout` → TRANSFER_SRC), `vkCmdCopyImage` (or `vkCmdBlitImage` if format
   conversion to `VK_FORMAT_B8G8R8A8_UNORM` is needed — paraLLEl-GS images may be RGBA/BGRA UNORM;
   handle both) into a host-visible staging buffer via `vkCmdCopyImageToBuffer`, transition back,
   fence.
3. Release the queue lock; wait the fence (M1 is allowed this synchronous cost — ~1-2 ms at
   1280×896; do NOT try to be clever yet).
4. `vkMapMemory` staging (persistently mapped at init), hand the pixel pointer + pitch to the
   normal software-frame flow — i.e. call the same path `coreVideoRefresh` takes for CPU frames
   (XRGB8888). The existing CPU pipeline does caps renegotiation on size change already.
5. Advance the sync index.

Pool/staging sizing: allocate from `image->create_info.extent` on FIRST frame; **reallocate when
extent changes mid-run** (PS2 games flip height 448↔447 — this is a day-one requirement, tested by
Stuntman save-resume).

## 4. Config plumbing

- `pkg/config` Emulator core struct: add `HwContext string` (values "", "gl", "vulkan") or a bool
  `UsesVulkan` — follow the existing style (`IsGlAllowed`, `UsesLibCo`) and thread it through
  `pkg/worker/caged/libretro` the same way `isGlAllowed` flows.
- `docker/arcade/config.worker-gl.yaml`: for the TEST, a ps2 variant with
  `hwContext: vulkan` (and no `skip_hw_context_destroy` per §2). Do not change the shipped ps2
  entry in the repo until review.
- The GL zero-copy (`emulator.glZeroCopy`) must NOT arm for Vulkan cores (it wraps a WGL context
  that won't exist). Guard `InitGLZeroCopy`/`GLContextReady` behind the gl path.

## 5. Acceptance criteria (M1 done =)

1. A Stuntman room with `pcsx2_renderer: "paraLLEl-GS"` reaches `Playing`, streams ≥30 s, and the
   bench screenshots show the actual game (compare against the SW-renderer look; the intro FMV is
   b/w vintage footage — do not mistake it for gameplay; see the save-seeding recipe in §0 to test
   in-level).
2. Worker log shows the negotiation + interface handshake lines you add (log: negotiation version,
   device name, queue family) and NO `rejected non-GL hw render context` warning.
3. pace-diag holds ~60 ticks in menus; `[perf-attr]` lines flow. (Perf beyond "not broken" is NOT
   an M1 gate — the readback is a known temporary cost.)
4. Frame-size change survives: resume Eric-style save (448↔447 flip) without pipeline error —
   grep the log for `Internal data stream error` (must be absent) and confirm video continues
   (client `framesDecoded` advancing, or bench luma changing).
5. Room close is clean 5× consecutively: no exit-70 wedge, no 0xC0000005, workers respawn and next
   room works. Then a mixed sequence: vulkan room → GL room (another ps2 game on OpenGL profile) →
   vulkan room, all on one worker process.
6. `git` hygiene: branch off `movietheater-fork`, commits with clear messages,
   `scripts/export-arcade-fork.ps1` run at the end (it compile-proves `fork.patch` on pristine
   upstream — the fork discipline; see arcade-cloudretro-fork memory).

## 6. Footguns (each one cost this project real time — do not re-pay)

- **Struct-layout drift**: use LRPS2's exact `libretro_vulkan.h`. Do not hand-declare structs.
- **The core checks `interface_version != 5` and refuses** (LRPS2 main.cpp:1672). Set it.
- **paraLLEl-GS needs negotiation v2** (`create_instance`/`create_device2`, main.cpp:2072). A
  v1-only implementation will "work" for GSdx-Vulkan and silently break the actual target.
- **LibCo thread affinity**: context_reset and every interface callback the core invokes happen on
  the core's cothread — your C code must be callable there (no Go callbacks in the hot path; keep
  the interface pure C, Go only for setup/config, same split nanoarch already uses).
- **Queue locking**: every one of YOUR submits under `lock_queue`. A single unlocked
  vkQueueWaitIdle races the core's submit thread and corrupts rarely — i.e. undebuggably.
- **Teardown order** (the deinitVideo lesson, glzerocopy.go:136): your objects die before the
  core's device. And test close 5×, not once — the wedge classes here are intermittent.
- **Do not judge smoothness in headless Chrome** and do not touch the box during a measured arm
  (arcade-benchmark-isolation memory). Functional gates only from the bench; perf verdicts come
  later from the instrumented core + a real client.
- **game-overrides.json is generated** — regenerate from the DB after every hand-edit test arm
  (`arcade-gameconfig-export`, §0), or the next export silently reverts your test config anyway.
- **The pool has THREE workers** (8446/8447/8448 incl. capture) and the coordinator's /status port
  numbers (900x) do NOT stably map to workers — identify workers by UDP mux port or ConfDir.
- vkDeviceWaitIdle before ANY pool realloc on size change (in-flight copies reference old images).

## 7. Deliverables

1. Branch `vulkan-capture-m1` off `movietheater-fork` with the implementation.
2. `fork.patch` regenerated + compile-proven (export script).
3. A short RESULTS.md (or PR description): what was implemented, acceptance evidence (log
   excerpts, screenshots), deviations from this spec with reasons, and open questions for review.
4. NO deploy to the live pool beyond your own gated test windows; final deploy happens after
   review.

## 8. Explicitly out of scope for M1

- Vulkan→GL external-memory zero-copy (M2 — do not start; the M1 staging design should merely not
  preclude it: keep the pool-image abstraction separable from the staging readback).
- Other cores (mupen/dolphin/flycast/ppsspp) — M3.
- Any encoder/GStreamer change. If you think you need one, stop and flag it for review instead.
