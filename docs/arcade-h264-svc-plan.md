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

---

## Results — 2026-07-26 (shipped)

Shipped end to end: gst patch extended, DLL rebuilt + installed, fork commit `800dcac` pushed to
`github/movietheater-fork`, both worker configs carry `temporal-layers=2`, binaries deployed.
Every number below is measured on Ziggy (AD104 / RTX 4070 Ti, driver NVENC 13.1, GStreamer 1.28.4).

### P0 — discovery

**Caps (queried directly against nvEncodeAPI, no DLL involved):**

| cap | H.264 | AV1 |
|---|---|---|
| `SUPPORT_TEMPORAL_SVC` | 1 | 1 |
| `NUM_MAX_TEMPORAL_LAYERS` | **4** | 3 |
| `SUPPORT_HIERARCHICAL_PFRAMES` | 1 | 0 |

So the hardware was never the constraint — as with AV1, only the plumbing was.

**Mechanism — `hierarchicalPFrames`, NOT `enableTemporalSVC`.** Both were prototyped and the
resulting bitstreams are BYTE-IDENTICAL (188,780 bytes, same per-frame sizes, same pattern).
`enableTemporalSVC` costs two things and buys nothing:

- it *requires* `maxNumRefFrames >= 4`. Init fails with `NV_ENC_ERR_INVALID_PARAM` and the driver
  string *"Number of reference frames are lesser than minimum required for temporal / heirarchical
  coding"* at 1, 2 and 3 refs. That is a larger DPB for every decoder, for no bits saved.
- it prepends an **Annex-G SVC prefix NAL (type 14)** to every slice (+8..9 bytes/frame). It can be
  suppressed with `disableSVCPrefixNalu=1` (verified: 0 type-14 NALs after), but the cleanest way
  not to emit an Annex-G construct into a Constrained Baseline stream is not to ask for it.

Also measured: `numTemporalLayers=2` **alone is inert** — 300/300 frames `nal_ref_idc>0`, output
byte-identical to `temporal-layers=1`. `hierarchicalPFrames` is the switch; `numTemporalLayers` is
only the count. 3 layers initializes fine on this GPU but is deliberately unreachable (see below).

**NAL pattern (300 frames, `byte-stream, alignment=au`, one AU per file via `multifilesink`):**

```
temporal-layers=1:  AUD+P ... 300 AUs, ALL nal_ref_idc=3, 0 non-ref     236,574 bytes
temporal-layers=2:  AUD+P ... 300 AUs, strict alternation               188,780 bytes
                    layer pattern 0101010101010101...  (150 base / 150 TL1)
                    AU composition: 297x "AUD+P", 3x "AUD+SPS+PPS+IDR"
                    NO type-14 prefix NALs, no SEI, nothing else new
```

Cleanly separable, and separable by the ONE field the sender can read cheaply: base frames carry
`nal_ref_idc=3`, TL1 frames carry `nal_ref_idc=0`. Golden AU prefixes are checked into the fork as
`goldenIDR` / `goldenTL0` / `goldenTL1` in `pkg/network/webrtc/svc_test.go`.

**layerShare — 80, not the 53 the plan assumed.** Base-layer byte fraction by configuration:

| configuration | base share |
|---|---|
| 640x480 ball @5000 | 66.3% |
| 1280x1056 ball @8000 | 66.2% |
| 1280x1056 smpte @4000 | 81.6% |
| 1280x1056 ball @1500 | 82.2% |
| 640x480 snow @5000 | 82.8% |

The ~82% cluster is every case where CBR actually **binds**; 66% only appears when the content is
too cheap to spend the target rate. A ladder only ever operates in the binding regime, so the
per-codec entry is `layerShare["h264"][2] = {80, 100}` — just under the binding cluster, and
pessimistic-by-design (a too-high share cuts the room deeper and drops a layer sooner than needed,
which is the safe direction; a too-low share would leave a starved peer believed served).

**Consequence, stated plainly: H.264's ladder is REAL but SHALLOW.** A base-only H.264 peer still
costs ~80% of the room versus AV1's ~28%. The starve cap improves from `est` to only
`est/0.80 = 1.25x`, not the 1.9x the plan projected off the AV1 share. The win that survives is the
*first* response to congestion being "30 fps for that peer" instead of "fewer bits for everyone".

### P1 — gst patch

`0002-nvcodec-temporal-svc.patch` now carries a `gstnvh264encoder.cpp` section (the old bit-depth
hunk is folded into it). Property `temporal-layers`, **range-capped at 1-2**, default 1. The cap is
the enforcement point for the plan's load-bearing decision: at 3+ layers the middle layer is a
reference frame, `nal_ref_idc` stops separating the layers, and the sender — which identifies
droppable frames *by* `nal_ref_idc` — could not even see the difference. Config wiring also refuses
to engage when `bframes > 0` (logs a warning instead of silently building an unreadable pyramid).

Verified against the INSTALLED DLL, not the prototype:

- `nvh264enc` and `nvautogpuh264enc` (the d3d11 zero-copy element) both expose `temporal-layers`;
  `nvav1enc` keeps `temporal-layers` + `intra-refresh-period` + `intra-refresh-count`.
- `temporal-layers=1` output is **byte-identical** to the pre-change baseline (236,574 bytes, 300
  reference frames) — the property really does default OFF.
- `temporal-layers=2` reproduces the pattern above, byte-identical to the prototype build.

Backups: `libgstnvcodec.dll.pre-h264svc.bak` (the previous PATCHED build — the important one) beside
the older `.pre-intrarefresh.bak` (stock).

> WARNING — build-script gotchas, unchanged by this work: `build-gst-nvcodec-patched.ps1` needs
> **`patch.exe`** on PATH (`C:\Program Files\Git\usr\bin`), and `D:\msys64\usr\bin` must NOT come
> before System32 (msys `tar` fails the `-C D:\...` extract and the script throws "extract failed").
> Its running-worker guard also trips on the CAPTURE worker, which is never to be stopped; the
> install was therefore done by hand after the build, with the same verification the script does.

### P2 — fork (`800dcac`)

- `svc.go`: `h264TemporalID` (Annex B walk to the first VCL NAL, answer from `nal_ref_idc`;
  unparseable => 0 = "send it"), `SetVideoCodec`/`VideoCodec` plus a `temporalID(codec, frame)`
  dispatch, and `CLOUD_GAME_SVC_FORCE_LAYER` (read once at startup).
- `webrtc.go` `videoSender()`: dispatches by the room's codec instead of calling `av1TemporalID`
  unconditionally, and applies the force-layer override.
- `gstreamer.go`: `Codec()` — read from the same `VideoSettings()` lookup that selects the element,
  so the tagger can never disagree with the bitstream.
- `coordinatorhandlers.go`: sets codec and layers together; WARNs loudly when the force override is
  on (a knob that silently halves everyone's frame rate must announce itself).
- `abr.go`: `layerShare` is now `[codec][layers][l]`; `pickLayer` takes the share slice. An unknown
  codec/layer combination means no ladder, exactly as before SVC existed.
- Tests: golden H.264 AUs, 3-byte start codes, parameter-set runs, garbage inputs, cross-codec
  dispatch, and an H.264-specific `pickLayer` case.

### P3 — live verification (genesis, 960x672, theater.carpouzis.com, harness `arcade-diag`)

| check | result |
|---|---|
| h264 room logs the ladder | `Temporal SVC: 2 layers, h264` / `abr: start 3000 kbps (floor 1500, ceiling 5000, h264 temporal layers 2, base share 80%)` |
| h264 room quality | **60 fps**, 0 freezes, 0 dropped, 0 nack, 0 pli, decode 0.3-0.5 ms |
| ABR assigns a layer | `abr: peer layer 7 -> 1 (estimate 7354 kbps, room sends 3000)` |
| **base layer alone is decodable** (`CLOUD_GAME_SVC_FORCE_LAYER=0`) | **exactly 30 fps**, 0 freezes, 0 nack, 0 pli, decode 0.3-0.6 ms; presentation-interval histogram **437 of 449 in the 33 ms bucket** — an evenly paced 30 fps stream, not a stuttering 60 |
| AV1 regression | `Temporal SVC: 3 layers, av1`, base share 28%, 60 fps, 0 freezes — unchanged |

### What is NOT proven

- **The real thing: a congested tablet.** Everything above ran on a healthy LAN, so no peer was ever
  *forced* down a layer by congestion — the drop path was proven with the diagnostic override, not
  by a bad link. That test is Eric's. What to look for in `D:\ArcadeStorage\logs\glworker*.log`:
  `abr: peer layer 1 -> 0` lines (now possible in h264 rooms, previously impossible), and
  starve/cut lines becoming rarer and shallower than the 2026-07-26 session's `11370 -> 1964`.
- **Per-peer independence in an h264 room.** The force knob is room-wide, so "one peer at 30 while
  another keeps 60" was not demonstrated. The mechanism is per-peer by construction (each Peer owns
  its own `TrackLocalStaticSample` and its own `maxLayer`) and is already proven for AV1, but it has
  not been observed for h264.
- **Long-run quality of the layered encode.** The 20-25 s harness runs show no artifacts, but nobody
  has played a full session on a layered h264 stream. Non-reference frames are coded cheaper by
  construction, so a discerning eye may find every other frame slightly softer.

### Rollback (in increasing order of severity)

1. **Config only** — drop `temporal-layers=2` from the h264 `params:` in
   `D:\ArcadeStorage\worker-gl\config.yaml` and `worker-gl-2\config.yaml` (dated `.pre-h264svc-*`
   backups sit beside them) and recycle. The worker then reads 1 layer and never drops a frame; the
   DLL and binary are unchanged and AV1 is untouched.
2. **Binary** — `bin/worker.pre-h264svc.exe` -> `bin/worker.exe`.
3. **DLL** — `libgstnvcodec.dll.pre-h264svc.bak` -> `libgstnvcodec.dll` (workers stopped).
