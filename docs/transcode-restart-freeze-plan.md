# Mid-session transcode restarts freeze the picture (copied video + HLS renumbering)

Status: **root cause confirmed 2026-07-28, fix not yet written.** Written up while the evidence was
fresh; the fix is the one open item.

## Symptom

Watching a channel, the **video freezes while the audio plays on**. It recovers, then freezes again,
roughly every 30 seconds. Not title-agnostic: it showed up on *The Secret of NIMH (1982)* — a 20.7 Mbps
1080p H.264 Blu-ray remux with FLAC audio — and the same part of the same film had played cleanly
earlier the same day.

Everything the symptom *looks* like, and isn't:

| Looks like | Ruled out by |
|---|---|
| Client can't keep up | It's a wired media PC; ffmpeg ran at 9.8× realtime with no errors |
| ABR flapping / never dropping a rung | 26 sessions in 2 h, every one `v=copy` — the cap never changed |
| Two viewers colliding | The whole storm ran inside ONE session directory, one client |
| The client re-tuning | No `User policy for "Alex"` line at the restarts — no `PlaybackInfo`, so no `Stream/Start` |
| The site's DB being hammered by a debug script | Restarts predate the script by 45 minutes |

## Root cause

The video is **copied** (`-codec:v:0 copy`); only the FLAC audio is re-encoded. ffmpeg can therefore
only cut segments on the source's own keyframes, which on this encode fall **~8.6 s** apart. Jellyfin,
meanwhile, computes `-start_number` for a restart as `seek ÷ segment length` using the **6 s** it asked
for (`-hls_time 6`).

Within one session the two disagree, and disagree *inconsistently*:

```
-ss 00:40:13  →  -start_number 279     (implies 8.65 s/segment)
-ss 00:45:33  →  -start_number 338     (implies 8.09 s/segment)
```

So each restart renumbers the timeline. Caught live in the session directory — the restarted encoder
writing segment numbers *below* ones already on disk, and rewriting the fMP4 init segment underneath a
playing decoder:

```
…346.mp4   25528 KB  22:44:45   ← previous run
…-1.mp4        1 KB  22:44:46   ← init segment rewritten
…338.mp4   14316 KB  22:45:03   ← restarted run writes 338, 339, 340…
```

The player's next segment is never written, so the picture freezes on buffered video while audio
drains. ~30 s later Jellyfin restarts the encoder at the new playhead — reproducing the mismatch.
Self-sustaining, and entirely server-side.

## Fix

Force the re-encode **up front** when Jellyfin would copy video whose keyframe spacing exceeds the HLS
segment length. A real encode emits its own keyframes on the segment boundary, so numbering and reality
agree and there is nothing to renumber.

The mechanism already exists: `StreamController.StartRequest.ForceTranscode` (→ `AllowVideoStreamCopy=false`).
Today it only fires as a client-side escalation after `RETUNE_LOOP_LIMIT` re-tunes in `TvPage.tune()` —
which never trips here, because the client never re-tunes.

1. In `StreamController.Start`, after Jellyfin describes the source, compute the video stream's keyframe
   spacing. `MediaStream` doesn't carry it directly; candidates, cheapest first:
   - a persisted probe on `MediaFile` (one ffprobe at ingest, reused forever) — preferred;
   - `ffprobe -read_intervals` on demand, cached per file.
2. When the session would be an HLS **copy** and that spacing exceeds the segment length, set
   `AllowVideoStreamCopy=false` for that session and log it once. Direct play is unaffected — it has no
   segments at all.
3. Prefer this over raising `-hls_time`: a longer segment only moves the mismatch, since keyframe
   spacing is a property of the source, not a setting.

Costs a GPU encode on affected titles instead of a copy. That is the same trade the existing escalation
already makes, just taken before the freeze rather than after it.

## Verifying a fix

Reproduce on a long-GOP copy title, then confirm from the server side — no guessing from the picture:

- `C:\ProgramData\Jellyfin\Server\log\FFmpeg.*.log` — count sessions per 10 min, and read `-ss` /
  `-start_number` from each command line. A fixed build shows **one** session per join.
- `C:\ProgramData\Jellyfin\Server\cache\transcodes\<hash>*` — segment numbers must only ever increase,
  and `<hash>-1.mp4` (the init segment) must be written once.
- A restart with no `User policy for "…"` line before it in `log_*.log` means the server restarted the
  encoder on its own; a restart *with* one means the client re-tuned. Different bug, different fix.

## Workaround until then

Pin a fixed quality rung (e.g. **1080p · 12 Mbps**) instead of Auto. That forces the re-encode by hand
and stops the restarts. "Original" does **not** help on a title like this: the FLAC audio still has to be
re-encoded, so it stays on the HLS path even though the video is copied — and the player's "Video Copied"
readout is true for both direct play and remux, so it can't be used to tell them apart.
