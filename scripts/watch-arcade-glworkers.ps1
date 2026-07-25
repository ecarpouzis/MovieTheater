<#
.SYNOPSIS
    Watchdog for the Windows-native CloudRetro GL workers: recycles a worker that is
    disconnected, port-drifted, or wedged holding a dead room, so its restart loop can
    respawn it fresh.

.DESCRIPTION
    Guards four observed failure modes (a kill is always safe: run-arcade-glworker.ps1's
    restart loop respawns the worker in ~4 s and it re-registers with the coordinator):

    A) DISCONNECTED (first seen 2026-07-07, dolphin/GameCube teardown): the worker partially
       deadlocks at session teardown -- its HTTP /echo keeps answering, but its coordinator
       WebSocket stops pumping; the coordinator drops it and (in this variant) it never
       reconnects. Detection: a worker.exe process with NO established TCP connection to the
       coordinator port. Two consecutive strikes 30 s apart -> kill.

    B) PORT DRIFT (first seen 2026-07-10): a worker's coordinator socket drops while the
       process lives (e.g. the coordinator's i/o timeout severs it mid-wedge). The worker DOES
       reconnect after ~10 s -- but its reconnect re-runs single-port setup, finds its OWN old
       socket still bound to the configured port, and port-probes upward (8446 -> 8449). It
       then advertises a port the router doesn't forward and Defender doesn't allow: every
       future room on it is silently media-dead. Detection: a worker.exe owning any UDP port
       in the mux range that is NOT in -WorkerPorts. Kill immediately (nothing on it is
       usable; grace period skips fresh spawns).

    C) ROOM-CLOSE WEDGE (first seen 2026-07-10, twice: snes9x and pcsx2 teardown): the room's
       last player leaves, core teardown hangs (e.g. PCSX2 stops after "Releasing host
       memory..."), the room never closes, and the coordinator considers the worker busy
       FOREVER -- rooms then hang while a worker slot is silently gone. The coordinator's own
       read-timeout may reap it ~19 min later; that is far too slow.
       Detection: the coordinator's /status endpoint (patch 0033) lists each worker's Room;
       a LIVE room always writes pace-diag to its worker's log every 5 s, so
       "coordinator says busy" + "that worker's log silent > WedgeStaleSec" = wedged.
       The busy Room id is mapped to its worker via the "New room" line in the worker logs
       (log name -> WorkerId -> port -> owning PID). Two strikes -> kill.
       /status unavailable (pre-0033 coordinator) -> check C is skipped silently.

    D) IDLE-WEDGE (first seen 2026-07-22, dolphin/Wii boot-hang + a dolphin Vulkan teardown
       wedge): the worker HANGS while going idle -- a hard hang in core boot, or after a room
       teardown -- so it never emits another log line. Because the coordinator RELEASED its
       slot on the game-start timeout, /status marks it FREE, and its coordinator TCP socket
       stays ESTABLISHED (no FIN). So checks A (socket exists), B (right port) and C (busy)
       are all BLIND to it -- both GL workers sat wedged-but-"free" for 18 min and every launch
       died at "malformed WebRTC init/game start response error=timeout" (docs +
       [[arcade-idle-wedge-escapes-watchdog]]). Detection uses the coordinator's OWN failed-work
       signal: a fresh `malformed (WebRTC init|game start) response error=timeout` in
       coordinator.log means SOME worker accepted an assignment and did not respond. Response:
       recycle every GL worker past grace whose log is silent > WedgeStaleSec (a live room writes
       pace-diag every 5 s, so busy/booting workers are spared; the wedged one is guaranteed to be
       in that set; a healthy idle worker swept up here just respawns in ~4 s). Acts once per
       distinct timeout (tracked), and only on timeouts newer than ~3 cycles to avoid startup noise.

    E) STALE CONFIG (first seen 2026-07-23): workers read config.yaml ONLY at startup, so a config
       deploy/edit is silently INERT on every already-running worker until someone recycles it BY
       HAND. Both GL workers ran a 5-min-stale config whose default n64 core was still the OLD core,
       so a no-core-override room booted the WRONG core and its render-profile options reconciled
       DEAD ([[config.yaml is dead; diff before deploy]] is about deploying the file; this is about
       making the running worker actually load it). Detection: a worker whose ConfDir config.yaml
       LastWriteTime is AFTER the worker's process start. Response: gracefully recycle it (runner
       respawns on the current config) — but ONLY when it is FREE (not hosting a coordinator-known
       room, same busy->port map as check C), past grace, and not already handled by C/D; at most
       ONE per cycle so the pool is never drained (a busy worker is picked up once it goes free).
       This makes config deploys self-applying. (Capture worker excluded: its ConfDir isn't on the
       worker-gl/-N convention, so ConfDirForPid can't find its config and check E skips it.)

    F) ABSENT RUNNER (first seen 2026-07-25): every check above recycles a worker and trusts the
       runner's `while ($true)` loop to respawn it -- but nothing watched the RUNNER. When that
       PowerShell dies, its worker.exe keeps serving rooms ORPHANED and looks perfectly healthy;
       the moment it stops, the zone vanishes from the coordinator and NOTHING rebuilds it (the
       worker tasks are logon-triggered, and were registered with RestartCount 0). The capture
       worker sat orphaned ~13.7 h this way. Detection: a configured port with no UDP listener
       whose scheduled task is in state Ready (Running = the loop is alive and respawns in ~4 s;
       Disabled = deliberate, never overridden), two consecutive cycles so a normal recycle isn't
       mistaken for it. Response: Start-ScheduledTask. See [[arcade-runner-death-orphans-worker]].

    Registered as scheduled task "MovieTheater - Arcade GL Worker Watchdog" (logon trigger,
    same pattern as the worker tasks). Safe to run interactively too.
#>
param(
    [int]    $CoordinatorPort = 8000,
    [int]    $IntervalSec = 30,
    [int]    $GraceSec = 60,
    [int]    $WedgeStaleSec = 150,
    [int[]]  $WorkerPorts = @(8446, 8447, 8448),   # 8448 = capture worker (H5). Extend when adding workers.
    #  ⚠ The running watchdog TASK must be re-registered to pick up a new port — otherwise it reaps the
    #    new worker as "port drift" (bound a mux port not in this list). See the watchdog task registration.
    [string] $LogDir = "D:\ArcadeStorage\logs",
    [string] $LogFile = "D:\ArcadeStorage\logs\glworker-watchdog.log",
    [string] $CoordLog = "D:\ArcadeStorage\logs\coordinator.log",  # check D: coordinator work-timeout signal
    # Husk accounting: how many lingering husks before we advise a reboot. Small on purpose -- husks
    # cost ~0.7-0.9 GB of COMMIT and ~1700 handles each (measured 2026-07-24), so a couple is already
    # worth clearing at the next convenient moment even though none of them costs any CPU.
    [int]    $HuskAdviseThreshold = 2
)

# The task runs under a headless conhost: any cmdlet progress rendering (Invoke-RestMethod
# especially) throws "No process is on the other end of the pipe" at EndProcessing and aborts
# the whole cycle. Suppress progress globally.
$ProgressPreference = 'SilentlyContinue'

# SINGLETON: Stop-ScheduledTask kills only the conhost wrapper and orphans the powershell
# child, so task restarts stack watchdog instances (4 observed 2026-07-10) -- duplicate strike
# counting then kills workers after a single bad cycle. Newest instance wins: kill any older
# sibling running this script.
Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" -ErrorAction SilentlyContinue |
    Where-Object { $_.ProcessId -ne $PID -and $_.CommandLine -match 'watch-arcade-glworkers' } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }

New-Item -ItemType Directory -Force (Split-Path $LogFile) | Out-Null
function Log([string]$msg) {
    [System.IO.File]::AppendAllText($LogFile, ("{0}  {1}`r`n" -f (Get-Date -Format o), $msg), [System.Text.Encoding]::UTF8)
}

# Worker N (port 8445+N) logs to glworker.log (N=1) / glworker-N.log (N>1) -- the
# register-arcade-glworker-task.ps1 convention.
function WorkerTaskName([int]$port) {
    # Same port->id->name convention register-arcade-glworker-task.ps1 uses: worker 1 keeps the
    # historical unsuffixed name, 2+ get a numeric suffix.
    $id = $port - 8445
    if ($id -le 1) { return "MovieTheater - Arcade GL Worker" }
    return ("MovieTheater - Arcade GL Worker {0}" -f $id)
}

function WorkerLogPath([int]$port) {
    $id = $port - 8445
    if ($id -le 1) { return (Join-Path $LogDir "glworker.log") }
    return (Join-Path $LogDir ("glworker-{0}.log" -f $id))
}

# The LAST "New room" id in a worker's log (checks the rotated .1 file as fallback).
# Captures the id from BOTH shapes the logger emits:
#   room="sv-1-24-0-nes___Super Mario Bros. 3"      <- quoted, because the id contains spaces
#   room=sv-1-61329-0-capture___switch-kirby-...    <- UNQUOTED: zerolog only quotes when it must
# The quoted-only regex was a silent hole: CAPTURE room ids never contain spaces, so they never
# matched, every cycle logged "busy room not found in any worker log (skip)", and check C could not
# see the capture worker at all. It sat wedged holding its one room for a DAY (2026-07-11 -> 07-12)
# while the watchdog "ran" — and a wedged capture worker looks exactly like "the arcade is full".
#
# ⚠ The worker log is COLORIZED: zerolog writes an ANSI escape between `room=` and the value, so a
# pattern anchored on the literal `room="` matches NOTHING — check C was dead for EVERY worker, not
# just capture. Strip the escapes first, then accept either shape.
$roomRx = 'New room.*room=(?:"([^"]+)"|(\S+))'
$ansiRx = "$([char]27)\[[0-9;]*m"
# Returns @{ Room = <id>; Time = <[datetime] of the line, or $null if unparseable> }.
# The Time matters: a busy room can only genuinely live on a worker if the CURRENT process on that
# port wrote the "New room" line — a line older than the process's start time is a GHOST (a stale
# log tail left by a previous incarnation, echoing a dead coordinator slot). On 2026-07-20/21 the
# old first-port-match mapping hit exactly that: both GL logs' last room was the same wedged title,
# the watchdog blamed port 8446 all night (~18 innocent idle workers recycled, one every ~3 min)
# while the truly wedged worker on 8447 sat untouched for 5.5 h.
function LastRoomInLog([string]$path) {
    foreach ($f in @($path, "$path.1")) {
        if (-not (Test-Path $f)) { continue }
        $m = Get-Content $f -Tail 20000 -ErrorAction SilentlyContinue |
            ForEach-Object { $_ -replace $ansiRx, '' } |
            Select-String -Pattern $roomRx | Select-Object -Last 1
        if ($m) {
            $g = $m.Matches[0].Groups
            $room = if ($g[1].Success) { $g[1].Value } else { $g[2].Value }
            $t = $null
            if ($m.Line -match '^(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d+)') {
                $parsed = [datetime]::MinValue
                if ([datetime]::TryParse($Matches[1], [ref]$parsed)) { $t = $parsed }
            }
            return @{ Room = $room; Time = $t }
        }
    }
    return $null
}

# Map a worker PID to its ConfDir via the mux UDP port it owns (8446 -> worker-gl, 8447 -> worker-gl-2, ...),
# so we can drop that worker's graceful-stop sentinel.
function ConfDirForPid([int]$wpid) {
    $p = (Get-NetUDPEndpoint -ErrorAction SilentlyContinue |
        Where-Object { [int]$_.OwningProcess -eq $wpid -and $_.LocalPort -ge 8446 -and $_.LocalPort -le 8465 } |
        Select-Object -First 1).LocalPort
    if (-not $p) { return $null }
    $id = $p - 8445
    if ($id -le 1) { return "D:\ArcadeStorage\worker-gl" } else { return "D:\ArcadeStorage\worker-gl-$id" }
}

# $zombies: PIDs that SURVIVED a force-kill (kernel-stuck GPU-teardown thread, unkillable from user mode).
# They hold their ConfDir's DLL + shader cache locked and their coordinator slot is dead until a reboot.
# We track them so we (a) surface them every cycle instead of silently forgetting (PID 7948 sat 10 h), and
# (b) do not thrash them with useless kills.
$zombies = @{}

function KillWorker([int]$wpid, [string]$why) {
    if ($zombies[$wpid]) { return }   # known unkillable; surfaced in the main loop, don't thrash it
    # GRACEFUL first: let the worker flush its GS shader cache and tear down GL/NVENC cleanly, so we don't
    # hand the next player a cold cache (periodic in-game audio skips) or strand a kernel thread (zombie).
    # Requires a worker built with the stop-file watch (pkg/os ExpectTermination); older binaries ignore
    # the sentinel and we fall through to force after the wait -- still correct, just not graceful.
    #
    # The wait is 60s, NOT seconds: the worker bounds its own wedged-teardown stages internally (room
    # close 30s, media destroy 10s, whole-shutdown deadman 45s) and each ends in a self-TerminateProcess.
    # We must outwait ALL of those so a wedged worker dies by its own hand. The old 8s window force-killed
    # workers mid-GPU-teardown -- the proven trigger for the UNKILLABLE zombie (2026-07-19 PID 11328,
    # 2026-07-21 PID 4100; docs/arcade-worker-unkillable-wedge.md). Force-kill is a true last resort.
    $conf = ConfDirForPid $wpid
    if ($conf) {
        Log ("worker PID {0} -- requesting GRACEFUL stop ({1})" -f $wpid, $why)
        $sf = Join-Path $conf ".stop"
        Set-Content -Path $sf -Value (Get-Date -Format o) -Encoding ASCII -ErrorAction SilentlyContinue
        for ($i = 0; $i -lt 120 -and (Get-Process -Id $wpid -ErrorAction SilentlyContinue); $i++) { Start-Sleep -Milliseconds 500 }
        Remove-Item $sf -Force -ErrorAction SilentlyContinue
        if (-not (Get-Process -Id $wpid -ErrorAction SilentlyContinue)) {
            Log ("worker PID {0} exited GRACEFULLY -- {1} -- restart loop respawns it" -f $wpid, $why); return
        }
    }
    Log ("KILLING worker PID {0} (force) -- {1}" -f $wpid, $why)
    Stop-Process -Id $wpid -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
    # VERIFY: a force-kill that does not take means the thread is stuck in the GPU driver (kernel wait).
    if (Get-Process -Id $wpid -ErrorAction SilentlyContinue) {
        $m = ("WEDGED/UNKILLABLE: worker PID {0} SURVIVED force-kill (half-exited process pinned by a " +
              "crashed core/GPU thread -- see docs/arcade-worker-unkillable-wedge.md). Holds its " +
              "ConfDir locked, coordinator slot dead. BOX REBOOT REQUIRED. ({1})") -f $wpid, $why
        Log $m
        $zombies[$wpid] = $true
        Set-Content -Path (Join-Path $LogDir ("WEDGED-worker-{0}.flag" -f $wpid)) `
            -Value ("{0}  {1}" -f (Get-Date -Format o), $m) -Encoding UTF8 -ErrorAction SilentlyContinue
    }
}

# ---- Husk accounting ---------------------------------------------------------------------------
# A HUSK is a worker process that has already died in every sense that matters -- its last thread is
# parked forever in a kernel-mode GPU-driver teardown -- but that Windows will not reap. It is NOT the
# same population as $zombies: $zombies only holds PIDs THIS watchdog force-killed and watched survive.
# A husk can also be left behind by the worker's own sentinel HardExit or by a core crash, i.e. by a
# path the watchdog never touched, so nothing was counting them and they accumulated unnoticed.
#
# Signature (measured 2026-07-24): process name 'worker', thread count 1 (a healthy worker runs ~14-21),
# 0 CPU forever because that one thread is never scheduled, tiny working set (0.2-7 MB) -- but ~0.7-0.9 GB
# of COMMIT CHARGE and ~1700 handles each. Commit is the real cost: enough husks and the box starts
# refusing allocations while Task Manager still shows plenty of "free" RAM.
#
# We only report. Killing is proven futile (that is what makes it a husk), and a reboot is an operator
# decision -- so the output is an advisory, not an action.
$huskSeen = @{}          # PID -> $true, so we log each husk's arrival once instead of every 30s
$huskLastAdvise = [datetime]::MinValue

function HuskScan {
    $now = Get-Date
    $husks = @()
    foreach ($p in @(Get-Process -Name worker -ErrorAction SilentlyContinue)) {
        try {
            # Guard against a worker caught mid-startup: a booting process can momentarily show few
            # threads, and reaping-adjacent alarms about a healthy worker are worse than a late one.
            if ($p.Threads.Count -gt 1) { continue }
            if (($now - $p.StartTime).TotalSeconds -lt 120) { continue }
            $husks += $p
        } catch { continue }   # process exited between enumeration and inspection
    }

    foreach ($p in $husks) {
        if ($huskSeen[$p.Id]) { continue }
        $huskSeen[$p.Id] = $true
        # PageFileUsage (commit) is the number that actually matters; WorkingSet looks harmlessly small.
        # It is reported in KILOBYTES, so GB = /1MB. (Dividing by 1KB gives MB -- that mislabel read as
        # "830 GB per husk" in testing, which would have sent someone hunting a nonexistent leak.)
        $commitGb = 0; $handles = 0
        try {
            $ci = Get-CimInstance Win32_Process -Filter ("ProcessId={0}" -f $p.Id) -ErrorAction SilentlyContinue
            if ($ci) { $commitGb = [math]::Round($ci.PageFileUsage / 1MB, 2); $handles = $ci.HandleCount }
        } catch { }
        Log ("HUSK: worker PID {0} is a 1-thread husk (started {1}) -- 0 CPU, but ~{2} GB commit and {3} handles. Cannot be killed; only a reboot frees it." `
             -f $p.Id, $p.StartTime.ToString('s'), $commitGb, $handles)
    }
    foreach ($k in @($huskSeen.Keys)) { if ($husks.Id -notcontains $k) { $huskSeen.Remove($k) | Out-Null } }

    if ($husks.Count -ge $HuskAdviseThreshold) {
        # Re-state periodically rather than every cycle: this is a "when convenient" nudge, and a line
        # every 30s would train the reader to scroll past it.
        if (($now - $huskLastAdvise).TotalMinutes -ge 10) {
            $script:huskLastAdvise = $now
            $totalGb = 0
            foreach ($p in $husks) {
                try {
                    $ci = Get-CimInstance Win32_Process -Filter ("ProcessId={0}" -f $p.Id) -ErrorAction SilentlyContinue
                    if ($ci) { $totalGb += $ci.PageFileUsage / 1MB }
                } catch { }
            }
            Log ("REBOOT ADVISED WHEN CONVENIENT: {0} wedged worker husks lingering (PIDs {1}) holding ~{2} GB of commit. Nothing is broken right now -- they just never go away on their own." `
                 -f $husks.Count, (($husks.Id | Sort-Object) -join ','), [math]::Round($totalGb, 2))
        }
        Set-Content -Path (Join-Path $LogDir "HUSKS-reboot-advised.flag") `
            -Value ("{0}  {1} husks: {2}" -f (Get-Date -Format o), $husks.Count, (($husks.Id | Sort-Object) -join ',')) `
            -Encoding UTF8 -ErrorAction SilentlyContinue
    } else {
        # Cleared by a reboot (or by the husks finally being reaped) -- drop the flag so it never
        # advises a reboot that already happened.
        Remove-Item (Join-Path $LogDir "HUSKS-reboot-advised.flag") -Force -ErrorAction SilentlyContinue
    }
}

Log "watchdog v2 started (coordinator port: $CoordinatorPort, interval: ${IntervalSec}s, worker ports: $($WorkerPorts -join ','), wedge stale: ${WedgeStaleSec}s, husk advise at: $HuskAdviseThreshold)"
$strikes = @{}       # PID -> consecutive no-coordinator-connection strikes (check A)
$wedgeStrikes = @{}  # PID -> consecutive busy-but-silent strikes (check C)
$absentStrikes = @{} # port -> consecutive "no listener AND no runner" strikes (check F)
$lastActedTimeout = [datetime]::MinValue  # newest coordinator work-timeout already acted on (check D)

while ($true) {
    try {
        $workers = @(Get-CimInstance Win32_Process -Filter "Name='worker.exe'" -ErrorAction SilentlyContinue)
        $age = @{}
        foreach ($w in $workers) { $age[[int]$w.ProcessId] = ((Get-Date) - $w.CreationDate).TotalSeconds }
        $livePids = @{}; foreach ($w in $workers) { $livePids[[int]$w.ProcessId] = $true }

        # -- A) coordinator connection check ------------------------------------------------
        $connected = @{}
        Get-NetTCPConnection -State Established -RemotePort $CoordinatorPort -ErrorAction SilentlyContinue |
            ForEach-Object { $connected[[int]$_.OwningProcess] = $true }

        foreach ($w in $workers) {
            $wpid = [int]$w.ProcessId
            if ($age[$wpid] -lt $GraceSec) { $strikes[$wpid] = 0; continue }
            if ($connected[$wpid])         { $strikes[$wpid] = 0; continue }
            $strikes[$wpid] = [int]$strikes[$wpid] + 1
            Log ("worker PID {0} has NO coordinator connection (strike {1})" -f $wpid, $strikes[$wpid])
            if ($strikes[$wpid] -ge 2) {
                KillWorker $wpid "disconnected from coordinator"
                $strikes.Remove($wpid) | Out-Null
            }
        }

        # -- B) port-drift check -------------------------------------------------------------
        # Any worker-owned UDP port in the mux neighbourhood that is not a configured port
        # means the worker rebound after an in-process reconnect: recycle on sight.
        $muxLo = ($WorkerPorts | Measure-Object -Minimum).Minimum
        $muxHi = $muxLo + 20
        Get-NetUDPEndpoint -ErrorAction SilentlyContinue |
            Where-Object { $_.LocalPort -ge $muxLo -and $_.LocalPort -le $muxHi -and $livePids[[int]$_.OwningProcess] } |
            ForEach-Object {
                $wpid = [int]$_.OwningProcess
                if (($WorkerPorts -notcontains [int]$_.LocalPort) -and $age[$wpid] -ge $GraceSec) {
                    KillWorker $wpid ("port drift: bound UDP {0}, expected one of {1}" -f $_.LocalPort, ($WorkerPorts -join ','))
                }
            }

        # -- C) room-close wedge check (needs coordinator /status, patch 0033) ---------------
        $status = $null
        try {
            $status = Invoke-RestMethod -Uri "http://localhost:$CoordinatorPort/status" -TimeoutSec 3
        } catch { }  # old coordinator: no endpoint; skip silently

        $wedgedPids = @{}
        if ($status) {
            foreach ($entry in @($status | Where-Object { $_.room })) {
                # Map the busy room to a worker log by its "New room" line — with a GHOST guard:
                # a port only qualifies if its log's LAST room is this room AND that line was written
                # by the current process on the port (line time >= process start). A line that
                # predates the process is a ghost (stale tail / dead coordinator slot, e.g. a zombie
                # worker's room) — killing by ghost shoots healthy workers, so ghosts are logged,
                # never killed. When several ports qualify, the newest line wins.
                $port = $null; $portTime = $null
                foreach ($p in $WorkerPorts) {
                    $last = LastRoomInLog (WorkerLogPath $p)
                    if (-not $last -or $last.Room -ne $entry.room) { continue }
                    $owner = (Get-NetUDPEndpoint -LocalPort $p -ErrorAction SilentlyContinue | Select-Object -First 1).OwningProcess
                    $proc = if ($owner) { $workers | Where-Object { [int]$_.ProcessId -eq [int]$owner } | Select-Object -First 1 } else { $null }
                    if ($last.Time -and $proc -and $last.Time -lt $proc.CreationDate) {
                        Log ("ghost room '{0}': port {1} log matches but the line ({2}) predates its current worker PID {3} (started {4}) -- stale coordinator slot; NOT killing" -f `
                            $entry.room, $p, $last.Time.ToString('o'), [int]$proc.ProcessId, $proc.CreationDate.ToString('o'))
                        continue
                    }
                    if (-not $port -or ($last.Time -and $portTime -and $last.Time -gt $portTime)) { $port = $p; $portTime = $last.Time }
                }
                if (-not $port) { Log ("busy room not found in any live worker log (skip): {0}" -f $entry.room); continue }
                $logPath = WorkerLogPath $port
                $staleSec = ((Get-Date) - (Get-Item $logPath).LastWriteTime).TotalSeconds
                if ($staleSec -lt $WedgeStaleSec) { continue }   # room is ticking (pace-diag every 5 s)
                $wpid = (Get-NetUDPEndpoint -LocalPort $port -ErrorAction SilentlyContinue | Select-Object -First 1).OwningProcess
                if (-not $wpid -or $age[[int]$wpid] -lt $GraceSec) { continue }
                $wpid = [int]$wpid
                $wedgedPids[$wpid] = $true
                $wedgeStrikes[$wpid] = [int]$wedgeStrikes[$wpid] + 1
                Log ("worker PID {0} (port {1}) BUSY with '{2}' but log silent {3:n0}s (wedge strike {4})" -f `
                    $wpid, $port, $entry.room, $staleSec, $wedgeStrikes[$wpid])
                if ($wedgeStrikes[$wpid] -ge 2) {
                    KillWorker $wpid "room-close wedge (busy + silent log)"
                    $wedgeStrikes.Remove($wpid) | Out-Null
                }
            }
        }
        foreach ($k in @($wedgeStrikes.Keys)) { if (-not $wedgedPids[$k]) { $wedgeStrikes.Remove($k) | Out-Null } }

        # -- D) idle-wedge check (FREE-but-wedged; checks A/B/C blind spot) ------------------
        # A worker that hangs in boot/teardown while going idle keeps its Established coordinator
        # socket and its port, and the coordinator releases its slot on the game-start timeout so
        # /status shows it FREE — invisible to A/B/C. The coordinator's OWN failed-work signal is
        # the tell: a fresh `malformed (WebRTC init|game start) response error=timeout` means some
        # worker accepted an assignment and never responded. Recycle every GL worker past grace
        # whose log is silent > WedgeStaleSec (a live room writes pace-diag every 5 s -> busy/booting
        # workers are spared; the wedged one is guaranteed in the set; a healthy idle worker swept
        # up here just respawns in ~4 s). Acts once per distinct timeout; ignores timeouts older
        # than ~3 cycles so a stale line at startup can't trigger a recycle.
        $newestTimeout = $null
        try {
            foreach ($line in @(Get-Content $CoordLog -Tail 300 -ErrorAction SilentlyContinue)) {
                if ($line -notmatch 'malformed (?:WebRTC init|game start) response error=timeout') { continue }
                if ($line -match '^(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d+)') {
                    $lt = [datetime]::MinValue
                    if ([datetime]::TryParse($Matches[1], [ref]$lt)) {
                        if (-not $newestTimeout -or $lt -gt $newestTimeout) { $newestTimeout = $lt }
                    }
                }
            }
        } catch { }

        if ($newestTimeout -and $newestTimeout -gt $lastActedTimeout `
                -and $newestTimeout -ge (Get-Date).AddSeconds(-3 * $IntervalSec)) {
            $lastActedTimeout = $newestTimeout
            $acted = $false
            foreach ($p in $WorkerPorts) {
                $wpid = (Get-NetUDPEndpoint -LocalPort $p -ErrorAction SilentlyContinue | Select-Object -First 1).OwningProcess
                if (-not $wpid) { continue }
                $wpid = [int]$wpid
                if (-not $livePids[$wpid]) { continue }
                if ($age[$wpid] -lt $GraceSec) { continue }        # fresh spawn: not this
                if ($wedgedPids[$wpid]) { continue }               # already handled by check C
                $logPath = WorkerLogPath $p
                if (-not (Test-Path $logPath)) { continue }
                $staleSec = ((Get-Date) - (Get-Item $logPath).LastWriteTime).TotalSeconds
                if ($staleSec -lt $WedgeStaleSec) { continue }     # actively logging => not wedged
                Log ("worker PID {0} (port {1}) IDLE-WEDGE: coordinator reported a worker work-timeout at {2} and this worker's log is silent {3:n0}s while marked free -- recycling (checks A/B/C blind spot)" -f `
                    $wpid, $p, $newestTimeout.ToString('o'), $staleSec)
                KillWorker $wpid "idle-wedge (coordinator work-timeout + free + silent log)"
                $acted = $true
            }
            if (-not $acted) {
                Log ("coordinator work-timeout at {0} but no past-grace, log-silent worker to recycle (all busy/booting/fresh) -- noted only" -f $newestTimeout.ToString('o'))
            }
        }

        # -- E) STALE-CONFIG check (self-applying config deploys) ----------------------------
        # Workers read config.yaml ONLY at startup; a deploy/edit is inert on a running worker until
        # it is recycled. When a worker's ConfDir config.yaml was written AFTER the worker started,
        # gracefully recycle it so the runner reloads the new config -- but ONLY when it is FREE (not
        # hosting a coordinator-known room; same busy->port map + ghost guard as check C), past grace,
        # and not already being recycled by C/D. At most ONE per cycle so the pool is never drained
        # (a busy worker is recycled once it goes free on a later cycle). Graceful path flushes the
        # shader cache like any recycle. See the .DESCRIPTION E) note (the 2026-07-23 stale-core trap).
        if ($status) {
            $busyPorts = @{}
            foreach ($entry in @($status | Where-Object { $_.room })) {
                foreach ($p in $WorkerPorts) {
                    $last = LastRoomInLog (WorkerLogPath $p)
                    if (-not $last -or $last.Room -ne $entry.room) { continue }
                    $owner = (Get-NetUDPEndpoint -LocalPort $p -ErrorAction SilentlyContinue | Select-Object -First 1).OwningProcess
                    $proc  = if ($owner) { $workers | Where-Object { [int]$_.ProcessId -eq [int]$owner } | Select-Object -First 1 } else { $null }
                    if ($last.Time -and $proc -and $last.Time -lt $proc.CreationDate) { continue }  # ghost tail; not really busy here
                    $busyPorts[$p] = $true
                }
            }
            foreach ($p in $WorkerPorts) {
                if ($busyPorts[$p]) { continue }                                    # hosting a live room
                $owner = (Get-NetUDPEndpoint -LocalPort $p -ErrorAction SilentlyContinue | Select-Object -First 1).OwningProcess
                if (-not $owner) { continue }
                $wpid = [int]$owner
                if (-not $livePids[$wpid]) { continue }
                if ($age[$wpid] -lt $GraceSec) { continue }                         # fresh spawn already read the current config
                if ($wedgedPids[$wpid]) { continue }                                # C is handling it
                $conf = ConfDirForPid $wpid
                if (-not $conf) { continue }
                $cfgPath = Join-Path $conf "config.yaml"
                if (-not (Test-Path $cfgPath)) { continue }                         # e.g. capture worker's ConfDir differs; skip
                $cfgTime   = (Get-Item $cfgPath).LastWriteTime
                $startedAt = ($workers | Where-Object { [int]$_.ProcessId -eq $wpid } | Select-Object -First 1).CreationDate
                if ($startedAt -and $cfgTime -gt $startedAt) {
                    Log ("worker PID {0} (port {1}) STALE CONFIG: {2} written {3:o} but worker started {4:o} -- graceful recycle so it reloads the current config (free, past grace)" -f `
                        $wpid, $p, $cfgPath, $cfgTime, $startedAt)
                    KillWorker $wpid "stale config (config.yaml newer than worker start)"
                    break   # one per cycle: never drain the pool
                }
            }
        }

        # -- F) absent-runner check ----------------------------------------------------------
        # Every check above assumes a worker EXISTS to be recycled, and every recycle assumes the
        # runner's `while ($true)` loop will respawn it. Nothing watched the runner itself: when that
        # PowerShell dies, its worker.exe keeps serving rooms ORPHANED and looks perfectly healthy --
        # until it stops, and then the zone silently disappears from the coordinator with nothing left
        # to rebuild it. Observed 2026-07-25: the capture worker (8448) had been orphaned ~13.7 h.
        #
        # The task STATE is what disambiguates, so this never races a live runner: task Running means
        # the loop is up and will respawn within ~4s on its own -- leave it alone. Only a port with no
        # listener AND a task sitting in Ready has genuinely lost its supervisor. Disabled is somebody's
        # deliberate choice and is never overridden. Two consecutive cycles required so a normal
        # recycle (KillWorker -> 4s respawn) is never mistaken for an absent runner.
        foreach ($p in $WorkerPorts) {
            $owner = (Get-NetUDPEndpoint -LocalPort $p -ErrorAction SilentlyContinue | Select-Object -First 1).OwningProcess
            if ($owner) { $absentStrikes.Remove($p) | Out-Null; continue }
            $taskName = WorkerTaskName $p
            $task = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
            if (-not $task) { continue }                                            # worker not registered on this box
            if ($task.State -ne 'Ready') { $absentStrikes.Remove($p) | Out-Null; continue }  # Running = healthy, Disabled = deliberate
            $absentStrikes[$p] = [int]$absentStrikes[$p] + 1
            Log ("worker port {0}: no listener and task '{1}' is Ready -- runner is GONE (strike {2})" -f $p, $taskName, $absentStrikes[$p])
            if ($absentStrikes[$p] -ge 2) {
                try {
                    Start-ScheduledTask -TaskName $taskName -ErrorAction Stop
                    Log ("  started '{0}' -- worker port {1} should re-register with the coordinator shortly" -f $taskName, $p)
                } catch {
                    Log ("  Start-ScheduledTask '{0}' FAILED: {1}" -f $taskName, $_.Exception.Message)
                }
                $absentStrikes.Remove($p) | Out-Null
            }
        }

        # Surface unkillable zombies every cycle -- they do NOT self-clear (only a reboot frees the
        # kernel-stuck thread), so an operator must see them. Clear the entry once the PID is finally gone.
        foreach ($zp in @($zombies.Keys)) {
            if ($livePids[$zp]) { Log ("REMINDER: worker PID {0} still WEDGED/unkillable -- BOX REBOOT REQUIRED" -f $zp) }
            else { $zombies.Remove($zp) | Out-Null }
        }

        # Count husks the watchdog never killed itself (sentinel HardExit, core crash) -- see HuskScan.
        HuskScan

        # Tidy strike entries for PIDs that no longer exist.
        foreach ($k in @($strikes.Keys)) { if (-not $livePids[$k]) { $strikes.Remove($k) | Out-Null } }
    } catch {
        Log ("watchdog cycle error: {0}" -f $_.Exception.Message)
    }
    Start-Sleep -Seconds $IntervalSec
}
