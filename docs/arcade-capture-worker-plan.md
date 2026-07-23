# Arcade capture lane — generic desktop capture/encode worker (heavy-lane H5)

Planned 2026-07-11. Executes the H5 slot of `docs/arcade-heavy-lane-plan.md` (supersedes its
"Apollo multi-client" sketch — this is the better experiment). Goal: **heavy titles (yuzu/RPCS3/
shadPS4, and eventually the Steam library) playable in the browser tab** through the existing
CloudRetro/WebRTC arcade, with per-seat internet multiplayer pads — the thing the Moonlight lane
structurally cannot do. The Moonlight/Artemis lane stays the premium low-latency path; this lane
is the zero-install path.

This document is written to be executed without re-deriving anything: every byte layout, pipeline
string, file:line anchor, and trap below was verified against the live source on 2026-07-11.
Worker tree = `D:\Arcade\build\cloud-game-gl` (pinned CloudRetro `13852a7` + patches 0001–0036).
Repo = `F:\Work\MovieTheater`.

---

## 0. Verdict and shape

CloudRetro's worker was built around a small app interface (`pkg/worker/caged/app/app.go`) —
libretro is just the one registered implementation. We add a second implementation, **the capture
mod**: instead of running a core, it launches a native Windows program via the heavy lane's
existing prepare→attach→finish gateway contract, captures the desktop with GStreamer's
`d3d11screencapturesrc` (Windows Graphics Capture), feeds frames/PCM into the **unchanged**
encode/WebRTC pipeline, and turns browser pad packets into **ViGEm virtual Xbox 360 pads**.

Reuse inventory (all verbatim, no changes): coordinator, signaling, Pion WebRTC + all our patches
(ABR 0021/0025/0026, AV1 0024, per-room codec 0036, pacing 0028, PLI 0029, stable mux 0034,
heartbeat 0035), the site room/seat/heartbeat model, ClaimSeat local-multiplayer → remote pads,
HeavyLock/HeavyStager/HeavyVault/heavy descriptors, the streamed-pad guard, box art, age gates.

New code: one Go package (the capture mod) + a ~10-line branch in the worker, a zone param
threaded through site+gateway, a `userId` param on gateway prepare, card UX, one worker task.

---

## 1. Verified environment facts (2026-07-11, Ziggy)

- GStreamer **1.28.4** (MSYS2 UCRT64, `D:\msys64\ucrt64\bin` — the exact runtime the worker
  tasks run against). Elements PRESENT (checked with `gst-inspect-1.0 <el> --exists`):
  `d3d11screencapturesrc`, `d3d12screencapturesrc`, `wasapi2src`, `wasapisrc`, `nvav1enc`,
  `nvautogpuav1enc`, `nvh264enc`, `nvautogpuh264enc`, `d3d11convert`, `d3d11download`,
  `cudaupload`, `opusenc`.
- `nvautogpuav1enc` sink caps accept `video/x-raw(memory:D3D11Memory)` in
  NV12/RGBA/BGRA/BGRx/P010 — a fully GPU-resident capture→encode path exists (kept as the P4
  optimization; v1 uses one GPU-side downscale + one download, §4.3).
- `d3d11screencapturesrc` properties: `monitor-index` (−1 = primary), `monitor-handle`
  (HMONITOR), `show-cursor`, `crop-x/y/width/height`.
- `wasapi2src` properties: `loopback` (render-endpoint loopback), `loopback-mode`
  (`include-process-tree` = one process + children), `loopback-target-pid`,
  `loopback-silence-on-device-mute`, `low-latency`.
- **ViGEmBus is installed and OK** ("Nefarius Virtual Gamepad Emulation Bus", PnP status OK) —
  Apollo already streams pads through it. No HidHide.
- ⚠ **SudoVDA trap, observed live**: a background session on Ziggy saw primary display
  `\\.\DISPLAY33`, **600×1232** — a SudoVDA *virtual* display (Apollo creates per-client virtual
  displays; display numbers churn). "Capture the primary monitor" is NOT stable on this box.
  The capture worker must enumerate monitors at boot, log them, and pin the **physical** monitor
  via config (`monitorIndex`, resolved to an HMONITOR at pipeline build).
- Deterministic room ids (already minted by the site for every room):
  `sv-{userId}-{gameId}-{slotId}-{system}___{gameKey}` — `ArcadeSaveId.Mint`,
  `src/MovieTheater.Core/ArcadeSaveId.cs:24`. The capture worker parses the owner's userId and
  the heavy app id straight out of its own room id. No protocol changes needed for identity.

---

## 2. Architecture

```
Browser (same SPA room page, same cloudRetroClient.js)
  │ control API            │ signaling /w/{token}&zone=capture       ║ WebRTC media+input
  ▼                        ▼                                         ║
ArcadeController      ArcadeGateway :2303 ──────────────┐            ║
 CreateRoom:           - zone now passes through        │            ║
  lane=heavy +         - skips retro save seed for      │            ║
  gameKey ⇒ capture      system=="capture"              │            ║
  room, zone=capture   - /heavy/prepare gains ?userId=  │            ║
                                                        ▼            ▼
                      coordinator :8000  ──assigns──▶  capture worker (task 3, UDP 8448, zone=capture)
                       (zone-aware already:             │  caged mod "capture":
                        Worker.In(region))              │  1. parse room id → userId + heavyAppId
                                                        │  2. POST gateway /heavy/prepare?userId= (lock+seed)
                                                        │  3. swap input profile, create ViGEm pad(s)
                                                        │  4. launch exe (job object), POST /heavy/attach
                                                        │  5. d3d11screencapturesrc → VideoCb ┐ unchanged
                                                        │     wasapi2src(pid) → AudioCb       ├ GStreamer→
                                                        │     pad packets → ViGEm XUSB        ┘ NVENC→Pion
                                                        └─ room close → kill tree, restore profile,
                                                           POST /heavy/finish (harvest → vault → site)
```

One capture room at a time (it IS the desktop); the two retro workers are untouched. Retro rooms
keep working exactly as before — they just start saying `zone=main` explicitly.

---

## 3. Locked design decisions (and why)

1. **Second caged mod, same worker binary.** `caged.Manager` is a map of `ModName → app.App`
   (`pkg/worker/caged/caged.go:13-43`); we register `Capture ModName = "capture"` next to
   `Libretro`. All workers run the same `worker.exe`; only the capture worker's config enables
   the mod. No fork, no second binary to deploy.
2. **Discriminate by `System == "capture"`, not by name prefix.** The library already derives a
   game's `System` from its file extension via `Emulator.GetEmulator`
   (`pkg/config/emulator.go:114-130`). We add a pseudo-core `capture:` with `roms: ["capture"]`
   to the capture worker's yaml and drop **stub files** `<heavyAppId>.capture` into its library
   dir. `FindAppByName` then resolves capture games with zero library-code changes, and the
   branch in `HandleGameStart` tests `gameInfo.System == "capture"`.
3. **Zone routing, zero coordinator changes.** The coordinator is already zone-aware:
   `Worker.In(region)` at `pkg/coordinator/worker.go:136` is `region == "" || region == w.Zone`,
   and `find1stFreeWorker(zone)` filters by it (`hub.go:286`). Retro workers already register
   `CLOUD_GAME_WORKER_NETWORK_ZONE="main"` (`scripts/run-arcade-glworker.ps1:66`). The gateway
   currently *strips* `zone=` (one-pool migration, `Program.cs` WsTransformer regex ~:565) — we
   stop stripping, the site sends `zone=main` for retro rooms and `zone=capture` for capture
   rooms. ⚠ Empty zone matches ANY worker — retro rooms MUST start saying `main` explicitly or
   they can land on the capture worker. Misrouting is additionally self-guarding: each worker's
   library only contains its own games, so `FindAppByName` fails fast on a wrong-zone start.
4. **Worker-driven lifecycle through the existing gateway heavy endpoints.** The worker is the
   Interactive-desktop process (GL workers run as Interactive principals —
   `scripts/register-arcade-glworker-task.ps1:51` — and a launched game needs the interactive
   desktop), and it is the only component that knows definitively when the room closes
   (`Room.Close → HandleClose → CloseRoom t=202`). So the capture app calls
   `POST /heavy/prepare/{appId}?client=capture-room&userId={uid}` (lock + vault seed),
   `POST /heavy/attach/{appId}` (PID), and `POST /heavy/finish/{appId}` (release + harvest).
   HeavyLock/HeavyVault reuse is 100%; the Apollo lane and the capture lane exclude each other
   through the same lock.
5. **Owner identity from the room id, not HeavyClient.** Capture rooms are site-authenticated;
   `payload.UserId` is in the capability token and baked into the deterministic room id. The
   worker parses `sv-{u}-{g}-…` and passes `?userId=` to prepare; the gateway skips the
   HeavyClient device lookup when the param is present (it is InternalAuth-gated anyway).
6. **v1 video = "design A": GPU downscale, one download, existing appsrc pipeline.** The capture
   app runs its own small GStreamer pipeline ending in `appsink` and pushes CPU BGRA frames
   through the normal `SetVideoCb → ProcessVideo → appsrc` path. This keeps EVERY media behavior
   (per-room codec 0036, ABR bitrate property on `video_enc`, forced keyframes in `ProcessVideo`,
   keyframe-on-join 0012, PLI 0029, pacing) working with zero media-package changes. Frames are
   downscaled to the encode size **on the GPU before download**, so the copy cost is one 1080p
   BGRA memcpy per frame (~8 MB, ~500 MB/s at 60 fps — trivial for the 13700K). The fully
   zero-copy `nvautogpuav1enc(memory:D3D11Memory)` variant is P4: it bypasses `ProcessVideo`,
   so forced-keyframe/ABR plumbing must be re-routed (GstForceKeyUnit events + direct property
   sets) — do it only if profiling demands it.
7. **Pads are ViGEm X360 targets, one per occupied seat.** RetroPad packets (14-byte LE) map to
   `XUSB_REPORT`. This matches the input-profile work already shipped: yuzu's streamed profile
   binds Player 1 to "Xbox 360 Controller", identical for Apollo pads and capture pads.
8. **Input profile swap moves to Go** (same contract, same files). The worker reads the SAME
   `D:\ArcadeStorage\heavy\profiles\profiles.json` and applies the same semantics as
   `heavy-launch.ps1` (backup current `player_0_*` block to `<streamedProfile>.restore`, swap in
   the streamed profile, restore on finish, heal leftover `.restore` on next launch). Both lanes
   stay mutually crash-healing because they share the marker files.
9. **Steam is P3, not v1.** Launching is easy (`steam://rungameid/<appid>` or
   `steam.exe -applaunch`), lifetime is not (Steam forks; must watch a named child exe).
   Kernel anti-cheat multiplayer titles are explicitly out of scope/no-promises.

---

## 4. The worker: capture caged mod

New package: `pkg/worker/caged/capture/` (new files — keeps patch 0037 mostly additive).

### 4.1 Registration + the branch point

- `pkg/worker/caged/caged.go`: add `const Capture ModName = "capture"`; in `Manager.Load`
  (:34-43) add the capture case (constructed from a new `config.Capture` section; only loaded
  when `conf.Capture.Enabled`).
- `pkg/worker/worker.go:38`: after `manager.Load(caged.Libretro, conf)`, conditionally
  `manager.Load(caged.Capture, conf)`.
- **The single app-selection branch point is `pkg/worker/coordinatorhandlers.go:163`**
  (`app := room.WithEmulator(w.mana.Get(caged.Libretro))`). Everything from :164-232 is coded
  against the concrete `*libretro.Caged` (`ReloadFrontend`, `SetSessionId`,
  `EnableCloudStorage`, `EnableRecording`, `SetRoomCheats`, `Load`, `VideoChangeCb`,
  `ViewportRecalculate`) — do NOT try to make the capture app satisfy those. Branch instead:

```go
if gameInfo.System == "capture" {
    capApp := w.mana.Get(caged.Capture).(*capture.App)   // nil ⇒ misroute; return error packet
    if capApp == nil { /* worker not capture-enabled: return api.EmptyPacket + log */ }
    if err := capApp.LoadApp(gameName, rq.Rid); err != nil {  // gameName = heavyAppId (stub name)
        // 409 from gateway prepare (lane busy / not staged) lands here → fail the start cleanly
        c.log.Error().Err(err).Msg("capture prepare failed")
        return api.EmptyPacket
    }
    r.SetApp(capApp)             // room.Room.SetApp accepts any app.App (room.go:109)
} else {
    app := room.WithEmulator(w.mana.Get(caged.Libretro))
    // ...existing :164-232 block unchanged...
}
// the geometry/media block :234-243 is generic APART from two lines — see below
```

- The media-setup block (:234-243) reads `r.App()` interface methods plus **two things to
  special-case**: `coreConf := w.conf.Emulator.GetLibretroCoreConfig(game.System)` (returns a
  zero `LibretroCoreConfig` for "capture" — `MaxThreads=0`/`VFR=false` are both correct
  defaults, so it can stay) and `app.FPS()` — `FPS()` is NOT on the `app.App` interface, it's a
  concrete method. Give `capture.App` an `FPS() float64` method returning the configured capture
  fps (60) and call it through a tiny local interface `interface{ FPS() float64 }`.
- Input wiring (:267-283) is already fully interface-driven (`r.App().Input(...)`,
  `r.App().KbMouseSupport()`) — no changes; keyboard/mouse channels appear automatically when
  `KbMouseSupport()` is true.
- `StartGameResponse` (:285-305): the AV-info block uses `AspectEnabled()/AspectRatio()/
  Flipped()/Rotation()` — all interface methods we implement. `RegisterRoom`/`CloseRoom`
  untouched.

### 4.2 `capture.App` — interface contract (exact values)

| Method | Return / behavior |
|---|---|
| `Init()` | connect ViGEm client; enumerate monitors (log each: index, device, WxH, primary flag); resolve `monitorIndex` → HMONITOR; load `profiles.json`; nil error |
| `AudioSampleRate()` | `48000` (pipeline caps pin it — avoids the resample stage, `gstreamer.go:614-625`) |
| `ViewportSize()` | configured encode input size, default `1920, 1080` |
| `Scale()` | `1.0, "nearest-neighbour"` (scaledW/H == W/H; capture already downscaled on GPU) |
| `AspectRatio()` | `float32(w)/float32(h)` (16:9) |
| `AspectEnabled()` | `true` (client render hint only) |
| `Flipped()` | `false` |
| `Rotation()` | `0` |
| `PixFormat()` | `1` (= BGRA, 4 bpp — the map at `gstreamer.go:112-135`; DXGI/WGC native order) |
| `FPS()` (concrete) | configured fps, default `60` |
| `KbMouseSupport()` | from the stub/descriptor (`input.kbm == true`); v1/v2 return `false` |
| `SetVideoCb/SetAudioCb` | store; invoked from the capture pipelines' appsink callbacks |
| `SetDataCb` | no-op (room never wires it) |
| `Start()` | start video+audio pipelines (idempotent) |
| `Input(port, device, data)` | §4.5 |
| `Close()` | §4.6 — MUST be idempotent and finish well under 10 s (the media-teardown watchdog `TerminateProcess`es the worker at 10 s, `gstreamer.go:346-360`); runs on the coordinator pump goroutine |

Video contract reminders (from `ProcessVideo`, `gstreamer.go:445-483`): `Frame.Data` length must
be ≥ `Stride*H` (it slices exactly that); frames are **copied** at push, so the appsink buffer
can be unmapped immediately; per-frame `Duration` must be real (`time.Second/60`) — it drives
RTP timestamps. Audio contract: interleaved **S16LE stereo** at `AudioSampleRate()`; empty
batches tolerated.

### 4.3 Video capture pipeline (inside the capture app)

The worker already links GStreamer via cgo; reuse the same binding layer the media package uses
(`pkg/worker/media/gstreamer.go` imports an internal `gst` wrapper — check its import path and
use the same package; do NOT introduce a second binding). Build:

```
d3d11screencapturesrc name=cap_src monitor-handle=<HMONITOR> show-cursor=false
  ! video/x-raw(memory:D3D11Memory),framerate=60/1
  ! d3d11convert
  ! video/x-raw(memory:D3D11Memory),format=BGRA,width=1920,height=1080
  ! d3d11download
  ! queue max-size-buffers=2 leaky=downstream
  ! appsink name=cap_sink sync=false max-buffers=2 drop=true
```

- In the appsink callback: map the buffer with `gst_video_frame_map` (or read the
  `GstVideoMeta`) to get the **real stride** — `d3d11download` output can be row-padded; never
  assume `W*4`. Emit `app.Video{Frame: RawFrame{Data, Stride, W:1920, H:1080},
  Duration: time.Second/60}` via the stored VideoCb.
- **WGC only delivers frames on screen change.** A static screen starves the encoder: joiners
  would decode nothing (keyframe-on-join has no frame to bind to). Keep a copy of the last
  frame and re-push it if >500 ms pass without a fresh one (2 Hz ticker). This also keeps
  `ProcessVideo`'s forced-keyframe cadence alive.
- `framerate=60/1` on the src caps makes WGC pace at 60; keep encode fps == capture fps.
- Cursor: `show-cursor=false` for pad games (yuzu/RPCS3 render no cursor and a remote cursor is
  noise). Revisit at P3 for mouse games (`show-cursor=true` + pointer lock).
- Monitor selection: `monitor-handle` from the boot-time enumeration (config `monitorIndex`
  indexes the ENUMERATED PHYSICAL monitors, not GDI display numbers — SudoVDA trap §1).
- Do NOT set colorimetry here; the media pipeline's `video_caps` stage owns it
  (`1:3:5:1` full-range, config).

### 4.4 Audio capture pipeline

```
wasapi2src name=aud_src loopback=true low-latency=true
           loopback-mode=include-process-tree loopback-target-pid=<gamePid>
  ! audioconvert ! audioresample
  ! audio/x-raw,format=S16LE,rate=48000,channels=2,layout=interleaved
  ! appsink name=aud_sink sync=false max-buffers=4 drop=true
```

- Start it AFTER attach (needs the PID). `include-process-tree` covers children (yuzu spawns
  helpers).
- Fallback (config `audioScope: "system"` or preroll failure): drop the two `loopback-*-pid`
  properties → whole-endpoint loopback. Acceptable because the heavy lock means nothing else
  intentional is playing audio.
- Push `app.Audio{Data: <mapped bytes>, Duration: 0}` — duration is ignored for audio
  (`gstreamer.go:384`, opus `frame-size` drives timing).

### 4.5 Input: RetroPad → ViGEm XUSB

Packet (verified `pkg/worker/caged/libretro/nanoarch/input.go:59-84` + shim
`cloudRetroClient.js:217-222`): `[BTN:u16 LE][LX:i16][LY:i16][RX:i16][RY:i16]([L2:i16][R2:i16])`
— the browser currently sends the 10-byte form (buttons + 4 axes); L2/R2 analog bytes are
optional. `port` = seat index (0..3), passed by the room wiring, NOT in the bytes.

RetroPad bit → XUSB mapping (`XUSB_REPORT.wButtons` values in parens):

| RetroPad bit | XUSB button |
|---|---|
| 0 B | A (0x1000) |
| 1 Y | X (0x4000) |
| 2 SELECT | BACK (0x0020) |
| 3 START | START (0x0010) |
| 4 UP | DPAD_UP (0x0001) |
| 5 DOWN | DPAD_DOWN (0x0002) |
| 6 LEFT | DPAD_LEFT (0x0004) |
| 7 RIGHT | DPAD_RIGHT (0x0008) |
| 8 A | B (0x2000) |
| 9 X | Y (0x8000) |
| 10 L | LEFT_SHOULDER (0x0100) |
| 11 R | RIGHT_SHOULDER (0x0200) |
| 12 L2 | bLeftTrigger = 255 (digital) or analog byte from L2 int16 (`v>>7`) |
| 13 R2 | bRightTrigger likewise |
| 14 L3 | LEFT_THUMB (0x0040) |
| 15 R3 | RIGHT_THUMB (0x0080) |

Sticks: `sThumbLX = LX`, `sThumbLY = -LY` (**invert Y** — Gamepad API is down-positive, XInput
is up-positive; same for RY), clamp −32768→−32767. `XUSB_REPORT` layout:
`{WORD wButtons; BYTE bLeftTrigger; BYTE bRightTrigger; SHORT sThumbLX, sThumbLY, sThumbRX,
sThumbRY}`.

ViGEm integration — pick at P1 start, in order of preference:
1. An existing maintained pure-Go ViGEmBus client (evaluate `github.com/openstadia/go-vigem` or
   similar: needs connect + x360 target alloc/add/update/remove). Pure Go keeps the UCRT64
   build simple.
2. A minimal cgo binding to `ViGEmClient` (7 functions: `vigem_alloc/connect/free`,
   `vigem_target_x360_alloc/add/remove`, `vigem_target_x360_update`), vendored + built in the
   UCRT64 shell.
Do NOT hand-roll the bus IOCTLs from scratch.

Seat model: create the port-0 pad in `LoadApp` **before launching the emulator** (yuzu binds at
boot — same timing Apollo relies on). Ports 1..3: create lazily on the first packet from that
port, cap at the stub's `maxPads`; remove all targets in `Close()`. ClaimSeat input-only
sessions already deliver per-port packets — remote multiplayer needs zero extra plumbing.

Keyboard/mouse (P3, only when `KbMouseSupport()`): the room auto-opens `"keyboard"`/`"mouse"`
DataChannels (`coordinatorhandlers.go:271-274`). Formats (verified): keyboard = **7 bytes
BIG-endian** `[RETROK keycode:u32][pressed:1][mods:u16]` (`input.go:109-131`); mouse = tag byte
`0`=move `[dx:i16 BE][dy:i16 BE]` (relative), `1`=buttons `[bitmask:1]` L=1 R=2 M=4
(`nanoarch.go:552-565`, `input.go:153-169`). Inject via `SendInput` (scancode-mapped keys,
`MOUSEEVENTF_MOVE` relative deltas). The shim has NO sender for these today — P3 adds pointer
lock + key capture to `cloudRetroClient.js` using these exact formats (crib the
`KeyboardEvent.code → RETROK` table from upstream cloud-game's web client).

### 4.6 Lifecycle (LoadApp / process / Close)

`LoadApp(heavyAppId, roomId string)`:
1. Parse roomId: must match `sv-{userId}-{gameId}-{slot}-capture___{heavyAppId}` (Go port of
   `ArcadeSaveId.TryParse` — split on FIRST `___`, prefix splits on `-` into 4). No match ⇒
   refuse (capture rooms are only ever site-minted).
2. `POST {gateway}/heavy/prepare/{heavyAppId}?client=capture-room&userId={userId}` with header
   `X-Arcade-Internal-Secret` (secret file path from config). 409 ⇒ return the error (lane busy
   at the lock, or not staged). Response carries `{exe, args, workingDir, rom}`.
3. Swap input profile (Go port of `Apply-StreamedInputProfile` from
   `scripts/heavy-launch.ps1`: profiles.json keyed by exe leaf; heal leftover `.restore`;
   backup live `player_0_*` block; write streamed block; UTF-8 no BOM).
4. Create ViGEm pad port 0.
5. Launch: `exec.Command` with parsed args + workingDir; assign to a **Windows Job Object with
   `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`** so the game tree dies with the worker no matter what.
   Record PID. Poll (≤30 s) for the process's main window and `SetForegroundWindow` (fullscreen
   focus for input).
6. `POST /heavy/attach/{heavyAppId}` body `{pid}`.
7. Start audio pipeline (PID-scoped); video pipeline starts in `Start()`.
8. Spawn the exit-watcher goroutine: on process exit while the room is still open, immediately
   (a) push black frames (privacy: don't stream the bare desktop), (b) restore the input
   profile, (c) `POST /heavy/finish` (harvest NOW — don't wait for stragglers to leave; finish
   is re-entrant by design, the second call at Close is a safe no-op).

`Close()` (idempotent, <10 s, called BEFORE `media.Destroy()` — `room.go:118-133`):
1. Stop both capture pipelines (SetState NULL with a short timeout).
2. Kill the game's job object if still alive (nobody can see it anymore). Give it 2 s of
   graceful `WM_CLOSE` first ONLY if trivial; do not block — the vault does not need a clean
   exit (dir-zip of save files, and step 3 harvests after death like the Apollo lane does
   within the 90 s PID grace).
3. Restore the input profile.
4. Remove ViGEm targets.
5. `POST /heavy/finish/{heavyAppId}` (release ⇒ harvest ⇒ site mirror — existing gateway code).
Watchdog note: keep a 5 s heartbeat log line ("capture tick: frames=N pads=M") — the GL-worker
watchdog's check C treats "busy room + silent log" as a wedge and recycles the worker
(`scripts/watch-arcade-glworkers.ps1`, coordinator `/status` patch 0033).

### 4.7 Worker config

New `config.Capture` section (add to `pkg/config/worker.go` WorkerConfig + yaml):

```yaml
capture:
    enabled: true
    gatewayUrl: http://localhost:2303
    secretFile: D:/ArcadeStorage/heavy/gateway-secret.txt
    profilesFile: D:/ArcadeStorage/heavy/profiles/profiles.json
    monitorIndex: 0          # index into ENUMERATED PHYSICAL monitors; log the pick at boot
    width: 1920
    height: 1080
    fps: 60
    audioScope: process      # process|system
```

New conf dir on Ziggy: `D:\ArcadeStorage\worker-capture\config.yaml` = copy of
`docker/arcade/config.worker-gl.yaml` (same encoder/webrtc sections) MINUS the libretro core
list, PLUS the `capture:` section, PLUS the pseudo-core:

```yaml
emulator:
    libretro:
        cores:
            list:
                capture:
                    lib: capture          # never dlopened — the branch fires first
                    roms: ["capture"]
library:
    basePath: "D:/ArcadeStorage/heavy/capture-stubs"
    watchMode: true
```

Repo copy: `docker/arcade/config.worker-capture.yaml` (authoritative, deploy = cp, same
convention as config.worker-gl.yaml).

Stub files (Ziggy-local, NOT in repo — same rule as heavy descriptors):
`D:\ArcadeStorage\heavy\capture-stubs\<heavyAppId>.capture`, content irrelevant (empty file);
the FILENAME is the join key: it must equal the heavy descriptor id AND the ArcadeGame row's
`CloudRetroGameKey`. One stub per capture-enabled title (not every heavy app: Big Box no,
Bloodborne yes, etc.).

### 4.8 Patch packaging

Single feature patch `docker/arcade/patches/0037-capture-app.patch` (mostly new files under
`pkg/worker/caged/capture/` + the small diffs in `caged.go`, `worker.go`,
`coordinatorhandlers.go`, `pkg/config/worker.go`, `pkg/config/config.yaml`). Regenerate per
`patches/README.md`: apply 0001–0036 to a clean `13852a7`, commit, apply this feature,
`git diff --cached`. Build: UCRT64 shell (`PATH=D:\msys64\ucrt64\bin`, CGO on, Go 1.26.4),
`go build -o bin/worker.exe ./cmd/worker`. Go module deps (ViGEm lib) vendor as needed.

---

## 5. Gateway changes (`src/MovieTheater.ArcadeGateway`)

1. **Zone passthrough**: `WsTransformer` currently strips `zone=` (Program.cs ~:565). Preserve
   it verbatim instead. (Site-side, retro rooms start sending `zone=main` in the same deploy —
   ordering: gateway first, then site; a passing `zone=` with no site change sends `""` which
   matches any worker, i.e. today's behavior, so the deploy order is safe.)
2. **Skip retro save handling for capture rooms**: in the `/w/{token}` save block
   (Program.cs:159-211), `ArcadeSaveId.TryParse` yields `system` — add
   `if (system == "capture") skip` around the harvest/fresh/seed mount logic. Capture saves ride
   HeavyVault at prepare/finish; the CloudRetro saves mount must stay untouched (a dirzip
   accidentally seeded as a `.dat` would resurrect the Snowboard-Kids class of wedges).
3. **`/heavy/prepare/{appId}` gains `?userId=`** (int, optional): when present, skip
   `ResolveHeavyUserAsync(clientName)` and use it directly for `SetUser` + `Seed`. The endpoint
   is already InternalAuth-gated; the worker sends the same secret.
4. Optional (nice error): prepare 409 body already carries holder info — worker logs it; no
   change needed.

RomCache is unaffected (heavy gameIds are not in the JIT manifest ⇒ `IsManaged` false).

---

## 6. Site changes

### 6.1 API (`src/MovieTheater/Controllers/ArcadeController.cs`)

- **CreateRoom** (:427-551): replace the flat heavy rejection (:442-445) with:

```csharp
var isCapture = string.Equals(game.Lane, "heavy", StringComparison.OrdinalIgnoreCase);
if (isCapture && string.IsNullOrEmpty(game.CloudRetroGameKey))
    return BadRequest(new { message = "This title plays via Moonlight, not in the browser — use its card's Play instructions." });
```

  For capture rooms: skip cheats resolution (none apply), keep the codec pill (per-room codec
  0036 works unchanged — design A builds the same encoder pipeline), keep `vbr` (ABR ceiling —
  add `"capture"` to the `DefaultVideoBitrateKbps` table at ~12000), and pass **zone**.
- **Zone threading**: `CloudRetroHost.BuildJoinDescriptor` (CloudRetroHost.cs:28-54) currently
  emits `zone={zone}` from the dead `ArcadeZoningEnabled` flag. Give it a zone argument:
  `"main"` for retro rooms, `"capture"` for capture rooms — Join/ClaimSeat descriptors must
  carry the SAME zone as the create (the coordinator's `findWorkerByRoom` filters by
  `w.In(region)`, `hub.go:232-246`).
- **Games response** (:210-222): add `capture = vs.Any(g => g.Lane == "heavy" && g.CloudRetroGameKey != null)`
  so the card knows to offer browser play.
- **Deterministic room id**: already minted with `system` — for capture rooms it becomes
  `sv-{u}-{g}-0-capture___{gameKey}` automatically IF the ArcadeGame row's System is... note:
  `Mint(user, game, 0, system, launchKey)` uses the game's System field. Heavy catalog rows
  (61327-31) carry their real system (`switch`/`ps3`/`ps4`). **Mint capture rooms with the
  literal system string `"capture"`** (one-line special case at :488) — the worker and the
  gateway skip-guard both key on it.

### 6.2 DB data (no migration)

`ArcadeGame` needs no schema change. Per capture-enabled title set:
`CloudRetroGameKey = <heavyAppId>` (e.g. `switch-kirby-forgotten-land`) and
`MaxPlayers = <descriptor input.maxPads>`. SQL (run against the shared live DB, deliberately):

```sql
UPDATE ArcadeGames SET CloudRetroGameKey = 'switch-kirby-forgotten-land', MaxPlayers = 4 WHERE Id = 61329;
-- repeat per title; leave CloudRetroGameKey NULL to keep a heavy title Moonlight-only
```

### 6.3 UI

- `HeavyGameModal.js`: when the game has `capture` and staging is `done`/`local` and the lane
  isn't locked → primary section gains **"▶ Play in browser"** which calls the SAME create-room
  path GameCard uses for retro titles (`MovieAPI.createArcadeRoom(versionId, …)` → navigate to
  the room page). Keep "Launch on this device (Artemis)" alongside — label the tradeoff
  ("lowest latency" vs "no app needed, friends can join with pads").
- `ArcadeRoomPage.js`/`cloudRetroClient.js`: no changes for v1/v2 (pads work as-is; the
  streamed-pad guard already exists for Ziggy-local browsing). P3 adds kb/mouse senders.
- `arcadeSystems.js`: no new system label needed (cards keep their real system); the room page
  shows whatever the descriptor `system` says — it will say `capture`; add a label
  (`capture: "Live"` or similar) to avoid an ugly raw key.
- Busy UX: the modal already polls `/API/Arcade/Heavy/Status` — show the existing "in use by X"
  banner; a race that slips past it fails at worker `LoadApp` with the prepare 409 (browser
  gets a failed start — acceptable v1, message polish later).

---

## 7. Deploy (Ziggy)

1. **Task**: `scripts/register-arcade-glworker-task.ps1 -WorkerId 3` → "MovieTheater - Arcade
   GL Worker 3", port **8448**. `run-arcade-glworker.ps1` needs two new params threaded:
   `-Zone` (default `main`; capture task passes `capture` → `CLOUD_GAME_WORKER_NETWORK_ZONE`)
   and `-ConfDir` (already exists as `--w-conf`; point at `D:\ArcadeStorage\worker-capture`).
2. **Router**: forward UDP 8448 → Ziggy (same as 8446/8447; media rides it for internet peers).
3. **Watchdog**: `-WorkerPorts @(8446,8447,8448)` on the watchdog task registration; the 5 s
   capture tick log satisfies check C.
4. **`docker/arcade/.env` untouched** (ICE IPs are worker-global env, LAN-first order rule
   still applies — `ZIGGY_PUBLIC_IP=192.168.68.69,98.15.249.217`).
5. Coordinator: NO redeploy needed (zones + `/status` already live).
6. Gateway + site: normal flows (stop task → build → start; push to master → CI).
7. `ArcadeMaxConcurrentRooms` stays 3 (= worker count; t=112 remains the authoritative backstop).

---

## 8. Phases

### P0 — capture/encode spike (half a day, no code)
From a normal PowerShell on Ziggy (`$env:PATH = "D:\msys64\ucrt64\bin;$env:PATH"`), with a game
or video playing on the physical monitor:

```powershell
# video: capture → GPU downscale → download → I420 → NVENC AV1, measure fps
gst-launch-1.0 -v d3d11screencapturesrc monitor-index=0 show-cursor=false `
  ! "video/x-raw(memory:D3D11Memory),framerate=60/1" ! d3d11convert `
  ! "video/x-raw(memory:D3D11Memory),format=BGRA,width=1920,height=1080" `
  ! d3d11download ! videoconvert ! "video/x-raw,format=I420" `
  ! nvav1enc preset=p6 tune=ultra-low-latency rc-mode=cbr bitrate=8000 zerolatency=true `
  ! fpsdisplaysink text-overlay=false video-sink=fakesink

# audio: PID-scoped loopback (yuzu running; substitute its PID)
gst-launch-1.0 -v wasapi2src loopback=true low-latency=true `
  loopback-mode=include-process-tree loopback-target-pid=<PID> `
  ! audioconvert ! audioresample `
  ! "audio/x-raw,format=S16LE,rate=48000,channels=2,layout=interleaved" `
  ! opusenc ! fakesink
```

Gates: steady ≥59 fps at 1080p with a retro room running concurrently; PID-scoped audio actually
isolates the game; note measured CPU/GPU. Also spike the P4 zero-copy string
(`… ! nvautogpuav1enc …` without d3d11download) and record whether it negotiates — informs P4
only. If `monitor-index=0` grabs a SudoVDA display, enumerate with
`gst-device-monitor-1.0 Source/Monitor` and pin by handle — and write down the mapping.

**P0 RESULTS (executed 2026-07-11 on Ziggy, GStreamer 1.28.4).** All gates pass.
- Video design-A path steady **~60.2 fps avg, 0 dropped** at 1080p
  (`d3d11screencapturesrc → d3d11convert(BGRA) → d3d11download → videoconvert → NV12 → nvav1enc
  preset=p6 cbr 8000`). 767 frames in ~13 s, `current` 58.8–61.9.
- ⚠ **CORRECTION to the P0 string above**: this build's `nvav1enc` sink caps are
  `video/x-raw{,(memory:CUDAMemory|D3D12Memory|GLMemory)} format={NV12,P010_10LE,VUYA,RGBA,RGBx,
  BGRA,BGRx,RGB10A2_LE}` — **I420 is NOT accepted**. Use `format=NV12` (or feed BGRA straight in).
  The worker's design-A path is unaffected (it pushes BGRA into the media package's existing
  appsrc, which converts), but any direct-to-nvenc string must say NV12.
- Audio loopback negotiates cleanly (`wasapi2src loopback=true low-latency=true → audioconvert →
  audioresample → S16LE/48000/2 → opusenc`), no errors. PID-scoping not yet exercised (no game
  running at spike time) — validate `loopback-target-pid`/`include-process-tree` at P1.
- P4 zero-copy (`d3d11convert(NV12,D3D11) → nvautogpuav1enc`, no download) **negotiates + runs
  60 fps 0-dropped**. Confirms the P4 optimization is viable; still deferred.
- **Monitor map (no SudoVDA active at spike time — no Apollo client connected):**
  DISPLAY1 = **primary**, 2560×1440, `hmonitor=1959992039`; DISPLAY2 = 2560×1440,
  `hmonitor=24447515`; both on the RTX 4070 Ti. Enumerate at boot and pin by hmonitor (SudoVDA
  churns the GDI numbers — §1). `monitor-index` on `d3d11screencapturesrc` also works but is the
  unstable coordinate.
- **ViGEm decision (P1 §4.5):** only the bus driver is installed
  (`C:\Program Files\Nefarius Software Solutions\ViGEm Bus Driver\ViGEmBus.sys`) — there is **no
  `ViGEmClient.dll`/SDK on the box**. So the cgo `ViGEmClient` route would require vendoring the
  native lib; the **pure-Go `github.com/openstadia/go-vigem`** (talks the ViGEmBus IOCTLs directly,
  needs only the driver) is fetchable and is the chosen binding.

### P1 — the capture mod, hardcoded (worker only; ~3-5 days)
Patch 0037 v0: mod + branch + video/audio pipelines + ViGEm port-0 pad + direct exe launch
(hardcode one stub + exe path; SKIP the gateway contract). Room reached by starting the capture
worker with zone `capture` and hand-minting a room (temporary: point a dev site at zone capture,
or temporarily register the capture worker as the only worker in a test coordinator on :8001).
Gates: play Kirby in a browser tab on the LAN — video+audio+pad all live; second browser joins
as spectator; retro rooms unaffected (zone isolation proven); `Close()` kills the game.

**P1 RESULTS (executed 2026-07-11 on Ziggy).** The capture caged mod is WRITTEN and the whole
worker COMPILES against the pinned tree; the capture lane is proven live against real Kirby.
- **Code (new package `pkg/worker/caged/capture/`):** `capture.go` (App + app.App interface +
  LoadApp/Start/Close/exit-watcher/heartbeat + ArcadeSaveId parse), `pipeline.go` (go-gst
  video+audio appsink pipelines + last-frame re-push ticker), `input.go` (go-vigem X360 pad
  manager + RetroPad→XUSB), `launch.go` (gateway prepare/attach/finish + profile-swap Go port +
  Job-Object KILL_ON_JOB_CLOSE launch + foreground focus), `monitor.go` (EnumDisplayMonitors →
  HMONITOR). Wiring: `caged.go` (register Capture mod + `CaptureSystem`), `worker.go` (conditional
  Load when `conf.Capture.Enabled`), `coordinatorhandlers.go` (the ONE branch at the app-select
  point — shared encoder/media block, libretro path untouched), `config/worker.go` (`Capture`
  struct). `go build ./cmd/worker` = **exit 0** (CGO + go-gst + go-vigem all link).
- **ViGEm reality vs the note above:** `go-vigem` is NOT pure-IOCTL — it `LazyDLL`s
  `vigemclient.dll`, which is NOT on the box (Apollo/Sunshine static-link it). **Built
  `ViGEmClient.dll` (x64) from Nefarius source with the UCRT64 g++** (`-DVIGEM_DYNAMIC`, 30/30
  `vigem_*` exports) and ship it beside `worker.exe`. Smoke test: go-vigem created a real virtual
  X360 pad (`attached=true`, pressed A, moved stick).
- **Live probe (`cmd/capture-probe`, NOT part of patch 0037 — delete before packaging):** drove
  `capture.App` against real Kirby through the real gateway. Verified: monitor pinned; **prepare
  took the heavy lock**; yuzu launched + **attach** posted; **video 60 fps** to the media callback
  (1319 frames/22 s); **audio ~50/s** PID-scoped WASAPI loopback; **ViGEm pad created**;
  exit-watcher blanked; heartbeat ticked; **Close released the lock**. Retro workers + coordinator
  + gateway untouched throughout.
- **⚠ Two prerequisites for the emulator half of the gate (NOT capture-code bugs):** (1) yuzu
  exited ~1.6 s into its library scan — **session isolation**: launched outside the interactive
  desktop it can't stand up its Vulkan/GL context (the real worker runs as an Interactive-principal
  scheduled task, which fixes this). (2) `yuzu-streamed.player0.ini` did not exist yet → assumed
  pads were unbound in yuzu. That assumption was WRONG (see trap #10); profile captured 2026-07-13.
- **Deploy artifacts staged:** repo `docker/arcade/config.worker-capture.yaml` (authoritative;
  encoder in lockstep with worker-gl); `D:\ArcadeStorage\worker-capture\bin\` (worker.exe target +
  `vigemclient.dll`); Kirby stub `D:\ArcadeStorage\heavy\capture-stubs\switch-kirby-forgotten-land.capture`.
- **REMAINING for the full browser-tab gate:** run the capture worker as the Interactive scheduled
  task (so yuzu survives) + the WebRTC/coordinator/browser leg. Reaching a browser safely requires
  P2's zone routing (retro rooms send/derive `zone=main`) so a capture worker can share the live
  coordinator without stealing retro rooms (trap #7) — i.e. the browser gate lands together with P2.

### P2 — full integration (site+gateway+deploy; ~2-3 days)
Gateway (§5), site (§6), lifecycle-via-gateway (prepare/attach/finish + profile swap + job
object + exit-watcher), lazy pads for seats 1-3, worker task 3 + watchdog + router, DB keys for
the 5 catalog titles. Gates: (a) "Play in browser" from the card on the tablet — no app, no
pairing; (b) save round-trip: play, leave, verify HeavyVault harvest + ArcadeSave row, relaunch
via Artemis and see the same save (cross-lane vault proof); (c) lock exclusion both directions
(Artemis session ⇒ browser Play 409-blocked and vice versa); (d) 2-seat remote co-op with a
second account; (e) crash-of-yuzu ⇒ black frames + harvest, no desktop leak; (f) both retro
workers + capture room concurrent — watch encoder/GPU contention numbers.

**P2 RESULTS (executed + deployed 2026-07-11, commit 86fd2ee).** Code done and live.
- **Gateway** (runs LOCALLY on Ziggy from repo Debug bin — NOT k8s): zone is now DERIVED from the
  room-id system in `WsTransformer` (capture→`capture`, everything else incl. GL `zone=gl` and
  legacy/random ids → `main`) rather than stripped. This is safer than the plan's "site sends zone":
  no deploy-order hazard, and it protects the retro/GL pool at the gateway (trap #7) with no site
  dependency. Also: capture rooms skip the CloudRetro save-mount (`svSystem != "capture"` guard);
  `/heavy/prepare` honors `?userId=`. Rebuilt (Debug) + task restarted; serving, lock free.
- **Site** (prod k8s via CI): `ArcadeController.CreateRoom` capture branch (launchKey =
  CloudRetroGameKey, mint `system="capture"`, cheats skipped, `roomSystem` threads vbr); games list
  `capture` flag. **Capture-enabled gate = `CloudRetroHost.CaptureEnabledKeys` allowlist (Kirby),
  NOT CloudRetroGameKey** — every heavy row already carries a key (it's the heavy descriptor id), so
  that can't distinguish "has a capture stub". ⇒ the plan's DB `UPDATE … SET CloudRetroGameKey` is
  UNNECESSARY (Kirby already `switch-kirby-forgotten-land`, MaxPlayers 2). To enable a new title: add
  its key here AND drop a `<key>.capture` worker stub — together.
- **UI**: `HeavyGameModal` shows **"▶ Play in browser" beside "Launch on this device (Artemis)"**
  when `game.capture` — both coexist; title/intro adapt; `arcadeSystems.js` labels `capture`→"Live".
- **Deploy**: `run-`/`register-arcade-glworker-task.ps1` gained `-Zone`/`-ConfDir`/`-LibraryBasePath`
  /`-WorkerExe`; watchdog `WorkerPorts` includes 8448 (re-registered). `config.worker-capture.yaml`
  authoritative in repo.
- **LIVE on Ziggy**: capture worker = task **"MovieTheater - Arcade GL Worker 3"** (port 8448,
  zone=capture, `-WorkerExe D:\ArcadeStorage\worker-capture\bin\worker.exe`). Coordinator `/status`
  shows **2× main + 1× capture, all free**; the worker runs in **session 1 (physical console)** so a
  launched yuzu renders (the probe's session-isolation death does NOT apply to the task). Stable, no
  crash-loop, ViGEm DLL loaded (no warning).
- ~~**REMAINING before a full browser gate**: (D) capture the yuzu streamed input profile~~ — done
  2026-07-13, and it was never blocking: yuzu was already bound to the Xbox 360 guid that ViGEm
  presents, so pads worked (trap #10). Router UDP 8448 forward only needed for OFF-LAN peers
  (Defender already allows 8443-8448 on LAN).

### P3 — Steam + keyboard/mouse (~3-4 days)
Descriptor additions: `exeWatch` (child process name to track when `exe` is a launcher),
`input.kbm: true`. Steam launch via `steam://rungameid/<appid>`; lifetime = poll for `exeWatch`
child (Sunshine-style), attach THAT pid (audio scope + exit watch). KBM: shim senders (formats
§4.5 — keyboard 7B BE, mouse tag+BE), pointer lock UI on the room page, `SendInput` injection,
`show-cursor` per descriptor. Trial titles: one native-pad Steam game (proves launcher
lifetime), one mouse game (proves KBM). Out of scope: kernel anti-cheat multiplayer.

### P4 — optional/deferred
Zero-copy pipeline (design B: `nvautogpuav1enc(memory:D3D11Memory)` + GstForceKeyUnit for
keyframes + direct bitrate property for ABR); DS4/gyro ViGEm targets (`input.gamepad: "ds4"`);
mid-session periodic harvest; virtual-display isolation (own SudoVDA display so the physical
desktop stays private); fresh-start (`?fresh=1` analogue through HeavyVault).

---

## 9. Risks / traps appendix

1. **SudoVDA vs "primary monitor"** (§1) — pin by enumerated HMONITOR; log at boot. Apollo
   *Desktop* streaming does NOT take the heavy lock, so a live Desktop stream while a capture
   room runs would fight over the screen — operational rule for now; virtual-display isolation
   (P4) is the real fix.
2. **WGC frame starvation on static screens** — last-frame re-push ticker (§4.3), or joiners
   see nothing.
3. **Stride** — `d3d11download` may pad rows; read the video meta, never assume `W*4`
   (`ProcessVideo` slices `Stride*H` exactly and panics short).
4. **Endianness split** — pads are LITTLE-endian, keyboard/mouse are BIG-endian (verified in
   source; easy to get backwards).
5. **Y-axis inversion** — Gamepad API down-positive vs XInput up-positive; invert LY/RY.
6. **Coordinator pump blocking** — `LoadApp` runs on the coordinator message pump; prepare +
   launch + window-wait can take seconds. Patch 0035's unsolicited pong keeps the socket alive
   (7 s server deadline) but keep `LoadApp` under ~15 s and move the window-focus poll to a
   goroutine. `Close()` hard limit: 10 s (media-teardown watchdog kills the process).
7. **Empty zone matches any worker** (`Worker.In`, worker.go:136) — retro rooms MUST send
   `zone=main` once the capture worker exists; ship the site zone change in the same release
   as the worker task. Deploy order gateway→site is safe (§5.1).
8. **Gateway retro-save block must skip `system=="capture"`** or a future dirzip in the
   savestore layout could be mis-seeded into the CloudRetro mount (§5.2).
9. **Prepare-before-stage** — gateway prepare 409s when unstaged (by design); the card gates
   "Play in browser" on staging state so users normally never hit it.
10. **yuzu input profile** (resolved 2026-07-13; keep the mechanism, distrust the warning) — the
    swap only matters when the emulator's saved bindings name a DIFFERENT pad than the streamed
    one. They didn't: ViGEm XUSB *is* an Xbox 360 pad (SDL guid `030000005e04...7801`) and yuzu was
    already bound to that guid, so pads worked without any swap. The worker logs `no streamed
    profile — pads may be dead` whenever the file is absent, which is a guess, not a measurement —
    verify with the guid before believing it. `capture-yuzu-streamed.ps1` has now been run, so the
    swap is armed for the case that actually breaks: someone rebinds yuzu to a DualSense at the desk.
    (That script was also unrunnable until now — saved UTF-8 **without a BOM**, so PowerShell 5.1
    read the em-dashes as ANSI and the file didn't parse. Any .ps1 with non-ASCII text needs a BOM.)
11. **Encoder/GPU contention** — capture room + 2 retro rooms = 3 NVENC sessions (fine) but
    yuzu/RPCS3 3D load + retro GL cores share the 4070 Ti; measure at P2 gate (f); consider
    `preset=p4` for the capture encoder or per-room bitrate ceilings if retro rooms stutter.
12. **Desktop exposure** — game crash shows the desktop until the exit-watcher blanks it
    (small window); Close() also blanks. P4 virtual display eliminates the class.
13. **Latency expectations** — glass-to-glass ~80–120 ms on LAN (vs Moonlight ~40–70). Set
    the card copy accordingly ("lowest latency: use Artemis").

---

## 10. Implementation review (2026-07-11, post-P2 / post first live browser test)

Full audit of the shipped implementation: the worker capture mod (5 files in
`pkg/worker/caged/capture/` + wiring), the repo side (commit 86fd2ee), configs, scripts, and
deploy state. **Overall verdict: the architecture landed as designed and the lane works live**
— zone derivation, save-mount skip, prepare `?userId=`, the allowlist gate, watchdog
compatibility, input byte-mapping, the profile swap, the job object, and the privacy
black-frame path all verified correct. The findings below are what's left, ranked. Items
marked ✔ were fixed in the same pass as this review.

### MUST FIX (worker crashes / dead rooms / lock leaks)

**W1 — `Close()` panics on any failed room start (close of nil channel) → whole worker dies,
heavy lock leaks.** `stopRepush` is only created in `Start()` (`capture.go:155`), but
`stopPipelines` closes it unconditionally (`pipeline.go:204-207`). The coordinator's designed
clean-failure path (`LoadApp` error → `r.Close()`, `coordinatorhandlers.go:190-196`) therefore
**panics before `Start()` ever ran** — and there is no `recover()` anywhere on the websocket
packet-pump path, so the process dies. Triggers on every prepare 409 (lane busy — a *normal*
user action), missing secret file, or launch failure. When prepare succeeded but launch failed,
the panic lands before `gwFinish`, so the acquired lock leaks until respawn.
Fix: initialize `stopRepush` in the constructor (or nil-guard), AND gate teardown on a
`started` flag. Related: **on a prepare 409, `Close()` must NOT call `gwFinish` at all** —
`HeavyLock.Release` keys on appId only, so a capture 409 against an *Artemis session of the
same title* would release that session's lock and harvest mid-play. Track a `prepared` bool;
only finish if we actually acquired.

**W2 — singleton `started`/`closed` latch forever → the SECOND capture room on the same worker
process is dead.** The mod is a process-lifetime singleton and nothing resets state between
rooms (`capture.go:146-153, 167-174`); worker processes serve sequential rooms without
restarting (only crash/watchdog recycles them). Room #2: `Start()` no-ops (`started` still
true) → no video/audio/heartbeat → watchdog recycle; worse, its `Close()` no-ops (`closed`
still true) → game not killed, profile not restored, **lock not released**. The first live
test worked because it was the process's first room.
Fix: reset all per-room fields (`started`, `closed`, `proc`, `swappedExe`, `lastFrame`,
`stopRepush`, `heavyAppId`, `roomId`, `prepared`) at the top of `LoadApp` — mirroring what
`ReloadFrontend()` does for the libretro app. (A fresh App per room is cleaner but touches the
manager contract; the field reset is the minimal correct fix.)

**R1 — patch 0037 was never generated: the entire worker-side capture mod exists ONLY in
`D:\Arcade\build\cloud-game-gl`.** `docker/arcade/patches/` ends at 0036. A rebuild-from-virgin
(the documented workflow) silently loses the whole lane. Fix: after W1/W2 land, generate
`0037-capture-caged-mod.patch` per `patches/README.md` (apply 0001–0036 to clean `13852a7`,
commit, apply capture work, `git diff --cached`), delete `cmd/capture-probe` first (it's marked
"delete before packaging"), commit the patch + a README entry. Until then the worker binary is
unreproducible from the repo.

### SHOULD FIX (leaks / races / dead config)

**W3 — `syscall.NewCallback` allocated inside the `focusPid` poll loop** (`launch.go:312-325`).
Callback slots are process-global and permanent (Go caps ~2000/process); ~66 allocations per
launch. With W2 fixed (long-lived process, many rooms) this eventually panics
("too many callbacks"). Fix: hoist the callback out of the loop (build once, reset the captured
pointer per iteration).

**W4 — pad use-after-free race**: `pads.apply` fetches the pad under `p.mu` but calls
`Reset/PressButton/Update` unlocked (`input.go:142-190`) while `Close→removeTargets` releases
targets — a pad packet racing teardown can call into `vigemclient.dll` on a freed target.
Fix: hold `p.mu` across the whole `apply` (input is ~60 Hz; the serialization cost is nil).

**W5 — ~~dead audio-routing config~~ RESOLVED while this review was running**: the VB-CABLE
per-PID isolation landed in a parallel session (config.Capture gained
`AudioSink`/`AudioCaptureDevice`/`SoundVolumeView`; `routeAudioToSink` moves only the game's
audio to CABLE Input with escalating retries, `startAudioPipeline` captures from the CABLE
Output device instead of loopback; verified live with Kirby). Remaining improvement: a few
seconds of host-speaker bleed at boot until the retry catches yuzu's late audio session —
consider `/Mute` on the app's session first, or pre-routing by exe name, when touching this.
(Note: the W1–W4 line numbers above were read against the pre-VB-CABLE tree and may be
slightly shifted; the findings themselves are in untouched code paths.)

### NICE TO HAVE

- **W6 stride heuristic**: appsink infers stride as `size/h` with fallback `w*4`
  (`pipeline.go:57-61`) instead of reading `GstVideoMeta.stride[0]` — safe on this GPU today,
  shear risk on other drivers. (Trap #3 said read the meta; do it when touching the file.)
- **W7/W8 lastFrame**: ~8 MB retained for process lifetime (free it in Close once W2's reset
  exists); the repush/capture writers share one buffer with the async `videoWorker` copy —
  worst case cosmetic tearing, same class as the libretro contract; note only.
- **W9 nits**: URL-escape `heavyAppId` in gateway paths (`launch.go:53,77,86`); accept 2-byte
  buttons-only pad packets like libretro does (`input.go:143`); ALT-tap fires before
  SetForegroundWindow and can momentarily pop an emulator menu (cosmetic).
- **R2 Join/ClaimSeat descriptors carry `system="switch"`, creator gets `"capture"`**
  (`ArcadeController.cs:980,1033` vs `:516`). Routing is immune (gateway re-derives zone from
  room_id), but client tables keyed on `descriptor.system` (`profileFor`, `FALLBACK_AR`,
  the "Live" label) silently diverge between creator and joiners the moment anyone adds a
  `capture` entry. Fix: thread the stored room system into both descriptors.
- **R3 no 16:9 fallback aspect**: `FALLBACK_AR` has no `capture` entry and the video element is
  `objectFit:"fill"` — if av-info geometry ever fails to arrive, the 1080p desktop stretches to
  4:3. Add `capture: 16/9`.
- **R4** ✔ stale "IDENTICAL / keep in lockstep" comments in `config.worker-capture.yaml`
  contradicted the deliberate colorimetry divergence (limited `2:3:5:1` vs GL pool's full
  `1:3:5:1`) — reworded in this commit so nobody "resyncs" the washout bug back in.

### Open items carried from the live test
- ~~Audio host-bleed~~ solved via VB-CABLE per-PID routing (see W5); only the boot-bleed
  window remains as polish.
- ~~yuzu streamed-profile capture (trap #10)~~ CLOSED 2026-07-13, and it was never actually
  broken: yuzu's live P1 binding uses SDL guid `030000005e0400008e02000000007801` — the **Xbox 360
  Controller** guid, which is exactly what a ViGEm XUSB pad presents. Our virtual pad matched the
  existing bindings, so pads worked in yuzu all along; the worker's `no streamed profile — pads may
  be dead` warning is what made this look pending. The profile is now captured anyway, so the swap
  is ARMED: rebinding yuzu to another pad at the desk can no longer silently kill streamed pads.
- Router UDP 8448 forward for off-LAN peers.

---

## 11. HDR strategy

Context: the first live play was washed out on the tablet. Two independent causes were
identified: SDR full-range colorimetry (fixed — capture pool now encodes limited `2:3:5:1`)
and the possibility of the desktop being in HDR mode during capture (yuzu exclusive-fullscreen
flipped it to SDR in every probe so far, but the desktop is *usually* HDR). Investigated
2026-07-11: what happens when HDR is on, and whether true HDR can reach the browser.

**Can we patch HDR support in ourselves? Capture side: nothing to patch — it's already in the
box. Browser side: no — the wall is Chrome's code running on the clients, not ours.** Detail:

### Tier 1 — HDR-proof the SDR stream (do now; fixes washout permanently)

`d3d12screencapturesrc` (present in the UCRT64 GStreamer 1.28.4 the workers run) has built-in
HDR handling that the d3d11 element lacks: a `tonemap` property (enum `linear`|`reinhard`,
"tonemapping method to use when HDR capturing is enabled") and 16-bit `RGBA64_LE` output.
When the desktop is HDR it tonemaps to correct SDR; when SDR it passes through. Swap the
capture source in `pipeline.go`:

```
d3d12screencapturesrc tonemap=reinhard show-cursor=false
  ! d3d12convert
  ! video/x-raw(memory:D3D12Memory),format=BGRA,width=1920,height=1080,framerate=60/1
  ! d3d12download ! queue ... ! appsink        # rest unchanged, incl. limited-range encode
```

**Verified live on Ziggy (2026-07-11): this exact shape negotiates and runs (exit 0), including
with `tonemap=reinhard` set.** One negotiation quirk found: constraining format+framerate
directly on the src caps fails (`not-negotiated`); constrain AFTER `d3d12convert` (as above).
Monitor pinning (`monitor-handle`/`monitor-index`) and `show-cursor` exist identically on the
d3d12 element. Remaining verification is user-assisted: turn display HDR ON, start a capture
room, confirm colors (and try both tonemap modes; `reinhard` preserves highlight detail,
`linear` clips brighter). This also retires the CCD-API "force display to SDR" idea — no
display-state mutation needed.

### Tier 2 — true HDR to the browser (bounded weekend experiment; desktop-only; likely fails)

Browser reality (researched 2026-07-11, sources in the session log):
- Chrome's WebRTC **software** AV1 decoder (dav1d wrapper) **hard-rejects non-8-bit**
  (`dav1d_decoder.cc`: "Only accept 8 bit depth", current main). A 10-bit stream = decode
  error, not tone-mapped video. Any PLI-triggered fallback from hardware to software decode
  kills the stream permanently. The Android tablet would land on software dav1d → dead.
- H.264 High10 in browser WebRTC: **impossible** (profile not negotiable in libwebrtc).
- The only browser HDR-over-WebRTC ever proven in production was **Stadia (2020) via VP9
  Profile 2 hardware decode** — libwebrtc still carries the 10-bit VP9 frame plumbing — but
  NVENC has no VP9 encoder, so that path would mean software libvpx encode: not viable.
- Chrome DOES offer the libwebrtc **color-space RTP header extension**
  (`http://www.webrtc.org/experiments/rtp-hdrext/color-space`, carries H.273 CICP + optional
  HDR10 static metadata) in every video SDP by default; GStreamer ships `rtphdrextcolorspace`
  (1.20+); Pion registers arbitrary extensions via `MediaEngine.RegisterHeaderExtension`.
  Whether current Chrome actually *renders* a PQ WebRTC stream as HDR post-Stadia is
  **unconfirmed by anyone** — the 10-minute empirical test beats further reading.

If attempted anyway, the recipe: capture `RGBA64_LE` → `d3d12convert` → `P010_10LE`
(**this negotiation is verified working on the box**, even from an SDR desktop) → `nvav1enc`
10-bit PQ with caps `colorimetry=bt2100-pq` → repeat the AV1 sequence-header OBU on every
keyframe (a known Chrome HW-decode trap even for 8-bit: a keyframe without it demotes Chrome
to software decode) → stamp the color-space RTP extension on frame-final packets (worker patch:
register in Pion + populate from caps). Test ONLY desktop Chrome + HW AV1 decode (RTX/Arc) +
HDR monitor, behind a per-room flag, never default. Expected failure modes, in order: renders
tone-mapped/washed anyway; first packet loss → software fallback → dead video; scRGB→PQ
transfer conversion turns out unsupported in `d3d12convert` (if so, THAT is the one place a
GStreamer patch by us is plausible — precedent: `gst-nvcodec-intrarefresh.patch`).

One spillover check worth doing regardless of HDR: verify our current 8-bit AV1 keyframes
(patch 0029 PLI responder path) carry the sequence-header OBU — if not, Chrome clients may be
silently degrading from hardware to software AV1 decode after the first PLI, which is the
known tablet-killer pattern.

### Tier 3 — the future path (revisit ~2027, don't block on it)

WebCodecs + WebTransport custom player: 10-bit AV1 NVENC → WebTransport → `VideoDecoder` →
WebGPU float16 canvas with `toneMapping: {mode:'extended'}` (extended-range WebGPU canvas
shipped default-on in Chrome 129, Sep 2024). All pieces exist; nobody has publicly assembled
them for game streaming; you'd own jitter-buffer/AV-sync yourself and HDR metadata is still
spec-open (w3c/webcodecs#384). The right time is when someone else ships the player skeleton.

Bottom line: **Tier 1 ships as part of the W-fixes worker rebuild and makes the lane
color-correct in all display states; true HDR waits for the browser world to catch up, and the
tablet (probably a non-HDR panel — check it) would not benefit anyway.**

---

## 12. Performance review — audio lag on the tablet (2026-07-11, post-§10/§11)

Context: all §10/§11 items are implemented and live (W1–W6 + graceful game close + d3d12
tonemap; verified in code this pass — the fixes are correct, and the deploy round surfaced two
genuinely subtle worker lessons now baked in: a d3d12 WGC capture session cannot be
PAUSE/NULL-resumed in-process, so the video pipeline stays PLAYING for process life behind a
`capturing` gate; and per-room goroutines must own their room's proc or room 1's exit-watcher
blanks room 2). Remaining complaint from live play: **audio lags noticeably on the tablet**
(H.264 room). Investigated; root cause found on the box.

### 12.1 Root cause: VB-CABLE's internal transfer, misconfigured on top of that

The audio path is: game → per-PID route → CABLE **Input** (render endpoint) → *VB-CABLE
internal transfer buffer* → CABLE **Output** (capture endpoint) → `wasapi2src device=` →
opus → WebRTC → tablet jitter buffer. Facts measured on Ziggy:

- Registry `HKLM\SOFTWARE\VB-Audio\Cable`: `VBAudioCableWDM = 7168` (max-latency buffer,
  samples) and `VBAudioCableWDM_SR = 96000` — **the cable's internal engine runs at 96 kHz
  while every endpoint is 48 kHz**. So the game's audio is SRC'd 48k→96k→48k inside the
  driver AND buffered up to ~7168 samples in the transfer: **≈75–150 ms added**, all of it
  invisible to GStreamer. Everything downstream is already lean (`low-latency=true`, 10 ms
  opus frames, `max-buffers=4 drop=true`, small queues), which is why the lag survived tuning.
- Video has no comparable stage (capture→encode ≈ 5–10 ms), so audio trails video — exactly
  the "laggy audio" percept.

### 12.2 Fixes, ranked

**A (recommended, ~5-line worker change + config): loopback-capture the CABLE *Input* render
endpoint instead of the CABLE *Output* capture device.** WASAPI loopback taps the render-mix
*before* the cable's internal transfer, so the 7168-sample buffer AND the 96 kHz double-SRC
drop out of the path entirely — VB-CABLE's only remaining job is being a default device that
isn't the speakers. **Verified live on Ziggy** (2026-07-11): `wasapi2src loopback=true
low-latency=true device="{0.0.0.00000000}.{ceabe612-e9ce-404e-8be3-e5d660d90bfe}" !
audioconvert ! audioresample ! S16LE/48k/2ch ! sink` negotiates and streams cleanly.
Implementation: in `startAudioPipeline`'s `AudioCaptureDevice` branch, emit `loopback=true`
when the configured id is a render endpoint (WASAPI render ids start `{0.0.0.`; capture ids
start `{0.0.1.`) — or add an explicit `AudioCaptureLoopback: true` config bool; then point
`AudioCaptureDevice` at the CABLE Input id above. Expected win: **−75–150 ms**, which should
take the tablet from "noticeably laggy" to imperceptible. Refresh patch 0037 + rebuild.

**B (no-code, complementary hygiene): fix the cable's own settings.** Run
`VBCABLE_ControlPanel.exe` as admin: internal sample rate 96000 → **48000** (removes the
double-SRC) and Max Latency 7168 → **2048** (~43 ms ceiling); needs a driver restart (the
panel offers it; otherwise reboot). With fix A shipped this only matters for anything else
that ever listens on CABLE Output, but it's 30 seconds of clicking and removes a trap.

**C (site, one line): default in-frame pacing for capture rooms.** The capture stream is the
site's fattest (12 Mbps H.264, intra-refresh so *every* frame is sizable); un-paced frame
bursts on tablet WiFi queue behind each other and jitter the audio packets sharing the air,
which grows the browser's adaptive audio jitter buffer. Patch 0028's `?pace=` exists but only
rides the URL when the client asks (`ArcadeController.cs:541`); capture rooms should default
it server-side (start with `pace=8`) the same way vbr defaults to 12000. Cheap, and it helps
video smoothness on WiFi too.

**D (housekeeping, found during this pass): Ziggy's system default render device is currently
CABLE Input** — ALL system audio is going into the cable (host speakers silent), which looks
like leftover test state, not the per-PID design (`routeAudioToSink` moves only the game).
Restore Speakers as the Windows default; nothing in the lane depends on the default being the
cable.

### 12.3 Other optimization notes (no action needed now)

- **Zero-copy got a new concrete option**: `d3d12h264enc` exists in this GStreamer build and
  accepts `D3D12Memory` directly — a true end-to-end-GPU H.264 path
  (`d3d12screencapturesrc ! d3d12convert(NV12) ! d3d12h264enc`) with zero downloads. Note
  `nvautogpuh264enc/av1enc` take D3D11/CUDA memory, NOT D3D12, so the d3d12 encoder is the
  natural partner for the new d3d12 capture chain. Still a P4 item (bypasses `ProcessVideo`,
  so keyframe-forcing and ABR need rerouting to GstForceKeyUnit events + direct property
  sets); today's two CPU copies at 1080p60 are comfortably within budget.
- The always-PLAYING capture pipeline's idle cost is negligible (WGC delivers frames only on
  change; an idle desktop produces a trickle).
- Opus settings are already right for latency (10 ms frames, inband FEC); the browser's
  adaptive audio jitter buffer is not directly controllable — steady delivery (fix C) is the
  only lever on that term.

---

# 2026-07-16 live-ops findings — doc/code drift closure + W9 status

Written during the Vulkan live-ops session. The capture worker (8448) was updated to the current worker
build (srflx ICE + M2 zero-copy + W3 fixes) — it previously ran a pre-srflx/pre-M2 binary. Registered
clean ("capture: libgstd3d12 is the patched build", "srflx NAT1To1 active", coordinator-connected).

## DRIFT CLOSED — items this doc listed as deferred/missing that are ALREADY LIVE
- **§12.2-C server-side pace default for capture rooms — SHIPPED.** `ArcadeController.cs:625`:
  `var paceMs = request.PaceMs > 0 ? request.PaceMs : (isCapture ? 8 : 0);` — capture rooms already
  default to pace=8 the same way vbr defaults. No change needed.
- **R3 FALLBACK_AR capture entry — SHIPPED.** `ArcadeRoomPage.js:681`: `capture: 16 / 9`. No change needed.
- (Per the umbrella note: config `zeroCopy: true` + capture.go ZeroCopy, d3d12 tonemap capture,
  limited-range colorimetry, and the §12.2-A loopback audio fix are also live — the doc's §8 P4
  "deferred" list is partially shipped.)

## W9 ITEM 1 — AV1 sequence-header OBU audit: RESOLVED, no bug (evidence-first)
Verified with gst-launch on the box (msys2 gst 1.28.4, patched nvcodec):
- `nvav1enc gop-size=30 bitrate=5000 preset=p6 ... ! av1parse` over 150 frames → **5 sequence-header OBUs
  for 5 keyframes** (GST_DEBUG=av1parse:7). So nvav1enc attaches an OBU_SEQUENCE_HEADER to EVERY KEY_FRAME.
- The live intra-refresh params (`gop-size=-1 intra-refresh-period=120 intra-refresh-count=15`) → **1**
  sequence header at start (correct: an infinite GOP has no periodic keyframes).
- A PLI-forced keyframe (patch 0029 responder / ABR / capture GstForceKeyUnit) is a KEY_FRAME, so it
  carries the sequence header too. `nvav1enc` has **no `repeat-sequence-header` property** (only nvh264enc
  does) — it does not need one; av1 emits the header per keyframe inherently.
- **Verdict: the tablet-killer "keyframe without sequence header → HW→SW AV1 decode after first PLI" bug is
  NOT present in our AV1 path.** No encoder/parse fix needed. The only remaining confirmation is a live PLI
  observation (decodeMs jump / fps sag fingerprint) via the capture test-card harness (item 0d) — a
  validation, not a fix.

## W9 remaining — scoped follow-up (NOT built this session; designs stand)
Items 0 (machine-readable test-card app + capture-diag.mjs harness), 2 (zero-copy tracer A/B), 3
(bitrate/preset ladder, judged on the test card's banding-step + sharpness + latency metrics), 6 (SudoVDA
per-room virtual-display isolation — privacy window + Apollo screen-fight + per-room resolution/supersampling),
and 7 (per-title fidelity profiles) are a coherent day-scale build best done as a focused follow-up with the
test card landing FIRST (it is the instrument every other item is judged with). Item 8 (Tier-2 HDR/10-bit)
stays out of scope. The OBU finding above means item 1 is closed; the pace/AR drift means items 4 and 5c are
closed. Hygiene reads (§12.2-B VB-Audio registry, §12.2-D default render device) need admin/driver-restart
and are flagged for Eric, not automated. Router UDP 8448 forward for off-LAN peers remains Eric's action.

---

# 2026-07-23 — WGC WINDOW CAPTURE MODE (isolated capture that works over RDP)

The capture lane can now capture the GAME'S WINDOW (by HWND) instead of a monitor. This removes the
SudoVDA / console-session / autologin dependency for the "owner works over RDP while a friend plays"
case, and is the DEFAULT (`captureMode: window` in config.worker-capture.yaml). The monitor path
(`captureMode: monitor` + `virtualDisplay`) is kept verbatim as a config fallback.

## Why window capture (the road here)

Monitor capture forces a display: SudoVDA (an indirect display driver) attaches its hidden per-room
display only on the **console** session, so an RDP login — which REPLACES the console's display stack —
breaks it (the ADD "succeeds" but no monitor ever surfaces). WGC also captures a single WINDOW
(`IGraphicsCaptureItemInterop::CreateForWindow`), grabbing the window's DWM visual, which composits even
**off-screen or occluded** (frames only stop while MINIMIZED). Park the game window off-screen and
capture it by HWND and the game never appears on the owner's desktop, in ANY session.

The blocker was the toolchain, not the API: GStreamer 1.28.4's `d3d12screencapturesrc` implements window
capture, but the whole WGC feature set is behind `HAVE_WGC` and **won't compile under mingw/ucrt64**
(WinRT/WRL headers don't emit the `__FITypedEventHandler` typedefs the MSVC SDK does — 28 errors,
confirmed against the official MSYS2 build too; MSYS2 disables `d3d12-wgc` for this reason). Our entire
GStreamer/Go/cgo stack is mingw, and an MSVC-built plugin won't link into it.

## Architecture — a standalone .NET helper + shared-memory ring, into design-A appsrc

`.NET 10` has first-class WinRT projections for WGC, so the capture lives in a tiny separate process,
`src/ArcadeCaptureHost` (NOT in MovieTheater.sln, NOT built by CI — Windows/Ziggy-only, deployed by file
copy beside worker.exe). A WGC/D3D crash there stays out of the worker (history: the stock plugin's
cursor bug abort()ed the whole worker, read by players as "the arcade is full").

Flow: the off-screen game window is captured by WGC `CreateForWindow` in ArcadeCaptureHost (own D3D11
device, `Direct3D11CaptureFramePool.CreateFreeThreaded`, B8G8R8A8, 2 buffers). Each FrameArrived does
CopyResource to a staging texture, Map, and packs rows into a shared-memory ring. The worker's
`shmReadLoop` (OpenFileMapping / MapViewOfFile / OpenEvent + seqlock) reads the latest frame and calls
`cacheAndPush` — the SAME design-A appsrc path monitor capture uses — so ABR, per-room codec, keyframes,
the re-push ticker, and black-on-exit all reuse unchanged.

The helper is spawned per room with the game's HWND + generated shm/event names; the worker holds its
stdin open (closing it makes the helper exit — it dies with the worker) and parses one JSON status line
per stdout line (`ready` / `resize` / `recover` / `error` / `stopped`).

### Shared-memory IPC protocol (identical in SharedFrameRing.cs and wincapture.go, little-endian x64)

Header (64 bytes): `magic 'MTWC'` u32, `version=1` u32, `width` u32, `height` u32, `stride` (=width*4)
u32, `slotCount=3` u32, `slotSize` (page-aligned pixel capacity) u32, `generation` u32, `latestSlot`
i32, `frameSeq` u64, `qpc` u64. Then 3 slots of (8-byte seq + slotSize pixels) at `64 + i*(8+slotSize)`.
The writer round-robins into a slot that is NOT latestSlot, marks its seq odd (seqlock) while copying,
even when done, then publishes latestSlot / frameSeq / qpc and sets the event. The reader takes
latestSlot, copies under the seqlock, and retries on a torn read. The helper always normalizes output to
the configured width x height (clamp + zero-fill), because the worker builds a fixed-geometry pipeline.

## Worker behavior in window mode (pkg/worker/caged/capture/wincapture.go)

- Launch the game as before (job object, graceful WM_CLOSE kill, audio PID to VB-CABLE routing — unchanged).
- Park the window fully OFF-SCREEN (a rect past `SM_XVIRTUALSCREEN + SM_CXVIRTUALSCREEN`, recomputed per
  room because RDP changes geometry), borderless, with a persistent re-assert that wins the SM64-Plus
  re-home race (`placeOffScreen`) — and NEVER focus it (the owner keeps typing; the pad still reaches an
  unfocused SDL window via `SDL_JOYSTICK_ALLOW_BACKGROUND_EVENTS=1`, set at launch).
- Minimize guard (`windowGuardLoop`, 1 Hz): WGC stops delivering while minimized, so restore
  SHOWNOACTIVATE + re-park.
- `ZeroCopy()` is forced off (frames arrive over shm, not an in-GStreamer chain; zeroCopy config is
  ignored + logged). The SudoVDA open and the RDP console-refusal gate are skipped in window mode.
- Room close kills the helper (close stdin, then terminate) before the game kill. Black-frame-on-exit
  privacy holds: on game death the helper stops producing, `setCapturing(false)` drops frames, and
  `pushBlackFrames` pins black — window mode never captures the desktop to begin with.

## Mode matrix

| aspect | `captureMode: window` (default) | `captureMode: monitor` (fallback) |
|---|---|---|
| what is captured | the game window, by HWND, parked off-screen | a physical monitor (optionally a per-room SudoVDA vdisplay) |
| works over RDP | YES | NO (IDD displays attach on the console session only) |
| isolation from owner's desktop | window is off-screen, never focused | game on a hidden vdisplay (console) / refuses over RDP |
| capture path | ArcadeCaptureHost helper, shm, design-A appsrc | d3d12/d3d11 screencapturesrc (zero-copy or design-A) |
| SudoVDA / autologin needed | no | vdisplay needs the console session |

## Deploy

`src/ArcadeCaptureHost/build.ps1` publishes a self-contained single-file win-x64 exe; copy it to
`D:\ArcadeStorage\worker-capture\bin\ArcadeCaptureHost.exe`. Worker deploy is the usual capture-lane
dance (stop watchdog + task 3, back up worker.exe, swap, restart, then restart the watchdog). Config
deploy = byte-exact copy of `docker/arcade/config.worker-capture.yaml` to the live `config.yaml` (diff
first — hard rule).

## Verified live 2026-07-23 (RDP session)

P0 standalone: off-screen notepad and off-screen SM64 Plus both captured non-black frames; frames stop
on minimize and resume on restore. P2 live room: SM64 Plus streamed to the browser (AV1 1080p, 0
freezes) with the game window settled off-screen @(3560,0) and absent from the owner's desktop; both
retro workers stayed healthy. **fps under RDP is ~32** because the Microsoft Remote Display Adapter for
the RDP session runs at 32 Hz and WGC delivery is bounded by DWM's composition (present) rate = the
display refresh; on the physical console session (120 Hz monitors) the game vsyncs to 60 and capture
delivers 60. Game vsync on/off cannot exceed the compositor rate for WGC delivery, so this is an
RDP-display artifact, not a lane limit.

## Known caveats / follow-ups

- **~1s desktop flash at launch**: the game window is created on the visible desktop (e.g. @(317,154))
  and only parked off-screen a beat later when `placeOffScreen` first sees it. Under RDP the owner sees
  a brief game-window flash before it vanishes. Future: create the window hidden/off-screen from the
  start (SDL hint / CBT hook) — untested, risks WGC not compositing a never-shown window.
- **GPU shared-texture stage 2 (capture lane v2)**: today the helper reads back BGRA to system memory
  and the worker re-uploads for NVENC (the design-A cost). Sharing a D3D11 keyed-mutex texture across
  the helper→worker boundary removes that round trip. **The cross-process GPU path is now PROVEN on
  Ziggy (2026-07-23) — see the "CAPTURE LANE V2" section at the end of this doc.** The one design
  correction the proof forced: the transport is NT-handle **duplication**, NOT open-by-name (by-name
  `CreateSharedHandle`/`OpenSharedResourceByName` is process-local on this box). The live pipeline
  wiring (helper texshare ring + worker `d3d11zerocopy.go` + nvautogpu encoder) is specced but NOT yet
  built/deployed; v1 (the shm readback path above) remains the shipping default.
- **Per-title background-input**: SDL titles need `SDL_JOYSTICK_ALLOW_BACKGROUND_EVENTS=1` (set). Other
  emulators (yuzu) may need their own "input while unfocused" setting in profiles.json / per-title ini.
- **HDR**: window-capturing an SDR game window sidesteps the desktop-HDR tonemap problem (no monitor HDR
  in play). If the desktop is ever in HDR mode, revisit — the helper captures the window's SDR visual,
  so this is expected to be a non-issue.

---

# 2026-07-23 — CAPTURE LANE V2: cross-process GPU shared texture (window mode zero-copy)

Goal: restore zero-copy in WINDOW capture mode by sharing a GPU texture across the helper→worker
process boundary, replacing v1's CPU-readback + shm-pixel-copy chain (helper `Map`/readback ~6 ms +
two system-memory memcpys + a worker-side re-upload for NVENC).

**Status: the cross-process GPU shared-texture MECHANISM is PROVEN end-to-end on Ziggy. The live
pipeline wiring is specced and de-risked but NOT built/deployed. v1 remains the shipping default and
is untouched.** This section is written so the remaining wiring needs no re-derivation.

## The load-bearing finding (this OVERTURNS the agreed design's transport)

The agreed design said the worker would open the helper's shared texture **BY NAME**
(`IDXGIResource1::CreateSharedHandle(name)` in the helper, `ID3D11Device1::OpenSharedResourceByName`
in the worker) to avoid a handle-duplication dance. **That does not work cross-process on this
Win11 / NVIDIA 576.x box.** Verified with a standalone writer+reader (2026-07-23):

- The helper CAN reopen its own named handle **in-process** (2nd device, same adapter → OK).
- NO reader process can open it: `OpenSharedResourceByName` returns **E_INVALIDARG (0x80070057)** for
  a bare name AND for `Global\`/`Local\`/`Session\1\` prefixes (tested with the helper CREATING each
  prefixed name too — not just the reader varying the open name). Same failure on a plain same-adapter
  D3D11 device AND on a `GstD3D11Device`, so it is NOT a gst issue — the DXGI named-handle namespace is
  process-local here.

**Correct transport = NT-handle DUPLICATION** (the fallback the design itself anticipated). This is
proven to work and returns correct pixels:

1. Helper creates the texture with `MiscFlags = SharedNTHandle | SharedKeyedMutex`, then
   `IDXGIResource1.CreateSharedHandle(null, Read|Write, name:null)` → an NT `HANDLE` valid in the
   helper process. Helper keeps the handle open (the object lives only while a handle is open) and
   publishes: its **pid**, the **raw handle value**, and its **adapter LUID**.
2. Worker: `OpenProcess(PROCESS_DUP_HANDLE, false, helperPid)` →
   `DuplicateHandle(helperProc, helperHandleValue, self, &localH, 0, false, DUPLICATE_SAME_ACCESS)` →
   `ID3D11Device1::OpenSharedResource1(localH, IID_ID3D11Texture2D, &tex)`. Worker → helper is a
   sibling/parent relationship in the same session, so `PROCESS_DUP_HANDLE` is granted.

The worker's `GstD3D11Device` MUST be created for the helper's adapter (`gst_d3d11_device_new_for_
adapter_luid(luid, 0)`) — `gst_d3d11_device_new(0,0)` can land on the Intel iGPU and cross-adapter
opens fail. (Verified: the gst device lands on `NVIDIA GeForce RTX 4070 Ti, luid=30804071`, matching
the helper.) So the helper's ready JSON must advertise the LUID.

## What the proof validated (all green — no walls)

- **Public `gstreamer-d3d11-1.0` API IS in the UCRT64 GStreamer 1.28.4** (`pkg-config --exists` → yes;
  headers `gstd3d11device.h`/`gstd3d11memory.h`/`gstd3d11bufferpool.h` present). No patched-plugin
  route needed. Use `gst_d3d11_device_new_for_adapter_luid`, `gst_d3d11_device_get_device_handle`,
  `gst_d3d11_device_get_device_context_handle`, `gst_d3d11_device_lock/unlock`,
  `gst_d3d11_pool_allocator_new` + `_acquire_memory`, `gst_d3d11_memory_get_resource_handle`.
  ⚠ define `GST_USE_UNSTABLE_API` (the header is `#pragma message`-noisy otherwise).
- **cgo COM works in mingw/ucrt64**: `#define COBJMACROS` + `<d3d11_1.h>`/`<dxgi1_2.h>`, link
  `-ldxguid -luuid` for the IIDs. `ID3D11Device1_OpenSharedResource1`, `IDXGIKeyedMutex_AcquireSync/
  ReleaseSync/Release`, `ID3D11DeviceContext_CopyResource` all resolve. (The exact ~90-line cgo
  `proof_read()` is the prototype for `d3d11zerocopy.go`.)
- **Encoder**: `nvautogpuav1enc` AND `nvautogpuh264enc` exist and accept
  `video/x-raw(memory:D3D11Memory)` in **BGRA** directly (also NV12/RGBA/…). So push BGRA D3D11Memory
  straight into the nvautogpu encoder — no `d3d11convert` needed unless colorimetry demands it.
- **ABR / SVC survive the encoder swap**: `nvautogpuav1enc.bitrate` is "changeable in PLAYING" (kbit/s,
  same units as `nvav1enc`) and it exposes `temporal-layers` (1–3). So `Encoder.WithOverrides` /
  `setEncoderParam(...,"bitrate",...)` and `SetTemporalLayers` work unchanged against `video_enc`.
- **Keyed-mutex cross-process copy returns correct pixels**: 256×256 4-quadrant texture written by the
  C#/Vortice helper device, opened via handle-dup on the worker's `GstD3D11Device`, keyed-mutex
  acquired (key 0), `CopyResource` into a staging texture, read back — **all four quadrants match**.

## Protocol additions (helper → worker), building on the existing shm CONTROL channel

Keep the existing `mtwc-*` shm ring header + frame event as the CONTROL channel; in v2 it carries a
**slot index + seq** instead of pixels (skip the readback + pixel memcpy). Add to the helper's `ready`
JSON line: `"texShare":true, "texCount":4, "luid":<int64>, "pid":<int>, "texHandles":[<u64>,…]`
(one raw NT-handle value per ring texture). Worker parses these; ABSENT ⇒ run v1 silently
(mixed-version tolerance: new worker + old helper). New helper + old worker ⇒ worker never opens the
handles, nothing leaks (helper closes them on exit).

Per-frame publish (helper): acquire keyed mutex key 0 on the round-robin slot texture, GPU-copy the
WGC surface into it (or `VideoScaler` blits straight into the slot texture for the 300%-DPI console
case — the slot texture is a render target), release key 0, then write slot/seq into the shm header
and set the event. Worker read loop: wait event → read slot/seq → keyed-mutex acquire → **CopyResource
the shared slot texture into a buffer from a `GstD3D11BufferPool`** on the gst device → release keyed
mutex → push that buffer. The pool copy is deliberate (design §2): it decouples encoder buffer
lifetime from the shared ring so the keyed mutex is never held across the async encode.

## Live-pipeline wiring that REMAINS (not built)

1. Helper: `SharedTextureRing.cs` (keyed-mutex NT-handle BGRA texture ring + control publish) + a
   `--texshare` flag in `Program.cs`; `WindowCapture` copies each WGC frame into the current slot
   texture (GPU) / `VideoScaler` renders into it. Opt-in; v1 shm path stays default.
2. Worker: `pkg/worker/media/d3d11zerocopy.go` (new, modeled on `glzerocopy.go`; the proof's
   `proof_read` is the validated core) — GstD3D11Device-for-luid, handle-dup open, GstD3D11BufferPool,
   per-frame acquire/copy/release/push. `wincapture.go` parses `texShare` and runs a D3D11 push loop
   instead of `shmReadLoop`.
3. `gstreamer.go` THIRD pipeline mode (closest to the GLZeroCopy shape): `appsrc name=video_src
   caps="video/x-raw(memory:D3D11Memory),format=BGRA,width=W,height=H,framerate=…"` → (optional
   `d3d11convert` for limited-range colorimetry, stays on GPU) → `nvautogpu{av1,h264}enc name=video_enc`
   (the room codec's nvautogpu variant) → existing `video_q2 ! appsink`. Reuse `pullVideo`'s zero-copy
   keyframe clock (`maybeForceKeyframe` via upstream `GstForceKeyUnit`) and the 500 ms re-push guard
   (keep a ref to the last pushed `GstBuffer`; re-push it, or a dedicated re-push pool texture).
4. `capture.go`: `windowMode() && texShare && windowZeroCopy` ⇒ `ZeroCopy()`-equivalent for the new
   mode; wire `SetVideoValveDrop` as the blank gate (no Go frame path to gate).
5. `coordinatorhandlers.go`: build the D3D11 head (analogous to the existing `capApp.ZeroCopy()`
   branch) — set the appsrc-D3D11 mode + hand the app the pool push fn.
6. `config.Capture.WindowZeroCopy *bool` (nil/true = ON per the default-on policy; false = force v1).
7. **Fallback is mandatory + automatic**: any failure (helper can't make textures / can't publish
   texShare, worker can't open the gst d3d11 device or dup the handles, caps don't negotiate) ⇒ log
   loudly + fall back to v1 shm readback for that room.

## Verified vs pending ledger

| Item | Status |
|---|---|
| gstreamer-d3d11-1.0 public API in UCRT64 | ✅ verified (present, 1.28.4) |
| nvautogpu{av1,h264}enc take D3D11Memory BGRA + live bitrate/temporal-layers | ✅ verified (gst-inspect) |
| cgo COM (OpenSharedResource1 / IDXGIKeyedMutex) links in ucrt64 | ✅ verified (proof builds) |
| Cross-process keyed-mutex texture copy returns correct pixels | ✅ verified (4-quadrant proof PASS) |
| Transport = handle-dup (NOT by-name) | ✅ verified (by-name is process-local here) |
| Helper texshare ring + worker d3d11zerocopy.go + 3rd pipeline mode | ⛔ NOT built |
| Latency tracer A/B (target ~3 ms vs v1 ~9.2 ms) | ⛔ pending (needs the built pipeline) |
| Automatic fallback + windowZeroCopy knob | ⛔ NOT built |
| Live-room test (real site / test-roms) | ⛔ pending (needs Eric / harness) |

Proof harness (throwaway, session scratchpad, not committed): a C#/Vortice `texwriter` (creates the
keyed-mutex shared texture, publishes pid+luid+handle) and a Go/cgo `texreader` (GstD3D11Device +
handle-dup open + keyed-mutex readback + quadrant verify). Reproduce the transport from the recipe
above; the `texreader` cgo is the drop-in prototype for `d3d11zerocopy.go`.

## Auto-reattach to the console when nobody is attached (2026-07-23)

Window capture over RDP runs at the RDP display's refresh (~32Hz), and after an RDP *disconnect* the
session can sit in a stalled disconnected-DWM state rather than reattaching to the console. So when a
capture room starts and the worker's session is **DISCONNECTED**, the worker now reattaches the session
to the physical console (`tscon <id> /dest:console`) BEFORE launching the game — restoring the real
high-refresh GPU displays so capture is full-rate (60fps). A friend who starts a game while nobody is
attached gets full performance automatically.

**Hard guard — Active is never touched.** The trigger fires ONLY when the worker's own session is
`WTSDisconnected` (queried via `WTSQuerySessionInformation(WTSConnectState)` on the worker's session id).
It NEVER fires while the session is `WTSActive` — tscon then would forcibly kick a live RDP owner to the
console mid-work. Active-RDP rooms therefore stay at the reduced (~32Hz) rate by design.

**Elevation split** (tscon needs SeTcbPrivilege; the worker task is non-elevated):
- `scripts/reattach-console.ps1` (deployed to `D:\ArcadeStorage\worker-capture\bin`) does the tscon,
  run as SYSTEM. It resolves the target session from the worker's `.reattach-session` sentinel (written
  beside it), falling back to auto-detecting the single Disconnected session, and re-guards against
  moving an Active session.
- `scripts/register-reattach-console-task.ps1` — Eric runs this ONCE, elevated. It registers the
  on-demand task **"MovieTheater - Reattach Console"** as SYSTEM / RunLevel Highest via the
  `Schedule.Service` COM API, with an SDDL (`D:(A;;GRGX;;;AU)(A;;GA;;;BA)(A;;GA;;;SY)`) granting
  Authenticated Users read+run so the non-elevated worker can start it.
- The worker triggers it with `schtasks /Run /TN "MovieTheater - Reattach Console"`, then polls its own
  session state for up to 10s for Active-on-console before proceeding.

**Graceful everywhere.** Session query fails / task not registered / still disconnected after 10s → the
worker logs loudly and starts the room anyway (a reduced-rate or stalled room beats no room). Config
knob `reattachConsole` (Capture, `*bool`, default true — absence of the key means enabled) in
config.worker-capture.yaml; set false to disable.

**One-time elevated setup (Eric):**
```
powershell -NoProfile -ExecutionPolicy Bypass -File "F:\Work\MovieTheater\scripts\register-reattach-console-task.ps1"
```
(run from an elevated/admin PowerShell). Then: disconnect RDP, start a capture room from a second
device, and the worker log should show `session N connect state = Disconnected` → `reattaching to the
console` → `reattached to the console (Active) — full-rate capture available`, with capture at 60fps.

Tested as far as non-elevated + owner-attached allows (2026-07-23): the worker correctly logs the
session state at room start and, while Eric is RDP-**Active**, does NOT attempt tscon (verified live —
the safety guard holds); `schtasks /Run` on the unregistered task fails gracefully (worker logs +
continues). The Disconnected→tscon→60fps path is Eric's to verify once the elevated task is registered.
