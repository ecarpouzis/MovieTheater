# Music: one continuous stream (MSE + fMP4)

Plan for taking JavaScript out of the track boundary. Status: **Phase 0 done, Phase 1 not started.**
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

## What is measured (and what is not)

Verified in a real Chromium (headless 148, desktop) — `mse_probe.py`, `mse_changetype.py`:

| Question | Answer |
|---|---|
| `audio/mpeg` (raw MP3) in MSE | **supported** |
| `audio/mp4; codecs="flac"` in MSE | **supported** |
| `audio/mp4; codecs="mp4a.6B"` (MP3-in-MP4) | **NOT supported** |
| `changeType` mpeg → mp4/flac | OK |
| `changeType` mp4/flac → mpeg | OK |
| alternating four times | OK |
| `mode = "sequence"`, `timestampOffset` | available |

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
- what happens when the sample rate changes across a switch (44.1k → 96k);
- iOS Safari, where MSE on iPhone is restricted (Managed Media Source). **Open question: which
  phone is this failing on?** If iPhone is in scope the fallback path below is not optional.

## Mixed FLAC/MP3 queues — the explicit requirement

A playlist mixing formats freely must work. It does, and this is the mechanism:

- **One** `SourceBuffer`, `mode = "sequence"` so appended tracks land back-to-back without computing
  timestamps.
- **MP3 is appended raw as `audio/mpeg`.** No ffmpeg, no remux, no server work — 81% of the library.
- **FLAC is appended as `audio/mp4; codecs="flac"`** from the new gateway lane, produced with
  `-c:a copy`. A container change only: no decode, no re-encode, bit-identical, still lossless.
- **`changeType()` at every format switch.** Verified in both directions and alternating.

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
- Media Session position state
- the stall watchdog's progress mark

**The boundary becomes bookkeeping.** Nothing happens in the media pipeline; a `timeupdate` that
crosses the next track's start updates the index for the UI. If the renderer is frozen, audio
continues regardless and the UI catches up on wake — which is the entire goal.

**Memory is bounded by eviction, not by restraint.** `SourceBuffer.remove()` drops what is behind the
playhead. Target: ~1 track behind, 1–2 ahead. An hours-long queue costs the same as a short one.

## The visualizer must not break

A hard constraint, and MSE makes it *safer* rather than riskier:

- Today `createMediaElementSource` must be called on **both** decks; a deck that misses it plays
  silent forever once the graph exists. With one persistent element there is exactly one source node,
  routed once.
- The element's source becomes a same-origin `blob:` MediaSource URL, so the graph cannot be
  CORS-tainted. Fetching moves into JS, where CORS applies to us and not to the element.
- `ensureAudioGraph()` keeps its current shape and lazy-on-first-open behaviour.

Verification: open the visualizer, play across a FLAC→MP3 boundary, confirm audio and that the
analyser still produces data. A silent-after-boundary regression is the specific failure to watch for.

## Phases, each with a gate

**Phase 0 — foundation. DONE.**
`MusicStreamRoutes.Fmp4` + the gateway lane (`-c:a copy`, shares the transcode concurrency cap, 404s
without `FfmpegPath`). ffmpeg confirmed present on the gateway by exercising the `.wma` transcode
route in prod end to end. Capability probes above.

**Phase 1 — prove the bytes. GATE.**
Deploy the gateway (NSSM on Ziggy — rebuild + redeploy required, see `stream-gateway-deploy-on-ziggy`).
In a real browser: append one MP3 and one FLAC-fMP4 into one SourceBuffer with a `changeType` between
them and assert **continuous playback across the join**, then repeat at 96 kHz.
*Nothing below starts until this passes.* If it fails, the fallback ladder is the answer and the
client engine is not worth building.

**Phase 2 — engine behind a flag.**
`MusicMseEngine` alongside the existing deck player, off by default. Playback and queue advance only;
no seek, no lyrics. Falls back to decks on any unsupported condition. Both paths must coexist —
this is not a rewrite of the player.

**Phase 3 — the timeline module.**
`elementTime ⇄ track` mapping, then move progress, seek, lyrics and Media Session onto it. Tests
first: a boundary crossed mid-lyric is the regression that will not be noticed by eye.

**Phase 4 — eviction and long queues.**
`remove()` behind the playhead; measure memory on a 3-hour queue. Verify a seek backwards into
evicted range re-appends rather than dying.

**Phase 5 — the phone, and the flag comes off.**
The incident reporter (`MusicPlaybackIncident`) already collects failures unprompted, so this is
measured rather than asked about: boundary incidents should go to zero. Keep the flag until a full
album crosses several boundaries with the screen off.

## Fallback ladder

Never remove the deck path. Fall back a rung on any of these, and log which rung was used:

1. **MSE unavailable** (`MediaSource` missing, or neither type supported) → decks.
2. **`changeType` rejected** for the next track's format → finish the current buffer, deck-flip that
   one boundary, start a fresh MediaSource after it.
3. **Sample rate / channel change** the buffer won't take → same as 2.
4. **fMP4 lane 404s** (gateway without ffmpeg, or not yet redeployed) → FLAC plays via decks; MP3
   still goes through MSE.
5. **Append error / QuotaExceeded** → evict, retry once, then decks.

Rung 4 matters operationally: the site deploys on push, the gateway does not. A site that is ahead of
the gateway must degrade quietly, exactly as the token design already ensures for the other lanes.

## Open questions for Eric

1. **Which phone?** iPhone changes Phase 1 (Managed Media Source) and makes the ladder essential.
2. **Is a `changeType` glitch acceptable** if a 44.1k→96k switch proves to need a buffer reset, or
   should such boundaries always take the deck path?
3. **ffmpeg cost on the gateway**: one process per FLAC track. It is a copy, not an encode, but if
   the media host is busy the concurrency cap may need raising or the fMP4 caching.
