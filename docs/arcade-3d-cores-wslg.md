# Arcade — GL 3D cores (PSP / Dreamcast / Naomi / Atomiswave) under WSLg

**Status: BLOCKED, scoped not built (2026-07-04).** The 2D breadth added the same day is live and
verified; the GL 3D cores are cataloged + configured but crash on launch. This is the scope for
making them work. See also `config.yaml` (the psp/dc/naomi/atomiswave core entries),
`arcade-gpu-research.md` (the D3D12/NVENC saga), and the arcade skill.

## Symptom

Launching a PSP or Dreamcast game connects, then the room dies before streaming. Per-worker logs:

- **ppsspp (PSP):** reaches "Playing", then crashes —
  `signal arrived during cgo execution … RGFW_window_makeCurrentContext_OpenGL` +
  `X Error … BadAccess`.
- **flycast (Dreamcast/Naomi/Atomiswave):** loads the ROM (reios HLE BIOS), then
  `X Error … BadMatch … Major opcode 148 (GLX)`.

## Root cause (verified)

The workers render under **WSLg's X server (Xwayland)**, whose GPU-accelerated GL is exposed via
**EGL**, not **GLX**. Direct confirmation from inside a worker (worker GL env, `glxinfo`):

```
glx: failed to create drisw screen
X Error of failed request:  BadValue …  X_GLXCreateNewContext
```

- **mupen64plus_next (N64) works** because its context path (as CloudRetro drives it) resolves on
  the EGL/d3d12 stack.
- **flycast + ppsspp create their GL context through GLX** (via the libretro `glsm` HW-render glue),
  and GLX context creation is broken on Xwayland → `BadMatch` / `BadAccess`. This is the same family
  as the documented angrylion panic (GL scaffolding of `isGlAllowed` cores).
- `LIBGL_ALWAYS_SOFTWARE=true GALLIUM_DRIVER=llvmpipe` does **not** help: the software GLX path
  (`drisw`) is also broken here (see glxinfo), and llvmpipe would be far too slow for 3D at 60fps
  regardless (the N64 case already couldn't afford 2× internal res on llvmpipe).

## Fix options (in preference order)

### A. Make CloudRetro create GL contexts via EGL (the real fix)

CloudRetro's `pkg/worker/caged/libretro/graphics` (RGFW-based) creates the shared front-end GL
context. Under WSLg it must use **EGL** (`eglGetDisplay`/`eglCreateContext` with
`EGL_OPENGL_API`), not GLX (`glXCreateNewContext`). mupen already works, so the goal is to route
flycast/ppsspp through the same EGL-backed context, requesting an explicit
**`EGL_CONTEXT_OPENGL_CORE_PROFILE_BIT` / core profile with a valid major.minor** (3.3+; flycast
wants ≥3.1 core / GLES3, ppsspp ≥3.3). This is a new CloudRetro patch (call it `0005-egl-context`)
against the pinned SHA `13852a7`, then an image rebuild.
- Investigate whether RGFW already has an EGL backend that can be selected at build/run, vs. a
  bespoke context path.
- Verify with `eglinfo` inside the worker that EGL exposes an OpenGL (not just GLES) core ≥3.3 on
  the d3d12 device; if only GLES is available, the cores must be built/configured for GLES.

### B. `libgomp1` in the image (already staged, required for flycast)

flycast_libretro dlopens `libgomp.so.1` (GNU OpenMP), absent from the base image → `cannot open
shared object file`. **Done in `docker/arcade/Dockerfile.gpu`** (added `libgomp1` to the apt line);
inert until the next image rebuild. Verified at runtime by installing it into the live workers.

### D. Windows-native worker — sidestep WSLg entirely (scoped 2026-07-04)

The worker is an upstream-supported **native Windows build** (MSYS2/UCRT64, WGL on a hidden
window → real NVIDIA GL 4.6, CI-tested on windows-latest at our pinned SHA). One Windows worker
zoned `gl` beside the WSL stack unblocks all four cores with **no CloudRetro graphics patch at
all**. Full scope, build recipe, zone-routing requirements, and run model:
**`docs/arcade-windows-worker.md`**. This is now the preferred alternative if option A's EGL
probe disappoints.

### C. PPSSPP software renderer — a PSP-only sidestep (no GL context)

PPSSPP libretro has a **software renderer**. With `ppsspp_software_rendering` (or the
backend/renderer option) enabled and **`isGlAllowed: false`**, the core rasterizes on the CPU and
hands CloudRetro plain framebuffers — no GL context, so it dodges the GLX/EGL problem entirely.
- Trade-off: software PSP is slow; lighter/2D-ish titles may reach playable fps on the CPU, 3D-heavy
  ones likely won't. Worth a test-roms pass to see what's viable.
- flycast/Naomi/Atomiswave have **no** software renderer, so this only helps PSP.

## Also seen during testing (not blockers)

- **`ppsspp_libretro.so` downloaded as 0 bytes** once (a `206 Partial Content` resume left a corrupt
  file → `file too short`). Fix was deleting it from the `arcade_cores` volume so `repo.sync` did a
  clean `200` full download (21 MB). If core auto-download flakes again, delete the stub + restart.

## Verification

Use the **test-roms** harness against **prod origin** (the gateway defaults to Production —
`SiteOrigin=theater.carpouzis.com`; a localhost:3000 browser is rejected at the WS handshake).
`arcade-diag.mjs --game "<title>"` with a `system=` filter; success = `status: Playing` holding with
`video.fps ~55`, `freezes:0`, frames decoding. Today: PSP/DC reach "Playing" (ppsspp) or ROM-load
(flycast) then die at context creation; that's the bar to clear.

## Rebuild reminder

Any of A/B/C that touches the image needs the `Dockerfile.gpu` rebuild in the Ubuntu-24.04 WSL
docker (see `docker/arcade/README.md` + `patches/README.md`), then `docker compose -f
docker-compose.gpu.yml up -d`. The `cores` named volume persists auto-downloaded cores across
rebuilds.
