# Mid-film HLS joins shift the timeline — subtitles lead the picture (initPTS misalignment)

Status: **root cause proven by measurement 2026-07-29.** Companion to
`transcode-restart-freeze-plan.md` — same evening, same title, different bug. That doc's fix stops
Jellyfin's copy-mode restart storms; THIS doc fixes what its workaround exposed: pin a quality rung
(forcing a real encode) and the subtitles stop matching the picture.

## Symptom

On /tv, after flipping *The Secret of NIMH* to the 1080p rung, subtitles "suddenly wouldn't match up
at all." NIMH's subs are all PGS (Blu-ray bitmap, rendered client-side by libpgs against
`video.currentTime`). Meanwhile the FFmpeg logs show the client re-tuning every ~15 s with join
offsets bouncing ±1 min — the drift corrector fighting something.

## Root cause (measured, not inferred)

When Jellyfin starts (or restarts) an HLS **encode** at a mid-file position, it issues an **input
seek with `-noaccurate_seek`**: ffmpeg lands on the source keyframe **at or before** the requested
time and encodes from there, and `-copyts` keeps those frames' true timestamps. The early frames are
muxed into the **requested segment number** anyway. Replaying Jellyfin's exact command for tonight's
join proves it:

```
requested:  -ss 00:45:11.709  -start_number 903      (segment slot starts at 903 × 3.003 = 2711.71 s)
measured:   first packet of segment 903 has pts_time 2702.950  ← 8.76 s EARLY (the source GOP is ~8.6 s)
```

hls.js (1.6.16, fMP4 passthrough path) then aligns the first fragment to its playlist slot by
computing `initPTS = truePTS − playlistTime` (here **−8.76 s**) and mapping every fragment to
`start = decodeTime − initPTS` (see `PassThroughRemuxer` in `hls.js/dist/hls.mjs`, the
`baseTime = baseOffsetSamples.start - timeOffset * timescale` / `startTime = decodeTime - initPTS…`
lines). Net effect: **the media timeline (`currentTime`, `buffered`) = true content time + 8.76 s.**
Audio and video shift together, so playback looks internally fine — but:

- **Subtitles**: PGS/ASS/VTT cues are authored in true content time and rendered against
  `currentTime` → every cue fires ~8.76 s **before** its dialogue. The offset is the distance to the
  previous source keyframe, so it re-rolls randomly (0 … one GOP) on every join, re-tune, and seek.
  Long-GOP remuxes (~8.6 s) make it flagrant; typical 2–4 s GOP content keeps it under the radar.
- **The /tv drift corrector**: compares `currentTime` to the channel clock. The picture really is
  ~8.76 s of *content* behind the channel, but seeking `currentTime` to the expected value loads new
  segments → Jellyfin restarts the encoder → a **new random offset** → still out of tolerance →
  seek again every `SYNC_SEEK_COOLDOWN_MS` (15 s). That is exactly the ~15 s tune/seek cadence in
  tonight's logs.
- **Progress reports** (`/API/Stream/Progress`, resume positions) send `currentTime`, so they are
  off by the same amount on any misaligned HLS session.
- The **copy path** usually hides this (copy segments start exactly on keyframes… when Jellyfin's
  segment numbering is right). When its numbering drifts (the companion doc's bug), initPTS goes off
  by *minutes*, which is why the corrector also seek-stormed during the copy freeze loop.

Why the older Godzilla-x-Kong investigation concluded "not a timeline offset": that session's HLS
copy stream started at position 0 — no mid-file seek, so initPTS ≈ 0 and the sidecar VTT really was
aligned. The offset only exists for mid-file starts/restarts, which is /tv's normal case and any
Watch-page seek.

## Fix — make the client timeline-offset-aware

There is no server lever: Jellyfin quantizes the seek to ITS segment grid (`-ss` = segment × 3.003 —
the logged `00:45:11.709` is exactly that, not our requested integer), so we can never request a
keyframe-aligned start. But hls.js **tells us the exact offset**: the `INIT_PTS_FOUND` event
(payload `{ frag, id, initPTS, timescale, trackId }`).

Define once, in the shared engine (`src/ui/src/streamEngine.js`, `createHls`):

```
timelineOffsetSeconds = −(initPTS / timescale)      // +8.76 in tonight's case
trueContentTime(T)    = T − timelineOffsetSeconds   // T = video.currentTime
```

Listen on every `INIT_PTS_FOUND` (it can re-fire after seeks/discontinuities — always keep the
latest), surface it via a callback option (e.g. `onTimelineOffset`), and reset to 0 per new Hls
instance. Direct-play (non-HLS) sessions have no offset — leave them at 0.

Apply it in four places (both players — they share the engine):

1. **Subtitle renderers** — a cue authored at true time C must display at
   `currentTime = C + timelineOffsetSeconds`:
   - sidecar VTT `<track>` cues: fold the offset into the existing `useSubtitleOffset` cue-re-time
     machinery as an automatic baseline **added to** the user's manual nudge (the nudge remains a
     user-visible correction for badly-authored subs; the baseline is invisible plumbing and must
     not show up in the delay readout).
   - `usePgsSubtitle` (libpgs) and `useAssSubtitle` (libass/jassub): pass the offset through
     (inspect each lib in node_modules for a native time-offset option; otherwise shim the clock
     they sync from). These hooks re-instantiate per deliveryUrl — make sure a late-arriving
     `INIT_PTS_FOUND` still reaches an already-mounted renderer (offset as state/prop, not a
     construction-time constant).
2. **TvPage drift corrector**: `drift = (currentTime − timelineOffsetSeconds) − expected`, and the
   catch-up jump becomes `video.currentTime = expected + timelineOffsetSeconds`. This also stops the
   corrector's seek storms on any misaligned stream (encode OR copy).
3. **Progress/resume reporting** (TvPage beat + Watch player): report
   `currentTime − timelineOffsetSeconds`.
4. **Any other place that equates `currentTime` with content position** on an HLS session (end-of-
   film detection tolerances, the Watch scrub bar's displayed position if it matters — audit, don't
   assume).

Sign sanity check (use tonight's numbers in a comment or test): initPTS = 2702.95 − 2711.71 =
−8.76 → offset = +8.76 → a cue at 2705 renders at currentTime 2713.76, where the picture shows true
content 2713.76 − 8.76 = 2705. Correct.

## Verifying

Reproduce on NIMH via /tv (or Watch + a mid-file seek) at a pinned 1080p rung, subtitles on:

- Console: log the received offset; it should be 0–~8.6 s, changing across re-joins.
- Subs must land on dialogue with English PGS on, both right after a join and after a manual seek.
- The 23:06-style churn must be gone: one `Stream/Start` per join in the FFmpeg logs, no ~15 s
  seek/restart cadence while watching untouched.
- Resume position after closing the Watch player mid-film must match the scene you left.

## Related, found the same night (fix separately)

`Flash Gordon (1980)`'s `MediaFile.DurationTicks` is ~5.58 s (the AVI is really 111 min) — the
channel scheduler trusted it and built a 5.58-second slot, which is why the channel "advanced" twice
within seconds after NIMH's skip. Fix the row, audit for other absurd durations, and give the
schedule builder a duration floor (exclude + warn under ~60 s) so bad metadata can never mint
micro-slots.
