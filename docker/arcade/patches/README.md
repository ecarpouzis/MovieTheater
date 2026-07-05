# CloudRetro image patches

Small source edits we own on top of the pinned CloudRetro commit (`13852a7`). CloudRetro cuts no
releases and exposes no plugin seam for these, so we patch the source before `docker build`. Applied
in `../README.md` → "Building the image".

Apply (from the CloudRetro checkout, after `git checkout 13852a7`):

```bash
git apply /path/to/docker/arcade/patches/0001-jit-scan-on-miss.patch
```

## 0001-jit-scan-on-miss.patch

`pkg/games/launcher.go` — `FindAppByName` now forces one (throttled) library `Scan()` and retries
when a game name isn't found, instead of failing immediately.

**Why:** the JIT ROM cache (`docs/arcade-jit-cache.md`) extracts a game's ROM into the read-only ROM
volume on demand, *after* the worker's boot-time scan. CloudRetro only rescans via fsnotify
(`WatchMode`), which does **not** reliably fire across a Windows-host → WSL2 bind mount — so without
this, a freshly-materialized game would 404 at launch. `Scan()` re-walks the directory tree and needs
no inotify event, so the retry always sees the new file. Scan is internally throttled, so the miss
path can't stampede.

Re-generate against a new SHA if it stops applying:
`git diff pkg/games/launcher.go > 0001-jit-scan-on-miss.patch`

## 0002-ipv4-singleport-mux.patch

`pkg/network/webrtc/factory.go` — the single-port WebRTC mux binds `"udp4"` (0.0.0.0) instead of
`"udp"` (dualstack `[::]`).

**Why:** under WSL2 mirrored networking the Windows→WSL UDP relay delivers into AF_INET sockets but
not AF_INET6 dualstack ones (TCP is relayed for both — which is why signaling worked while media
didn't). All our WebRTC clients are IPv4, so an IPv4-only bind costs nothing in production.
Note this alone did NOT fix same-host play — the other half is `.wslconfig`
`[experimental] hostAddressLoopback=true` + advertising the LAN IP (see docker-compose.gpu.yml).

## 0003-h264-nvenc.patch

`Dockerfile` — the minimal from-source GStreamer build adds the **nvcodec** plugin
(`-Dbad=enabled` + `-Dgst-plugins-bad:nvcodec=enabled`, plus the same docs/meson.build sed fix
plugins-good already needed). nvcodec dlopens `libcuda`/`libnvidia-encode` at runtime — no CUDA
SDK at build time. Enables GPU (NVENC) encoding — Part B of `docs/arcade-next-steps.md`.

## 0004-video-pipeline.patch

`pkg/worker/media/gstreamer.go` — the video-side media changes (one file, two features):
- Implements the (previously commented-out) `h264` case in the pipeline builder; element + tuning
  come entirely from `encoder.list` in config.yaml (we set `nvh264enc`; keyframe cadence via
  `gop-size`; a `videoconvert` bridges the shared I420 caps to NVENC's NV12-only sink).
- **Decouples push from pull** in the video worker: frames are now COPIED into GStreamer (freeing
  the emulator's reused frame buffer immediately) and encoded output is delivered via appsink
  callbacks on GStreamer's own thread — the same shape the audio path always had. Before, the
  worker pushed a frame and synchronously waited for its encoded output, so throughput was capped
  at 1/encode-round-trip and overflow frames were dropped SILENTLY (a `default:` case with a
  comment). The drop path now logs a warning ("video pipeline overloaded") — that silence cost a
  full night of diagnosis. The frame duration rides on the GstBuffer for RTP timestamps.

Selecting `encoder.video.codec: h264` in config.yaml turns NVENC on; setting it back to `vp8` is
the instant software fallback. pion already speaks `video/h264` — no WebRTC-side change.

## 0005-disk-control.patch

Multi-disc disc-swap for the one-card-per-game lobby (docs/arcade-dedupe-multidisc-plan.md, Phase 2).
Touches 6 files:
- `nanoarch/nanoarch.c` + `.h` — capture the core's `retro_disk_control_callback` and add
  `nano_disk_set_index(index)` = eject → `set_image_index` → insert (guarded: no-op without disk
  control or out of range).
- `nanoarch/nanoarch.go` — handle `RETRO_ENVIRONMENT_SET_DISK_CONTROL_INTERFACE` (13, previously
  unhandled → cores got no disk control); a `pendingDisk atomic.Int32` on `Nanoarch`; `Run()` drains
  it and applies the swap **on the emulator thread** before the core steps; `RequestDisk(i)` queues it.
- `frontend.go` / `caged.go` — plumb `RequestDisk` out to the app.
- `coordinatorhandlers.go` — a `"disc"` WebRTC data channel (same mechanism as `keyboard`/`mouse`);
  the browser sends a 1-byte target image index → `RequestDisk`. No-op for non-disc cores.

**Why:** CloudRetro never registered a disk-control interface, so pcsx_rearmed loading an `.m3u` could
not switch discs. This captures the interface and swaps on the emulator thread (disc cores —
pcsx/Sega CD/Saturn — are non-LibCo, so `Run()` is the correct thread; a LibCo disc core would need
the swap dispatched via `same_thread`).

**Also required (not in this patch):**
- `docker/arcade/config.yaml` — add `m3u` to the `roms` list of each disc core (`pcsx`, and
  segacd/saturn when added): `roms: ["m3u", "cue", "chd"]`, so the library scan sees `.m3u` playlists.
- Browser (`src/ui/src/Pages/Arcade/cloudRetroClient.js`) — open a negotiated `"disc"` data channel
  and send `[targetIndex]`; `ArcadeRoomPage` shows `Disc x/N` + Swap controls (disc count comes from
  the room descriptor, which the site fills from the game's `.m3u` disc rows).
- JIT (`ArcadeGateway/RomCache.cs`) — materialize the `.m3u` (extract ALL discs + write the playlist)
  on first play; one `ArcadeGame` row per multi-disc game keyed to `<game>.m3u`.

**Compile status:** written against `13852a7` and applies clean, but NOT yet compiled (the dev box has
no Go/cgo toolchain) — verify on the image build. Re-generate: `git diff > 0005-disk-control.patch`.

## 0006-hw-render-gl-only.patch

GL-only hw-render negotiation (fixes the flycast PC=0 boot GPF on the Windows GL worker;
docs/arcade-windows-worker.md). Apply AFTER 0005 (touches adjacent nanoarch.go context). Two files:
- `nanoarch/nanoarch.go` — answer `RETRO_ENVIRONMENT_GET_PREFERRED_HW_RENDER` with
  `RETRO_HW_CONTEXT_OPENGL` when the core is GL-allowed, and REJECT `SET_HW_RENDER` for any
  context type other than OPENGL/OPENGL_CORE (logged at warn).
- `graphics/rgfw.go` — treat `wglGetProcAddress` sentinel returns (1/2/3/-1) as NULL (Windows-only
  code path; inert on Linux).

**Why:** flycast probes hw-render APIs in order D3D11 → Vulkan → GL when the frontend doesn't state a
preference. nanoarch blind-accepted ANY context type, so Windows flycast committed to D3D11, its
`dx11_context_reset` silently bailed (no `GET_HW_RENDER_INTERFACE`), while `config::RendererType`
stayed OpenGL — first `retro_run` then called through never-resolved glsm GL pointers → call to
address 0. The Linux build accepted Vulkan instead (which does set RendererType), masking the same
latent bug. With this patch flycast goes straight to its GL path, matching the only context RGFW can
create (WGL on Windows, GLX in the image). Safe for mupen (asks for GL anyway) and 2D cores (gl
disabled → unchanged).

## 0007-timing-and-audio-fixes.patch

Three small runtime fixes found by playing real games on the Windows GL worker (applies after 0006):

- **`pkg/worker/media/gstreamer.go` — PSP gameplay-start crash (the big one).** PPSSPP emits a
  **zero-length audio batch** the instant real gameplay begins; `ProcessAudio` did `&audio[0]` on it
  → `panic: index out of range [0] with length 0` → the worker crashed and every PSP game
  "disconnected as soon as the game started". Now guards `len(audio) == 0`. Verified: Loco Roco 2
  runs into gameplay (60fps, no panic).
- **`pkg/worker/caged/libretro/nanoarch.go` — `SET_SYSTEM_AV_INFO` applied timing, not just geometry.**
  The handler ignored `av.timing`, so a core that changes fps/sample-rate mid-run (e.g. flycast on a
  cutscene) left the frontend pacing/timestamping at the old rate. Now updates `sys.av.timing` +
  `tickTime`, and logs `[AV-CHANGE]`.
- **`pkg/worker/caged/libretro/frontend.go` — the main loop recomputes `targetFrameTimeNs` when
  `VideoFramerate()` changes**, so pacing follows a mid-run fps change instead of running the new
  segment at the old rate. (Candidate fix for the reported DC-cutscene double-speed — the emulator
  itself was measured at a correct 1x, so the issue is presentation/pacing; not yet reproduced in
  automation, needs a live cutscene to confirm.)
