# Vulkan capture M2 — zero-copy implementation spec (for the implementing agent)

**Mission**: replace the M1 per-frame readback (Vulkan blit → host staging → CPU frame path) with a
**Vulkan→GL external-memory zero-copy** path: the core's frame lands in a pool of exportable
VkImages that are imported ONCE into OpenGL and pushed to the encoder as GL textures through the
EXISTING `zc_push` GLMemory path. The frame never visits system memory.

**Why now**: M1 is merged and LIVE (Stuntman ships on paraLLEl-GS since 2026-07-15 ~23:59, profile
row Id 7). The user's verdict: "INCREDIBLY smooth" audio, but **slight video hitching** — the
measured signature is the M1 readback's synchronous fence+copy chain (maxTick 31–45ms windows with
up to half the ticks >20ms while meanTick ~12ms and audio perfect). M2 deletes that chain.

**Review gate**: same as M1 — reviewed by the authoring session before merge/deploy. Branch
`vulkan-capture-m2` off current `movietheater-fork` (HEAD `dd4bb1a` = M1 + per-game wiring +
srflx, all deployed). Do not push, do not touch the shipped `fork.patch`, produce
`RESULTS-vulkan-m2.md`.

---

## 0. Environment facts (M1 spec §0 still applies — read it: docs/arcade-vulkan-capture-spec.md)

Deltas since M1:
- The deployed worker IS the M1 binary (`bin/worker.exe`, rollback `worker.pre-vkm1.exe`).
- **Stuntman's live profile is paraLLEl-GS** — a bench room on Stuntman exercises the Vulkan path
  with NO config hand-edits. (Still restore anything you do change, and still gate every
  deploy/bench on `curl -s localhost:8000/status` showing no live rooms — the user plays on this
  pool, possibly your test title.)
- Useful live baseline for your A/B: the user's verdict session (glworker.log, room at
  2026-07-15 23:58:13) — the M1 hitching signature to beat is its maxTick/slowTicks profile.
- M1 code map: `nanoarch_vulkan.c` (all Vulkan), `nanoarch.go` (vk plumbing + `HwContextOverride`),
  `overrides.go` (`WantsVulkanRenderer`), `coordinatorhandlers.go` (room-level decision + the
  BUILD-TIME `m.GLZeroCopy`), `pkg/worker/media/glzerocopy.go` (the GL zc path you are joining),
  `pkg/worker/caged/libretro/graphics/` (RGFW GL context + the GL zero-copy texture pool).

## 1. Architecture

### 1.1 The GL side needs a context a Vulkan room doesn't have

Today a Vulkan room creates NO GL context (`SwitchGraphics(false)`, no RGFW). The GL zc path
(`zc_prepare`/`zc_share` in glzerocopy.go) wraps *the core's* HGLRC — which doesn't exist here.
M2 must:
1. Create a dedicated RGFW/WGL context for Vulkan rooms (the same `graphics.RGFW` machinery GL
   cores use; owned by the graphics thread — `thread.SwitchGraphics(true)` for vk rooms again).
   This context exists ONLY to hold the imported textures and to be wrapped for GStreamer.
2. Run the `InitGLZeroCopy(hglrc, unbind, rebind)` flow against it (zc_prepare needs the context
   CURRENT to read function pointers; zc_share needs it NOT current — the existing dance, reuse it).
3. Arm `m.GLZeroCopy = true` for vk rooms when zero-copy is enabled — **this reverses M1's guard**
   in coordinatorhandlers.go: the decision becomes `glZC = conf.GlZeroCopy && (gl-core || (vk-game
   && vkZeroCopyEnabled))`. Keep a config/env kill-switch (`CLOUD_GAME_VK_ZEROCOPY=0` or a config
   field) that restores the M1 readback + CPU pipeline wholesale — that is also the automatic
   fallback when any required extension is missing.

### 1.2 The pool (Vulkan side)

- N=3 exportable VkImages (`VK_FORMAT_R8G8B8A8_UNORM` — NOTE: **not** M1's BGRA; the GL zc path
  ships RGBA and glcolorscale/nvenc consume RGBA, so no channel swap belongs in the blit anymore),
  `VK_IMAGE_TILING_OPTIMAL`, usage TRANSFER_DST | SAMPLED, created with
  `VkExternalMemoryImageCreateInfo` (handleType OPAQUE_WIN32) and **dedicated allocations**
  (`VkMemoryDedicatedAllocateInfo`) — NVIDIA requires dedicated for reliable Win32 export.
- Export each allocation's HANDLE via `vkGetMemoryWin32HandleKHR`.
- Required device extensions: `VK_KHR_external_memory_win32` (+ `VK_KHR_external_semaphore_win32`
  for §1.4) — ⚠ **the core creates the device** (negotiation create_device2). Inject requirements
  in the EXISTING `create_device_wrapper` (nanoarch_vulkan.c): append our extension names to the
  core's `VkDeviceCreateInfo.ppEnabledExtensionNames` (dedup; verify support with
  `vkEnumerateDeviceExtensionProperties` first; if unsupported → log + set the fallback flag so the
  room runs M1 readback). Same wrapper trick for instance extensions if any prove needed
  (external_memory_capabilities is core in Vulkan 1.1; the core's instance is 1.3+).

### 1.3 The import (GL side, once per pool (re)allocation)

On the graphics thread with the RGFW context current:
- `glCreateMemoryObjectsEXT` + `glImportMemoryWin32HandleEXT(GL_HANDLE_TYPE_OPAQUE_WIN32_EXT)`
  per pool image, then `glTextureStorageMem2DEXT(tex, 1, GL_RGBA8, w, h, memObj, 0)`.
- Required GL extensions: `GL_EXT_memory_object`, `GL_EXT_memory_object_win32` (present on this
  NVIDIA driver; still CHECK and fall back).
- The resulting GL texture ids feed `zc_push` exactly like the GL cores' pool textures do. Study
  `graphics/zerocopy.go`'s pool bookkeeping — **a pool slot must not be reused until GStreamer
  releases it** (`goReleaseZeroCopyTexture`); reuse that mechanism (or mirror it faithfully) rather
  than inventing a new one. The marquee-jitter bug in glzerocopy.go's history is what happens when
  this is wrong.

### 1.4 Synchronization (the UB-sensitive heart — read carefully)

Cross-API Vk→GL access to external memory is NOT implicitly coherent. The spec-correct mechanism is
**external semaphores** (`VK_KHR_external_semaphore_win32` ↔ `GL_EXT_semaphore` /
`GL_EXT_semaphore_win32`): per pool slot, a Vk→GL "ready" semaphore (Vulkan signals after the blit,
GL waits before sampling, with `glWaitSemaphoreEXT`'s srcLayouts naming the image layout) and a
GL→Vk "done" semaphore for reuse.

The complication: the GL consumer is GStreamer's own thread/context, whose element code we do not
modify. The layered strategy — implement in this order, validate at each layer, and STOP at the
first one that proves correct:

1. **Import-context wait**: after the Vulkan blit submit signals the slot's "ready" semaphore,
   issue `glWaitSemaphoreEXT` + `glFlush` on OUR import context (graphics thread via
   `thread.Main`), then `zc_push`. Rationale: the semaphore wait orders the transition into the GL
   share group; subsequent bind-to-use by GStreamer's shared context follows normal share-group
   visibility rules. This is the pattern production Vk/GL interop projects use on NVIDIA.
2. **Conservative fallback** (also the correctness reference for A/B): fence-wait the Vulkan blit
   on the cothread before push (a blit-only fence — still strictly cheaper than M1's
   blit+copy+host-visibility chain), plus layer-1's GL wait.
3. If either shows tearing/stale frames, document findings and stop for review — do not improvise
   further sync inventions.

**Validation for this section is mandatory and specific**: the historical zc failure mode is not a
clean error — it is frames showing FUTURE pixels and snapping back (scrolling content jitters).
Test with moving content (drive a take; the failed-take screen is STATIC and proves nothing),
record ≥60s, and inspect bench screenshots for coherence. Also run the luma-motion check the
test-roms harness provides.

### 1.5 Per-frame flow (replacing M1's readback in `coreVideoRefresh`'s vk branch)

1. Acquire a released pool slot (block briefly or reuse-oldest — match graphics/zerocopy.go's
   policy); if none, `OnDup()` and skip (frame dup beats a stall).
2. Record+submit under `lock_queue`: barrier core image → TRANSFER_SRC, blit (scaling, LINEAR when
   sizes differ — keep M1's F1 semantics) into the slot's VkImage, barrier core image back, barrier
   slot image to the layout named in the GL-side wait; signal the slot's "ready" semaphore
   (+ fence for the fallback/teardown accounting).
3. GL-side wait per §1.4, then `pushGLTexture(slotTexID, w, h, durNs)` (the existing zc entry).
4. NO host mapping, NO CopyImageToBuffer — delete/bypass that code path for zero-copy rooms (keep
   it compiled: it IS the fallback).

### 1.6 Geometry changes (448↔896 flips are ROUTINE for this title)

Pool realloc on extent change: `vkDeviceWaitIdle` + wait until GStreamer released all slots (or
tear the pipeline's buffers via the existing Reinit path) → destroy GL imports + memory objects +
semaphores + VkImages → recreate + re-export + re-import. `zc_push` already renegotiates appsrc
caps on size change — do not duplicate that. This path WILL be exercised within seconds of real
gameplay; treat it as first-class, not an edge case.

### 1.7 Teardown ordering (extends the M1 order)

GL imports/memory objects/semaphores die on the graphics thread BEFORE `nano_vk_predestroy`
destroys the Vulkan pool; then the M1 order continues (pool → core context_destroy skip → device →
instance). The GStreamer side must have released the textures first — the existing
"pipeline tears down from inside the core's teardown" ordering (coordinatorhandlers comment) is
what guarantees that; do not reorder it.

## 2. Acceptance criteria (M2 done =)

1. **The hitching signature improves measurably**: on a driven Stuntman session (paraLLEl-GS,
   high-res scanout), the maxTick/slowTicks profile beats the 23:58:13 baseline (no recurring
   30–45ms maxTick windows attributable to the push path), audio stays 60/48000/forgiven=0.
2. **Visual correctness under MOTION**: correct colors (RGBA order — regression risk vs M1's BGRA
   blit!), no tearing/stale frames/marquee jitter, full frame under high-res scanout.
3. Geometry flips 448↔896 mid-session survive: pool realloc logs, caps renegotiation, no pipeline
   error, video continues.
4. Fallback proven: with the kill-switch set (and separately with a simulated missing extension),
   the room runs the M1 readback path end-to-end.
5. Mixed sequence + clean closes: vk-zc room → GL room → vk-zc room on one worker process; close
   clean ×5; no zombie/wedge flags.
6. Hygiene: branch `vulkan-capture-m2`, clean commits, compile-proven patch saved to scratchpad
   (restore shipped fork.patch), RESULTS-vulkan-m2.md with evidence + deviations + open questions.

## 3. Footguns (M1 spec §6 all still apply, plus)

- **The core owns the device** — extension injection happens in create_device_wrapper or nowhere.
  If injection fails, DO NOT create a second device: fall back to readback.
- **Do not modify GStreamer elements or the encoder** — if layer-1 sync proves insufficient, stop
  for review rather than patching gst internals.
- The import context and the core's Vulkan work are on DIFFERENT threads (graphics thread vs
  cothread) — every GL call on the graphics thread (`thread.Main`), every Vulkan submit under
  `lock_queue`, no exceptions.
- `glTextureStorageMem2DEXT` sizes must match the VkImage exactly (same w/h/format/mips=1);
  mismatch is silent corruption, not an error.
- Handle lifetime: close exported Win32 HANDLEs after import (GL takes a reference), or you leak a
  handle per realloc — under the 448↔896 flip rate that is a real leak.
- The user may open a room at any time: pool-idle gates on EVERY deploy/recycle/bench, no
  exceptions, even at 3am.

## 4. Deliverables

Branch + RESULTS-vulkan-m2.md + compile-proven patch in scratchpad + restored shipped fork.patch +
live pool restored to the shipped M1 state (binary, configs) after your test windows. No deploy —
merge/deploy happens after review, same as M1.
