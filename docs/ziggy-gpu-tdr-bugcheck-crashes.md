# Ziggy — recurring GPU driver bugcheck (0x116 VIDEO_TDR_FAILURE), not an app bug

**Status (2026-07-21): documented, root cause NOT found — this is a Windows/NVIDIA driver-level
failure on Ziggy that has recurred 5 times since January 2026, unrelated to any specific app. All
services self-heal (NSSM auto-restart) within minutes, so the practical impact is "playback drops
for ~7 minutes, then works again," not data loss or an app defect to fix.**

## The trigger for this investigation

2026-07-21, ~9:41 AM: Eric's daughter's playback of *Ada Twist, Scientist* S02E01
(`Ada.Twist.Scientist.S02E01...PROPER.1080p.NF.WEB-DL.DDP5.1.x264-TVSmash.mkv`) errored out. First
guess was an app-level bug (Jellyfin/StreamGateway/the site) — turned out to be a full Windows
crash and reboot of Ziggy itself, mid-playback.

**Arcade was confirmed NOT in use at the time** (Eric, 2026-07-21) — ruling out the CloudRetro GPU
workers as the trigger for *this* occurrence, unlike the separate, still-open
[[arcade-worker-unkillable-wedge]] investigation which also implicates `nvlddmkm.sys` but is a
different symptom (a single worker process becoming unkillable, not a full system bugcheck).

## What the evidence shows, in order

1. **Jellyfin's own log** (`C:\ProgramData\Jellyfin\Server\log\log_20260721.log`) is mid-sentence:
   at 09:41:22.658 it's logging ffmpeg subtitle-track extraction #35 of 35 for the Ada Twist file
   (this release has an unusually large number of embedded subtitle streams), then the log **just
   stops** — no shutdown message, no exception, nothing — until 09:48:33 when a brand new Jellyfin
   process logs a fresh `Kestrel is listening` startup. That gap is a hard kill, not a graceful
   stop.
2. **StreamGateway's Yarp reverse-proxy** (`.NET Runtime` Event ID 1000 warnings in the Windows
   Application log) shows ~38 concurrent `/Subtitles/N/0/Stream.vtt` proxy requests for that same
   file all failing with `TaskCanceledException` / `IOException: ... aborted because of either a
   thread exit or an application request` at 09:41:21–23 — consistent with the player firing
   requests for all 35 subtitle tracks at once, right as the box went down underneath them. A later
   warning at 09:46:39 shows StreamGateway getting flat-out `SocketException 10061: connection
   refused` trying to reach `localhost:8096` — Jellyfin wasn't listening at all by then.
3. **Windows Application event log**, `Microsoft-Windows-WER-SystemErrorReporting`:
   ```
   2026-07-21 09:46:40  Id 1019  The computer has rebooted from a bugcheck. Possibly related driver: nvlddmkm.sys.
   2026-07-21 09:46:40  Id 1001  The computer has rebooted from a bugcheck. The bugcheck was:
                                 0x00000116 (0xffffc808b0b38010, 0xfffff80282362440, 0xffffffffc000009a, 0x0000000000000004).
                                 A dump was saved in: C:\WINDOWS\MEMORY.DMP.
   ```
   Bugcheck 0x116 = `VIDEO_TDR_FAILURE` — a GPU driver stopped responding and Windows' own
   Timeout Detection and Recovery *reset attempt itself* also failed, escalating to a full
   bugcheck/reboot (this is a harder failure than the common "display driver stopped responding and
   has recovered" event 4101 — there was no recovery here).
4. **NSSM** auto-started the Jellyfin service at 09:48:31/09:48:41 (its normal `DELAYED_AUTO` +
   `AppExit Default=Restart` behavior — same mechanism documented in [[jellyfin-ops]]), which is why
   the outage window was only ~7 minutes end to end.

## Memory dump analysis (`C:\WINDOWS\MEMORY.DMP`, 3.4 GB kernel dump)

Required an elevated (`Run as Administrator`) session — `cdb.exe` (from the WinDbg Preview Store
package, `C:\Users\Atoramos\AppData\Local\Microsoft\WindowsApps\cdbX64.exe`) refuses to open
`C:\WINDOWS\MEMORY.DMP` with "Access is denied" when run non-elevated; works fine elevated.
Command used: `cdb -z C:\WINDOWS\MEMORY.DMP -y "SRV*<symcache>*https://msdl.microsoft.com/download/symbols" -c "!analyze -v; q"`.

`!analyze -v` confirms and sharpens the event-log summary:

```
VIDEO_TDR_FAILURE (116)
Attempt to reset the display driver and recover from timeout failed.
FAILURE_BUCKET_ID:  0x116_IMAGE_nvlddmkm.sys
IMAGE_NAME:         nvlddmkm.sys
SYMBOL_NAME:        nvlddmkm+1a22440
PROCESS_NAME:       System

STACK_TEXT:
nt!KeBugCheckEx
dxgkrnl!TdrBugcheckOnTimeout+0x101
dxgkrnl!ADAPTER_RENDER::Reset+0x220
dxgkrnl!DXGADAPTER::Reset+0x58a
dxgkrnl!TdrResetFromTimeout+0x15
dxgkrnl!TdrResetFromTimeoutWorkItem+0x22
nt!ExpWorkerThread+0x4bb
nt!PspSystemThreadStartup+0x5a
```

**Reading this stack:** the crash is *inside Windows' own GPU-recovery machinery*
(`dxgkrnl!TdrResetFromTimeoutWorkItem` → `DXGADAPTER::Reset` → `ADAPTER_RENDER::Reset`), not inside
whatever app originally queued the GPU work that hung. `PROCESS_NAME: System` confirms this —
the bugcheck happened on a kernel worker thread doing the *reset*, not on any user-mode process
(Jellyfin, the site, a browser, an arcade worker). **This means the dump cannot identify which
process's GPU command originally caused the underlying hang** — by the time this thread runs, the
adapter has already been idle/hung long enough that TDR kicked in and started a reset; the dump
only captures the reset failing, not the original stall. One instruction of context was also lost
to paging (`Page a17b5a not present in dump file`), typical for a kernel triage dump and not
something worth chasing further.

**So "was Ada Twist actually the cause?" remains genuinely unresolved and probably unresolvable
from this dump alone.** Circumstantial case against it being causal: subtitle extraction is
CPU-only ffmpeg demuxing (no GPU codec/filter involved), no `FFmpeg.Transcode-*.log` was created
that morning (the episode was direct-played, not hardware-transcoded, so Jellyfin wasn't pushing
anything through NVENC/NVDEC for this title), and arcade — the box's other major GPU consumer — was
confirmed idle. The likelier read is coincidental timing: whatever hung the GPU adapter was
unrelated background activity (composited desktop rendering, a Windows/driver background task,
thermal/power event, or a spontaneous driver fault), and it happened to land during this playback.

## Recurrence history — this is NOT a one-off

Same bugcheck code, same general signature, found via
`Get-WinEvent -FilterHashtable @{LogName='Application'; ProviderName='Microsoft-Windows-WER-SystemErrorReporting'}`:

| Date | Bugcheck |
|---|---|
| 2026-01-04 10:23 | 0x116 |
| 2026-01-24 15:24 | 0x116 |
| 2026-03-28 00:23 | 0x116 |
| 2026-04-16 22:40 | 0x116 |
| 2026-07-21 09:46 | 0x116 (this incident, `nvlddmkm.sys` explicitly named) |

Roughly every 4–8 weeks, no obvious shared trigger across occurrences (times of day vary, and only
today's is known to correlate with any specific app activity — and even that correlation is weak,
per the analysis above). **Read this as ongoing GPU/driver instability on Ziggy, not something the
MovieTheater codebase or Jellyfin config can fix.**

**Currently installed driver: NVIDIA GeForce RTX 4070 Ti, version 596.21** (same version referenced
in [[arcade-worker-unkillable-wedge]]'s diagnostic snapshot — that doc independently flagged this
driver version as implicated in a *different* GPU-hang symptom on the same box). TDR registry
settings (`HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers`) are all unset/default
(`TdrLevel=3`, `TdrDelay=2s`) — already at Windows' most sensitive/aggressive recovery setting, so
there's no registry lever left to tune here either.

## Open next steps (not done this session)

1. **Driver update/rollback as a controlled experiment.** No specific known-bad-version report was
   found for 596.21, but two independent GPU-hang symptoms now point at this box's driver — worth
   trying a newer or deliberately older driver build and watching whether bugchecks continue at the
   same ~monthly cadence. Re-verify the arcade GPU quality pipeline afterward if this is done (it's
   expensive to re-verify — see the arcade-vulkan docs).
2. **Correlate future occurrences against Arcade activity more rigorously** (worker logs / room
   history timestamps vs. bugcheck timestamps) — today's was confirmed idle, but the historical
   4 earlier bugchecks were never checked against arcade usage. If a pattern emerges even loosely,
   that would upgrade the arcade GPU workload from "ruled out this once" to "actually implicated."
3. **Check GPU health signals going forward**: `nvidia-smi -q` for ECC/Xid errors, thermal/power
   readings around past bugcheck timestamps if any monitoring history exists; none was checked this
   session.
4. No app-side action is warranted — Jellyfin/Caddy/StreamGateway already self-heal via NSSM within
   minutes of any reboot, which is the correct behavior here.

Related: [[jellyfin-ops]] (NSSM resilience setup that makes this self-healing),
[[arcade-worker-unkillable-wedge]] (separate `nvlddmkm.sys`-implicated GPU symptom on the same box,
same driver version — worth a joint look if either investigation advances),
[[arcade-cloudretro-vertical]] (confirms Ziggy's GPU role/hardware).
