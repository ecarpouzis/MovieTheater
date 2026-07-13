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

## 0014-joypad-bitmask-input.patch

**`pkg/worker/caged/libretro/nanoarch/nanoarch.c` — support the RetroPad bitmask input query.**
`core_input_state_cgo` only handled per-id joypad queries (`buttons >> id & 1`). Modern cores that
read the whole pad in one call via `RETRO_DEVICE_ID_JOYPAD_MASK` (== 256) hit `buttons >> 256`, which
is undefined behaviour — on x86 the shift count masks to 0, so the core got garbage and **no input
registered at all**. Discovered on **PS2 (LRPS2)**: the game sat in attract/demo mode because not one
button reached it, while the client was verified (via temporary `[INDBG]` logs) to be sending every
bit correctly. Older cores (snes9x, mupen64plus) were unaffected because they honour the frontend's
`GET_INPUT_BITMASKS → false` and fall back to per-id queries. The fix returns `buttons & 0xFFFF` for
the mask query; the per-id path is unchanged, so no working core regresses. Any future bitmask-reading
core (Beetle PSX HW, newer cores) benefits too. Verified live: PS2 input fully functional after the fix.

## 0015-video-caps-renegotiation.patch

**`pkg/worker/media/gstreamer.go` — renegotiate the appsrc caps when the core's frame size changes.**
The video appsrc caps (`width`/`height`) are fixed at pipeline build time, but a core can change its
output dimensions at runtime. Many **PS2 (LRPS2)** games — God of War, Kingdom Hearts, FFX, Shadow of
the Colossus, Monster Hunter — render the main scene at **512×448** while the initial AV info reported
640×448, so once the size diverged the pushed buffer no longer matched the caps and `videoconvertscale`
rejected **every** frame (`invalid video buffer received`, flooding the log) → a solid **green screen**
(GT4 and 2D games keep a constant size and were fine). `pushVideoBuf` now checks the appsrc's current
caps and, when the frame's width/height differ, updates them (format + framerate preserved) before
pushing. The downstream capsfilter keeps the scaled output constant, so the encoder/WebRTC stream are
unaffected — videoconvertscale simply rescales the new input. Fixed the entire green-screen class at
once; logs `[video] source caps WxH -> WxH` on each change. Verified live: all affected games render.

## 0016-dolphin-shared-context-and-serialize-probe.patch

**`pkg/worker/caged/libretro/nanoarch/nanoarch.go` — the two frontend behaviours GameCube (dolphin)
needs.** Both were found bringing up `dolphin_libretro` (GameCube) on the Windows GL worker:

1. **Grant `RETRO_ENVIRONMENT_SET_HW_SHARED_CONTEXT` (env 44) on the GL path.** Dolphin's OGL backend
   renders/compiles shaders on threads other than the retro_run graphics thread and asks the frontend
   to allow additional GL contexts sharing the main one's object namespace. Unhandled (= refused),
   dolphin logged `SetHWRender - unable to set shared context` and fell into a degraded path. Dolphin
   creates its own shared WGL contexts (`wglGetCurrentContext/DC` at context_reset → `wglShareLists`
   on its worker threads); the frontend only needs to say yes. Non-GL cores still get `false`.
2. **`skip_serialize_size_probe` core hack.** Right after `retro_load_game`, nanoarch probed
   `retro_serialize_size` for a "Save file size" log line. The dolphin libretro port defers its whole
   video/emu init to `context_reset` + the first `retro_run` (its EmuThread spawns there), so the
   load-time probe walks the save-state chain into a NULL `g_vertex_manager` → `0xc0000005` (read of
   `this+0x180` at dolphin+0x390fc5) and the worker died on **every** GameCube boot. The probe only
   feeds that log line — the real save path re-queries the size at save time — so cores that list the
   hack skip it. Opt-in per-core (`hacks: [skip_serialize_size_probe]` on `gc`); no other core changes.

Bring-up note (not in this patch): dolphin option values must be the core-options-v2 VALUE strings,
not display labels — `dolphin_efb_scale: "1"`, NOT `"1x Native (640x528)"`. An unmatched value leaves
the port's typed option cache 0 → 0×0 geometry → the same null-video crash signature from a different
door. Verified live 2026-07-07: F-Zero GX 60fps, input + memory card working.

## 0017-media-teardown-timeout.patch

**`pkg/worker/media/gstreamer.go` — hard timeout on media pipeline teardown.** The worst outage
class of GameCube launch night: `gst_element_set_state(NULL)` can hang FOREVER inside
**libgstnvcodec** (the NVENC element never finishes its state change — captured in a full process
dump: the goroutine parked in the plugin's cond wait). `Destroy()` runs SYNCHRONOUSLY on the
coordinator's message-pump goroutine (`TerminateSession → Room.Close → GstMediaPipe.Destroy`), so
one stuck encoder killed the worker's brain: pings stopped, the coordinator dropped it ("no free
workers" → every room create returned t=112 "the arcade is full"), yet the process never exited, so
the restart loop never fired. It struck on MOST session teardowns that night (7+ wedges), on every
core (the dolphin correlation was traffic, not cause).

Fix: run the real teardown on a goroutine; if it doesn't finish in 10 s, log and **hard-exit via
`TerminateProcess`** — `os.Exit`/`log.Fatal` run `ExitProcess`, whose DLL_PROCESS_DETACH
notifications block behind the very thread wedged in nvcodec (verified live: the Fatal logged and
the process survived 41 more seconds until the watchdog shot it). TerminateProcess skips DLL detach
and cannot block. One room per worker → nothing else lives in the process; the runner respawns a
clean worker in ~4 s. Verified live: wedge fired → hard exit 63 ms after the log → respawn +
coordinator re-register in 4 s → next room played. Total blast radius ≈ 15 s of one worker,
self-healing (the external watchdog task remains as backstop).

## 0018-per-room-encoder-settings.patch

**Per-room video bitrate + opus FEC, chosen by the room creator.** Touches five files:
`pkg/api/user.go` + `pkg/api/worker.go` (two new fields on `GameStartUserRequest` /
`StartGameRequest`: `video_bitrate` kbps + `audio_fec` 0/1/2), `pkg/coordinator/workerapi.go`
(copies them across the coordinator→worker seam), `pkg/config/worker.go` (new
`Encoder.WithOverrides(bitrateKbps, fec)` + `setEncoderParam` helper), and
`pkg/worker/coordinatorhandlers.go` (applies the override at `HandleGameStart`).

**Why:** the encoder is built **per room** (`media.NewGstreamer(w.conf.Encoder, …)` on the creator's
t=104), so a room's stream quality can be the creator's choice instead of one global config value.
`WithOverrides` returns a COPY — it deep-copies the `List` map (a reference type) before rewriting
`CodecSettings.Params`, so concurrent rooms never affect each other. Video bitrate is a **replace-only**
rewrite of the active codec's `bitrate=` (clamped 500–20000 kbps) so a vp8 fallback's different rate
key (`target-bitrate`) is never corrupted; FEC rewrites opus `inband-fec` + `packet-loss-percentage`.
Both default to no-op (0) → identical to stock when the creator sends nothing (backward compatible with
an un-upgraded client). Lower bitrate = smaller per-frame RTP bursts = less audio contention (see 0019)
+ less upstream for remote players. The site threads the choice through the WS URL (`?vbr=&fec=`) → the
shim's t=104; only the creator's request builds the encoder, so only the creator's values take effect.

Re-generate: per-file `git diff` of the five files above.

## 0020-split-audio-peerconnection.patch

**Opus on a dedicated aux `PeerConnection` (replaces the DELETED 0019 un-bundle enabler).** Touches
`pkg/network/webrtc/webrtc.go` (aux PC on `Peer`), `pkg/worker/coordinatorhandlers.go` (opt-in +
aux offer send), and a warning comment in `pkg/network/webrtc/factory.go`.

**Why 0019 was deleted:** its premise was false. pion/webrtc STORES `BundlePolicy` but never reads
it — one ICE/DTLS transport per PeerConnection is hardcoded — so `BundlePolicyMaxCompat` allocated
nothing, and a browser that stripped `a=group:BUNDLE` (max-compat) created transports with NO peer:
2 of its 3 DTLS handshakes could never complete and the pre-negotiated DataChannel rode a dead one →
every room hung at "Negotiating" (2026-07-08). Multiport was tested and REFUTED (3 distinct worker
ports bound → still hung): the failure was never port/5-tuple demuxing.

**What 0020 does instead — the shape Pion supports:** when the browser's `InitWebrtcStream` carries
`sdp:"audio-pc"` with `initiator:false` (that field is otherwise unused in that state), the worker
builds the room's media as video+data on the main PC and **opus on a second, audio-only PC**. The
browser gives that PC its own local port → its own 5-tuple → its own DTLS session, so a burst of
video RTP shares no socket, queue, or transport with audio (the real goal un-bundle chased). Aux
signaling is tunneled through the EXISTING opaque signal strings — worker→browser `"aux-sdp:<json>"`
(the offer) and `"aux-ice:<json>"` (candidates) inside the ice field, browser→worker `"aux:<json>"`
on sdp/ice — which the coordinator relays verbatim: NO protocol/coordinator change. Old client ↔ new
worker: flag never sent → stock path. New client ↔ old worker: ask ignored, audio arrives on the main
PC as always. An aux failure costs only audio; the room (video+input) still runs. Client half:
`cloudRetroClient.js`, localStorage `arcade.audioPC`.

Re-generate: `git diff pkg/network/webrtc/webrtc.go` verbatim + the 0020 hunks of
`pkg/worker/coordinatorhandlers.go` / `pkg/network/webrtc/factory.go` (both files also carry
earlier patches' hunks — trim to this feature's).

---

## 0021-abr-send-side-bwe.patch — adaptive bitrate

Drives a room's encoder bitrate from the **worst peer's** send-side bandwidth estimate. One encoder
serves the whole room, so the stream can only be as good as the worst receiver can carry.

**Why a rung is free.** `nvh264enc`'s `bitrate` is "changeable in the PLAYING state" — NVENC
reconfigures its rate controller in place. No pipeline rebuild, no renegotiation, no reconnect,
nothing the player sees. (Contrast a RESOLUTION change, which re-inits the pipeline — patch 0015 —
and is visible. ABR therefore adapts bitrate ONLY.) This is the opposite of the Jellyfin ABR restart
storm, where each rung meant a new transcode.

**Two things Pion does not give you for free.** Its default video codecs advertise
`goog-remb/ccm-fir/nack` and **no `transport-cc`**, and `RegisterDefaultInterceptors` installs
`twcc.SenderInterceptor`, which *generates* TWCC reports for INBOUND streams. We send and never
receive media, so neither helps. The patch adds:
1. `m.RegisterFeedback(TypeRTCPFBTransportCC, video)` — so the SDP asks the browser to report; and
2. `ConfigureTWCCHeaderExtensionSender` — which stamps the TWCC sequence number on OUTGOING RTP.
   Nothing in the default set does this, and without it the browser has nothing to report on.

Then `cc.NewInterceptor(gcc.NewSendSideBWE(...))` builds one estimator per PeerConnection,
**synchronously inside `api.NewPeerConnection`**, so `ApiFactory.newPeer` serialises creation and
drains a channel to claim the estimator that provably belongs to the PC it just made. The GCC pacer is
`NewNoOpPacer` — the leaky-bucket pacer would re-time our RTP, which is the latency this stack exists
to avoid. GCC is used purely as a congestion **detector**.

**Why we probe upward.** A send-side estimate can never exceed what you actually send — you cannot
measure capacity you never use. Following the estimate would pin the room at its opening bitrate
forever. So `pkg/worker/abr.go` treats "estimate keeping up with what we send" (≥95%) as permission to
raise the target 15%/tick, and drops straight to the estimate when it falls below 85%. Ceiling = the
room's chosen quality (the creator's t=104 bitrate, else the config default); floor = 1500 kbps.

`GstMediaPipe.SetVideoBitrate` remembers the target and **re-applies it after `Reinit`** — a geometry
change rebuilds the pipeline and with it the encoder element, which would otherwise silently revert to
the config bitrate. `Room.closed` became an `atomic.Bool` because the ABR goroutine reads it while
`Close()` writes it.

Measured on the LAN (GameCube, F-Zero GX, ceiling 14000): `6000 → 13875 kbps` over six ticks, and the
browser's `inbound-rtp` confirms the **bits** moved (12.0–15.9 Mbps received vs 4.5–5.7 at a 5 Mbps
ceiling), 60 fps / 0 freezes throughout. A genuine back-off was observed unprompted:
`13875 → 11011` when the estimate fell, then recovery to `12662`.

Re-generate: apply 0001–0020 to a clean `13852a7`, commit, apply this feature, `git diff --cached`.
(`git add -N` for the new `abr.go` diffs it against an empty blob instead of emitting `new file mode`,
and the patch then fails to apply.)

---

## 0022-raw-frame-dump.patch — debug: raw frames at `video_cb`

Env-gated (`CLOUD_GAME_FRAME_DUMP_DIR`, off otherwise: one nil check on the emulator thread). Writes
`frame-NNN.bin` (raw core pixels) + `.json` (w/h/stride/bpp/pixfmt/flip), bounded by
`CLOUD_GAME_FRAME_DUMP_COUNT` (default 3) after `CLOUD_GAME_FRAME_DUMP_SKIP` frames (default 300).

It exists because two questions **cannot** be answered from the browser:

**1. Anti-aliasing.** H.264 quantization erases exactly the sub-pixel edge gradients MSAA/SSAA create,
so an A/B on the decoded stream measures the encoder, not AA. On raw frames the answer is unambiguous.
Metric: the *share* of edges that are hard, `hard/(hard+mid)` — AA converts hard edges into mid ones.
(Absolute edge counts are useless: the same config twice gave hardEdge 0.157% and 0.576%.)

| Core | MSAA option | Verdict |
|---|---|---|
| `gc` (dolphin) | `dolphin_anti_aliasing: "2"` | **works** — hardShare 6.29% → 4.08%, no overlap over 3 runs each |
| `n64` (gliden64) | `mupen64plus-MultiSampling` | **inert** — BIT-IDENTICAL raw frames at 0/4/8 |
| `psp` (ppsspp) | `ppsspp_mulitsample_level` | **inert** — BIT-IDENTICAL |

libretro's `hw_render` FBO isn't multisampled; only Dolphin's OGL backend manages its own framebuffers.

**2. What size a core really hands us.** CloudRetro sizes the encode from the core's *base* geometry, so
a core that renders bigger internally looks identical downstream. The dump prints the truth:

| System | raw (`video_cb`) | delivered | |
|---|---|---|---|
| n64 / gc / psp | 960×720 / 1280×1056 / 960×540 | same | clean |
| **dc** | **1280×960** | 640×480 | **3 of 4 samples discarded** (nearest downscale) |
| **ps2** | **1024×896** | 1280×896 | non-integer 1.25× *nearest* stretch → `scaleMethod: bilinear2` |
| snes | 256×224 | 768×672 | intended integer nearest upscale |

**3. Reference-grade encoder tuning.** Dump 120 deterministic frames, encode/decode them offline with
`gst-launch` + `nvh264dec`, and PSNR against the source. That is how `profile=high` was found to be worth
**+1.02 dB at 8 Mbps** (and `multi-pass=two-pass-quarter` to be *worse*, and `spatial-aq` to be
unjudgeable by PSNR). Preset barely matters: p6 adds +0.01 dB over p4 once High profile is on.

Bit-identical raw frames across runs also proved that **mupen64plus is deterministic at a fixed frame
index**, which is what makes its attract demo a usable A/B fixture.

---

## 0023-fractional-scale-supersampling.patch — supersampling

One gate: `if conf.Scale > 1` became `if conf.Scale > 0 && conf.Scale != 1`. `round()` in the media pipe
already handled fractional scale, so that is the entire change.

**Why.** MSAA is inert on every core except Dolphin — libretro's `hw_render` FBO isn't multisampled, so
mupen64plus and ppsspp produce BIT-IDENTICAL frames with their MSAA options on or off (patch 0022 proved
it on raw frames). The AA that *does* work is to render big and average down. `scale < 1` shrinks.

Measured on raw frames from the **same source image** (so content cannot confound it), 1920×1440 → 960×720:

| resampler | hard edges |
|---|---|
| nearest-neighbour | 2.112% |
| **bilinear2** | **1.792%** (−15%) |
| lanczos | 1.969% |

> ⚠ A fractional scale MUST be paired with a smooth `scaleMethod`. Downscaling with nearest
> point-samples: it *aliases* rather than anti-aliases. That is precisely the bug DC had for months.

Cost, per room, measured (worker CPU, 13700K): n64 0.37 → 1.02 cores; psp 0.64; gc 1.38. Two concurrent
rooms fit comfortably. GPU stays ≈20%.

Live config now renders above the delivered size on n64 (1920×1440 → 1280×960), psp (1920×1088 →
960×544), gc (1920×1584 → 1280×1056, on top of working 4× MSAA) and the flycast family (1920×1440 →
1280×960, where `scale: 2` multiplies flycast's permanently-640×480 base).

---

## 0024-av1-optin.patch — AV1 as an opt-in codec

Two small cases: `video/av1 -> webrtc.MimeTypeAV1` in the codec→mime map, and an `av1` branch in
`buildVideoPipeline`. Pion already registers AV1 in `RegisterDefaultCodecs` and ships an AV1 RTP
payloader, so that is the whole worker-side change.

**⚠ `av1parse` in the caps is not optional.** Pion's AV1 payloader needs TEMPORAL-UNIT aligned OBUs;
without `alignment=tu` the receiver gets fragments of a TU and renders nothing.

**Measured at MATCHED actual bitrate** — `nvav1enc` overshoots its CBR target by ~19%, so a naive
same-`bitrate=` comparison flatters AV1 by 2.4 dB and is meaningless:

| | bytes | Y-PSNR |
|---|---|---|
| H.264 baseline @8000 | 2,145,612 | 37.872 dB |
| AV1 @7200 | 2,111,238 | **38.756 dB** |

**+0.884 dB for 1.6% fewer bits** (≈15% bitrate saving). Verified live: n64 1280×960, 60fps, 0 freezes,
browser reports `mimeType: video/AV1` and `colorSpace.fullRange: true`. Client decode cost rises
(2.8–3.6 ms vs 1.3 ms) but is nowhere near the frame budget.

**Not the default**, purely a browser question: Chrome **and Firefox** both advertise `video/AV1`
receive; **Safari does not**. CloudRetro negotiates one codec per room, so an AV1-only track means no
video at all on a browser that cannot decode it. Flip `encoder.video.codec: av1` to use it.

### Why `profile=high` was reverted (and AV1 is the better answer)

High profile measured **+1.02 dB at 8 Mbps** and was shipped for about an hour. Without a profile in the
caps, `nvh264enc` emits `profile_idc=66` (Constrained Baseline) — exactly what Pion advertises
(`profile-level-id=42001f`). Forcing High emits `profile_idc=100` while the SDP still promises baseline.
Chrome decodes it anyway; Firefox's WebRTC H.264 decoder is OpenH264, which decodes only *some* subsets
of High ([bug 1411681](https://bugzilla.mozilla.org/show_bug.cgi?id=1411681)) — a friend on Firefox would
get **no video, silently**. Not worth 1 dB. Claiming it honestly would mean registering H.264 with a
matching `profile-level-id` in Pion's MediaEngine, which drops Firefox from H.264 entirely.

## 0025-abr-vbv-scaling.patch — cap keyframe bursts, proportionally

`SetVideoBitrate` (patch 0021) now also sets `vbv-buffer-size = kbps/20` (≈3 frame-budgets at 60fps,
floor 100) on every rung — the property is changeable in the PLAYING state, so it rides the existing
ABR path for free.

**Why:** with NVENC's default (unbounded) HRD, a CBR IDR bursts to 4–6× the per-frame budget
(measured: a 94 KB IDR in a 14 Mbps stream whose frames average 22 KB). That transmission bulge
delays everything queued behind it on a thin link — audio included — and is the mechanism of the
residual keyframe hitch. Capping at ~3 budgets **halves the worst burst** for −0.65…−1.5 dB, paid
only in transparent-quality territory (post-supersampling content undershoots CBR; see
docs/arcade-quality-plan.md §17 for the full matrix, including why a FIXED cap is wrong: −3.5 dB at
14 Mbps, free at 5 Mbps).

**Best-effort by design:** `vbv-buffer-size` is an nvh264enc property (x264enc names its equivalent
differently), so a failed set logs at debug and must never kill the bitrate rung that just succeeded.

## 0026-abr-backoff-confirmation.patch — don't trust a single low estimate

The ABR loop (0021) backs off only after **two consecutive** below-threshold ticks; the counter
resets on any non-low tick, and while the estimate *stays* low the backoff applies every tick, so a
real outage is not smoothed away — reaction to genuine congestion is delayed by exactly one second.

**Why:** we send unpaced on purpose (NoOpPacer — pacing is queued latency), so every encoded frame
leaves as a microburst, and GCC's delay-based detector occasionally reads one as congestion even on
idle wired gigabit (measured: a single-tick 11000→7828 dip on an ethernet LAN over a direct host
pair, recovered in 4 s). Those one-tick artifacts no longer move the encoder at all.

## 0027-per-room-cheats.patch — cheats the room creator picked in the lobby

Adds `core_options` (map) and `cheats` (list of raw codes) to the `GAME_START` (t=104) packet, relays
them through the coordinator, and applies them in nanoarch around `retro_load_game`:

- **Core options** merge into `n.options` *before* the load (the core reads its variables while
  loading), after the config and the `game-overrides.json` manifest — the room creator's explicit
  choice is the last writer. This is how PS2's `pcsx2_widescreen_hint` gets switched on per room.
- **Cheat codes** go to `retro_cheat_set` *after* the load, because a code pokes the loaded game's
  memory. A new C bridge exposes `retro_cheat_reset` / `retro_cheat_set`; nanoarch had neither.

**Two traps this patch is shaped around:**

1. `api.StartGameRequest` (worker) must carry the fields, not just `GameStartUserRequest` (browser).
   The coordinator decodes into the former and **silently drops unknown JSON fields** — the exact way
   per-room bitrate (0018) went nowhere for days. Rebuilding the worker alone is not enough:
   **rebuild the coordinator too.**
2. `nanoarch.Nan0` is a process-wide singleton and one worker serves rooms back to back, so
   `retro_cheat_reset` is called on **every** load (not only when the room has cheats) and the staged
   options/codes are cleared once consumed. Otherwise a room that asked for no cheats inherits the
   previous room's.

**Not a guarantee that a code does anything.** Every libretro core *exports* `retro_cheat_set`; many
implement it as an empty stub (pcsx2, dolphin, ppsspp read their own cheat formats from disk instead).
Nothing observable distinguishes the two at runtime, so the SITE decides which systems may offer codes
— `ArcadeCheatCatalog.SupportsCheatCodes`. See docs/arcade-cheats.md.

## 0028-inframe-packet-pacing

Opt-in smoothing of each encoded frame's RTP burst. The stack deliberately runs GCC with a
NoOpPacer (a leaky-bucket pacer queues RTP — latency), and on a LAN wire-speed bursts are
harmless — but on cellular/shallow-buffer paths a ~14–40 KB burst every 16 ms overflows the
queue and panics the bandwidth estimator (measured on real 5G 2026-07-09: estimate collapse
to 525 kbps, the whole session pinned at 1500–2500 of a 5000 ceiling). When a room's t=104
carries `pace` (ms; the lobby Network profile sends 5 for Remote, 8 for 5G), an outbound
interceptor breaks the frame's packets into groups of 8 with a 1 ms pause between groups,
capped at `pace` pauses per frame — Windows timer granularity makes grouping, not per-packet
spacing, the portable unit. Pacing off (0, the LAN default) is byte-identical to pre-patch.
The coordinator relays the new StartGameRequest field — as with 0018/0027, REBUILD THE
COORDINATOR TOO or the field is silently dropped.

## 0029-pli-keyframe-responder

Answers RTCP PLI/FIR with a forced keyframe (rate-limited to one per 500 ms), via the patch-0012
RequestKeyframe window. REQUIRED once the encoder runs infinite-GOP intra-refresh (see
docker/arcade/gst-nvcodec-intrarefresh.patch): with no periodic IDRs and feedback ignored, one
unrepaired loss freezes a viewer until the next join — the 2026-07-09 bug, reintroduced. The RTCP
drain loop already existed and discarded everything; this parses it. Room wiring is a process-global
callback set in NewRoom (one worker = one room, the SetPacing pattern).

## 0030-serialize-on-core-thread

Save-state was broken two ways for LibCo cores (whose entire lifecycle — retro_run,
context_reset — lives on the same_thread pthread): retro_serialize_size ran on the CALLER's
goroutine (the documented autosave AV for PPSSPP; PCSX2 died the same way — 0xC0000409 on the
Save button, live 2026-07-09), and same_thread_with_args read the Go pointer-to-size as the
size itself, handing every LibCo serialize/unserialize a garbage byte count (mupen's "flaky"
load states match exactly). serialize_size now runs via a new CALL_SERIALIZE_SIZE on the core's
thread, and the size travels by value. Also fails soft with an error when a core reports
serialize_size 0 instead of crashing on an empty buffer.

NOTE the header change: same_thread_with_args2's last parameter is now size_t (was void*).

## 0031-always-save-on-close

Save-on-quit was gated on HasSave() ("only re-save if saved before"), so a session where nobody
clicked Save harvested nothing and the vault's Continue slot stayed empty — and PS2 players
couldn't click Save at all until 0030. The close-time save is now unconditional: one serialize at
teardown (before Shutdown, core still live), invisible to players, and the vault always has
something to harvest. Periodic autosave (autosaveSec) stays 0 — flycast hitches visibly on every
serialize, and close-save covers continuity.

## 0033-coordinator-status-endpoint

Adds GET /status to the COORDINATOR (rebuild the coordinator, not the workers): a JSON list of
connected workers with their occupancy ({addr, port, zone, room, free}). Consumed by
scripts/watch-arcade-glworkers.ps1 check C to detect the room-close wedge (2026-07-10, twice:
snes9x + pcsx2 teardown hang): a worker stays "busy" at the coordinator forever while its log
goes silent, and every new room then hangs with a worker slot silently gone. A live room always
writes pace-diag every 5 s, so busy-at-coordinator + silent-log = wedged -> the watchdog
recycles the worker. NOTE the JSON "port" is the worker HTTP port from the handshake
(9000/9001...), NOT the UDP mux port -- the watchdog maps rooms to workers via the "New room"
log line instead. The coordinator is localhost-only behind the gateway; the endpoint leaks
nothing beyond room ids.

## 0034-worker-stable-singleport

One WebRTC ApiFactory (= one single-port UDP mux) per WORKER PROCESS, and the mux bind is now
STRICT — no port roll. Fixes the reconnect port-drift that killed live F-Zero GX rooms twice on
2026-07-10 (16:35 + 19:06): the factory used to be created inside HandleRequests, i.e. once per
coordinator CONNECTION, so when the worker's coordinator WS died and auto-reconnected it bound a
SECOND mux while the first stayed open, NewSocketPortRoll walked 8446→8448, and the worker
advertised ICE candidates the router doesn't forward — every room after the reconnect was
media-dead until watchdog check B recycled the process (taking whatever room it had just been
handed down with it). The factory depends only on config, so it now lives on the Worker struct
(created at boot, reused across reconnects: reconnect no longer touches the mux at all). The
strict bind retries up to 10×1 s for a stale predecessor's socket (runner respawn gap), then
fails the boot — an unreachable advertised port is strictly worse than a dead worker, and the
runner/watchdog respawn path already owns recovery. Worker-only (coordinator does not create
ApiFactory).

## 0035-worker-pong-heartbeat

The worker's coordinator link now sends an unsolicited PONG every 3.5 s from the WRITER goroutine
(client connections only; RFC 6455 §5.5.3 permits one-way pong heartbeats, and the coordinator's
PongHandler only refreshes its read deadline). ROOT CAUSE FIX for the recurring
"read tcp i/o timeout → worker reconnect → room dies at boot" chain: the read pump executes packet
handlers INLINE, and gorilla only answers server pings inside ReadMessage — so any handler that
blocks for seconds (a dolphin game start ~3.5 s, a close-save serialize, a PS2 load) starved the
coordinator's 7 s pong deadline whenever it straddled one (~55% odds at the 6.3 s ping cadence for
a dolphin boot, which is why GC rooms died constantly and 2D almost never). The severed socket
then canceled the game start ("malformed game start response"), and the worker's reconnect loop
Reset() every live room — the 2026-07-10 F-Zero GX "slow then crashed" report, live-fired again at
20:00 the same day under harness. Companion to 0034 (which stopped the SAME reconnect from
drifting the UDP mux port); with both, a slow handler costs nothing and a genuinely wedged pump is
the GL Worker Watchdog's job (busy-at-coordinator + silent log), not the pong deadline's. NOTE the
coordinator's 10 s RPC CallTimeout still bounds a game start — a cold-shader-cache boot >10 s fails
that one join cleanly (retry works; socket and rooms survive). Worker-only rebuild.

## 0036-per-room-video-codec

Per-room video codec override, end to end: `video_codec` ("av1"/"h264") on BOTH the user
INIT_WEBRTC packet (t=100) and the game start (t=104), relayed by the coordinator (rebuild it —
the JSON relay drops unknown fields, the 0018 lesson) and consumed by the worker twice: the
INIT value fixes each PEER's track mime (the PeerConnection is created before game start), the
t=104 value swaps the active codec in the room's WithOverrides encoder copy (before the bitrate
rewrite, so vbr lands on the right codec entry). The worker sanitizes against encoder.list and
falls back to the config default on anything unknown. Site side: the lobby's Codec pill →
CreateRoom {videoCodec} (allowlisted hard) → stored on the in-memory RoomState → `&codec=` on
EVERY member's WS URL (creator, joiners, ClaimSeat input-only sessions), because all tracks must
match the room's one encoder.

**Why:** AV1 (default since 0024) is a tablet-killer in practice — Chrome advertises software
dav1d, so a tablet NEGOTIATES AV1 it cannot decode at 1280x1056@60 in real time, and the
keyframeless intra-refresh stream gives the decoder nothing to skip to: video falls minutes
behind live audio (2026-07-10, Eric's tablet; audio rides the separately-decoded aux opus PC).
H.264 hardware-decodes on effectively everything and its gop-size=120 IDR cadence naturally
bounds receiver backlog. AV1 stays the room default; the creator picks H.264 when a tablet will
play. Rehydration caveat: the room codec lives in the site's in-memory state only — after a pod
restart a NEW joiner of a codec-overridden room falls back to the default track mime (video
broken for that joiner only; accepted rare-edge, noted in ArcadeRoomService).

## 0037-capture-caged-mod.patch
The "capture" caged mod — the heavy BROWSER lane (H5, docs/arcade-capture-worker-plan.md). A second
`app.App` implementation alongside libretro: instead of running a core it launches a native Windows
program through the gateway heavy contract (prepare/attach/finish), captures the desktop with
GStreamer `d3d12screencapturesrc` (WGC, `tonemap=reinhard` so HDR desktops don't wash out), feeds
BGRA frames + PID-scoped PCM into the UNCHANGED media/encode/WebRTC pipeline, and turns browser
RetroPad packets into ViGEm virtual Xbox 360 pads. New package `pkg/worker/caged/capture/` (capture/
pipeline/input/launch/monitor) + wiring: `caged.go` registers the mod + `CaptureSystem`; `worker.go`
loads it when `conf.Capture.Enabled`; `coordinatorhandlers.go` branches on `gameInfo.System ==
"capture"` (the ONE app-select point — libretro path untouched); `config/worker.go` gains the
`Capture` section. Adds `github.com/openstadia/go-vigem` (go.mod/go.sum) — needs `vigemclient.dll`
beside worker.exe (built from Nefarius source; not in this patch).

**Why:** heavy titles (yuzu/RPCS3/…) that structurally can't run in libretro become playable in a
browser tab with per-seat internet pads — the zero-install complement to the Moonlight/Artemis lane
(both launch paths coexist on the card). Only the capture worker enables it (zone=capture);
config in `docker/arcade/config.worker-capture.yaml`.

**Load-bearing worker gotchas baked into the code (all verified live):** the video pipeline stays
PLAYING for the process's whole life and gating is done by dropping frames — a d3d12 WGC capture
session can NOT be PAUSE- or NULL-then-resumed in-process (room 2 froze at ~1 fps otherwise);
per-room goroutines (exit-watcher, audio-router) capture their own proc and bail when
`a.gamePid` moves on, or a stale room blanks the next one; `Close()` nil-guards the re-push channel
and only `gwFinish`es when it actually acquired the lock (a prepare 409 must not release an Artemis
session of the same title). `cmd/capture-probe` (the hardcoded live harness) is intentionally NOT
in this patch.

## 0038-system-scoped-library-and-foreign-state-guard.patch

**What:** two independent worker faults that combined into "Gauntlet Legends won't boot" (2026-07-11).

*System-scoped library.* Stock indexes the ROM library by BARE FILENAME (`res[value.Name] = value`,
"games with duplicate names are merged"), so of two same-named ROMs in different systems only the
last one scanned is reachable — **1352 names in our library collide across systems**. The ROM tree
walks alphabetically, so `ps2/Gauntlet - Dark Legacy (USA).cso` overwrote
`gc/Gauntlet - Dark Legacy (USA).gcz`, and the GameCube card silently booted the PS2 disc in PCSX2.
Now the library indexes by relative PATH and resolves within the room's system — which the room id
already carries (`sv-<user>-<game>-<slot>-<system>___<key>`, the site's `ArcadeSaveId`), so **no
protocol, coordinator, site or client change is needed**. On a miss inside a known system it REFUSES
rather than widening the search: booting another system's ROM is worse than not booting.

- The site's system code is not the core key and not always the folder (site `ps1` → folder `psx`,
  core `pcsx`; site `arcade` → `mame`). Mapping lives in config: `library.systemAliases`. **A new
  system whose folder name differs from its site code MUST be added there** or it silently falls
  back to the old cross-system lookup.
- Same name twice in ONE system is now ranked, not merged: a file whose core actually owns its folder
  wins. The library's supported-extension set is the union of every core's, so `psx/Foo.bin` is
  indexed too and `GetEmulator` hands it to whoever claims `.bin` (Atari's) — stock hid those by
  accident (the `.cue`, scanned later, overwrote them). Ambiguity is now logged, never silent.

*Foreign-state guard.* `RestoreSaveState` passed `len(st)` straight to `retro_unserialize`. Handed a
Dolphin state, PCSX2 logged `GS: Savestate version is incompatible. Load aborted.` and then
access-violated on the next tick (0xC0000005) — the worker died, the runner restarted it, the
gateway re-seeded the same save, and the room **crash-looped forever**. A core is entitled to crash
on foreign bytes; our job is not to hand them over. It now checks the save against
`retro_serialize_size()` first.

> **The test is size, but it must NOT be equality.** `retro_serialize_size` is not stable for every
> core: Dolphin reports **111,735,182** bytes five seconds into Gauntlet's boot (when the deferred
> restore runs) and writes **122,064,041** at session end. Strict equality refused the player's own
> GameCube save — caught only by testing the restore live. A foreign core is wrong by MULTIPLES
> (PCSX2 wants 50,599,322 for that same state, 2.4x; a PS2 state offered to Dolphin is 0.45x), so
> the ratio is bounded to 0.5x–2x. Refusing costs only the resume; the vault copy is untouched.

## 0039-per-user-memory-cards.patch

**What:** virtual MEMORY CARDS become per-user, like every other save.

A card is the one save class libretro never exposes: PCSX2 and Dolphin write theirs into their own
directories, so a card was ONE file shared by every player, outside the save vault and unbacked. For
some games the card IS the progress — Gauntlet Dark Legacy keeps its named characters on the
GameCube card, not in the save-state — so a player's characters were visible to (and deletable by)
everyone, and would die with the directory.

The WORKER does the seed/harvest, not the gateway: the COORDINATOR chooses which worker serves a
room, so the gateway can never know whose card directory is in play. The worker knows its own dirs
and runs exactly one room at a time. It parses the owner out of the room id (`ArcadeSaveId`), seeds
that user's card before `retro_load_game`, and harvests it back AFTER `Shutdown` (the core flushes
the card as it closes — harvest earlier and you vault a stale one).

- **Each GL worker now needs its OWN ConfDir** (`worker-gl`, `worker-gl-2`, …). Two workers sharing
  one would seed and harvest each other's cards — one player's characters handed to another. And
  `libretro\system` must be a REAL per-worker copy of the BIOS dir, **not a junction to the shared
  one**: PCSX2 writes its cards into the system dir, so a shared junction is a shared card.
  `register-arcade-glworker-task.ps1` now derives the ConfDir per worker so a re-register can't
  silently re-merge them.
- Config: `emulator.cardVault` + `emulator.cards` (`"<save|system>:<relpath>"`). **Only the card
  subtree moves.** The save dir also holds Dolphin's ~850 MB of shader caches — shared, disposable,
  and regenerating them per session (or vaulting them) would be absurd. That is why `uniqueSaveDir`,
  which scopes the WHOLE save dir, is the wrong tool here.
- A `.owner` stamp in the card dir recovers the previous player's card if a worker DIED without
  harvesting (the known teardown crash) — never clear one player's progress to make room for another.
- A user with no vault entry gets a FRESH card, not whatever the last player left behind. Existing
  shared cards are migrated into each player's vault out of band before first use, so nobody starts
  empty. Cards are backed up daily (`scripts/backup-arcade-memcards.ps1`).

## 0040-capture-d3d11-screen-source.patch

**What:** the capture lane's screen source moves from `d3d12screencapturesrc` to
`d3d11screencapturesrc` (config: `capture.videoSource`).

**Why:** `d3d12screencapturesrc` CRASHES THE WORKER whenever the captured app goes FULLSCREEN —
which every heavy title does (yuzu launches with `-f`). An out-of-bounds `std::vector<unsigned char>`
index inside `libgstd3d12` trips libstdc++'s bounds assertion and `abort()`s the process
(`0xC0000409`) 10–30 s into every session; Kirby never survived past 30 s. It is fatal rather than a
silent overread only because MSYS2 builds GStreamer with `_GLIBCXX_ASSERTIONS`.

> **Log adjacency lied twice here.** The last line before every abort was GStreamer's audio loopback
> (`fill_loopback_silence: Padding size 1056 is larger than or equal to buffer size 1056`) — a
> genuinely broken-looking message from the chattiest thread. Disabling the loopback path removed the
> message and the crash stayed. Only a gdb backtrace settled it: `abort ← libstdc++ ← libgstd3d12`.
> **Install gdb (`pacman -S mingw-w64-ucrt-x86_64-gdb`) and get a stack; don't infer from the tail of
> a log.**

Known upstream (fixed in MR 7293), but MSYS2's newest — 1.28.4 — still reproduces:
https://discourse.gstreamer.org/t/d3d12screencapturesrc-hangs-crash-when-using-a-full-screen-app/2088
Cost of d3d11: it lacks d3d12's HDR tonemapping (`tonemap=reinhard`), so an HDR desktop would look
washed out. Ziggy's desktop is SDR. Flip `videoSource` back to `d3d12` on a GStreamer bump.

## gst/0001-d3d12-dxgicapture-monochrome-cursor-oob.patch (GStreamer, not CloudRetro)

**The real fix for the capture-lane crash that 0040 worked around.** Rebuild + install with
`scripts/build-gst-d3d12-patched.ps1`; the source is gst-plugins-bad (match the installed GStreamer
version), and only the `d3d12` plugin is built.

`PtrInfo::buildMonochrom()` in `sys/d3d12/gstd3d12dxgicapture.cpp` reads the XOR half of a monochrome
cursor at `shape_buffer[src_pos + size]`, where `size` is the **destination** RGBA size
(`height * width * 4`) instead of the source offset (`height * Pitch`). A monochrome DXGI pointer is
a 1-bpp AND mask followed by an equally-sized XOR mask, both `Pitch`-strided — so for a 32×32 cursor
the buffer is `Pitch * Height = 4 * 64 = 256` bytes while `size` is `4096`. It reads ~16× past the
end, on EVERY monochrome cursor — which is exactly what Windows hands a FULLSCREEN game. Fatal rather
than a silent overread because MSYS2 builds libstdc++ with `_GLIBCXX_ASSERTIONS`: `operator[]`
bounds-checks and `abort()`s (`0xC0000409`). Kirby died 10–30 s into every session.

Still unfixed upstream in **1.28.4 and 1.28.5** (the known "fullscreen" MR 7293 is a different bug).
With the patch, d3d12 soaks 100 s clean and keeps its HDR tonemapping — which is the whole reason to
prefer it over the d3d11 fallback (`capture.videoSource`).

> ⚠ **A `pacman -Syu` that touches GStreamer overwrites `libgstd3d12.dll` with the stock build and
> silently brings the crash back.** Re-run `scripts/build-gst-d3d12-patched.ps1` after any upgrade.
> Stock DLL is kept at `D:\ArcadeStorage\backup\libgstd3d12.dll.msys2-stock-<version>`. Retire this
> patch once a release stops containing `src_pos + size` in that function.

## 0041-card-vault-dc-psp-and-close-wedge.patch

`pkg/os/os.go` (+tests), `pkg/worker/caged/libretro/frontend.go`, `pkg/config/emulator.go`,
`pkg/worker/room/room.go` — finishes the memory-card vault (patch 0039) for the last two systems that
keep progress outside a save-state, and stops a hung core from taking the whole worker pool with it.

**Cards for dc and psp.**
- **dc** — flycast's VMUs are LOOSE FILES (`vmu_save_A1..D1.bin`) sitting in the Dreamcast **system**
  dir, next to `dc_boot.bin` / `dc_flash.bin`. 0039's whole-subtree seed/harvest would have copied the
  BIOS into every player's vault and overwritten it on the next seed. Card specs therefore grow a glob
  form — `dc: "system:dc/vmu_save_*.bin"` — and every card operation (seed, harvest, crash-recovery)
  is pattern-scoped, so the BIOS beside them is invisible to all of it.
- **psp** — `psp: "save:PSP/SAVEDATA"`. Note the save root is the dir the CORES see,
  `<ConfDir>/libretro/legacy_save` (nanoarch), **not** `emulator.storage` (which holds CloudRetro's
  room-keyed save-states). Deliberately NOT `PSP/SYSTEM` (ppsspp.ini + CACHE) or `PPSSPP_STATE`.

**Vaulting rules** (all three of these were live data-loss paths):
- **Recovery now runs even when the previous owner is the SAME user.** It used to skip that case as a
  no-op. It is the worst case there is: a first-time player crashes before their first harvest,
  rejoins with an empty vault while their only copy sits in the worker's dir — and the "fresh card"
  branch wiped the session the crash had already failed to save.
- **Newer-wins.** A card stranded by a crashed worker is recovered by whatever room runs there next,
  which can be long after the player moved to the other worker and saved again. Recovering it must not
  drag their progress backwards. (`CopyTreeNewer`/`CopyGlobNewer`; `CopyFile` now preserves mtime, or
  the comparison would be meaningless.)
- **Harvest early and often.** Cards are vaulted on the autosave tick and BEFORE teardown, not only
  after it — the close harvest was a bet that the core survives its own shutdown, and flycast
  (`0xC0000374`) and PPSSPP (`0xC0000409`) routinely lose that bet. A torn copy is detected rather
  than guessed at: copy to `.part`, re-stat the source, discard if it changed under us, rename in.

**psp `noSaveStates`.** PPSSPP cannot be serialized *at all* — measured both ways. Off its libco
cothread (`skip_same_thread_save`) `retro_serialize_size` access-violates; ON it, the save works but
the core then **wedges** — 6 of 7 rooms booted, created their framebuffer, and never delivered another
frame. Patch 0039's autosave turned the first into a crash every 120 s. So the core keeps the hack and
never gets serialized: no autosave, no save-on-quit, no boot restore, and `Save`/`Load` return a clean
error. PSP's progress is its memstick card, which the vault now carries — the same bargain the real
console made. Revisit only with a PPSSPP whose libretro savestates work.

**The close wedge (`room.go`) — this is the "the arcade is full" bug.** Stock `Room.Close()` ran
`app.Close()` and only then told the coordinator the room was over. A core that never returns from
teardown therefore meant the coordinator was NEVER told: it went on believing the worker was BUSY with
a room that had no players left in it, and — one room per worker — the pool silently lost a slot.
Teardown now runs in a goroutine; if it does not finish, the worker **exits** (the runner replaces it
in ~4 s) and the coordinator drops it. It deliberately does NOT report the room closed in that case:
that would advertise a worker as free while its core is still stuck, and route the next player
straight into it. Note what is bounded — the teardown of a room that has *already* lost its last
player. It never policies a live session, so it cannot cut a slow game short.
