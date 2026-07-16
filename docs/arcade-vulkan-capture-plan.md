# Worker Vulkan capture support — design (2026-07-15)

Let the Windows GL worker host **Vulkan-rendering libretro cores** and feed their frames into the
existing GStreamer→WebRTC encode path. Written after the Stuntman investigation; not yet scheduled.

## Why (three independent justifications)

1. **paraLLEl-GS for PS2.** LRPS2 ships Themaister's Vulkan compute rasterizer (already in our DLL:
   `pcsx2_renderer: "paraLLEl-GS"`, plus `pgs_ssaa` / `pgs_high_res_scanout` / `pgs_deblur` options).
   It is SW-renderer-accurate *with* internal upscaling — i.e. it recovers the 2× sharpness Stuntman
   lost by moving to the SW rasterizer, while keeping the accuracy that fixed its audio. It also has
   a completely different cost model from the GL HW renderer whose per-write texture-cache
   invalidation + `GSState::Transfer` packet storm was measured as 99.3% of the level-start burst
   (arcade-stuntman-audio-skip memory) — GPU-compute rasterization plausibly absorbs streaming-heavy
   games without GS-thread saturation.
2. **Escape the GL single-thread tax generally.** Everything the GS/GL path does — draws, uploads,
   invalidation — serializes on one thread in our topology. Vulkan cores schedule their own queues.
3. **Unlock the Vulkan back-ends across the fleet**: mupen + **paraLLEl-RDP** (the N64 accuracy
   upgrade, same author/tech), Dolphin Vulkan (sidesteps the GL shared-context/async-shader saga),
   flycast and PPSSPP Vulkan. One frontend feature, five cores' options.

## Current state (grounded)

- `nanoarch.go` `SET_HW_RENDER` **rejects non-GL** context types (patch 0006, ~line 1044:
  "accepting D3D/Vulkan here strands the core") and `GET_PREFERRED_HW_RENDER` answers `OPENGL`.
- The GL capture path: core renders into our FBO → `video_cb` → `graphics.CopyFrameToPool`
  (GPU→GPU copy + glFinish) → `zc_push` wraps the pool texture as GstGLMemory →
  `appsrc ! glcolorscale ! nvav1enc` (pkg/worker/media/glzerocopy.go). Caps renegotiate on core
  frame-size changes (the a738868 fix; PS2 flips 448↔447 routinely).
- LRPS2's Vulkan side is **standard RetroArch protocol** (libretro/main.cpp:2060-2100):
  - GSdx-Vulkan registers the v1 negotiation interface (`get_application_info`, `create_device`).
  - **paraLLEl-GS registers v2** (`create_instance`, `create_device2` as well) — the worker must
    implement the negotiation interface *including the v2 entry points*.
  - Frames: `set_image(handle, &retro_vulkan_image, 0, nullptr, queue_index)`
    (GSRendererPGS.cpp:591) — the core hands a VkImageView per frame; the frontend samples/copies.
    Queue access from our side must respect `lock_queue`/`unlock_queue` (PGS uses them: :223/:227).

## The worker-side contract to implement (the "frontend half")

A new `nanoarch_vulkan` module (C, beside nanoarch.c; volk or hand-loaded entry points), modeled on
RetroArch's `vulkan_common.c` minus the swapchain:

1. **Negotiation**: honor `SET_HW_RENDER_CONTEXT_NEGOTIATION_INTERFACE`; on `SET_HW_RENDER` with
   `RETRO_HW_CONTEXT_VULKAN`, create VkInstance (core's `create_instance` if v2, else ours) and
   VkDevice via the core's `create_device`/`create_device2` (the core states extensions/features —
   paraLLEl-GS wants modern compute features; let IT pick).
2. **`retro_hw_render_interface_vulkan`** handed to the core: instance/gpu/device/queue/queue-family
   + callbacks — `set_image`, `get_sync_index`, `wait_sync_index`, `lock_queue`/`unlock_queue`,
   `set_command_buffers`, `set_signal_semaphore`. Sync-index protocol: we declare N frames in
   flight; RetroArch's implementation is the normative reference.
3. **Frame consumption**: on `video_cb` after the core `set_image`s, copy the core's VkImageView's
   image into OUR pool (vkCmdCopyImage/blit with layout transitions, submitted under lock_queue,
   fenced). The pool is the Vulkan twin of `graphics.zerocopy`.
4. **Teardown ordering** (the deinitVideo lesson): destroy encoder-side interop objects BEFORE the
   core's VkDevice. Vulkan has no GL-style thread-current dance (simpler), and no
   `skip_hw_context_destroy` class of problem is expected — but keep the room-close watchdog.

## Encode-path options (the "capture half")

| | Path | Verdict |
|---|---|---|
| **C (v1 milestone)** | Pool image → host-visible staging → CPU pointer → existing `pushVideoBuf` CPU path | Boring, correct, ~1-2 ms/frame readback at PS2 sizes. Proves the whole frontend half with zero new encode surface. |
| **A (v2, the goal)** | Pool VkImages allocated exportable (`VK_KHR_external_memory_win32`, OPAQUE_WIN32) → imported once into GL (`GL_EXT_memory_object_win32`, `glImportMemoryWin32HandleEXT` + `glTextureStorageMem2DEXT`) → the imported GL texture ids feed the EXISTING `zc_push` GLMemory path unchanged | Zero-copy, encoder pipeline (nvav1enc GLMemory, ABR, SVC, intra-refresh, caps-reneg) untouched. Sync via `VK_KHR_external_semaphore_win32` ↔ `GL_EXT_semaphore_win32`, or v2.0 conservatively fence-waits the copy before GL consumes (glFinish-class, measured acceptable today). NVIDIA supports this interop first-class; the worker already requires NVIDIA. |
| B (fallback) | Vk → CUDA external memory → gst CUDAMemory into nvenc | Avoids GL entirely but adds a CUDA dependency + a second caps path + gst.cuda context-sharing complexity. Only if A misbehaves. |

## Milestones

- **M1 — frontend half + readback (3–5 days):** negotiation v1+v2, interface callbacks, sync
  indices, pool copy, CPU staging → `pushVideoBuf`. Exit: LRPS2 `paraLLEl-GS` renders Stuntman in a
  live room (test-roms), correct colors/orientation, size changes survive (448↔447), room closes
  clean. perf-attr already rides the core and works under any renderer.
- **M2 — external-memory zero-copy (2–4 days):** exportable pool + GL import + semaphores; delete
  the staging copy. Exit: A/B vs M1 shows readback gone (vidpush channel), 60fps, no tearing
  (fence discipline), teardown clean across 20 room cycles.
- **M3 — fleet rollout (per-core validation):** mupen paraLLEl-RDP, Dolphin-VK, flycast-VK, PPSSPP-VK
  behind per-core config flags; each gets the standard live-room + save/close/teardown gauntlet.
  Per-title renderer choice stays in ArcadeGameProfile (the Stuntman precedent).

## Config & rollout shape

- Core config: allow `vulkan: true` (or `hwContext: vulkan`) per core entry in
  config.worker-gl.yaml; `GET_PREFERRED_HW_RENDER` answers VULKAN for those cores only. GL cores
  unaffected; zoning untouched.
- Stuntman end-state: profile flips to `pcsx2_renderer: "paraLLEl-GS"` + `pgs_high_res_scanout`
  once M1 proves it — restoring 2×-class sharpness with SW-class accuracy. Keep the SW row as the
  proven fallback.

## Risks / carried lessons

- **Sync-index protocol subtleties** — copy RetroArch's semantics exactly; do not improvise.
- **Per-core negotiation drift** (interface versions): the worker must tolerate v1 cores and v2.
- **Mid-run frame-size changes**: pool realloc + appsrc caps update (the 448↔447 lesson) is a
  DAY-ONE requirement, not a follow-up.
- **Teardown order kills workers** when wrong (nvcodec-against-dead-share-group class): encoder
  interop objects die before the core's device, always.
- **Verify on the real stack** (arcade-benchmark-isolation): quality via raw pool dumps (patch 0022
  analog), smoothness via pace-diag/perf-attr + Eric's remote client, never headless judgment.
- The gst nvcodec plugin (patched: intra-refresh + temporal SVC) is untouched by option A — it
  still receives GLMemory RGBA.

## Effort & sequencing

~1.5–2 weeks of focused work to M2 with one core proven. Sequence AFTER the ICE srflx deploy lands
(fork is contested) and ideally after the per-game encode-scale feature if 2× sharpness is wanted
sooner cheaply (that ~60-line feature gives upscaled-encode sharpness on SW today; this project
gives real internal-resolution sharpness plus everything else above).
