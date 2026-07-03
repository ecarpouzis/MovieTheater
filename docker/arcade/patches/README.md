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

Two edits enabling GPU (NVENC) video encoding — Part B of `docs/arcade-next-steps.md`:
- `Dockerfile` — the minimal from-source GStreamer build adds `-Dbad=enabled` +
  `-Dgst-plugins-bad:nvcodec=enabled` (plus the same docs/meson.build sed fix plugins-good already
  needed). nvcodec dlopens `libcuda`/`libnvidia-encode` at runtime — no CUDA SDK at build time.
- `pkg/worker/media/gstreamer.go` — implements the (previously commented-out) `h264` case in the
  video pipeline builder; the element + tuning come entirely from `encoder.list` in config.yaml
  (we set `nvh264enc`; keyframe cadence via `gop-size` since the vpx force-keyframe path doesn't
  apply to h264).

Selecting `encoder.video.codec: h264` in config.yaml turns it on; setting it back to `vp8` is the
instant software fallback. pion already speaks `video/h264` — no WebRTC-side change.
