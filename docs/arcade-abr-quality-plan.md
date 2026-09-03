# Arcade ABR quality plan — killing the "low-quality YouTube" stream

**Status (2026-08-04 EOD): Phases 0, 0.5, and 4 DEPLOYED; Phase 1.5's explicit-pick fix DEPLOYED.**
- Phase 0 (`cb73e2c`/`13e0410`): summary lines live on all three workers, coordinator relaying
  device_id/username, ArcadeLinkStat + Internal endpoint live; verified with a prod harness room +
  a marker-row POST. Baseline collection is RUNNING — let a few days of real sessions accumulate
  before Phase 1.
- Phase 0.5 (`def9b6a`): flap hysteresis live, K=5 (the 08-02 log shows a 4s dwell K=4 would have
  committed); `geomFlapsSuppressed=` on the close summary. Stuntman replay verification pending
  (Eric's save state staged on the test account; or Eric's next real session covers it).
- Phase 4 (`cd052fb`): "Codec: Auto" default probes hardware AV1 (powerEfficient) at room create;
  deliberate picks (codecChosen flag, or any stored h264) never migrated.
- Phase 2 (`78a092a`) DEPLOYED + VERIFIED: capture Auto derives — the site's isCapture pin is gone,
  and a live capture room logged `abr: auto ceiling 22394 — encode 1920x1080 @60, smooth/3d 0.180`.
  Verified precondition: deployed capture binary == GL build by hash (both lanes are one cmd/worker).
  Noted latent trap: capture's Scale() says "nearest-neighbour" at scale 1.0 — fix before ever
  scaling capture, else the derive flips to the magnified-2d class. VBV moves with the new ceiling
  (kbps/20 ≈ 1120) — judge capture on a real display; that's the Phase 3 knob shifting as a side
  effect. Phases 1, 1.5-Auto (need the baseline week), 3 (needs real-display A/B) remain PLAN.
- Phase 5 (fiber re-baseline) DONE 2026-09-02, the day after the cutover: uplink measured ~200 Mbps
  single-stream / ~630 aggregate; the three clamps moved together 25000 → 40000 ("Fiber · 40 Mbps"
  preset); `abrBppMagnified` 0.65 → 1.0 on a re-sweep to 50 Mbps that found the knee at ~1.0 bpp.
  Multi-viewer capacity on the new link still owed a real remote-peer session (see Phase 5 below).

## The problem, stated honestly

Rooms still spend too much of their life looking soft/blocky. The four guards + congestion
memory + same-host bypass (2026-07-26/30) fixed the *collapses*; what remains is that too many
sessions spend their first 15–35 seconds — or, for capture rooms, their whole life — well below
the quality the link could serve. Eric's report: "a lot of ABR issues where the stream looks
like low-quality youtube."

Decomposed, "looks like low-quality YouTube" is one of five distinct failure modes:

| # | Mode | Cause | Status |
|---|------|-------|--------|
| A | Soft picture at **room start**, every room, recovers after 15–35s | Cold opener `min(6000, 60%·ceiling)` + GCC-limited +15%/tick climb | **Unsolved for every non-same-host peer** (incl. wired LAN) |
| B | Soft picture that **sticks** after a transient dip | congKbps crawl — mostly by design, recovers in ~7s once the wall clears | Acceptable; one measured instance 08-04 cost ~15s post-recovery (field check below) |
| C | **Blocky at ceiling** (busy regions, `starved-cap 0, cong 0`) | Per-frame bit starvation, not link | spatial-aq shipped; VBV divisor is the remaining lever |
| D | **Capture rooms** soft always on Auto | Flat 12000 constant on the largest frame we send (1080p60 ≈ 0.097 bpp vs the calibrated 0.18) | Known gap, unbuilt |
| E | Weak peer drags an **h264 multi-peer room** | 80% base share — shallow ladder is structural | Partially mitigable (codec auto-pick) |
| F | **Ceiling thrash on geometry-oscillating cores** (found 2026-08-04 in the 08-02 logs) | Live derivation faithfully follows an interlace-style viewport flap; every flap commits a ceiling change and WIPES congestion memory | Phase 0.5 (hysteresis), unbuilt |

Mode A is the headline: it happens in *every* room for every remote/LAN peer, and Eric plays
many short rooms, so the ramp tax is paid over and over. Fixing A is the plan's centerpiece.

### Field check — two days of logs, 2026-08-04

A read of 08-02 → 08-04 across both GL workers and capture, done while writing this plan. Every
"ABR issue" in the window attributes cleanly, and none of them is mode A:

- **08-03 18:35–18:50, device 192.168.68.73** (N64 "Shotgun" ×3 at an *explicit* 25000 ceiling,
  then Bloodborne capture): every room collapsed to the floor. The rtt-probe shows why — mean RTT
  reached **1.4s, max 3.0s**, across several reconnects; the capture room's estimate hit 100 kbps.
  This is the *genuine-wireless* population: ABR cut and crawled exactly as designed, and warm
  start would change nothing. The same device sat at 3.4ms mean that morning and again later that
  evening — transient radio, not the device. Note the per-device + 12h-TTL rules would have
  behaved correctly here unprompted: the healthy morning row was 17h stale by the bad session.
- **08-04 13:12, device 192.168.68.64** (B3313, auto ceiling 13299): clean 8s ramp, ~100s flat at
  ceiling, then one RTT spike to 1.35s → cut to floor. ~75s total below ceiling, of which the last
  ~15s were congKbps crawl *after* `servable` had already recovered to 10445 — a measured instance
  of mode B, with the caveat that the estimate was still wobbling (10445 → 6196 → 6656), so the
  caution was arguably earning its keep.
- **08-02 11:58, Stuntman (PS2)**: the mode-F discovery — see Phase 0.5. Persona 3 FES flapped
  once the same day, briefly.
- Everything else in the window: clean ramp, flat at ceiling. No sawtooths, no false cuts.

The sample is small but it is exactly the split Phase 0's rows are designed to classify
automatically — both populations showed up in the wild inside 48 hours, and the plan's per-device
keying + TTL would have made the right call on each without any tuning.

## The constraint we must respect (and the loophole it leaves)

**+15%/tick is GCC's own maximum tracking rate.** The estimate can only rise as we actually
send more; any larger mid-session step is judged against an estimate taken at the old rate,
reads as congestion, trips the backoff, and poisons `congKbps` (fork `7b406fa` removed two
failed fast-ramp attempts; do not rebuild them).

But the constraint is about *climbing against a live estimator*. Two facts open a loophole:

1. `pkg/network/webrtc/factory.go` constructs each peer's estimator with
   `gcc.SendSideBWEInitialBitrate(bweInitialBps)` — a hard-coded **6_000_000**. The estimator
   *starts* wherever we tell it to. The 17–35s ramp is really "GCC certifying, from 6 Mbps,
   capacity we already certified yesterday, and the day before, on the same link."
2. The worker already *knows* the answer by the end of every session: the rate the link
   sustained. It just forgets it when the room closes.

**A warm start is not a fast ramp.** Opening the encoder *and the estimator* together at a
rate this link has already proven is the same move as the same-host bypass — declining to ask
the estimator for permission it has no information to grant — except gated on measured history
instead of topology. All four guards stay live underneath it; if the link got worse since
yesterday, one confirmed cut walks it down exactly as today.

---

## Phase 0 — Instrumentation: make "looks bad" attributable (no behavior change)

Everything later is an A/B; this is the harness. Also computes the number Phase 1 stores.

1. **`abr: summary` line at room close** (`pkg/worker/abr.go`, in the `r.Closed()` branch,
   next to the final rtt-probe): one greppable key=value line per room:
   - `open=` / `ceil=` / `codec=` / `layers=`
   - `rampTicks=` ticks from open to first tick ≥90% of ceiling (or `never`)
   - `atCeilPct=` % of ticks spent ≥90% of ceiling
   - `cuts=` confirmed cuts, split `cutsRamp=`/`cutsSteady=` (before/after first ceiling touch)
   - `starves=` same split — this encodes the "separate the RAMP from STEADY STATE" discipline
     into the log itself so nobody misreads a tail again
   - `congEpisodes=` / `congMaxHoldTicks=`
   - per peer: `sustained=` (highest rate held ≥5 consecutive healthy ticks — **this is the
     warm-start datum**), `rttMean=`, `rttSd=`, `path=` (samehost / direct / relay), `dev=`
2. **Session-close mirror to the site.** Reuse the Internal mirror callback path (the one RA
   unlocks already ride) to POST the summary per peer to the site. Store in a small table
   (`ArcadeLinkStat`: user, **deviceId**, system, codec, ceiling, sustainedKbps, rampTicks,
   atCeilPct, rttMeanMs, path, utc). `deviceId` is a random GUID the site frontend mints once
   into `localStorage` and sends with room create/join — one user's desktop, tablet, and phone
   must never share link history. This is what turns "it looked bad around 9pm" into a
   queryable row, and it is Phase 1's persistence for free.
3. **Answer the wireless question with data before crediting any fix.** Some share of bad
   sessions are genuine wireless capacity, and no ABR change fixes those — soft-but-smooth is
   the guards working as intended. The two populations separate cleanly in the rows:
   - *Ramp tax* — slow climb, then clean steady state at/near ceiling (`rampTicks` high,
     `atCeilPct` high, `cutsSteady`≈0). Warm start (Phase 1) fixes these.
   - *Genuine wireless limit* — low sustained, steady-state starves, cong episodes, elevated
     `rttMean`/`rttSd`. Warm start does NOT fix these; the levers are paceMs, codec/ladder,
     and expectations.
   Read the split from ~a week of rows; it is the denominator for judging Phase 1.
3. **Two weeks of baseline is NOT required** — a few days of normal play is enough to
   establish the ramp-tax numbers before Phase 1 lands. But Phase 0 must deploy alone first
   (one behavior change at a time; "two changes competing for blame is how you learn nothing").

**Exit criteria:** summary lines visible for both GL workers + capture; site rows accumulating;
baseline `rampTicks` distribution captured for Eric's usual devices.

## Phase 0.5 — Geometry-flap hysteresis: stop wiping congestion memory (fixes mode F)

**The finding (2026-08-04, from the 08-02 logs).** Stuntman (PS2, lrps2) toggles its output
between 1280x896 and 1280x447 every 1–3 seconds (interlace field behavior). The per-tick derive
follows it faithfully — which is the *spec* working — so the ceiling flapped `12373 ↔ 6173` for
the life of the room, and **every flap reset `congKbps`**: whatever ABR had learned about the
wall got amnesia every couple of seconds. Persona 3 FES did the same, briefly. The per-tick
derive is correct and stays; what's missing is hysteresis on *committing* the change.

**What this does NOT fix, stated up front:** each geometry change also destroys and rebuilds the
video pipeline (that's the core changing its frame, not ABR), and no ABR-side change touches
that glitch. The real cure for Stuntman-class titles is stopping the flap at the source —
check lrps2's deinterlace options and pin one in the per-game config for known flappers. This
phase only makes ABR stop *compounding* the problem.

**Design.**

- In the ABR loop, commit a derived-ceiling change (log line + `ceilKbps` + the congKbps reset)
  only after `ceilFn()` has returned the **same new value for K consecutive ticks** (start K=4;
  it only needs to exceed the flap period, and a legit mid-room resolution change landing K
  seconds late is invisible).
- A flapper then keeps its last committed ceiling and its congestion memory. Holding the
  pre-flap (higher) ceiling through the half-height fields does not endanger the link — the
  ceiling is a quality PERMISSION, and `servable`/starve logic still governs what is actually
  sent. It merely over-provisions the small frames, which is the cheap side of the trade; the
  alternative (commit the min) parks a mostly-full-height room at the half-height ceiling.
- Explicit lobby picks are already immune (fixed `ceilFn`), and a stable-geometry room commits
  exactly as today, K ticks later — this changes nothing outside flappers.

**Test:** replay Stuntman through the site. Success = at most one ceiling commit per *real*
resolution change, `congKbps` preserved across flaps, and the `abr:` log free of the
`12373 -> 6173 -> 12373` chatter. Then decide the per-game deinterlace pin separately.

**Order note:** cheap, independent, and worth landing **before Phase 1** — warm start makes
congestion memory more load-bearing, so protecting it from amnesia comes first.

## Phase 1 — Warm start: rooms open at the link's proven rate (fixes mode A)

**Design.** Per-user link memory, site-mediated, because the timing forces it: the estimator
is constructed inside `NewPeerConnection` *before* ICE, so the worker cannot key on remote IP
at construction time. The hint has to arrive with the join.

- **Learn:** worker computes `sustainedKbps` per peer (Phase 0) and mirrors it to the site at
  room close, tagged with `path` and RTT stats. **Samehost rows never feed warm values** (they
  measure our own encoder/CPU, not a link, and those rooms bypass ABR anyway).
- **Store:** site keeps recent rows per **user + deviceId** (`ArcadeLinkStat`) — one user uses
  many devices, and a warm value learned on the wired desktop applied to the Wi-Fi tablet is
  exactly the collapse the 60% opener exists to prevent. Warm value is computed from rows
  matching this device AND path class, with an **RTT-tiered haircut** (the rtt-probe
  measurements — wired 0.91ms vs Wi-Fi 3.86ms mean — graduate from observability to
  load-bearing here, as *stored history*, never a join-time guess):
  - device history sustained `rttMean < 1.5ms` (wired-class): `min(last 3 sessions) × 0.85`
  - wireless-class: `min(last 3 sessions) × 0.7` — wireless capacity varies day to day even on
    the same couch, so the haircut is deeper and the TTL matters more
  - no matching rows (new device, path-class mismatch, codec mismatch): **no warm** — today's
    opener, unchanged
  - all rows ignored after ~12h (conditions move; the couch is not the desk).
  Conservative on purpose: warm-too-low costs a short ramp from a high floor; warm-too-high
  costs a wireless collapse.
- **Send:** room create / join gains a `warmKbps` param alongside `vbr` (`ArcadeController.cs`
  ~1668 → t=104 fields → worker). Absent/0 = today's behavior, so a first-ever session, a new
  device, and every guest is unchanged.
- **Apply (worker):**
  - `factory.go`: replace the hard-coded `bweInitialBps` with a per-peer hint — the bweMu/bweChan
    serialization already guarantees the estimator being constructed belongs to the peer being
    created, so a "next initial bitrate" set under `bweMu` before `NewPeerBwe()` is race-free.
  - `abr.go`: opener becomes `clamp(warm, min(abrStartKbps, ceil·60%), ceil)` when a warm hint
    exists. Multi-peer: the room opens at the **min** over joined peers' warm values; a peer
    joining later than room start does not raise the rate (ramp handles that), only its
    estimator initial is set.
  - **Never** warm above the ceiling, never across a codec mismatch (h264/av1 ladders differ),
    never across a path-class mismatch (a relay session's history speaks only for relay
    sessions), and never from samehost history.
- **What this does NOT change:** the in-session ramp rate (GCC-bound, settled), the guards,
  the solo/multi layer rules, same-host bypass (same-host rooms don't need warm — they already
  jump to ceiling).

**Predicted result:** a returning link opens at e.g. 12–16 Mbps with the estimator *already
there* — no lag, no false cut — and covers the last 20% via the normal ramp in 2–3 ticks.
Time-to-90%-of-ceiling: ~17–35s → **≤3s** on Eric's regulars. This also finally serves the
wired-LAN desktop the same-host work explicitly left unsolved.

**Risks & tests:**
- *Wrong-high warm start on a degraded link* (the tablet scenario). Mitigations: per-device
  keying, RTT-tiered haircut (×0.7 for wireless-class), min-of-3, 12h TTL, all guards live.
  Test: artificially seed a too-high warm value for the tablet and
  verify the room settles within ~10s with one clean staircase, no sawtooth, and the next
  session's warm value has self-corrected downward.
- *Estimator initial too high is worse than encoder rate too high?* Verify pion gcc behavior:
  initial=proven-rate with actual send at the same rate should hold; A/B on the real tablet
  and the wired PC through the site (test-roms harness for LAN, real device for Wi-Fi — a LAN
  client cannot reproduce a wireless collapse).
- Deploy order: site table + mirror first (Phase 0), warm write path, THEN warm read path.

## Phase 1.5 — Network profile: measured Auto, and explicit picks that actually win

The Network dropdown is the un-measured sibling of quality-Auto. "Auto · match the frame" is
derived live from the frame we encode; the Network profile is a static, user-guessed three-way
switch that defaults to `lan`, whose label is discarded at the wire — and whose three options
today differ in exactly ONE live parameter: `paceMs` 0/5/8. All three send `audioFec: 1`, so the
FEC half of its job description does not actually vary (fec=2 is unreachable from the UI).

**Verified defect (2026-08-04, code read): an explicit LAN pick does NOT win on capture rooms.**
`ArcadeController` decides `paceMs = request.PaceMs > 0 ? request.PaceMs : (isCapture ? 8 : 0)` —
explicit 0 ("LAN · lowest latency") is indistinguishable from "no choice", so the capture
default of 8ms silently overrides it, and the code comment "An explicit lobby choice still wins"
is false for exactly that case. Compounding it, the UI cannot tell a deliberate LAN pick from
the seeded default: `setQ` persists `network: "lan"` even when the user only ever touched the
codec dropdown.

**Fix shipped with this plan revision (working tree 2026-08-04):**
- `PaceMs` becomes nullable: null = no choice (capture defaults to 8, GL to 0); explicit 0 =
  pacing off, honored everywhere.
- The UI stores `networkChosen: true` only when the Network dropdown itself is changed; room
  create sends `paceMs` only when the profile was deliberately chosen. Legacy stored values and
  fresh defaults omit it, preserving the capture-lane safety default.

**The Auto option (rides Phase 0/1, this phase's main deliverable):** a fourth, default entry —
**"Network: Auto · match the link"** — resolved site-side at room create from the same
`ArcadeLinkStat` device history warm start reads:

- wired-class history (rttMean < 1.5ms, low sd) → pace 0
- wireless-class → pace 5
- relay path or high rttSd → pace 8
- no rows → pace 5, NOT 0 — pacing is "invisible on a good line" (the code's own words), so the
  costs are asymmetric and the ignorant default must be the safe one. Today's ignorant default
  (`lan`, pace 0) is the risky one; the 08-03 field-check device (rttSd in the hundreds of ms)
  is precisely a link whose history says pace 8 while the default said 0.

This does NOT relitigate the banned lobby-label plumbing: the rule forbids feeding the
un-measured LABEL into ABR's rate logic. This is measured per-device history driving the two
non-ABR knobs the dropdown already owns, at join time; the label still never reaches `abr.go`.

Notes: the knobs are room-wide (one encoder), so Auto resolves from the CREATOR's device
history — a deferred idea is max(pace) over joined peers' histories. The three explicit options
stay as overrides. FEC: either give 5G a genuinely different FEC posture or accept the dropdown
is pacing-only — decide from Phase-0 audio-jitter evidence, don't guess. Relabeling ("audio
resilience & packet pacing") is optional polish.

## Phase 2 — Capture lane: derive the Auto ceiling (fixes mode D)

The capture lane encodes the largest frame we send and is the one lane whose Auto is a blind
constant (12000 = 0.097 bpp at 1080p60 — half the calibrated smooth target). Port the
`autoCeilingKbps` idea to the capture worker:

- Capture geometry is known to its pipeline (virtual display, 1920×1080@60 typical). Derive
  `bpp × w × h × fps`, clamp to `[5000, abrAutoMaxKbps]`. Start with `abrBppSmooth = 0.18`
  (captured desktop 3D content is the smooth class) → ~22.4 Mbps at 1080p60. The ceiling is a
  PERMISSION — remote peers get walked down by ABR as usual; same-host/LAN is where this pays
  today, and it is the named highest-value fiber prep.
- Then flip the site: `ArcadeController.cs` sends 0 for capture Auto (today 0 would fall to the
  config default — the derive must land in the capture binary FIRST, and the site change must
  check worker version or deploy after).
- Optionally calibrate: run `scripts/calibrate-arcade-bpp.sh` against captured frames (the 0.18
  figure was cross-checked on GameCube, never on capture content).

**Deploy discipline:** capture is its own binary, own ConfDir, port 8448; keep
`config.worker-capture.yaml` in lockstep with the GL config (diff the encoder blocks — the
spatial-aq drift already happened once).

## Phase 3 — At-ceiling fidelity: VBV divisor A/B (fixes mode C)

For rooms that ARE at ceiling and still block in busy regions. spatial-aq is shipped on all
lanes; VBV is already dynamic (`SetVideoBitrate` sets `vbv-buffer-size = max(kbps/20, 100)`,
patch 0025 — the yaml comment claiming "~1 frame VBV" is stale). The remaining lever:

- A/B `kbps/20` vs `kbps/13` vs `kbps/10` (≈3 vs ≈4.6 vs ≈6 frame-budgets at 60fps) on a
  worst-case 2D scroller (Sonic) and one 3D title.
- Judge **perceptually on a real display** (the PSNR A/B falsely rejected spatial-aq once) AND
  measure the latency cost: bigger VBV permits per-frame overshoot that drains later — run the
  latency probe (NES baseline ~86ms) and the test-roms frame-pacing probe before/after. If
  latency moves measurably, stay at /20.
- Optional second knob, same protocol: explicit `aq-strength` sweep (currently NVENC
  autoselect). `gst-inspect-1.0` every property on all four encoders before shipping anything —
  an unknown property wedges the room.

## Phase 4 — Codec auto-pick (mitigates mode E, opportunistic)

h264's 80% base share means one weak peer still drags a multi-peer room. The structural fix
doesn't exist (Constrained Baseline caps the ladder at 2), but rooms end up on h264 more than
they need to because it is a manual dropdown habit:

- Lobby probes `navigator.mediaCapabilities.decodingInfo` for hardware AV1 at room resolution;
  "Auto" codec picks AV1 unless the creator's device lacks it. Keep the explicit override.
- (Deferred idea, only if data shows it matters: warn in the lobby when a joining device forces
  the room's worst case.)

## Phase 5 — Fiber re-baseline (DONE 2026-09-02; cutover was 2026-09-01)

What was measured before anything moved:

- **Uplink**: curl to speed.cloudflare.com from Ziggy — ~200 Mbps on one TCP stream, ~630 Mbps
  across four parallel streams (the old line: ~35 Mbps up). IPv4 is now CGNAT; the media plane is
  IPv6-direct plus the Vultr door for v4-only viewers (`docs/site-ipv4-door.md`).
- **Phase-0 rows** (ArcadeLinkStat, 2026-08-05 → 08-26, all pre-fiber): the 13 direct-path rooms at
  the 25000 ceiling (Genesis, 08-15/16) sustained 0–14959 with `atCeilPct` 0 — the old uplink was
  the wall, so those rows say nothing about the new link and are NOT its baseline.
- **The 2D bpp sweep re-run to 50 Mbps** (`scripts/calibrate-arcade-bpp.sh` method, raw-vs-raw
  scoring — see the measurement trap in the skill): worst-frame error 1.9e-4 @25 → 1.41e-4 @32 →
  9.4e-5 @40 → 7.8e-5 @50. The "no knee" verdict of July was the sweep's range; the knee is ~1.0 bpp.

What moved (worker fork + site, same day):

- `abrAutoMaxKbps` 25000 → 40000, `Math.Clamp(..., 500, 40000)` in ArcadeController, lobby top
  preset "Fiber · 40 Mbps" (25 kept as a preset for anyone who chose it).
- `abrBppMagnified` 0.65 → 1.0: 3x gen/nes/snes Auto rooms derive 31–39 Mbps (were clamped at
  25000); gb 4x ~22; smooth/3D untouched. ~3 s more cold climb for 2D rooms, priced in.
- Not moved, on purpose: the 6000 opener, the ramp (GCC), `abrBppSmooth`, the capture lane.

Verified live the same evening (prod harness `drive-prod.mjs`, GL workers 1+2 recycled to fork
`fe24711`, site `b6ce75c5` deployed — the lobby chunk carries "Fiber · 40 Mbps"):

- Explicit 40000 (`--vbr 40000`, Sonic 2, gen): `Per-room encoder overrides: bitrate=40000` →
  `abr: start … ceiling 40000` → summary `atCeilPct=96 cuts=0 starves=0` over 25 ticks. The old
  site would have clamped that pick to 25000 before it ever reached the worker.
- Auto (same game): `abr: auto ceiling 26507 — encode 768x576 (256x192 x3), magnified-2d, 1.000
  bpp` at boot, then the per-tick derive followed Sonic's switch to 320x224 and the summary closed
  at `ceil=38657 atCeilPct=91 cuts=0` — the derive, the K=5 commit and the new target all in one
  room. (Harness rooms are same-host: they prove the plumbing, not the link.)
- Later the same evening (Eric: "when I stream Bloodborne it looks like potato"): the capture
  pseudo-core got `bppTarget: 0.32` (1080p60 → 39.8 Mbps; the 0.18 class default it inherited was
  cross-checked on GameCube-class 3D, and every August Bloodborne room derived 22394), the live
  capture config was redeployed, and the capture worker binary was brought up to the fork build
  (`ad98409a`; the old `435fc927` still carried the 25000 clamp and would have pinned the new
  target there). Bloodborne harness room: `abr: auto ceiling 39813 — core-override, 0.320 bpp`,
  `atCeilPct=98 cuts=0` over 79 ticks. Same-host, so it proves the ceiling, not the picture.
- Same commit, movie/TV side: the ABR ladder gained 30 and 20 Mbps 4K rungs (probed: a 94.5 Mbps
  4K HEVC source comes back 3840x2160 at both caps, 2560x1440 at 12) and a rung must sit ≤85% of
  the source bitrate to count. Details in the `movie-streaming` skill.

Still owed:

- **Multi-viewer capacity on the new link** — needs real remote peers (the Ziggy harness is
  same-host and never touches the uplink). Read `abr: summary-peer` rows dated after 2026-09-01,
  compare rooms at DIFFERENT ceilings.
- Warm start (Phase 1) is now the thing that makes a 39-Mbps Auto ceiling *feel* good at room
  start; the fiber rows are its denominator once they exist.

---

## Order, and why

`0 → 0.5 → 1 → 1.5 → 2 → 3 → 4 → 5`. Phase 0 is prerequisite instrumentation and Phase 1's
data source. Phase 0.5 is a small, independent guard that Phase 1 leans on (warm start makes
congestion memory more load-bearing, so stop wiping it first). Phase 1 attacks the mode Eric
hits every session. Phase 1.5's Auto profile rides Phase 0/1's rows and plumbing (its
explicit-pick fix is independent and already in the working tree). Phase 2 is one lane but a
total fix for it. Phase 3 needs careful A/B time. Phase 4 is small and independent. Each phase
deploys alone.

## Hard rules this plan already respects (do not relitigate)

- No faster in-session ramp — removed twice, root cause is GCC's tracking rate (`7b406fa`).
- No lobby Network-label plumbing into ABR — it defaults to `lan`, carries no information.
- No resolution adaptation — pipeline re-init is a visible glitch.
- Solo rooms never drop layers; multi-peer layer logic untouched.
- Verify encoder properties with gst-inspect before shipping params; keep the two configs in
  lockstep; GL config deploys to BOTH `worker-gl` and `worker-gl-2` ConfDirs.
- Fork changes: commit to `movietheater-fork` → `scripts/export-arcade-fork.ps1` → commit
  `fork.patch` → push `github`. Worker swap: `.stop` file, verify by HASH, 8448 = capture.
- Judge smoothness on a real display, never headless Chrome; separate RAMP from STEADY STATE
  before concluding anything from an `abr:` log.

## Success criteria

| Metric (from Phase-0 summaries) | Today | Target |
|---|---|---|
| Time-to-90%-of-ceiling, returning link | 17–35s | ≤3s |
| % ticks ≥90% ceiling, solo healthy link | (baseline TBD) | >95% |
| Steady-state starves/min on the Wi-Fi tablet | (baseline TBD) | no regression |
| Capture Auto ceiling at 1080p60 | flat 12000 | derived (~22 Mbps, ABR-governed) |
| Ceiling commits on a Stuntman-class flapper | one per 1–3s, congKbps wiped each time | ≤1 per real resolution change, congKbps preserved |
| Eric's report | "low-quality youtube" | ramp invisible on regular devices |
