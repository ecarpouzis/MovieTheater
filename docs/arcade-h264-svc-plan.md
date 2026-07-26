# H.264 temporal SVC — plan

**Goal:** give H.264 rooms the same graceful temporal ladder AV1 rooms have, so a weak peer
(tablet on Wi-Fi — the exact audience the codec picker sends to H.264) degrades to 30 fps instead
of dragging the room bitrate through starve/cut cycles.

**Why now (2026-07-26):** an older-tablet session (h264, ceiling 16000) ran with `temporal layers 1`
— no ladder — so every congestion event became a room-wide bitrate cut: 46 starves in one session,
`11370 -> 1964 kbps` in one tick. Four ABR fixes (opener scale, starve confirm 2-tick, 70% cut
clamp, congestion memory + probe hold — fork `5756866`..`23d7b3a`) made this *survivable*; a real
ladder makes it *graceful*: with 2 layers the starve cap is `est/0.53 ≈ 1.9x` instead of `est`, and
the first response to congestion is 30 fps for that peer, not fewer bits for everyone.

## Architecture (what exists — read these before coding)

| Piece | Where | State |
|---|---|---|
| Per-peer drop | fork `pkg/network/webrtc/webrtc.go` `videoSender()` (~line 438) | Skips `WriteSample` when `av1TemporalID(f.data) > maxLayer`. Per-peer packetizer ⇒ no seq gaps, no NACK storm. **Codec seam #1: the tagger is AV1-only.** |
| Layer count | `svc.go` `SetTemporalLayers` ← `coordinatorhandlers.go:213` ← `media/gstreamer.go:355` parses `temporal-layers=N` **from the encoder params string, codec-agnostically.** | Works for h264 the moment its params carry the token. |
| ABR consumption | `pkg/worker/abr.go` — `layers := webrtc.TemporalLayers()`, `layerShare = {1:{100}, 2:{53,100}, 3:{28,54,100}}` | Fully codec-independent (verified 2026-07-26). Nothing to change. |
| Encoder property | gst patch `docker/arcade/patches/gst/0002-nvcodec-temporal-svc.patch` — adds `temporal-layers` to **`gstnvav1encoder.cpp` only**. SDK 13 `nvEncodeAPI.h` already vendored (`USE_STATIC_SDK_VER 1`, needs NVENC 13.0 driver — Ziggy's 596.21 reports 13.0). | **Codec seam #2: no property on the h264 encoder.** |
| DLL build | `scripts/build-gst-nvcodec-patched.ps1` → `D:\msys64\ucrt64\lib\gstreamer-1.0\libgstnvcodec.dll` (stock kept as `.pre-intrarefresh.bak`) | Exists, documented. ⚠ re-run after any `pacman -Syu` touching gstreamer. |
| h264 pipeline | `media/gstreamer.go` — config `encoder: nvh264enc`; the GL zerocopy path swaps to `nvautogpuh264enc` (line ~853). Both elements register from **the same `gstnvh264encoder.cpp`** ⇒ one patch covers both. Caps: `byte-stream, alignment=au` (Annex B, AU-aligned). | Params live in `D:\ArcadeStorage\worker-gl\config.yaml` + `worker-gl-2\config.yaml` (per-worker dirs — diff before deploy; many stale `.bak` siblings). |

## Design decision: 2 layers for H.264, not 3 — this is load-bearing

`frame_num` increments only on **reference** frames. A 2-layer pyramid's TL1 frames are
non-reference (`nal_ref_idc==0`); dropping them leaves a stream indistinguishable from a 30 fps
encode — safe for every decoder. A 3-layer pyramid's TL1 frames ARE references: dropping them
creates `frame_num` gaps, which is decoder-dependent territory — and this room's SDP already
promises Constrained Baseline to Firefox's OpenH264, which the config block documents as fragile
(the profile=high revert). Chrome's own H.264 temporal path ships 2 layers for the same reason.
`layerShare[2] = {53, 100}` already exists. **Do not attempt 3 layers.** Also assert `bframes`
stays 0 — hierarchical P adds no reorder latency; B-frames would.

## Phases — each gates on the one before it

### P0 — Discovery (Ziggy, no deploys)
1. `NV_ENC_CAPS_SUPPORT_TEMPORAL_SVC` + `NV_ENC_CAPS_NUM_MAX_TEMPORAL_LAYERS` for **H.264** on the
   AD104 (the build script's probe covered AV1). Also confirm which SDK-13 `NV_ENC_CONFIG_H264`
   fields apply: `enableTemporalSVC`, `numTemporalLayers`, `hierarchicalPFrames`.
2. Decide the enable mechanism: prefer plain `hierarchicalPFrames=1` + `numTemporalLayers=2` IF it
   yields non-ref TL1 (nal_ref_idc==0) **without SVC prefix NALs (type 14)** — prefix NALs are an
   SVC-extension construct OpenH264 may not tolerate. If `enableTemporalSVC` emits prefix NALs,
   check whether they can be suppressed; if not, strip them at the tagger (sender-side) or fall
   back to hierarchicalPFrames alone.
3. Ground truth by encoding ~300 frames via `gst-launch` (patched-DLL prototype or raw NVENC
   sample) and dumping the per-frame NAL types + `nal_ref_idc` sequence. Expected 2-layer pattern:
   alternating idc>0 / idc==0. **Record the exact pattern in the results doc.**
4. Measure the real layerShare: bitrate fraction of idc>0 frames. If it lands outside 45–60%,
   plan a per-codec share entry instead of reusing `{53,100}`.

STOP and report if: caps say no h264 temporal SVC, or the pattern isn't cleanly separable.
Do not improvise an alternative encoder path without checking in.

### P1 — gst patch extension
- Extend `0002-nvcodec-temporal-svc.patch` with the `gstnvh264encoder.cpp` analog of the existing
  AV1 hunks: `PROP_TEMPORAL_LAYERS` (+ property install, getter/setter, and the
  `NV_ENC_CONFIG_H264` wiring chosen in P0). Mirror the AV1 hunk's style and comments.
- Rebuild via `scripts/build-gst-nvcodec-patched.ps1`. Back up the current DLL first
  (`libgstnvcodec.dll.pre-h264svc.bak`) — the running one is already patched (intra-refresh + AV1
  SVC); do NOT let the stock `.pre-intrarefresh.bak` be the only fallback.
- Verify: `gst-inspect-1.0 nvh264enc | grep temporal-layers`, then the P0 gst-launch dump against
  the installed DLL: 2-layer pattern present, and with `temporal-layers=1` byte-identical behavior
  to today (the property must default OFF).

### P2 — fork changes (branch `movietheater-fork`, push to `github` remote)
- `svc.go`: add `h264TemporalID(frame []byte) int` — Annex B walk, return 1 when the AU's VCL NALs
  (types 1/5) carry `nal_ref_idc==0`, else 0. Unparseable → 0 ("send it"), mirroring
  `av1TemporalID`'s degrade-to-safe. If P0 chose prefix-NAL stripping, do it here and document why.
- `videoSender()`: dispatch by the room's codec — plumb the active codec (from the media pipe /
  `coordinatorhandlers`) into the peer or a package-level setter, same pattern as
  `SetTemporalLayers`. Do NOT sniff the bitstream per-frame to guess codec.
- Config: add `temporal-layers=2` to the h264 `params:` in BOTH `D:\ArcadeStorage\worker-gl\config.yaml`
  and `worker-gl-2\config.yaml` (timestamped `.bak` first, diff after edit). `TemporalLayers()`
  then reports 2 with zero further code.
- Unit tests: golden Annex B AUs (captured in P0) for `h264TemporalID` — ref frame, non-ref frame,
  IDR, unparseable garbage. Follow `parse_test.go` conventions in `pkg/worker/cheevos` for style.
- Keep a diagnostic escape hatch: env `CLOUD_GAME_SVC_FORCE_LAYER` (int, unset = off) that caps
  every peer's layer — this is how base-layer decodability is proven live without congestion.

### P3 — deploy + honest verify (worker log, not vibes)
- Deploy dance (per the arcade skill): `curl localhost:8000/status` must show no live room —
  **Eric plays in the evening; check every time, wait if occupied.** Disable the two GL Worker
  tasks + Watchdog, stop watchdog loop FIRST, then runner loops, then `worker.exe`; swap binary
  (keep `worker.pre-h264svc.exe`); DLL already swapped in P1 (workers were stopped then too);
  re-enable. Capture worker (8448) untouched.
- Verify ladder up: force an h264 room via the test-roms harness
  (`page.evaluate` → `localStorage` quality key with `codec:"h264"` before creating the room —
  see `ArcadePage.js` `QUALITY_KEY`); assert the log shows `abr: start ... temporal layers 2`.
- Verify drop safety: same room with `CLOUD_GAME_SVC_FORCE_LAYER=0` on the worker — browser must
  show a clean ~30 fps (harness stats fps ≈ 30, freezes 0, decode errors 0). Then unset and
  confirm 60.
- Verify AV1 regression: default room still logs `temporal layers 3`, 60 fps.
- Verify per-peer independence if feasible (arcade-mp.mjs two-browser; cap one peer via the env
  knob is room-wide, so this may be observational only — note what was and wasn't proven).
- The REAL proof — a congested tablet — is Eric's to run. Leave the log breadcrumbs
  (`peer layer 1 -> 0` lines now possible in h264 rooms) and say exactly what to look for.

### P4 — ship the artifacts
- Fork: commit + push `movietheater-fork` (style: `feat(abr)`/`feat(svc)` with measured evidence
  in the body, `Co-Authored-By: Claude` + session trailer, as recent fork commits do).
- Site repo: regenerate `docker/arcade/patches/fork.patch` via `scripts/export-arcade-fork.ps1`
  (UCRT64 env: `PATH=D:\msys64\ucrt64\bin`, `PKG_CONFIG_PATH=D:\msys64\ucrt64\lib\pkgconfig`,
  `CGO_ENABLED=1`, `GOPATH=D:\Arcade\build\go`, `GOCACHE=D:\Arcade\build\gocache`) — it must
  report "applies cleanly" AND "builds from the patch alone". Commit the regenerated patch + the
  extended gst patch together. Stage files EXPLICITLY — never `git add -A`.
- Results: append a dated section to THIS doc (measurements, the NAL pattern, layerShare numbers,
  what was/wasn't proven live).

## Ground rules (hard)
- No worker restarts while `/status` shows an occupied room. Re-check immediately before acting.
- Back up every file you replace (binary, DLL, config) with a dated suffix; never overwrite a
  previous backup.
- The capture worker (Worker 3, port 8448) runs its own binary + config — leave it alone.
- Fork pushes go to the `github` remote, branch `movietheater-fork`. Site repo pushes to
  `origin master` — which AUTO-DEPLOYS the site; only push there when intended.
- If a verify step fails, roll back (binary/DLL/config backups) before investigating at leisure.
- Judge results from `D:\ArcadeStorage\logs\glworker*.log` and harness stats — never from
  headless-Chrome smoothness impressions (arcade skill rule).
