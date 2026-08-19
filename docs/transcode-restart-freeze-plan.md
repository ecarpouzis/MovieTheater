# Mid-session transcode restarts freeze the picture (copied video + HLS renumbering)

Status: **ROOT-FIXED 2026-08-02 — historical.** The fix went further than this plan: a patched
Jellyfin makes copied-HLS segmentation exact per-keyframe, and the full-library keyframe backfill
completed 2026-08-02 (18,674 files, 0 failures). The Part-1 sampled probe was built, used, and then
RETIRED with the whole workaround layer. Current truth lives in the `hls-copy-freeze` skill
(`.claude/skills/hls-copy-freeze`); this doc remains as the investigation record.

Original status (2026-07-28): root cause confirmed, fix not yet written. Plan verified against the
code 2026-07-28 (`StreamController.cs`, `TvPage.js`, `MediaFile.cs`).

2026-07-29 update: the workaround was field-tested — pinning 1080p did stop the server-side restart
storm (encode sessions number consistently), but it exposed a second, independent bug: mid-file HLS
joins shift the media timeline by up to one source GOP, so subtitles lead the picture and the /tv
drift corrector seek-fights. See `hls-join-subtitle-misalignment-plan.md`; ship both fixes together,
because force-encoding long-GOP titles makes every join on them take that path.

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
only cut segments on the source's own keyframes, which on this encode fall **~8.6 s** apart — not the
nominal **6 s** Jellyfin asks for (`-hls_time 6`). Jellyfin derives each restart's `-start_number`
from its own segment-duration bookkeeping, which can't know where the source's keyframes actually
fall, so within one session the numbering and the segments on disk disagree — and disagree
*inconsistently* run to run. The two restarts caught in the log imply two different segment lengths,
neither the nominal 6 s (2413 s ÷ 6 would be segment 402, not 279) nor each other:

```
-ss 00:40:13  →  -start_number 279     (implies 8.65 s/segment)
-ss 00:45:33  →  -start_number 338     (implies 8.09 s/segment)
```

(The exact formula inside Jellyfin doesn't matter for the fix; what matters, and what the log proves,
is that restart numbering is derived from assumed durations, not from the keyframes ffmpeg actually
cut on, and lands differently each time.)

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
segment length, **and the session opens mid-file**. A real encode emits its own keyframes on the segment
boundary, so numbering and reality agree and there is nothing to renumber.

Verified in the field 2026-07-29, four restarts on the encode path: ffmpeg is given
`-g:v:0 72 -keyint_min:v:0 72` (72 frames at 24 fps = exactly the 3 s `-hls_time`), and the implied
segment length came back 3.000 / 3.000 / 3.004 / 3.002 s — bookkeeping and reality agree. Compare the
broken copy sessions above: 8.65 s and 8.09 s, disagreeing with the nominal 6 s *and with each other*.

### Why the mid-file gate (added 2026-07-29)

Long spacing is only the **precondition**. The thing worth suppressing is not *a* restart but a
**self-sustaining storm** of them, and only a mid-file join produces that. Measured across the retained
FFmpeg logs (grouped by session, using each log's **creation** time — `LastWriteTime` is when that
ffmpeg *exited* and will mislead you):

| Session | Entry | Source GOP | ffmpeg starts | Outcome |
|---|---|---|---|---|
| Muppet Treasure Island | from 0 | 10.01 s | 4 over 72 min | recovered, never reported |
| James and the Giant Peach | from 0 | 10.39 s | 7 | recovered, never reported |
| The Secret of NIMH | **mid-file join** (`-ss 903`, 4 s after open) | 9.97 s | **58** | the reported freeze storm |

So a from-the-start session **does** get restarted at the playhead — it simply recovers, because nothing
is fighting the timeline. A mid-file join does not: the join lands up to one GOP early, the /tv drift
corrector reads that as lag and seeks, each seek restarts the encoder at a new wrong offset, and it
never converges. `StartRequest.StartSeconds` already tells the server which case it is.

Without the gate the rule fires on **~67% of h264 files** (6,631 confirmed of 16,028, trending to
~10,700 once probing finishes), converting a large share of the library from lossless copy to NVENC to
suppress restarts that recover on their own — real quality and GPU cost on a box with a 0x116 TDR
history.

Residual risk, accepted: a from-the-start session on a long-GOP source can still hiccup at each of those
few restarts. That is exactly the pre-fix behaviour, and nobody reported it. The client's
`ForceTranscode` escalation reopens the session as an encode if it degenerates, and the `LogDebug` on the
let-through path names the file if it ever bites.

This also explains why the same title froze one evening and played cleanly earlier the same day — the
variable was never the file, it was whether the session joined mid-film.

The mechanism already exists end to end: `StreamController.StartRequest.ForceTranscode` appends
`&AllowVideoStreamCopy=false` to the TranscodingUrl (`StreamController.cs:395`), and Jellyfin's
`CanStreamCopyVideo` short-circuits on it. Today it only fires as a client-side escalation after
`RETUNE_LOOP_LIMIT` re-tunes in `TvPage.tune()` — which never trips here, because the client never
re-tunes. Leave that escalation in place as the backstop; this fix adds a server-side trigger for the
same path.

### Part 1 — where the keyframe spacing comes from (build this first)

`MediaStream` doesn't carry it, and **neither does anything else we have**: there is no ffprobe
anywhere in this repo today. Every technical field on `MediaFile` (`Container`, `VideoCodec`,
`FrameRate`, …) comes from Jellyfin's API via `JellyfinSyncService`, and Jellyfin's API doesn't
expose keyframe interval. So this is a small build, not a lookup:

1. **New nullable column** `MediaFile.KeyframeIntervalSeconds` (`double?`). Nullable is the contract:
   null = "not probed yet", and the controller must treat null as *don't force* — today's behavior,
   with the client escalation still catching the worst cases. Add the EF property + migration; apply
   the `ALTER TABLE MediaFile ADD KeyframeIntervalSeconds float NULL` to the live DB directly (it's
   baselined and shared with prod — the usual SqlConnection path, not `database update`). Deploy
   order is a non-issue: the old build never reads the column, the new build tolerates null.
2. **New CLI probe command** (`[Command("probe-keyframes")]`, pattern off `SyncJellyfinCommand`,
   auto-registered by `AddCommandsFromThisAssembly` in `Program.cs`). It must run **on a Windows
   machine with the library drives mapped**: `MediaFile.Path` is a Windows path (`L:\…`) and prod is
   a Linux pod with no media mount — the gateway design deliberately keeps video bytes off the
   server. This is also why the earlier idea of "`ffprobe -read_intervals` on demand, cached per
   file" is a dead end: **the API server cannot reach the files at runtime. Don't build that.**
3. Probe cheaply — packets, not frames, so nothing is decoded and only the sampled windows are read:
   `ffprobe -v error -select_streams v:0 -show_entries packet=pts_time,flags -read_intervals
   "<t>%+30" -of csv` at a few offsets (e.g. 25/50/75% of `DurationTicks`), max gap between K-flagged
   packets across all windows. Sample mid-file, not the head — opening scene-cut keyframes
   underestimate the steady-state GOP. Fall back to `dts_time` when `pts_time` is `N/A`. A window
   with fewer than 2 keyframes means spacing exceeds the window: record the window length as a floor
   (it's already > 6, which is all the controller needs).
4. Per the global bulk-job rule, the command must be chunked and resumable: `--limit` batches,
   skip rows already probed (`KeyframeIntervalSeconds IS NOT NULL`, unless `--force`), skip
   `MissingSinceUtc IS NOT NULL`, print `{processed, remaining, forced-candidates-so-far}` each run,
   safe to re-run forever. Thousands of files over the NAS = multiple sittings; probing likely
   offenders first (largest `SizeBytes / DurationTicks` — the remuxes) makes the fix effective after
   the first batch.

### Part 2 — the controller change

In the transcode branch of `Start` (`StreamController.cs:361-409`) the copy decision already exists
as `videoIsCopied`. When the session would be an HLS **copy** and `file.KeyframeIntervalSeconds >`
the copy-path segment length (constant **6** — every `-codec:v:0 copy` session in the FFmpeg logs
uses `-hls_time 6`; note Jellyfin uses `-hls_time 3` for its *encode* sessions, so don't "confirm"
the constant against one of those), treat it exactly like `request.ForceTranscode`.
Cleanest shape: one local `forceEncode = request.ForceTranscode || (wouldCopy && spacing > 6)`
feeding everything in that branch that currently consults `request.ForceTranscode`. Concretely:

- append `&AllowVideoStreamCopy=false` to the TranscodingUrl;
- **flip `videoIsCopied` to false** and let `outputVideoCodec` come from
  `VideoCodecFromTranscodingUrl` — otherwise `isDirectStream` and the codec readout lie to the
  player ("Video Copied" on a session that is actually encoding);
- log once at Information with the MediaFile id and the spacing, so the FFmpeg-log verification
  below can be tied back to a decision.

Do **not** touch `allowDirectPlay` (`StreamController.cs:210`): direct play has no segments and must
stay eligible. The check belongs strictly on the HLS path, *after* direct play has been ruled out —
which also means it naturally applies whether the first or the second (pinned-tracks) PlaybackInfo
call produced the TranscodingUrl.

Free consequences, all intended:

- The Watch player uses the same `Stream/Start` endpoint, and any HLS copy session can hit this
  renumbering on a seek — so movies get the fix too, not just /tv.
- `prewarmNext` on /tv goes through `Start` and inherits it.
- Concurrency accounting doesn't move: a copy/remux HLS session already registers as a transcode
  (`Register(isTranscode: !directPlay)`), so `StreamingMaxConcurrentTranscodes` counts the same
  sessions before and after. Real GPU load does rise on affected titles — that is the trade, the
  same one the existing escalation already makes, just taken before the freeze rather than after it.

### Alternatives considered (rejected)

- **Raise `-hls_time`**: a longer segment only moves the mismatch — keyframe spacing is a property
  of the source, not a setting.
- **Jellyfin's "keyframe extraction" feature**: fed by library scans, which are deliberately
  disabled on this server (sync is manual), and it makes the *playlist* more truthful without making
  *restart renumbering* consistent. Not our lever.

## Verifying a fix

Reproduce on a long-GOP copy title (*The Secret of NIMH (1982)* is the known repro — probe it first),
then confirm from the server side — no guessing from the picture:

- The site log must show the new force-encode decision line for that file, and the `Start` response
  must say `isDirectStream: false` (player readout shows an encode, not "Video Copied"). If it still
  says copied, the probe value never landed — check `MediaFile.KeyframeIntervalSeconds` for that row
  before debugging the controller.
- `C:\ProgramData\Jellyfin\Server\log\FFmpeg.*.log` — count sessions per 10 min, and read `-ss` /
  `-start_number` from each command line. A fixed build shows **one** session per join, and its
  command line carries a real video encoder (e.g. `h264_nvenc`), not `-codec:v:0 copy`.
- `C:\ProgramData\Jellyfin\Server\cache\transcodes\<hash>*` — segment numbers must only ever increase,
  and `<hash>-1.mp4` (the init segment) must be written once.
- A restart with no `User policy for "…"` line before it in `log_*.log` means the server restarted the
  encoder on its own; a restart *with* one means the client re-tuned. Different bug, different fix.

## Workaround until then

Pin a fixed quality rung (e.g. **1080p · 12 Mbps**) instead of Auto. That forces the re-encode by hand
and stops the restarts. "Original" does **not** help on a title like this: the FLAC audio still has to be
re-encoded, so it stays on the HLS path even though the video is copied — and the player's "Video Copied"
readout is true for both direct play and remux, so it can't be used to tell them apart.
