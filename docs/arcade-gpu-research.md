# Arcade — GPU rendering research (findings)

**Goal:** determine how to give the CloudRetro workers GPU-accelerated rendering (to fix N64's llvmpipe
triangles + enable upscaling and heavier systems) on Ziggy — **Windows 11 + Docker Desktop (WSL2 backend)
+ RTX 4070 Ti (12 GB) + Intel UHD 770 iGPU**. Method: hands-on probing of real `--gpus all` containers on
the box (not literature). Companion: `docs/arcade-opus-worklist.md` (everything else) and
`arcade-next-steps.md` (strategy).

## ★ UPDATE — Path A INVESTIGATED: GPU rendering PROVEN (2026-07-02)
Installed a fresh **Ubuntu-24.04 WSL2 distro** (WSLg included) and probed it directly. Results:
- **Hardware GL on the 4070 Ti works:** in the distro, `GALLIUM_DRIVER=d3d12
  MESA_D3D12_DEFAULT_ADAPTER_NAME=NVIDIA glxinfo` →
  **`OpenGL renderer: D3D12 (NVIDIA GeForce RTX 4070 Ti)`, OpenGL 4.6 (Compat), Mesa 25.2.8.**
- **Headless EGL surfaceless works on the GPU** (`EGL_PLATFORM=surfaceless eglinfo` → same 4070 Ti
  renderer, no X server). **This resolves the GLX-vs-EGL risk** — CloudRetro can render headless on the
  GPU (via EGL; or Xvfb+GLX, which also worked via d3d12).
- **Must force the NVIDIA adapter** (`MESA_D3D12_DEFAULT_ADAPTER_NAME=NVIDIA`) or Mesa-d3d12 grabs the
  Intel UHD 770 iGPU (`D3D12 (Intel(R) UHD Graphics 770)` — hardware, but the wrong GPU).
- Why the earlier "rendering doesn't work" finding: it was measured **inside Docker Desktop containers**,
  whose `docker-desktop` distro lacks the D3D12 graphics runtime. A real WSL2 distro (Ubuntu+WSLg) HAS
  it: `/usr/lib/wsl/lib` contains `libd3d12.so` + `libd3d12core.so` + `libdxcore.so`.

**Deployment catch (also tested):** bind-mounting those WSL libs into a **Docker Desktop** `--gpus all`
container (at `/usr/lib/wsl/lib`, matching Mesa 25.2.8, `/dev/dxg` present) STILL fails —
`eglInitialize` → "failed to create dri2 screen". Docker Desktop's minimal distro exposes the GPU for
compute/CUDA but not the full WSLg/D3D12 *graphics* environment. **So GPU rendering requires running the
CloudRetro workers in a REAL WSL2 distro (Ubuntu-24.04+WSLg), not Docker Desktop's containers.**

**Recommended deployment:** install a container engine (docker-ce / Podman) **inside the Ubuntu-24.04 WSL
distro** and run the arcade worker stack there — that distro's containers inherit the working
`/dev/dxg` + `/usr/lib/wsl/lib` graphics env; bind-mount `/usr/lib/wsl/lib` + `--device /dev/dxg` and set
`GALLIUM_DRIVER=d3d12 MESA_D3D12_DEFAULT_ADAPTER_NAME=NVIDIA` (+ EGL, or Xvfb). Replace the image's
llvmpipe `libGL` with full Mesa (already has `d3d12_dri.so`). The coordinator/gateway/site are unchanged
(point at the distro's IP). *Not yet validated: the docker-in-WSL container run itself — the immediate
next implementation step.* The copied libs are staged at `D:\Arcade\wsl-gpu-libs` (21 `.so`).

**Net:** GPU N64 (clean gliden64 + upscaling) is achievable on this exact hardware — it's a
deployment-environment change (workers → WSL2 distro), not a dead end. NVENC still works in either setup.

## ★★ IMPLEMENTED (2026-07-02) — GPU worker image built + verified, NO source patch
Follow-up on the "docker-in-WSL untested" caveat above — now done, and it works:
- **CloudRetro uses RGFW→GLX (X11), not EGL** (`pkg/worker/caged/libretro/graphics/rgfw.go`), and **Xvfb
  (its software X server) + GLX does NOT reach the d3d12 GPU** (falls back / no renderer). So the CPU
  image's Xvfb approach can't be GPU-accelerated as-is.
- **The unlock (no C patch needed):** point the worker at **WSLg's GPU-backed X display (`DISPLAY=:0`)**
  instead of Xvfb. A container mounting `/tmp/.X11-unix` + `/mnt/wslg` + `/usr/lib/wsl` and using `:0` gets
  **hardware GLX: `D3D12 (NVIDIA GeForce RTX 4070 Ti)`**. CloudRetro's `XOpenDisplay(NULL)` just follows
  `DISPLAY`, so this is pure config.
- **Docker-in-WSL validated:** installed `docker.io` inside the Ubuntu-24.04 distro; a container there
  (with `--privileged --device /dev/dxg` — privileged needed for the dxg ioctls) renders on the 4070 Ti.
- **GPU worker image BUILT:** `docker/arcade/Dockerfile.gpu` → `movietheater/cloud-game:pinned-gpu`
  (base image + full Mesa, minus the llvmpipe libGL). Verified: `glxinfo` in the image →
  `D3D12 (NVIDIA GeForce RTX 4070 Ti)`. Deploy manifest: `docker/arcade/docker-compose.gpu.yml`.

**What's DONE:** the GPU worker image renders on the 4070 Ti; the run recipe (mounts/env/privileged/WSLg
display) is proven and captured in the compose. **What REMAINS to go live end-to-end:**
1. Run `docker-compose.gpu.yml` in the Ubuntu-24.04 distro's docker (not Docker Desktop) with a game and
   confirm N64 renders clean in an actual room (the last functional check).
2. **Networking:** enable WSL2 mirrored networking (`.wslconfig` `networkingMode=mirrored` + `wsl
   --shutdown`) so the coordinator (localhost:8000) + WebRTC UDP ports are reachable from Windows/the
   browser — this restarts Docker Desktop, so coordinate with the live CPU stack (don't do mid-session
   while the CPU arcade is in use).
3. Point the gateway/site at the GPU coordinator; keep the CPU stack as fallback.
This is deployment wiring, not research — the rendering question is settled: **N64 runs on the 4070 Ti.**

## ★★★ RESOLVED (2026-07-03) — end-to-end LIVE; the WebRTC networking riddle solved

The GPU stack now streams to a real browser: Mario Kart 64, clean geometry (no llvmpipe triangles),
640×480 upscaled internal render, multiplayer (2 browsers, invite link, P1+P2) verified by an automated
Playwright run. `glxinfo` inside the running worker env: `D3D12 (NVIDIA GeForce RTX 4070 Ti)`.

**What the days of `ice: failed` actually were** (in order of discovery, each necessary):
1. **Dualstack drop:** the single-port mux bound `[::]:8443`; mirrored WSL relays Windows→WSL UDP only
   into AF_INET sockets (TCP relays into both — hence green signaling, dead media). Fixed by patch
   `0002-ipv4-singleport-mux.patch` (`"udp"` → `"udp4"`).
2. **The 127.0.0.1 dead end:** advertising `ICEIPMAP=127.0.0.1` passed a naive same-host UDP echo test but
   can never carry WebRTC — Chrome binds each ICE socket to the LAN interface (192.168.68.69), and Windows
   refuses a LAN-bound socket sending to 127.0.0.1 outright ("requested address is not valid in its
   context"). The echo test only worked because its client socket was unbound. **Lesson: validate the
   path with a socket bound the way Chrome binds it.**
3. **The real fix:** `.wslconfig` `[experimental] hostAddressLoopback=true` — enables Windows↔WSL traffic
   over the host's own assigned IP, both directions. With it, `ICEIPMAP=192.168.68.69` works for the
   same-host browser AND LAN peers (and go-live just swaps in the public IP + router forward). Verified
   with bound-source echo tests in both directions, then a real room.

Ops: the distro (and stack) dies when WSL idles — `scripts/register-arcade-wsl-task.ps1` registers the
logon keepalive task. Docker in the distro is systemd-enabled; containers are `restart: unless-stopped`.

## ★★★★ Part B SHIPPED (2026-07-03) — NVENC H264 encoding live

Per-room video encode moved to the 4070 Ti: worker CPU during a 640×480/60 N64 room dropped from
**92–123%** (software VP8) to **~32%** (NVENC H264 — the remainder is emulation itself). Details and
the build recipe live in `arcade-next-steps.md` Part B + `docker/arcade/patches/README.md` (patch 0003).
Notes for posterity:
- nvh264enc registered fine inside the WSL-distro container (dlopens the driver libs from
  `/usr/lib/wsl/lib`), but probes **NV12-only** sink caps → the pipeline's shared I420 capsfilter
  needs a `videoconvert` bridge before the encoder (that was the one non-obvious failure:
  "could not link video_caps to video_enc").
- pion already speaks `video/h264`; GStreamer caps `stream-format=byte-stream,alignment=au` is what
  its H264 payloader wants. Late joiners decode thanks to gop-size=120 + repeat-sequence-header=true.
- The worker's session-teardown segfault (exit 139, silent) predates all of this — it happens on the
  pre-NVENC image too. Docker's restart policy masks it completely (fresh worker in ~4s).

## TL;DR (initial finding — superseded by the ★ update above for the rendering verdict)
- **GPU compute, NVENC (encode) and NVDEC (decode) fully reach containers today** — the 4070 Ti shows up
  in `nvidia-smi` inside a `--gpus all` container, with `libnvidia-encode`, `libnvcuvid`, `libcuda` all
  present. **→ the NVENC scalability lever is achievable now.**
- **GPU *rendering* (OpenGL/Vulkan) does NOT work in Docker Desktop's WSL2 backend.** Both GL (via GLX+Xvfb)
  and Vulkan fall back to **llvmpipe (software)**. The cause is a **missing D3D12 graphics runtime** — this
  host's WSL GPU stack is compute/video-only. **→ fixing the N64 triangles needs an infrastructure change,
  not a config tweak.**

## Evidence (from live probes on Ziggy)
`docker run --gpus all -e NVIDIA_DRIVER_CAPABILITIES=all ubuntu:24.04`:
- `/dev/dxg` present; `libdxcore.so`, `libnvidia-encode.so.1`, `libcuda.so.1` mounted.
- Mesa DRI drivers installed in-container: `d3d12_dri.so`, `zink_dri.so`, `swrast_dri.so`. So the *client*
  side of hardware GL is present.
- **`vulkaninfo --summary` → only `llvmpipe` (deviceType CPU)** — no hardware Vulkan device.
- **`glxinfo` under Xvfb → `OpenGL renderer: llvmpipe`**, even with `MESA_LOADER_DRIVER_OVERRIDE=d3d12`.
- `nvidia-smi -L` in-container → **`NVIDIA GeForce RTX 4070 Ti`** (so the GPU IS reachable — for compute).
- `find / -iname libd3d12core.so` → **nothing**. Host `C:\Windows\System32\lxss\lib` and the NVIDIA
  DriverStore contain only compute/video libs (libcuda, libnvcuvid, libnvidia-encode, libnvidia-ml,
  libnvoptix, libnvwgf2umx) — **no `libd3d12core.so`, no `libGLX_nvidia`, no Vulkan ICD**.
- Only one WSL distro exists (`docker-desktop`, minimal, no bash) — no user WSL2 Ubuntu to source graphics
  libs from or run natively.

**Why:** In WSL2, NVIDIA graphics reach Linux through the **D3D12** layer (`libdxcore` + `libd3d12core` +
Mesa's `d3d12` gallium driver), NOT classic GLX. Mesa-d3d12 needs `libd3d12core.so` (the Microsoft DirectX
12 runtime, shipped via **WSLg**) to create a device. This host has the NVIDIA compute/video WSL package
but **not WSLg / the D3D12 graphics runtime**, so Mesa-d3d12 can't create a device and silently falls back
to llvmpipe. Docker Desktop's `--gpus all` propagates the compute/NVENC libs but not a graphics stack.

## Paths to GPU rendering (ranked)

### Path A — Provide the WSL D3D12 graphics stack, then bind-mount it (lightest; TRY FIRST)
Install a real WSL2 distro with **WSLg** (`wsl --install -d Ubuntu-24.04`). WSLg ships the D3D12 graphics
runtime + Mesa-d3d12 + Dozen (Vulkan-on-D3D12) into that distro's `/usr/lib/wsl/lib`. Then:
1. Verify hardware GL/Vulkan **inside the WSL distro** (`glxinfo`/`vulkaninfo` should show `d3d12`/the 4070
   Ti, not llvmpipe). This is the make-or-break test — if WSLg's D3D12 path drives the 4070 Ti here, the
   rest follows.
2. **Bind-mount** that distro's `/usr/lib/wsl/lib` (the D3D12/Mesa/Dozen libs) into the CloudRetro worker
   container + add to `LD_LIBRARY_PATH`, alongside `--gpus all`. Replace the llvmpipe `libGL` in the image
   with full Mesa (d3d12). Set `GALLIUM_DRIVER=d3d12`.
3. **Unknown to resolve:** CloudRetro renders through **Xvfb + GLX**; WSLg/D3D12 GL may be **EGL-surfaceless**
   only. If GLX-over-Xvfb won't drive d3d12, either use `Xvfb + a GLX↔EGL shim`, or patch CloudRetro's GL
   context creation to EGL. (This same GLX-vs-EGL question is the core risk in every path.)
- *Effort:* moderate. *Risk:* WSLg-in-container libs may not cleanly bind-mount; GLX vs EGL.
- *Not yet tested* (installing a WSL distro is a system change / interactive first-run — hold for a human
  go-ahead; steps above are exact).

### Path B — Hyper-V VM with 4070 Ti DDA passthrough (heaviest; most certain)
Ziggy has **two GPUs** (Intel UHD 770 + RTX 4070 Ti), so the host can keep the iGPU and **pass the 4070 Ti
to a Linux VM** via Hyper-V **Discrete Device Assignment**. Inside the VM: native NVIDIA Linux drivers →
**real GLX + Vulkan + NVENC**, zero WSL2/D3D12/EGL quirks — gliden64 or parallel-RDP (Vulkan) run
hardware-accelerated and upscaled. Run the CloudRetro stack in the VM; the gateway/site are unchanged
(they just point at the VM's IP instead of localhost).
- *Effort:* high (DDA on Win11 Pro needs the dismount-and-assign PowerShell dance; a Linux VM + driver
  install). *Risk:* low once up — it's the "normal Linux GPU" environment everything expects.
- *This is the clean long-term home if GPU N64/heavier systems matter.*

### Path C — Ship CPU rendering (defer GPU)
N64 runs on llvmpipe **today** (correct orientation, playable, GL artifacts). 2D is pristine. Accept
rough-but-working N64 for v1; revisit A/B when there's appetite. **This is a legitimate v1** — the arcade's
value (couch multiplayer, breadth) doesn't hinge on pixel-perfect N64.

## NVENC (GPU encoding) — separate lever, achievable NOW
Independent of rendering; the GPU encode path is already open (`libnvidia-encode` + the 4070 Ti reachable).
- Build a worker image variant with GStreamer **nvcodec** plugins (`nvh264enc`, `nvav1enc`); run with
  `--gpus all -e NVIDIA_DRIVER_CAPABILITIES=all`.
- CloudRetro's pipeline reads the codec from `encoder.list` (`pkg/worker/media/gstreamer.go`), so an
  `encoder.list` entry for an NVENC element + `encoder.video.codec` may suffice; verify it doesn't hardcode
  `vp8enc` for the WebRTC track negotiation. 40-series NVENC does **AV1** (better quality/bitrate if the
  target browsers decode it; keep VP8 as fallback).
- *Payoff:* moves encoding off the CPU → many more concurrent rooms. *Effort:* medium. *Risk:* low —
  the plumbing is confirmed present. **Recommend doing this regardless of the rendering decision.**

## Recommendation
1. **NVENC now** (or when concurrency bites) — confirmed viable, high ROI, low risk.
2. **For rendering, run Path A next** (install a WSL2 Ubuntu+WSLg distro and test whether its D3D12 stack
   actually drives the 4070 Ti) — it's the cheapest experiment and decides everything. If it works →
   bind-mount + solve GLX/EGL. If it doesn't → **Path B (VM passthrough)** or **Path C (defer)**.
3. **Don't block v1 on rendering.** Ship 2D-clean + rough-N64 + (optionally) NVENC; treat clean N64 as a
   fast-follow once Path A/B is proven.

## Next concrete experiment (Path A, needs a human OK — it installs a WSL distro)
```
wsl --install -d Ubuntu-24.04            # then complete first-run user setup
wsl -d Ubuntu-24.04 -e bash -c "sudo apt update && sudo apt install -y mesa-utils vulkan-tools && \
  ls /usr/lib/wsl/lib | grep -iE 'd3d12|dozen|dxcore' && \
  glxinfo -B | grep -i renderer && vulkaninfo --summary | grep -i deviceName"
```
If that shows `d3d12`/the 4070 Ti (not llvmpipe), GPU rendering is unlocked and we proceed to bind-mount +
image work. If it still shows llvmpipe, the WSLg D3D12 path is unavailable on this driver and we go to
Path B.
