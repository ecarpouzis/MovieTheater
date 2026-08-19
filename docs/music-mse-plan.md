# Music: one continuous stream (MSE + fMP4)

Status: **SHIPPED — historical.** All 5 phases complete 2026-08-12; the MSE engine is the DEFAULT
player. Post-plan work (the hidden-restart mint carry, seekDetour's engine parking, `park:live`)
lives in the `music` skill (`.claude/skills/music`) and the `music-sleep-stall` case file — those
are the current truth. This doc remains as the spec record: the measured numbers (quota, lanes,
sleep-viability arithmetic) and route mechanics that code comments cite by §.

Plan for taking JavaScript out of the track boundary. Original status: **THE GATE RAN ON THE PHONE
2026-08-11 (Android Chrome 150) AND THE CENTRAL BET HELD: fetches issued while hidden completed
(2/2, 176 ms / 85 ms). All three changeType joins continuous on the phone, mp4a.6B refused, quota
11.85 MB, worst hidden execution gap 84 s. The run also produced the missing design rule — the
sleep-phase audio died when a 96 kHz FLAC (~0.3 MB/s) met the quota: 40 s of runway against an
84 s gap ran dry, `waiting` fired, the silent page lost its audible exemption and froze. Rule:
a treatment is sleep-viable only if `quota ÷ bitrate > worst execution gap` — ~1 Mbps ceiling on
this phone. MP3 and universal AAC pass comfortably; 44.1 FLAC is borderline; hi-res bit-perfect
FLAC is NOT sleep-viable and takes the universal lane while hidden (the phone-proven rate-switch
`changeType` is what makes that legal). ENDURANCE RE-RUN PASSED 2026-08-11 15:50: 13m11s hidden,
ZERO dry buffers, 10/10 hidden fetches, worst gap 270 ms — a page that keeps audio flowing keeps
its execution; the 84 s gaps belonged to the dying run. Engine design still assumes the
conservative 90 s. PHASE 1 GATE: PASSED. **PHASE 2 SHIPPED 2026-08-11 (commit e3ede0b), flag OFF:
the engine (`MusicMseEngine.js`) + shared rules (`musicTreatments.js`) live behind
`music.engine="mse"` / `?mse=1`, modelled as a THIRD DECK inside MusicPlayerContext. Desktop
browser-verified: mp3 → flac-fMP4 → wma-transcode → 96 kHz flac crossed in ONE SourceBuffer,
analyser fed throughout; flag off, decks byte-identical. Three live-found bugs are now guards in
code: synchronous element claim (toggle-vs-start race detached the MediaSource), `bufferCeilingSec`
takes min not max (quota is physics), and a low-water mark (no hysteresis = 80 fetches/track).
KNOWN GAPS until Phase 3/4: progress bar reads queue-time and seek is wrong under the flag
(timeline module owns it); fat tracks re-fetch ~3× on resume (lanes can't Range).
PHASE 2 GATE: PASSED ON THE PHONE 2026-08-11 ~20:01–20:44 (Android Chrome 150, `?mse=1`,
build v2 with the sticky-lane fix): ~40 min hidden, NINE hidden track boundaries, zero stalls;
fat FLAC resumed sticky on fMP4 (~22 s cadence, advancing cursor, ONE demote line per track);
every subsequent ~1.8 Mbps track demoted once to universal, changeType at the switch, buffer
climbed 43 s → steady 180 s; endOfStream on queue exhaustion, element drained its last 161 s.
⚠ OPEN: at queue end the element fired `waiting` instead of `ended` after draining — harmless
there, but the cross-engine deck handoff RELIES on the real `ended` after endOfStream(); guard
this (treat near-end waiting-after-endedStream as ended) before Phase 5. Also observed: each
hidden universal top-up re-ran ffmpeg on the gateway (piped stdout, no Range) — ~80 encodes in
one night; setting `MusicUniversalCacheDir` in prod makes it one encode + cheap Range serves.
PHASES 3+4 SHIPPED 2026-08-12 (commit 18f93e8): `musicTimeline.js` is THE elementTime ⇄
(track, offset) mapping; `trackTime()` on the context feeds the bar, lyrics and Media Session;
seek goes through tested `seekPlan` (in-buffer = native currentTime set; out-of-buffer =
engine restart at the track, logged — piped lanes have no Range, so mid-track re-entry is
physics, not a TODO). The queue-end guard is live (stall on ended stream at buffer end ⇒ real
`ended`, same onEnded as the deck flip) and a finished queue is LATCHED so wake shows finished
instead of restarting the last track at 0:00. Gateway universal cache LIVE same day with LRU
eviction (hit-touch recency, evict-to-90% on miss over cap, only own-named files).
PHASE 5 SHIPPED 2026-08-12 (commit 7ab06e3): **THE ENGINE IS THE DEFAULT PLAYER.** Every
browser that proves a treatment gets MSE with no flag; `?mse=0` opts out and is remembered;
`?mse=1` clears the opt-out; browsers proving nothing (no MediaSource; iPhone until the MMS
seam is built) keep the decks automatically, and every in-session failure still falls to the
deck floor with an incident filed — watch kind "mse" incidents for the rollout verdict.
THE PLAN IS COMPLETE. Residual (not blockers, watched via incidents): deck hand-off driven by
the queue-end guard untested in the field; seek during a cross-engine flip untested;
MMS/iPhone seam unbuilt (iPhones ride the deck floor).**
Companion to `music-plan.md`; cite sections from code the same way.

## Why

The album stops at a track boundary on a phone with the screen off. Four fixes have landed and each
made it rarer without ending it:

1. the stall watchdog no longer reads a screen-off page as a dead stream;
2. the boundary hands off synchronously instead of awaiting a signed URL;
3. two `<audio>` decks, so a boundary is a flip rather than a `src` swap;
4. the next deck is fetched by the ELEMENT (not a JS `fetch`) and pre-rolled muted.

They share one shape: **the boundary is still a JavaScript event.** A backgrounded renderer is
throttled, so every one of them is a race against being allowed to run. Eric's framing is the test
this plan has to pass — *"Spotify's web player has no such trouble"* — and the reason it doesn't is
that its boundary isn't script at all. The queue is one continuous media resource.

Also ruled out by Eric, correctly: buffering further ahead. A queue is hours long; holding it in
memory does not scale and only moves the failure later.

## The tension this design must answer first

The hard-won lesson of fix #4 was **"a JS `fetch()` is the first thing a backgrounded page stops
being allowed to run"** — and MSE moves fetching back into script. Every append starts as a `fetch`.
That is not a contradiction to wave past; it is the design's central bet, and it has to be named:

- **Why it should still work:** with MSE the element is *never* idle. Appends run ahead of the
  playhead (as far as the SourceBuffer quota allows — see "Route mechanics") while audio is
  continuously in flight, so the page holds its background-audio licence at every instant — unlike
  the deck world, where the licence lapsed exactly at the boundary. Spotify's web player is
  precisely this shape (MSE + a JS fetch loop) and survives the screen going off.
- **Why it might not:** the fetch for track n+1 will routinely be *issued while the page is already
  hidden* (screen went off during track n). If mobile Chrome throttles that fetch into never
  landing, the buffer runs dry at the boundary, the element stalls, the audio stops — and a stalled
  element has no audio in flight, so the licence is lost and we are back in the same bug wearing a
  new architecture.

**Phase 1 therefore measures exactly this on the real phone**: a fetch issued from the append loop,
screen off, MSE audio playing — does it complete? If it does not, the design is falsified outright
and no client engineering rescues it; stop and rethink rather than building Phase 2.

## What the incumbents do — and the one structural lesson to steal

Spotify, YouTube (Music), Pandora and Amazon Music web players are all the same shape under the
hood: **MSE, one persistent element, and a server-normalized catalog.** Every track arrives as the
same codec in the same container at the same sample rate — AAC-in-fMP4 (`mp4a.40.2`) or Opus-WebM,
picked ONCE per session by capability probe. That is the part this plan's earlier drafts
under-weighted: the incumbents never execute a `changeType` at a track boundary, never cross a
sample-rate switch, never mix containers in one SourceBuffer — not because those are handled well,
but because **their streams are homogeneous by construction**. Heterogeneity is where this plan's
residual risks live (the `changeType` gate, the 96 kHz question, the mp4a.6B trap, Firefox's
missing MP3 decoder), and the incumbents simply don't have it. On iPhone they use Managed Media
Source or fall to natively-played HLS; on browsers below that, they push the app.

We can't copy the ingest pipeline (the library is the library, and bit-perfect FLAC is a feature),
but we can steal the shape as a *tier*: a **universal treatment** that any MSE browser accepts —
transcode to AAC-fMP4 at the gateway, 44.1 kHz, channel count preserved. It goes at the bottom of
the treatment matrix and buys three things at once:

1. **Every format becomes MSE-able on every MSE browser** — .wma, .ape, odd sample rates, all of
   it. Deck-only tracks stop existing except on browsers with no MSE at all, which means the
   fragile cross-engine joins become a rare fallback instead of a routine event in mixed queues.
2. **A homogeneous-session mode** for browsers that can't take the bit-perfect treatments
   (Firefox's MP3 gap, any `changeType` refusal, any rate-switch failure): run the *whole* session
   through the universal treatment and there are zero `changeType`s and zero rate switches to
   survive — the exact Spotify shape, boring on purpose.
3. A better answer than "decks" for every ladder rung that used to end there.

This revises an earlier judgement in this doc: re-encoding lossy MP3 to AAC was "rejected" when the
only argument was staying in MSE for its own sake. Against the actual goal — **playback that never
stops** — continuity outranks fidelity wherever the bit-perfect route is the one that can stop.
Policy: bit-perfect treatments wherever they are *proven* (Chrome: raw MP3 + FLAC-fMP4, still the
default, still 94% of the library untouched); the universal treatment wherever they are not.
Fidelity degrades gracefully; stopping doesn't.

## What is measured (and what is not)

Verified in a real Chromium (headless 148, desktop). The probe scripts (`mse_probe.py`,
`mse_changetype.py`) were session scratchpad and are **not in the repo** — this table is the record,
and Phase 1 re-proves the load-bearing rows in the browsers that matter anyway:

| Question | Answer |
|---|---|
| `audio/mpeg` (raw MP3) in MSE | **supported** |
| `audio/mp4; codecs="flac"` in MSE | **supported** |
| `audio/mp4; codecs="mp4a.6B"` (MP3-in-MP4) | **NOT supported** |
| `changeType` mpeg → mp4/flac | OK |
| `changeType` mp4/flac → mpeg | OK |
| alternating four times | OK |
| `mode = "sequence"`, `timestampOffset` | available |
| REAL BYTES (2026-08-11, probe page, desktop Chrome): mp3↔flac join, 44.1→96 kHz join, stereo→mono join | **all continuous, zero stalls** |
| audio SourceBuffer quota (measured) | **11.80 MB** |
| 96 kHz after 44.1 kHz appended with the SAME MIME and **no** `changeType` | **SourceBuffer errors ~200 KB in** |

That last row is a load-bearing correction: `changeType` is required at every **sample-rate or
channel-count** switch too, not only container/codec — the MIME string being identical proves
nothing. The probe page's `switchReasonFor()` is the tested rule.

That third row is load-bearing: **remuxing MP3 into MP4 would have broken every MP3 in the library.**
The obvious "normalise everything to one container" design is wrong, and only cost one probe to find.

Measured from the catalog:

| | |
|---|---|
| mp3 | 34,299 tracks (81%) |
| flac | 5,509 (13%) |
| 44.1 kHz stereo, all codecs | **94%** |
| distinct (codec, rate, channels) combos | 26 |
| albums internally mixed | **102 of 2,919** (3.5%) |

**NOT verified — these gate the work:**

- that real bytes append and play across a `changeType` (the probes above are API-level acceptance,
  not playback);
- **mobile** Chrome, which is the only place the bug happens. Desktop support does not imply it;
- **a fetch issued while hidden completing** (the tension section above — the design's central bet);
- **which events actually fire with the screen off, and how sparsely** (the event census — "Route
  mechanics" sizes every lookahead from this number);
- **the real audio SourceBuffer quota** — Chrome's default is on the order of 12 MB, *less than one
  large FLAC*; the append window arithmetic depends on the measured value, and `QuotaExceeded` on
  the first big FLAC would otherwise be discovered in production;
- what happens when the sample rate changes across a switch (44.1k → 96k);
- **Firefox** — and here there is prior art in this codebase: Firefox's MSE **has no MP3 decoder**.
  MP3 audio copied into the movie player's HLS froze Firefox at 0:00 (fixed browser-aware in commit
  `558b70f`; see `streamCapabilities.js` and its `supportsMp3` probe). Expect raw `audio/mpeg`
  appends to be rejected there — `isTypeSupported` should say so and rung 1 catches it, but the
  capability check must be per-FORMAT, not just "is MediaSource present". `streamCapabilities.js`
  is the pattern to copy;
- iOS Safari, where MSE on iPhone is restricted (Managed Media Source). **Answered: the failing
  device is Android Chrome**, so iPhone is not a gate — but the site must keep working on every
  browser/phone combination, which is why routing is capability-probed per format (below) and the
  engine keeps a Managed Media Source seam (Phase 2). Any browser that proves nothing simply keeps
  today's deck player.

## Mixed FLAC/MP3 queues — the explicit requirement

A playlist mixing formats freely must work. It does, and this is the mechanism:

- **One** `SourceBuffer`, `mode = "sequence"` so appended tracks land back-to-back without computing
  timestamps.
- **`changeType()` at every switch of container/codec OR sample rate OR channel count** (the
  measured rule — same-MIME rate switches error without it; see the results table). Verified in
  both directions, alternating, and with real bytes across all three switch kinds.

**Routing is capability-driven, not browser-shaped.** Each source format has an ordered list of
candidate MSE *treatments*; at startup the engine probes each treatment with
`isTypeSupported`/`changeType` acceptance (the `streamCapabilities.js` pattern, which already does
exactly this for video) and each track takes its first supported treatment — or the decks. No
user-agent sniffing anywhere: Chrome, Firefox and Safari each get whatever they can prove, and a
browser that proves nothing gets today's player unchanged.

| Source | Candidate treatments, in order |
|---|---|
| .mp3 | **raw bytes as `audio/mpeg`** (the existing File lane, no ffmpeg, no server work) → universal |
| .flac | **fMP4 lane as `audio/mp4; codecs="flac"`** (`-c:a copy`, container change only: no decode, no re-encode, bit-identical, still lossless) → universal |
| .m4a (AAC) | *(later)* fMP4 remux `codecs="mp4a.40.2"` (`-c:a copy`) → universal |
| everything else (.ogg, .wma, .ape, …) | **universal** |

…where **universal** = the AAC-fMP4 transcode treatment from the incumbents section
(`audio/mp4; codecs="mp4a.40.2"`, 44.1 kHz, channels preserved) — the one row every MSE/MMS browser
supports. Decks remain below the whole matrix, for browsers with no MSE at all and as error
recovery.

Phase 2 implements the first two rows plus the universal row (the universal lane is a variant of
the gateway transcode route that already exists — see "Server work"). What this buys per browser,
with zero extra Chrome work: **Chrome/Edge (desktop + Android)** bit-perfect MSE for mp3+flac,
universal for the rest — the whole queue in one SourceBuffer; **Firefox** FLAC bit-perfect + MP3
via universal (its MSE has no MP3 decoder; a homogeneous session beats deck boundaries on a
sleeping phone); **Safari/iOS** universal homogeneous once the MMS seam is built, decks until then
— never worse than today.

⚠ **Never request the fMP4 lane for an MP3.** MP3-in-MP4 (`mp4a.6B`) is measured unsupported, and
the gateway does not guard against it — it will happily remux an mp3 into an fMP4 no browser
accepts. The client routing rule is the guard. Worth adding cheaply on the gateway too (reject
non-flac extensions on the Fmp4 route) so a routing bug fails loud at fetch time instead of as a
SourceBuffer error at a boundary.

So a shuffled playlist is a sequence of appends with a `changeType` whenever the format differs from
what the buffer currently holds. Format mixing is a normal case, not an edge case.

The residual risk is not the codec but the **sample rate**: 94% of the library is 44.1 kHz stereo, so
most switches are codec-only. A 96 kHz or mono track may not survive a `changeType` in the same
buffer — Phase 1 measures this, and the fallback ladder covers it either way.

## Architecture

One persistent `<audio>` whose `src` is a `blob:` URL for a `MediaSource`. It is never reassigned and
never goes to `HAVE_NOTHING`, which is the whole point.

```
queue ──► appendTrack(n)   fetch bytes ─► [changeType if needed] ─► appendBuffer
                                                    │
                        one SourceBuffer, mode="sequence"
                                                    │
   <audio src=blob:MediaSource> ──► MediaElementSource ──► visualizer / destination
```

**Timeline mapping is the real surface area.** The element has ONE clock; the UI thinks in tracks.
A single module owns `elementTime ⇄ (trackId, offsetInTrack)` from a list of
`{ trackId, startSec, durationSec }`, and every consumer goes through it:

- progress bar and the scrubber
- `seek()`
- **lyrics sync** (currently reads `currentTime` directly)
- Media Session position state, and its `seekto` handler
- the stall watchdog's progress mark

Two properties the module must have:

- **Track starts are corrected from `buffered`, not trusted from the DB.** `durationSec` is close but
  not exact; over an hours-long queue the error compounds, and lyrics are the consumer that notices
  a boundary drifting mid-line. After each track's append completes, the NEXT track's `startSec` is
  read off the SourceBuffer's actual buffered end.
- **MP3 boundaries carry encoder delay/padding** — the classic tiny gap or tick between raw-appended
  MP3s. The deck player has the same property today, so it is not a regression; it is written down
  here so nobody chases it as a bug. (Truly gapless MP3 means parsing LAME headers and trimming;
  explicitly out of scope.)

**The boundary becomes bookkeeping.** Nothing happens in the media pipeline; a `timeupdate` that
crosses the next track's start updates the index for the UI. If the renderer is frozen, audio
continues regardless and the UI catches up on wake — which is the entire goal.

**Memory is bounded by eviction, not by restraint — and by quota whether we like it or not.**
`SourceBuffer.remove()` drops what is behind the playhead. Chrome's default *audio* SourceBuffer
quota is small (on the order of 12 MB — less than ONE large FLAC track), so the working unit is a
window of seconds, not tracks: chunked appends ahead of the playhead, aggressive eviction behind,
sized by the measured quota (Phase 1). An hours-long queue costs the same as a short one. The
window arithmetic that keeps this alive while asleep is in "Route mechanics" below.

**What an element error means now.** A `MediaError` no longer condemns one track — there is no `src`
to swap, so it ends the *whole MediaSource*. Recovery is: build a fresh MediaSource and re-append
from the current track at the current offset, once; if the same track kills it again, that track goes
to the deck path (rung 6). The existing park/budget philosophy (`shouldPark`, `recoveryDecision`)
carries over — a network-level failure on a hidden page parks, a content failure spends budget.

## Server work the phases need (site half of Phase 0 — DONE, not yet deployed)

All items below are implemented (unstaged in the working tree as of 2026-08-11): `Start` returns
`fmp4Url` (flac only) + `universalUrl` + `sampleRateHz`, `StartBatch` exists (cap 200, `skipped`
list), `Capabilities` returns `fmp4Enabled`, the gateway guards Fmp4 to `.flac` (409) and serves
the universal lane with an optional on-disk cache (`MusicUniversalCacheDir`/`MusicUniversalCacheMaxMB`,
temp-file + atomic rename). Kept for the record — this is what was needed and why:

- **`/API/Music/Stream/Start` returns `fmp4Url`** alongside `url`, minted from the *same* token —
  the token is lane-agnostic by design, the route picks the treatment. Only for tracks that need it
  (flac), or always; either is fine, the client routes by `mimeType` regardless.
- **`/API/Music/Capabilities` advertises the lane** (e.g. `fmp4Enabled`, same server-side condition
  as `transcodeEnabled` today) so the engine can route without a discovery 404. The 404 fallback
  (rung 4) stays regardless, because the site deploys on push and the gateway does not — capability
  said yes, gateway not yet redeployed, must still degrade quietly.
- **Batch Stream/Start** (N trackIds → N signed URLs, one round trip) for the rolling pre-mint
  window in "Route mechanics". Minting stays exactly what it is today — sign a capability, no play
  count, no session — just N at a time.
- **The universal lane**: a variant of the existing transcode route that outputs AAC-fMP4
  (`mp4a.40.2`, 44.1 kHz, channels preserved) instead of streamed MP3 — same token, same
  concurrency cap, one more route constant in `MusicStreamRoutes`. Unlike the `-c:a copy` lanes
  this is a real encode (~1–2 s CPU per track), which is what makes **on-disk caching keyed by
  (path, mtime)** worth building alongside it rather than deferring: the second play of anything
  becomes pure file serving.
- Optional but cheap: the gateway's Fmp4 route rejects non-flac extensions (the mp4a.6B guard above).

## The visualizer must not break

A hard constraint, and MSE makes it *safer* rather than riskier:

- Today `createMediaElementSource` must be called on **both** decks; a deck that misses it plays
  silent forever once the graph exists. With one persistent element there is exactly one source node,
  routed once. (While the Phase 2 flag is on, the decks still exist for fallback — the MSE element is
  simply a third element routed through the same graph; `graphSourcesRef` already handles N elements.)
- The element's source becomes a same-origin `blob:` MediaSource URL, so the graph cannot be
  CORS-tainted. Fetching moves into JS, where CORS applies to us and not to the element — the
  gateway's ACAO already admits the site origin, and `fetch(url, { credentials: "omit" })` is the
  established pattern (`fetchToDeck`).
- `ensureAudioGraph()` keeps its current shape and lazy-on-first-open behaviour.

Verification: open the visualizer, play across a FLAC→MP3 boundary, confirm audio and that the
analyser still produces data. A silent-after-boundary regression is the specific failure to watch for.

## Phases, each with a gate

**Phase 0 — foundation. Gateway half DONE, site half NOT.**
`MusicStreamRoutes.Fmp4` + the gateway lane (`-c:a copy`, shares the transcode concurrency cap, 404s
without `FfmpegPath`) — committed in `c507d19`. ffmpeg confirmed present on the gateway by exercising
the `.wma` transcode route in prod end to end. Capability probes above. Remaining: the "Server work"
section — small, but Phase 2 cannot start without it.

**Phase 1 — prove the bytes, ON THE PHONE. GATE.**
Deploy the gateway (NSSM on Ziggy — rebuild + redeploy required, see `stream-gateway-deploy-on-ziggy`;
service paths are UNC, not `L:`). Then build a **committed probe page** — BUILT: `/music/mse-probe`, a self-gating diagnostics route,
one tap runs everything walk-away style with server-picked tracks — that:

1. appends one real MP3 (raw) and one real FLAC-fMP4 (from the deployed lane) into one SourceBuffer
   with a `changeType` between them, and asserts **continuous playback across the join**;
2. repeats with a 96 kHz FLAC and a mono file;
3. **measures the audio SourceBuffer quota**: chunk-appends a large FLAC-fMP4 until
   `QuotaExceededError`, reports the byte count — the append-window arithmetic in "Route mechanics"
   is sized from this number;
4. **screen-off test**: starts MSE playback, waits for the screen to go off, then issues the
   next-track fetch *while hidden* and reports (on wake, on-page — remember the diag lesson: evidence
   must survive) whether the fetch completed and the boundary crossed. This is the design's central
   bet from the tension section;
5. **event census, screen off**: while playing, logs every event that arrives (`timeupdate`, timer
   ticks, `updateend`, `progress`, `ended`, …) with timestamps, and reports the gap distribution on
   wake. The worst gap is the lookahead floor for every route.

The page doubles as a **capability-matrix reporter**: it runs the treatment probes from the routing
table and prints the verdicts, so answering "what does browser X on phone Y get?" is forever "visit
the URL and read the table" — that is how the general case stays measured instead of assumed.

The GATE runs on the actual failing phone: **Android Chrome**; desktop was API acceptance only.
Firefox and any Safari at hand are worth a visit for the matrix, not for the gate (Firefox: expect
MP3 unsupported — confirm `isTypeSupported` says so).
*Nothing below starts until this passes.* If the hidden fetch never lands, the design is falsified —
the fallback ladder is the answer and the client engine is not worth building.

**Phase 2 — engine behind a flag.**
`MusicMseEngine` alongside the existing deck player, off by default — flag via localStorage
(`music.engine = "mse"`) plus a `?mse=1` override, same pattern as `?diag=1`. Playback and queue
advance only; no seek, no lyrics. Routing comes from the capability matrix (the mp3 + flac
bit-perfect rows plus the universal row); any format without a proven treatment, and any
unsupported condition, falls back to decks. Both
paths must coexist — this is not a rewrite of the player. Cross-engine boundaries are pre-rolled
flips **from day one** (see the invariant section): even on Chrome a queue can hold a deck-only
format, so "MSE track → deck track → MSE track" is a normal Phase 2 sequence, and each of those
joins must keep audio in flight, not just the MSE-internal ones. Phase 2 also lands the two
route-mechanics pieces the deck floor itself needs — the rolling pre-mint window (with the batch
endpoint) and the `ended`-anchored deck chain replacing the `timeupdate`-driven leads — because the
ladder is only as strong as the floor it falls to, and both are improvements to today's player
independent of MSE. Two seams to cut correctly the
first time, because retrofitting them means reopening the engine core: the media-source
constructor is abstracted (`MediaSource` today, `ManagedMediaSource` for iPhone later — same
interface, plus its `startstreaming`/`endstreaming` hints), and treatments are the matrix's rows
rather than an if/else on extension (so AAC-fMP4 later is a row, not a rewrite). **Incident reporting from day one**: the engine reports through `MusicPlaybackIncident`
(kind `"mse"`) exactly as the deck player does — a screen-off failure that isn't self-reported is
invisible, and that lesson is already paid for. Every fallback logs its rung via `diagLog`; the
first rung use per session also files an incident (routine rung use flooding the table would drown
the signal).

**Phase 3 — the timeline module.**
`elementTime ⇄ track` mapping with buffered-corrected starts, then move progress, seek, lyrics and
Media Session (position state *and* `seekto`) onto it. Tests first: a boundary crossed mid-lyric is
the regression that will not be noticed by eye.

**Phase 4 — eviction, long queues, and seeks.**
`remove()` behind the playhead; measure memory on a 3-hour queue. Verify a seek backwards into
evicted range re-appends rather than dying — and a seek FORWARD past the appended range (a scrub to
a track not yet fetched) tears down and rebuilds cleanly rather than sitting on `waiting` forever.

**Phase 5 — the phone, and the flag comes off.**
The incident reporter already collects failures unprompted, so this is measured rather than asked
about: boundary incidents should go to zero. Keep the flag until, with the screen off the whole
time: (a) a full album crosses several boundaries; (b) a **mixed** queue crosses MSE↔deck
boundaries (seed it with a deck-only track between two MSE ones) — the invariant's hardest case;
and (c) a forced ladder rung (e.g. fmp4 lane blocked to trigger rung 4) still plays through. All
three are the same test at different rungs: sleep never stops playback, on any route.

## The invariant every route must hold: sleep never stops playback

**At no instant may the page have no audio in flight.** That is the whole of a hidden page's
licence to keep running — it is the property every fix so far was buying, and the property MSE
finally makes structural. Fallbacks may trade elegance, memory, even a gapless join. They may
never trade this. Concretely:

- **The deck path is the floor, with all four shipped fixes intact** — element-driven fetch of the
  next track, muted pre-roll before the boundary (`PREROLL_LEAD_SEC`), synchronous flip at `ended`,
  park/wake recovery. Falling back means landing on *that* player, never on anything weaker. The
  MSE work must not strip or bypass any of it: the two paths coexist (Phase 2) and the deck
  machinery keeps its tests.
- **Every cross-engine boundary is a pre-rolled flip.** Any boundary that leaves the MSE element
  for a deck (a deck-only format in the queue, rung 2's single-boundary flip, rung 4's
  FLAC-on-decks) — or returns from a deck to the MSE element — is a script event again, which is
  exactly the shape of the original bug. So the incoming element, whichever kind it is, is prepared
  like a deck is today: source installed (or buffer appended) well ahead, started **muted** before
  the outgoing audio ends, and the boundary itself is rewind-and-unmute — no load, no `play()` that
  can be refused, no network at the moment the page has least licence to use it. This applies from
  Phase 2 day one, because even on Chrome a queue can hold a deck-only format.
- The net effect: a queue mixing MSE-treatable and deck-only formats survives sleep exactly as well
  as today's player at those boundaries, and better everywhere else. No route regresses below
  today.

## Route mechanics: how each route survives an infinite queue asleep

The invariant above says *what* must hold; this section is *how*, route by route, with the trigger
for every step named — because the failed versions of this feature all died the same way: some step
was driven by a clock that does not run while the screen is off.

### The clock rule (what actually runs while asleep)

This project has already measured, on the real phone, what executes with the screen off while audio
plays (`music-stall-watchdog-fires-on-screen-off`): **`timeupdate` stops being delivered and
intervals stop firing or arrive minutes late** — while media *element events* demonstrably still
fire (`ended` runs today's boundary flip with the screen off, every night). Therefore:

- **No route may have a load-bearing step triggered by a timer or `timeupdate`.** Those are
  opportunistic accelerators for the awake case, nothing more. Load-bearing triggers are the events
  that fire regardless: `ended`, SourceBuffer `updateend`, `canplaythrough`, `error`, fetch
  completions, `visibilitychange`, `online`.
- **Every execution opportunity does ALL currently-possible advance work** ("earliest event", never
  "latest safe moment") — because the next opportunity is not schedulable.
- **No route may require a React render to keep audio flowing.** Index/state updates are
  bookkeeping that catches up on wake; the engine reads its plan from refs (the `handedOffRef` /
  `nextTrackRef` pattern already encodes this).
- Phase 1's probe page runs an **event census**: screen off, audio playing, record which events
  arrive and the gaps between them. The measured worst-case gap is an input to the lookahead
  arithmetic below, not a guess.

⚠ Consequence for the CURRENT deck player: its `PREFETCH_LEAD_SEC`/`PREROLL_LEAD_SEC` triggers are
`timeupdate`-driven, i.e. they exist only while awake. That is the residual hole the plan's intro
describes. The re-anchoring below fixes it and is worth shipping even before the MSE engine.

### URLs: minted while awake, never needed while asleep

Every route needs signed URLs, and minting one is a JS fetch — the least reliable operation on a
sleeping phone. So no route is allowed to *need* a mint while asleep: the player keeps a **rolling
pre-mint window** covering the next several hours of queue (tokens are stateless, free to sign, and
last 6h — minting ahead costs nothing, by design). Topped up at every execution opportunity while
awake. Server work: a **batch Stream/Start** (N trackIds → N URLs in one round trip) so a 500-track
queue is a handful of requests, not 500. A sleep longer than the token lifetime expires the
window's tail; that route parks and heals on wake — bounded, logged, and no worse than today.

### MSE route, asleep, forever

The chain: fetch in chunks → `appendBuffer` a chunk → `updateend` fires (guaranteed: it is the
completion of our own append) → check `currentTime` vs buffered end (readable synchronously inside
any handler, no `timeupdate` needed) → below target: append the next chunk / open the next track's
fetch; at target: stop and wait for the next opportunity. Steady state asleep is a cycle of
(execution opportunity → top up window → go quiet). The window target is
`max(worst measured execution gap + one fetch time, next boundary) + margin`, capped by the
SourceBuffer quota. `waiting` (buffer ran dry mid-play) is the failure signal, not a mechanism: its
handler appends whatever is in hand immediately AND files an incident, because reaching it means
the arithmetic above was wrong somewhere and that must surface, not vanish into a recovered gap.
And it is worse than a glitch: **a page whose audio has stopped loses its audible exemption and
freezes**, so the first `waiting` while hidden is usually also the last execution — measured
exactly so on the phone (2026-08-11: buffer dry → silence → frozen until wake).

**The sleep-viability rule (measured on the phone, 2026-08-11): a treatment may be appended while
hidden only if `quota ÷ bitrate > worst execution gap`, with margin.** Measured inputs: quota
11.85 MB, worst hidden gap 84 s → ceiling ≈ 1 Mbps on this phone. Raw MP3 (~5 min of runway in
quota) and universal AAC (~6 min) pass comfortably. 44.1 kHz FLAC (~70–95 s) is borderline —
judge per-track by real bitrate (`SizeBytes / DurationSec`), never by format. Hi-res FLAC (~40 s
of runway) is NOT sleep-viable: while hidden, any track whose bitrate breaks the ceiling is
appended from the **universal lane** instead — the rate-switch `changeType` proven on the phone is
what makes that switch legal — and bit-perfect appends resume once the page is visible again.
Fidelity while watching, continuity while asleep.

### Deck route, asleep, forever (the floor)

Anchor everything to `ended`, the one event proven to fire asleep. At boundary k, inside the
handler, synchronously: flip to the deck holding track k+1 (prepared one boundary ago) →
rewind/unmute (local ops, nothing refusable) → then immediately prepare track k+2 on the now-idle
deck: URL from the pre-mint window (no fetch), `src`/`load()` (the ELEMENT fetches — survives
sleep), start it muted. Every boundary thus leaves the next one fully prepared, with no near-
boundary trigger required: the chain is self-sustaining on `ended` alone, indefinitely.

**The honest floor:** a file bigger than Chrome's ~16 MiB media buffer cannot be made sleep-proof
on the deck route. Streaming it gets evicted and re-requested mid-song (a sleeping phone can't
service that); downloading it whole is a JS fetch (needs awake). Today's player has exactly this
hole; rungs that land on decks inherit it; the MSE route (chunked append + evict, no per-file
buffer cap) is precisely how big FLACs get fixed. Stated so nobody reads "decks" as "safe".

### Cross-engine joins, asleep

- **MSE → deck** (next track is deck-only): the MSE buffer must never simply run dry — that is
  `waiting`, stopped audio, licence lost. Instead, after the last MSE track's final append, call
  `mediaSource.endOfStream()`: the element then fires a REAL `ended` at the exact end of the audio,
  and the standard deck-flip machinery above takes over (the deck was installed + muted at an
  earlier opportunity, per the clock rule).
- **Deck → MSE**: the deck's `ended` flips to the MSE element, whose buffer was appended at prior
  opportunities (starting as early as the boundary that started the deck track). Its `play()` at
  the join is **the one refusable operation left in the whole system** — same risk class as today's
  non-prerolled flip, caught by `resumeOnWake` when refused, logged always. If Phase 5 shows it
  being refused in practice, the mitigation is to keep the MSE element playing muted appended
  silence during deck tracks so it never needs a `play()` at all — designed but not built until
  the incident data asks for it.

## Fallback ladder

Never remove the deck path. Fall back a rung on any of these, and log which rung was used. Every
rung's boundary mechanics obey the invariant above — a rung changes *who plays the bytes*, never
whether audio stays in flight:

1. **A format's bit-perfect treatment unsupported** (`isTypeSupported` false — per-format, which is
   what catches Firefox's missing MP3 decoder) → that format takes the **universal treatment**;
   still one SourceBuffer, still no script at the boundary.
2. **`changeType` rejected, or a sample-rate/channel switch the buffer won't take** → route the
   offending track through the **universal treatment** (44.1 kHz normalized: the switch it refused
   no longer exists). If universal is also refused mid-buffer → finish the current buffer,
   deck-flip that one boundary, fresh MediaSource after it.
3. **Repeated `changeType`/switch trouble in one session** → go **homogeneous**: the whole session
   through the universal treatment, zero switches left to survive.
4. **Gateway lanes 404/5xx** (no ffmpeg, or not yet redeployed — fMP4 and universal die together):
   MP3 still rides MSE raw; FLAC and transcode-only formats fall to decks. The degraded-est rung
   that still plays.
5. **Append error / QuotaExceeded** → evict, retry once, then decks for that track.
6. **Element MediaError** (which kills the whole MediaSource) → rebuild once from the current
   position; same track kills it twice → that track via decks.
7. **No MSE and no MMS at all** → decks, i.e. today's player with its four fixes plus the
   route-mechanics re-anchoring.

Rung 4 matters operationally: the site deploys on push, the gateway does not. A site that is ahead of
the gateway must degrade quietly, exactly as the token design already ensures for the other lanes.

## What can honestly be promised (the "can I assume it just works?" answer)

Directly: **"all queues, all media, all phones, never stops" is not assumable — for anyone.** It is
a property to be *earned per environment and then continuously measured*, which is what the probe
page (capability matrix + event census, run by visiting a URL on any device) and the incident
telemetry are for. Even the incumbents don't reach "all": Spotify's web player is killed by
aggressive OEM battery managers too. What this plan delivers, tier by tier:

| Environment | After full build-out | Mechanism |
|---|---|---|
| Android Chrome (the gate) | **Yes — the whole mixed queue**, bit-perfect for mp3+flac, universal for the rest, one SourceBuffer, no script at any boundary | Tier 1 + universal |
| Any other MSE browser (Firefox, desktop Safari, Edge, …) | Yes for the whole queue, at whatever fidelity its probes prove — worst case fully homogeneous AAC | matrix + universal |
| iPhone Safari | Yes once the MMS seam is implemented (a later phase, not Phase 2); decks until then | MMS + universal |
| No MSE at all | Today's player + the route-mechanics re-anchoring — materially better asleep than today, but boundaries are element-flips, not buffer continuations; big files stay the known hole | decks floor |

And the hard ceilings no web player escapes, stated so they're never debugged as regressions:
**(a)** an OS that kills the browser's media process (OEM "battery optimization") ends playback —
only a native app with a foreground service goes further; **(b)** a sleep longer than the token
lifetime (6 h) parks and heals on wake rather than playing through; **(c)** a network that stays
down longer than the buffered window parks and heals — that is the park machinery working, not a
route failing.

## Open questions for Eric

1. ~~**Which phone?**~~ **Answered: Android Chrome.** That is the Phase 1/5 gate. The general case
   (any browser/phone) is handled by capability-probed routing + the ladder — no browser ends up
   worse than today's player — with iPhone's Managed Media Source kept as a designed-in seam rather
   than a gate.
2. ~~**Is a `changeType` glitch acceptable?**~~ **Mostly dissolved by the universal treatment**: a
   switch the buffer refuses is now routed through the universal lane (normalized 44.1 kHz — the
   switch stops existing) rather than forced onto the deck path. Residual question: for a 96 kHz
   FLAC on a browser whose buffer won't take the rate switch, universal means that track plays
   lossy — acceptable, or should hi-res tracks prefer the deck path (bit-perfect, but boundary risk
   while asleep)? Default in this plan: continuity wins; universal.
3. **ffmpeg cost on the gateway**: `-c:a copy` remux lanes are I/O-cheap; the universal lane is a
   real encode (~1–2 s CPU/track), which is why its cache ships with it rather than later. Open
   part: does the shared concurrency cap need raising once MSE makes the fMP4/universal lanes the
   common path instead of the rare one?
4. **Universal-lane fidelity**: AAC at what bitrate? 256 kbps stereo is transparent for almost all
   material and is what the incumbents ship at their top web tier; cost is encode time + cache
   size, not much else. Default in this plan: 256 kbps, channels preserved.
