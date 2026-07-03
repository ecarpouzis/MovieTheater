# Arcade — Next Steps: N64 rendering quality + emulation breadth

**Status (2026-07-03):** The CloudRetro arcade is LIVE on Ziggy and streams end-to-end. **Part A is
DONE:** the stack now runs in the Ubuntu-24.04 WSL2 distro's docker with GPU rendering on the 4070 Ti
(`movietheater/cloud-game:pinned-gpu` + `docker-compose.gpu.yml`) — N64 renders clean (no triangle
artifacts) at 640×480 internal res, verified end-to-end incl. 2-browser multiplayer. The WebRTC
networking saga and its resolution (mirrored + hostAddressLoopback + udp4 mux + LAN-IP ICE) is written
up in `docs/arcade-gpu-research.md` ★★★. Background + gotchas: `docs/arcade-plan.md`, memory
`arcade-cloudretro-vertical`, `arcade-rom-bios-locations`.

**The core split to keep in mind:** 2D systems render in *software* inside the core — no GPU needed, and
they're already perfect. 3D systems (N64 and up) render via *OpenGL/Vulkan*, which currently runs on
**mesa-llvmpipe (software GL)** — that's what mangles the triangles. So the render-quality work is
entirely a **GPU** story; breadth is mostly a **ROM/config** story. They're independent tracks.

---

## Part A — Fix the N64 triangles: GPU-accelerated rendering

**Root cause (confirmed):** gliden64 (the N64 GL renderer) runs on `mesa-llvmpipe`, a CPU software GL
implementation that mis-rasterizes N64's 3D geometry. angrylion (pure-software rasterizer) would sidestep
GL but **hard-panics** CloudRetro (it nil-derefs the GL scaffolding CloudRetro sets up for `isGlAllowed`
cores). So on CPU there's no clean N64. The fix is real GPU-accelerated GL/Vulkan on the RTX 4070 Ti.

**What we already proved about the GPU (memory `arcade-cloudretro-vertical`):** `docker run --gpus all -e
NVIDIA_DRIVER_CAPABILITIES=all` injects `/dev/dxg`, `libdxcore.so`, `libcuda`, and `libnvidia-encode`.
WSL2 reaches the GPU through the **D3D12** layer (`libdxcore`), NOT classic NVIDIA GLX — so the render
path here is **Mesa-on-D3D12** (OpenGL) or **Dozen** (Vulkan-on-D3D12), not native `libGLX_nvidia`.

### A1. Build a GPU worker image (R&D — this is the crux)
Extend CloudRetro's Dockerfile into a second image tag (e.g. `movietheater/cloud-game:pinned-gpu`),
leaving the CPU image intact so 2D and the current setup keep working:
- Replace the mesa-llvmpipe `libGL` with a full **Mesa** that includes the **d3d12** gallium driver
  (`mesa-vulkan-drivers` + `libgl1-mesa-dri`), so OpenGL runs on the GPU via `libdxcore`.
- Env: `GALLIUM_DRIVER=d3d12`, `LIBGL_ALWAYS_SOFTWARE=0`, and the WSL GPU lib path on
  `LD_LIBRARY_PATH` (`/usr/lib/wsl/lib` when mounted).
- Run the worker with GPU access — compose `deploy.resources.reservations.devices` (or `gpus: all`) +
  `NVIDIA_DRIVER_CAPABILITIES: all`.

**Known unknown to resolve first:** CloudRetro renders GL through **Xvfb + GLX** (`DISPLAY=:99`). WSL2/D3D12
GL is typically **EGL-surfaceless**, not GLX-over-X11. So either (a) confirm Mesa-d3d12 satisfies CloudRetro's
GLX context over Xvfb, or (b) point CloudRetro at EGL. This is the single biggest risk in Part A — spike it
in isolation (a throwaway container running `glxinfo`/`eglinfo` + a GL demo with `--gpus all`) before
touching the arcade image.

*Acceptance:* Mario Kart 64 + Diddy Kong Racing render clean (no triangles) at full speed, and internal
resolution can be bumped (upscaled N64) via the `mupen64plus-*screensize` options.

### A2. Fallbacks (in order)
1. **parallel-RDP (Vulkan)** instead of gliden64 — `mupen64plus-rdp-plugin: parallel` on a working Vulkan
   ICD (Dozen or NVIDIA-via-dxcore). ParaLLEl-RDP is the most accurate N64 renderer and is Vulkan-native.
   Needs `libvulkan1` + a Vulkan ICD in the image; verify `vulkaninfo` sees the 4070 Ti in-container.
2. **Hyper-V Linux VM with GPU passthrough** — heavier setup but native NVIDIA drivers (real GLX + Vulkan),
   sidestepping all the WSL2/D3D12 quirks. Cleanest long-term home if Docker Desktop's GPU story stays
   fiddly. (This is the Appendix-E "bridged VM" option, now for GPU rather than networking.)

### A3. Keep it non-disruptive
Run the GPU image as a **separate worker service** (or a distinct worker pool) alongside the CPU workers;
route 3D cores to GPU workers and 2D to CPU workers if desired. The current CPU stack stays the safety net.
One GPU is shared across all GPU workers — bounded by VRAM (tiny per emulator) and NVENC sessions; the
4070 Ti is wildly overprovisioned for friends-scale.

---

## Part B — GPU video encoding (NVENC): the scalability lever — **DONE (2026-07-03)**

Every room's video is now **H264 on the 4070 Ti's NVENC** instead of software VP8. Measured on a live
Mario Kart 64 room at 640×480/60: worker CPU **~32%** of a core (vs **92–123%** on VP8) — encoding is
off the CPU, so one box hosts several times more rooms. Verified end-to-end: browser negotiates
`video/H264`, 55fps, 0 dropped; 2-browser multiplayer incl. a mid-game late joiner (the H264-specific
risk — solved with `gop-size=120` + `repeat-sequence-header=true`, since CloudRetro ignores RTCP PLI
and periodic IDRs are the only join-sync).

How it's built (all in `docker/arcade/patches/0003-h264-nvenc.patch` + `config.yaml`):
- The image's minimal from-source GStreamer now also builds the **nvcodec** plugin
  (`-Dbad=enabled -Dgst-plugins-bad:nvcodec=enabled`); it dlopens `libcuda`/`libnvidia-encode` from
  `/usr/lib/wsl/lib` at runtime (no CUDA SDK in the build).
- `gstreamer.go` implements the previously-commented `h264` pipeline case (element + tuning from
  `encoder.list`), with a `videoconvert` bridge (NVENC on WSL probes NV12-only; the shared I420 caps
  won't link directly).
- `config.yaml`: `encoder.video.codec: h264` + `list.h264` (nvh264enc, p1/ultra-low-latency/CBR 4Mbps).
  **Instant fallback: set codec back to `vp8`** — the software path is untouched.
- AV1 (`nvav1enc`) remains a future option for quality-per-bit; H264 chosen for universal decode.

Known quirk (pre-existing, NOT from Part B — confirmed on the old image too): the worker segfaults
(exit 139) at session teardown in the WSLg/D3D12 environment, after "Rom closed" (saves are flushed).
`restart: unless-stopped` brings it back in ~4s and every room gets a fresh worker, so player impact
is nil at friends-scale; worth a real fix only if it starts biting.

---

## Part C — Emulation breadth (Stage 2)

All on the **existing CPU stack** — no GPU needed for these (2D is software; PS1/pcsx is software).

### C1. 2D systems — NES, SNES, Genesis, GB/GBC/GBA
- **ROM source:** `R:\Roms\Games` (full-name folders, `.zip`) or the cleaner **No-Intro** set
  (`L:\4 - Software\No-Intro ROM Collection…`, also `.zip`, canonical names → better box-art matching).
- **THE open question:** does CloudRetro load **zipped** 2D ROMs? libretro usually unzips at the frontend,
  but CloudRetro's loader may not. **Spike:** drop one zipped SNES ROM in and see if it boots. If yes →
  great. If no → either serve bare ROMs (extract, like we did for the SNES test) or zip-normalize at
  ingest.
- **Core→folder confinement:** every 2D `.zip` looks identical by extension, so each core MUST pin its
  `folder` to that system's directory (full name, e.g. `folder: "Super Nintendo Entertainment System"`),
  or NES-zips get loaded as SNES. (Today we sidestep this with a curated `D:\Arcade\roms\<short>` tree.)
- **mgba covers gb/gbc/gba** (3 folders, one lib) — verify multiple core entries can share `lib:
  mgba_libretro` with different `folder`s. Also: mgba's buildbot download failed once ("bad content
  length") — retry / pin a known-good core.
- **Ingest:** update `ArcadeSystems.All` (ArcadeIngestCommand.cs) to the real full-name folders + `.zip`
  extension, OR keep curating into the short-name `D:\Arcade\roms` tree. Decide per the "curate vs mount"
  call below.

### C2. PlayStation 1 — CHD
- **ROM source:** `F:\Emulation\roms\psx` (147 `.chd` games — the playable working set; NOT the 448 GB
  `.7z` master on L:, which needs extraction). CHD needs no zip gymnastics.
- Core `pcsx_rearmed`, `roms: ["cue", "chd", "m3u"]` (add `m3u` for the 8 multi-disc games).
- **PS1 BIOS** (`scph5501.bin`) — locate in `C:\Network Share\bios` or the Mega BIOS pack; place in the
  worker's libretro `system` dir (HLE fallback works but is lower-compat).
- Stage `D:\Arcade\roms\psx` (copy/junction a curated multiplayer set: Bishi Bashi, Twisted Metal, Micro
  Machines, Bomberman) or mount `F:` and pin `pcsx.folder`.

### C3. Arcade — FBNeo
- **ROMs:** `R:\Roms\Games\Arcade` (~36k) / `MAME`. **BIOS:** the **Neo-Geo/CPS** pack at
  `L:\…\Mega_Bios_Pack_Ver1.1` (it's arcade BIOS, remember — extract `.rar`/`.zip` → `neogeo.zip` etc. into
  the arcade ROM folder).
- fbneo is finicky (parent romsets, versions) → **per-title enablement**, start with a curated shortlist
  (Metal Slug, Street Fighter, Marvel vs Capcom), not a bulk switch.

### C4. Curate vs mount — a decision that shapes C1–C3
- **Curate (current approach):** copy/junction chosen games into `D:\Arcade\roms\<short-code>`; ingest +
  CloudRetro work unchanged. Pro: clean, controlled, no config churn. Con: manual selection, disk copies.
- **Mount the full collections:** point `/roms` at `R:\Roms\Games` and give every core a full-name
  `folder`; update the ingest classifier. Pro: whole library available. Con: config complexity, the fbneo
  36k-zip scan, cross-system extension collisions.
- **Recommendation:** stay **curated** for v1 (it's working and safe); revisit mounting only if you want
  the entire library exposed. Curation doubles as taste-making for a friends arcade anyway.

---

## Part D — Multiplayer + go-live (NOT blocked by A/B/C)

### D1. Multiplayer test — do this FIRST, it's the whole point
Two browsers / two accounts, one invite link, same room: verify both seats fill, the join flow works, and
both players drive the same shared game (couch co-op at a distance). This exercises the seat/bind/presence
code that's built but never run with 2 humans. Mario Kart 64 or Bomberman 64 (4-player) is the test.

### D2. Internet access — share links with friends
- Caddy block `arcade.carpouzis.com → localhost:2303` + CNAME → books (files already in `docker/arcade/`).
- Router **UDP 8443–8445** forward → Ziggy + Windows Defender inbound rule.
- Set `SITE_ORIGIN` to the real page origin (tighten from `*`) and `ZIGGY_PUBLIC_IP` to the public IP/DDNS.
- **Confirm inbound UDP actually reaches Ziggy** (the one genuine project-killer — CGNAT on the uplink for
  UDP). A phone on cell data → a UDP listener behind the forward answers this in minutes.

### D3. Production hardening
- `ArcadeTokenSecret` into prod via `MOVIETHEATER_APPSETTINGS_JSON` (follow the movietheater-secret
  checklist — a malformed secret has taken prod down before).
- Compose autostart with Docker Desktop; gateway `/healthz` watched alongside StreamGateway's; save dir in
  Ziggy backups; `coordinator.origin.userWs` locked to the site origin.
- **Commit + deploy:** the entire arcade (backend, frontend, gateway, docker/) is currently uncommitted in
  the working tree. Stage explicit paths (never `git add -A` — see memory), commit, and push to deploy.

---

## Recommended sequencing

1. **D1 — multiplayer test.** Cheap, proves the core value prop, exercises untested seat/bind code. Now.
2. **C1 + C2 — 2D + PS1 breadth.** Biggest library gain per effort, all on the working CPU stack. The
   zipped-2D spike is the gating unknown; PS1 CHD is a near-freebie.
3. **A — GPU rendering.** Fixes N64 triangles + unlocks upscaling and heavier systems. R&D-heavy (the
   GLX-vs-EGL unknown); do when N64 quality matters enough to justify it.
4. **B — NVENC.** Scalability; do when concurrency demands it (or bundle with A's GPU image).
5. **D2 + D3 — go-live.** When you're ready to hand friends a link.

## Open decisions for you
- **Ship now or polish first?** The 2D tier is genuinely done and clean. N64 is playable-but-rough. You
  could ship a "2D + rough N64" v1 to friends immediately (D1 → D2) and treat GPU/breadth as fast-follows.
- **Is the N64 GPU R&D worth it now,** or is rough-but-working N64 fine until you have appetite for the
  WSL2/D3D12 spike?
- **Curate vs mount** the ROM libraries (Part C4).
