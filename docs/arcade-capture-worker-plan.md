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

### P1 — the capture mod, hardcoded (worker only; ~3-5 days)
Patch 0037 v0: mod + branch + video/audio pipelines + ViGEm port-0 pad + direct exe launch
(hardcode one stub + exe path; SKIP the gateway contract). Room reached by starting the capture
worker with zone `capture` and hand-minting a room (temporary: point a dev site at zone capture,
or temporarily register the capture worker as the only worker in a test coordinator on :8001).
Gates: play Kirby in a browser tab on the LAN — video+audio+pad all live; second browser joins
as spectator; retro rooms unaffected (zone isolation proven); `Close()` kills the game.

### P2 — full integration (site+gateway+deploy; ~2-3 days)
Gateway (§5), site (§6), lifecycle-via-gateway (prepare/attach/finish + profile swap + job
object + exit-watcher), lazy pads for seats 1-3, worker task 3 + watchdog + router, DB keys for
the 5 catalog titles. Gates: (a) "Play in browser" from the card on the tablet — no app, no
pairing; (b) save round-trip: play, leave, verify HeavyVault harvest + ArcadeSave row, relaunch
via Artemis and see the same save (cross-lane vault proof); (c) lock exclusion both directions
(Artemis session ⇒ browser Play 409-blocked and vice versa); (d) 2-seat remote co-op with a
second account; (e) crash-of-yuzu ⇒ black frames + harvest, no desktop leak; (f) both retro
workers + capture room concurrent — watch encoder/GPU contention numbers.

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
10. **yuzu input profile** — the capture lane depends on the SAME one-time streamed-profile
    capture as the Apollo lane (bind P1 → Xbox 360 Controller once, run
    `capture-yuzu-streamed.ps1`). If that hasn't been done, pads will be dead in yuzu only.
11. **Encoder/GPU contention** — capture room + 2 retro rooms = 3 NVENC sessions (fine) but
    yuzu/RPCS3 3D load + retro GL cores share the 4070 Ti; measure at P2 gate (f); consider
    `preset=p4` for the capture encoder or per-room bitrate ceilings if retro rooms stutter.
12. **Desktop exposure** — game crash shows the desktop until the exit-watcher blanks it
    (small window); Close() also blanks. P4 virtual display eliminates the class.
13. **Latency expectations** — glass-to-glass ~80–120 ms on LAN (vs Moonlight ~40–70). Set
    the card copy accordingly ("lowest latency: use Artemis").
