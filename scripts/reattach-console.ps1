<#
.SYNOPSIS
    Reattach the arcade capture session to the PHYSICAL CONSOLE (tscon /dest:console).

.DESCRIPTION
    Deployed beside the capture worker (D:\ArcadeStorage\worker-capture\bin) and run ON-DEMAND, as
    SYSTEM, by the scheduled task "MovieTheater - Reattach Console" (register-reattach-console-task.ps1).
    The non-elevated capture worker triggers that task at room start when it detects its own session is
    RDP-DISCONNECTED; tscon then moves that session back to the physical console so the game runs on the
    real high-refresh displays and WGC capture is full-rate (60fps) instead of the stalled/32Hz
    disconnected-DWM rate. tscon needs SeTcbPrivilege to move a session without a password, which is why
    the task runs as SYSTEM.

    Target session resolution:
      1. The worker writes its session id to `.reattach-session` beside this script — used if present.
      2. Otherwise, auto-detect the SINGLE Disconnected session (refuse if there are several).

    TWO GUARDS stand between a target and tscon, and BOTH must pass:
      A. STATE — the session must read exactly 4 (Disconnected). Anything else is someone arriving,
         present, or leaving, and moving it would kick them.
      B. DWELL — it must read 4 CONTINUOUSLY for -DwellSeconds. A single reading cannot tell an
         abandoned session from one mid-reconnect; only time can.

    ONE INVOCATION REACHES AN OUTCOME. The dwell is waited out IN PROCESS, re-reading the session every
    -PollSeconds, rather than stamping a file and exiting for someone to call back later. That "call
    back later" was the whole defect: the only caller that does is the watchdog's check I, on a FIVE
    MINUTE floor, so a 90 s dwell cost five minutes of stalled 32 Hz console minimum — and 24 minutes
    on 2026-08-07 11:50, because the floor is measured from the arming trigger. Polling also makes the
    guard mean what it says: it now OBSERVES continuity every few seconds instead of inferring it from
    two samples minutes apart, so a shorter dwell here is stronger evidence than a longer one was.

.PARAMETER DwellSeconds
    How long the target must stay continuously Disconnected before it may be moved. This is the one
    tuning knob: lower reclaims the console (and 60fps) sooner after a genuine disconnect, higher is
    more tolerant of slow/flaky RDP reconnects. The arbitration storm this guard exists to survive ran
    ~23 s, so 60 keeps ~2.6x margin over the measurement while putting the picture back inside a minute.

.PARAMETER PollSeconds
    How often to re-read the session while waiting out the dwell. Every poll is a chance to ABORT
    because the user came back, so this is the resolution of the kick-avoidance guard.

.PARAMETER MaxWaitSeconds
    Hard ceiling on the in-process wait, so the run always ends well inside the scheduled task's
    ExecutionTimeLimit (PT2M). Being killed by the task scheduler mid-wait would leave no verdict in the
    log at all. If the ceiling is reached first, the stamp is left armed and the next trigger finishes
    the job — i.e. it degrades to the old trigger-driven behaviour rather than to nothing.

.PARAMETER MaxStampAgeSeconds
    How old a cross-invocation dwell stamp may be before its credit is discarded and the count re-armed.
    The stamp only proves "was Disconnected when armed, and is Disconnected now" — nothing observed the
    interval between triggers, so the older it is, the weaker the continuity claim (the user could have
    reconnected and re-disconnected in between, which is exactly the mid-reconnect window the dwell
    exists to avoid). Legitimate gaps are trigger-cadence sized (the watchdog's five-minute floor plus
    slack); 900 covers those while refusing day-old credit.

.PARAMETER DryRun
    Evaluate and log the full decision, then stop short of tscon. Use this to confirm behaviour against
    a live session without moving anything.
#>
[CmdletBinding()]
param(
    [int]$DwellSeconds = 60,
    [int]$PollSeconds = 3,
    [int]$MaxWaitSeconds = 75,
    [int]$MaxStampAgeSeconds = 900,
    [switch]$DryRun
)

$ErrorActionPreference = 'Continue'
$log = 'D:\ArcadeStorage\logs\reattach-console.log'
try { New-Item -ItemType Directory -Force (Split-Path $log) | Out-Null } catch {}
function Log($m) { $ts = (Get-Date -Format o); try { Add-Content -Path $log -Value "$ts  $m" -Encoding UTF8 } catch {}; Write-Host $m }

Add-Type @"
using System; using System.Runtime.InteropServices;
public static class WTS {
  // CharSet.Unicode picks WTSEnumerateSessionsW to match the LPWStr field below. Without it the ANSI
  // entry point is called and every pWinStationName logs as mojibake (the ints are unaffected, which
  // is why this went unnoticed: the tscon decision reads State, not the name).
  [DllImport("wtsapi32.dll", SetLastError=true, CharSet=CharSet.Unicode)] public static extern int WTSEnumerateSessions(IntPtr h,int reserved,int ver,out IntPtr ppSessionInfo,out int count);
  [DllImport("wtsapi32.dll")] public static extern void WTSFreeMemory(IntPtr p);
  [StructLayout(LayoutKind.Sequential)] public struct WTS_SESSION_INFO { public int SessionId; [MarshalAs(UnmanagedType.LPWStr)] public string pWinStationName; public int State; }
}
"@

function Get-Sessions {
  $pp = [IntPtr]::Zero; $cnt = 0
  if ([WTS]::WTSEnumerateSessions([IntPtr]::Zero, 0, 1, [ref]$pp, [ref]$cnt) -eq 0) { Log "WTSEnumerateSessions failed"; return @() }
  $sz = [Runtime.InteropServices.Marshal]::SizeOf([type][WTS+WTS_SESSION_INFO]); $list = @()
  for ($i = 0; $i -lt $cnt; $i++) {
    $cur = [IntPtr]($pp.ToInt64() + $i * $sz)
    $list += [Runtime.InteropServices.Marshal]::PtrToStructure($cur, [type][WTS+WTS_SESSION_INFO])
  }
  [WTS]::WTSFreeMemory($pp); return $list
}

# WTS_CONNECTSTATE_CLASS, named so the log says what it saw instead of an integer nobody remembers.
$StateNames = @{ 0='Active'; 1='Connected'; 2='ConnectQuery'; 3='Shadow'; 4='Disconnected'; 5='Idle'; 6='Listen'; 7='Reset'; 8='Down'; 9='Init' }
function StateName($s) { if ($StateNames.ContainsKey([int]$s)) { $StateNames[[int]$s] } else { "unknown($s)" } }

# The dwell stamp: when we FIRST saw the current target sitting Disconnected. Lives beside the sentinel
# so it shares the sentinel's lifetime and cleanup. Cleared by every observation that is not a settled
# disconnect — that clearing is what makes the gate mean "continuously" instead of merely "now, and
# also at some point earlier".
$dwellFile = Join-Path $PSScriptRoot '.reattach-dwell'
function Clear-Dwell($why) {
  if (Test-Path $dwellFile) {
    try { Remove-Item $dwellFile -Force -ErrorAction Stop; Log "dwell cleared ($why)" } catch { Log "dwell clear FAILED ($why): $_" }
  }
}

$sessions = Get-Sessions
Log ("sessions: " + (($sessions | ForEach-Object { "id=$($_.SessionId) state=$($_.State)/$(StateName $_.State) name=$($_.pWinStationName)" }) -join ' | '))

$target = $null
$sentinel = Join-Path $PSScriptRoot '.reattach-session'
if (Test-Path $sentinel) {
  $raw = (Get-Content $sentinel -Raw).Trim()
  if ($raw -match '^\d+$') { $target = [int]$raw; Log "target from sentinel: session $target" }
}
if ($null -eq $target) {
  # Session 0 (the "Services" WinStation) is ALWAYS state 4 on Windows and can never be tscon'd, so it
  # must not count as a candidate. Including it made auto-detect useless on this box in both directions:
  # with no other disconnected session it picked SESSION 0 and ran tscon on it, and when the arcade's
  # own session went disconnected there were suddenly TWO and the script refused ("need the worker
  # sentinel") exactly when the recovery was wanted. Found 2026-08-06 while wiring the site's
  # remote-desktop warning, which triggers this script from two new places.
  $disc = @($sessions | Where-Object { $_.State -eq 4 -and $_.SessionId -ne 0 -and $_.pWinStationName -ne 'Services' })
  if ($disc.Count -eq 1) { $target = $disc[0].SessionId; Log "auto-detected single disconnected session $target" }
  elseif ($disc.Count -gt 1) { Log "REFUSING: multiple disconnected sessions ($(($disc | ForEach-Object SessionId) -join ',')); need the worker sentinel"; exit 2 }
}
if ($null -eq $target) { Log "no target (no sentinel, no single disconnected session) — nothing to do"; Clear-Dwell 'no target'; exit 0 }

$ts = $sessions | Where-Object { $_.SessionId -eq $target } | Select-Object -First 1

# The sentinel outlives the session it names (worker restarted into a new session id, box rebooted
# before the worker rewrote the file). Enumeration is the authority: an id we cannot see is not an id
# we may move. This used to fall through — $ts was allowed to be $null and the state guard below simply
# never fired, so tscon ran against a stale id and whatever now held it.
if ($null -eq $ts) { Log "target session $target is not present in enumeration (stale sentinel?) — nothing to do"; Clear-Dwell 'target gone'; exit 0 }

# ── GUARD A: STATE ───────────────────────────────────────────────────────────────────────────────
# This was "refuse if State -eq 0 (Active)", which let EVERY other state through — including 1=Connected
# and 2=ConnectQuery, which is precisely what a session looks like partway through an RDP handshake.
# Inverted to an allowlist: only a settled 4=Disconnected may be moved. Stating the one safe state is
# also future-proof in a way that enumerating unsafe ones is not.
if ($ts.State -ne 4) {
  Log "target session $target is $(StateName $ts.State) — NOT reattaching (only a settled Disconnected may be moved)"
  Clear-Dwell "session is $(StateName $ts.State)"
  exit 0
}

# ── GUARD B: DWELL ───────────────────────────────────────────────────────────────────────────────
# A state=4 reading on its own cannot distinguish a session abandoned ten minutes ago from one 200 ms
# into an RDP reconnect: RDP drops a session THROUGH Disconnected on its way back up, and session
# arbitration bounces it there repeatedly while a client negotiates. Firing into that window tscon'd
# the reconnecting user to the physical console — which their client reports as "an administrator has
# ended your session, or a network problem" — they reconnected, and the next watchdog cycle (~30 s, on
# its own schedule and NOT sharing the gateway's 60 s throttle) did it again. Unbreakable kick loop,
# observed 2026-08-07 10:19 as three cycles ~11 s apart. The state guard above cannot fix this alone,
# because at the instant we sample the session genuinely IS disconnected. Only elapsed time separates
# the two cases.
#
# The stamp on disk is the CROSS-INVOCATION half of the clock: the gateway and the watchdog both
# trigger this task, and a second trigger 30 s into a wait must not restart the count. The poll loop
# below is the WITHIN-invocation half. Credit already earned is honoured; only the remainder is waited.
#
# The stamp carries the SESSION ID it was armed for ("<id> <iso-timestamp>"), and both checks below
# exist because credit is only meaningful for the exact disconnect episode that earned it:
#   - a different target id (worker restarted into a new session; sentinel rewritten) must re-arm —
#     honouring session 3's credit for session 5 is zero observed dwell for the session about to move;
#   - a stamp older than -MaxStampAgeSeconds must re-arm — nothing observed the interval between
#     triggers, so an old stamp can span a reconnect + fresh disconnect, the precise mid-reconnect
#     kick this guard exists to prevent. A legacy timestamp-only stamp fails the parse and re-arms.
$now = Get-Date
$since = $null
if (Test-Path $dwellFile) {
  $raw = Get-Content $dwellFile -Raw -ErrorAction SilentlyContinue
  if ($raw) {
    $m = [regex]::Match($raw.Trim(), '^(\d+)\s+(\S.*)$')
    if (-not $m.Success) {
      Log "dwell stamp unreadable or session-less ('$($raw.Trim())') — re-arming"
    }
    elseif ([int]$m.Groups[1].Value -ne $target) {
      Log "dwell stamp belongs to session $($m.Groups[1].Value), target is $target — re-arming (credit is per-session)"
    }
    else {
      try { $since = [datetime]::Parse($m.Groups[2].Value, $null, [System.Globalization.DateTimeStyles]::RoundtripKind) }
      catch { Log "dwell stamp timestamp unreadable ('$($m.Groups[2].Value)') — re-arming"; $since = $null }
    }
  }
}
# A stamp from the future (clock change, or a file copied between boxes) would otherwise wedge the gate
# shut forever. Treat it as a fresh arm rather than trusting it.
if ($null -ne $since -and ($now - $since).TotalSeconds -lt 0) {
  Log ("dwell stamp is {0:N0}s in the future — re-arming" -f (($since - $now).TotalSeconds))
  $since = $null
}
if ($null -ne $since -and ($now - $since).TotalSeconds -gt $MaxStampAgeSeconds) {
  Log ("dwell stamp is {0:N0}s old (> ${MaxStampAgeSeconds}s) — too stale to vouch for continuity, re-arming" -f (($now - $since).TotalSeconds))
  $since = $null
}
if ($null -eq $since) {
  $since = $now
  try { Set-Content -Path $dwellFile -Value "$target $($since.ToString('o'))" -Encoding UTF8 -ErrorAction Stop } catch { Log "dwell stamp write FAILED: $_" }
  Log "session $target newly Disconnected — arming dwell, holding for up to ${DwellSeconds}s"
}

# Wait the dwell out HERE. Every poll re-enumerates and re-reads the target, so the moment the session
# stops being Disconnected — which is what a returning user looks like — we abort and leave them alone.
# That abort is the kick-loop guard doing its job; reaching the bottom of the loop is the only way past.
$deadline = (Get-Date).AddSeconds($MaxWaitSeconds)
while ($true) {
  $held = ((Get-Date) - $since).TotalSeconds
  if ($held -ge $DwellSeconds) { break }
  if ((Get-Date) -ge $deadline) {
    Log ("in-process wait hit its {0}s ceiling with only {1:N0}s of {2}s dwell — leaving the stamp armed for the next trigger" -f $MaxWaitSeconds, $held, $DwellSeconds)
    exit 0
  }
  Log ("session $target Disconnected {0:N0}s of ${DwellSeconds}s required — holding" -f $held)
  Start-Sleep -Seconds $PollSeconds

  $cur = Get-Sessions | Where-Object { $_.SessionId -eq $target } | Select-Object -First 1
  if ($null -eq $cur) {
    Log "target session $target vanished from enumeration while waiting — aborting"
    Clear-Dwell 'target gone mid-wait'
    exit 0
  }
  if ($cur.State -ne 4) {
    Log "target session $target went $(StateName $cur.State) while waiting — ABORTING (someone is using it)"
    Clear-Dwell "session went $(StateName $cur.State) mid-wait"
    exit 0
  }
}
Log ("session $target Disconnected {0:N0}s (>= ${DwellSeconds}s), continuously — both guards passed" -f $held)

if ($DryRun) { Log "DRY RUN: would run tscon $target /dest:console — stopping here"; exit 0 }

Log "running: tscon $target /dest:console"
$out = & tscon $target /dest:console 2>&1
Log ("tscon exit=$LASTEXITCODE out=$out")
# Post-move the session reads Active and the next run would clear this anyway; doing it here keeps a
# failed tscon from inheriting a satisfied dwell and retrying instantly on the next trigger.
Clear-Dwell 'reattach attempted'
exit $LASTEXITCODE
