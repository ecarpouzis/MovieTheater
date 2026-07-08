# Arcade audio — remaining residual & next-stage research plan

Status as of 2026-07-06. The arcade `/arcade` audio had a loud, obvious hitch and a subtle residual.
This session **fixed the two big causes**; this doc is the handoff for the **remaining residual**.

## ADDRESSED 2026-07-08 — the residual is now attacked from four angles (patches 0018/0019 + client)

The residual below (audio arrives bursty on the bundled transport → NetEq over-buffers to ~260ms and
time-stretches) is now countered by a layered, mostly-safe set of levers. In effectiveness × safety order:

1. **Global default bitrate 8000→5000 kbps** (GL worker `config.yaml`). Smaller per-frame RTP bursts =
   less audio head-of-line blocking. Live on both workers.
2. **Per-room bitrate + FEC, creator-picked in the lobby UI** (patch **0018**, `Encoder.WithOverrides`).
   The room creator's choice rides the WS URL (`?vbr=<kbps>&fec=<0|1|2>`) → shim t=104
   `video_bitrate`/`audio_fec` → a per-room COPY of the encoder config (deep-copied, never mutates the
   shared config). Lower bitrate = smoother audio; FEC OFF on pure-LAN drops the LBRR packet-size bloat.
3. **Audio-only `jitterBufferTarget` (default 80ms, client)** — the PRIMARY, default-on fix. Gives NetEq a
   small STABLE target so it stops adaptively inflating + time-stretching. Video stays at 0 (separate
   stream ids → video isn't lip-sync-delayed). Tunable/disable via `localStorage arcade.audioJitterMs`.
4. **True un-bundle (now DEFAULT-ON, per user 2026-07-08)** — patch **0019** sets the Pion offerer to
   `BundlePolicyMaxCompat` (per-m-line transports; safe, default path unchanged); the shim then strips
   `a=group:BUNDLE` from its answer so audio negotiates its OWN transport (video bursts can't block it at
   all). Both transports still ride the one single-port UDP mux (rtcp-mux → no extra port). **Escape
   hatch** if a room ever fails to CONNECT (not merely sounds off): `localStorage arcade.noBundle="0"` +
   reload → bundled fallback. Still **judge audio on a real browser** (headless can't).

Deployed: site changes committed (`a208e2d`) + pushed; GL worker rebuilt from patches 0018/0019 and both
workers restarted (clean boot, both ICE IPs). **Still to do:** judge audio smoothness on a REAL browser
(the only valid place), then decide whether the un-bundle (#4) beats the jitter-buffer (#3) enough to make
it default. The four levers below (hypotheses) informed which knobs were exposed.

## What was fixed this session (don't re-investigate these)

1. **Periodic-keyframe hitch (the "every ~2 seconds" tick).** The H.264 encoder emitted an IDR every
   `gop-size=120` frames (= 2.0s @60fps); the browser stalled decoding each fat keyframe and ticked the
   audio. Fixed by running an effectively infinite GOP (`gop-size=100000` → **verified `keyFramesDecoded`
   stays 0** in a live session) and forcing a short **spaced keyframe burst only when a viewer joins**
   (`patch 0012` — `GstMediaPipe.RequestKeyframe`, `kfJoinWindow=360`/`kfJoinSpacing=30`, wired through
   `MediaPipe` + `room.Router.AddUser`). Late-join verified with the 2-player harness (P2 gets video).
2. **LAN hairpin (steady concealment).** A LAN browser was connecting to the workers' **public** IP, so
   every packet hairpinned out to the router's WAN edge and back → bursty late audio. Fixed by
   advertising **both** LAN + public ICE host candidates (`patch 0013` — `IceIpMap` is now comma-split;
   `.env ZIGGY_PUBLIC_IP=98.15.249.217,192.168.68.69`). **Verified: `concealedSamples/s` dropped from
   6,049/s (12.6%) → 0/s** on the direct-LAN path.

Both deployed to the docker pool + the Windows GL worker (both rebuilt from the committed patches, clean,
no debug instrumentation). See commits `a428c23`, `6be8673`.

## The remaining residual (what to research)

Even on a **clean direct-LAN path** (candidate pair `…:8444`, 9ms jitter, **zero packet loss**), a live
Castlevania session still shows, from `chrome://webrtc-internals` inbound-rtp(audio):

```
jitterBufferTargetDelay   260ms      # Chrome insists on a big audio buffer
jitterBufferDelay          20→173ms  # actual buffer inflating toward the target
insertedSamplesForDeceleration/s  153 → 4,053   # audio TIME-STRETCHED ~8% to build the buffer
delayedPacketOutageSamples  climbing in bursts   # occasional late-packet outages
concealedSamples/s          0 at snapshots, but cumulative grows in bursts
```

**Interpretation:** the server produces + ships audio perfectly (proven by the temporary server-side
`[AUDBG]` probe: steady ~44,100 frames/s IN, steady ~100 opus pkts/s OUT, ~20ms `maxGap`, no keyframes).
Yet audio arrives at the browser in **late bursts**, so Chrome's NetEq over-buffers to 260ms and
**time-stretches** the audio to fill it — that stretch + occasional outage is the residual the user hears.
Because it persists on a direct LAN path with the server sending evenly, the bursts are introduced
**after the encoder, in the send/decode path**, not the network.

### Leading hypotheses (ranked)

1. **Audio is BUNDLED with video on one transport** (`a=group:BUNDLE 0 1 2`, one candidate pair, rtcp-mux).
   When Pion sends an 8 Mbps video frame burst, the tiny audio packets queue behind it → late bursts.
   *Test:* un-bundle audio to its own m-line/transport, or make Pion's pacer prioritize audio. *Also cheap
   pre-test:* drop `bitrate=8000`→`3000` in `config.yaml` list.h264 (8 Mbps is overkill at 640×480) and
   re-read the client stats — if `insertedSamplesForDeceleration` and `delayedPacketOutage` fall, bundling
   contention is confirmed.
2. **Opus emit pairing.** The server `[AUDBG] maxGap` was ~20ms (frame-size=10ms → occasionally 2 frames
   at once). If Pion forwards those pairs, the client sees a bursty cadence. *Test:* log per-packet send
   timestamps in Pion / try `frame-size=20` or a different opus `audio-type`.
3. **Opus FEC overhead is pure waste on LAN and may inflate the client buffer.** `packet-loss-percentage=15`
   + `inband-fec=true` → **~99.99% of FEC packets are discarded** (near-zero loss), and LBRR redundancy
   ~doubles audio packet size. *Test (cheap, config-only):* lower `packet-loss-percentage` to ~2 or set
   `audio-type=restricted-lowdelay` (CELT, no LBRR) and re-check the client buffer/outage. Caveat: FEC was
   added for lossy N64 rooms (roadmap WS-A.2) — only reduce it if a per-zone/per-loss policy is kept.
4. **Chrome NetEq target is genuinely reacting to real jitter** we haven't localized. *Test:* try
   explicitly `receiver.jitterBufferTarget = 40` in `cloudRetroClient.js` and see if `jitterBufferTargetDelay`
   actually drops below 260 (it may be Chrome-floored, in which case app hints can't lower it).

### How to instrument (re-add for stage 2)

The temporary server-side probe was **removed for the clean build** — re-add it to read the real session:
- In `pkg/worker/media/gstreamer.go`: a once/sec `[AUDBG]` in `ProcessAudio` (raw PCM IN frames/s) and in
  `pullAudio` (encoded Opus pkts/s + `maxGap` between pulls). Add per-packet **send timestamps** in Pion
  (`pkg/network/webrtc`) to see the wire cadence after bundling — that's the missing measurement.
- Client side: `chrome://webrtc-internals` inbound-rtp(audio) `delayedPacketOutageSamples`,
  `insertedSamplesForDeceleration/s`, `jitterBufferTargetDelay`, `jitter`, `packetsLost`. These are REAL
  prod data even on the user's normal browser (only my headless *harness* client stats are artifacts).
- The `instrument.py`/`instrument2.py` edit scripts used this session are the template (job scratch).

## Related follow-ups (surfaced this session, not audio-core)

- **GL worker 8447** advertises public-only (its standalone runner didn't re-read `.env`). Restart that
  runner in the interactive session, or better, register it as a proper scheduled task like 8446.
- **Two-pool inconsistency.** There are two worker pools — 3 docker/WSL "main" + 2 Windows-native GL —
  and `ArcadeZoningEnabled=false`, so PS1/2D rooms **leak onto either pool at random**. User's chosen
  direction: **move to Windows workers for everything**, split by save-need (see `arcade-save-persistence-by-core`).
  Wiring: `ZoneForSystem` + enable zoning + retire docker. Autosave knob is the "which workers save" lever
  (PSX/2D/N64 → `autosaveSec:60`; DC/PSP → `autosaveSec:0` for the PPSSPP crash).
- **Movies hairpin too.** Same LAN-hairpin root affects HLS via `stream.carpouzis.com`. Fix = split-horizon
  DNS so `*.carpouzis.com` resolves to `192.168.68.69` on the LAN (HTTP/HLS, not WebRTC — different fix).
  Lower start/seek latency + no wasted WAN bandwidth; no audible "hitch" (HLS is buffered).
