<#
.SYNOPSIS
    Registers the logon Scheduled Task that keeps the Windows-native arcade GL worker running
    (roadmap WS-B, docs/arcade-windows-worker.md). Sibling of register-arcade-wsl-task.ps1.

.DESCRIPTION
    WGL hardware acceleration needs an INTERACTIVE session — a session-0 Windows service gets
    software-only GL and defeats the whole point. So, exactly like the WSL keepalive, this runs in
    the user's interactive logon session: the task launches scripts/run-arcade-glworker.ps1, which
    sets the CLOUD_GAME_* env, prepends the MSYS2 UCRT64 bin to PATH, and loops worker.exe.

    Run ONCE as the user who logs in on Ziggy (no admin needed for an interactive-logon task).
    Prereqs: the worker built (worker.exe), config staged at the ConfDir, MSYS2 installed. Re-run to update.

    ONE WORKER = ONE CONCURRENT ROOM (a busy worker rejects with t=112). Concurrency is the number of
    registered worker tasks, so run this once per worker with a distinct -WorkerId and -SinglePort.
    Each worker needs its UDP mux port forwarded to Ziggy at the router; the Defender inbound rule
    "Arcade Site Traffic" already allows 8443-8448.

.PARAMETER WorkerId    1-based worker number. Names the task and its log; 1 keeps the original
                       (unsuffixed) task name so re-running is idempotent for the existing worker.
.PARAMETER SinglePort  That worker's WebRTC UDP mux port. Defaults to 8445 + WorkerId (8446, 8447, …).

.EXAMPLE
    # (re)register the first worker — port 8446
    powershell.exe -ExecutionPolicy Bypass -File register-arcade-glworker-task.ps1

.EXAMPLE
    # add a second worker — port 8447, a second concurrent room
    powershell.exe -ExecutionPolicy Bypass -File register-arcade-glworker-task.ps1 -WorkerId 2
#>
param(
    [string]$RunScript  = (Join-Path $PSScriptRoot "run-arcade-glworker.ps1"),
    [int]   $WorkerId   = 1,
    [int]   $SinglePort = 0,      # 0 => 8445 + WorkerId
    [string]$LogFile    = "",     # ""=> D:\ArcadeStorage\logs\glworker[-N].log
    # Capture worker (H5): register worker 3 as the browser capture lane —
    #   -WorkerId 3 -Zone capture -ConfDir D:\ArcadeStorage\worker-capture `
    #   -LibraryBasePath D:\ArcadeStorage\heavy\capture-stubs `
    #   -WorkerExe D:\ArcadeStorage\worker-capture\bin\worker.exe
    # (that bin dir also holds vigemclient.dll, next to worker.exe). Retro workers use the defaults.
    [string]$Zone            = "main",
    # "" => per-worker default below. Each retro worker MUST have its OWN ConfDir: the cores write
    # their virtual MEMORY CARDS into it (Dolphin under libretro\legacy_save, PCSX2 under
    # libretro\system), and the worker seeds/harvests those per user (patch 0039). Two workers sharing
    # a ConfDir would seed and harvest each other's cards — i.e. hand one player another's saves.
    [string]$ConfDir         = "",
    [string]$LibraryBasePath = "",  # "" => run script default (roms)
    [string]$WorkerExe       = ""   # "" => run script default
)

if ($SinglePort -le 0) { $SinglePort = 8445 + $WorkerId }

# Worker 1 keeps the historical unsuffixed task name / glworker.log (the watchdog + crash lore
# reference them); workers 2+ get a numeric suffix. Distinct log files are REQUIRED — the runner
# rotates its log at 25MB and two workers sharing one would clobber each other's crash dumps.
$suffix    = if ($WorkerId -eq 1) { "" } else { " $WorkerId" }
$TaskName  = "MovieTheater - Arcade GL Worker$suffix"
if (-not $LogFile) {
    $logSuffix = if ($WorkerId -eq 1) { "" } else { "-$WorkerId" }
    $LogFile   = "D:\ArcadeStorage\logs\glworker$logSuffix.log"
}

$principal = New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType Interactive

# Optional passthroughs (capture worker). Empty = the run script's own defaults (retro worker-gl).
$extra = ""
if ($Zone)            { $extra += " -Zone $Zone" }
# Per-worker ConfDir (see the param note): worker 1 keeps the historical dir, worker N gets its own.
# NEVER let two retro workers land on the same one.
if (-not $ConfDir -and $Zone -eq "main") {
    $ConfDir = if ($WorkerId -le 1) { "D:\ArcadeStorage\worker-gl" } else { "D:\ArcadeStorage\worker-gl-$WorkerId" }
}
if ($ConfDir)         { $extra += " -ConfDir `"$ConfDir`"" }
if ($LibraryBasePath) { $extra += " -LibraryBasePath `"$LibraryBasePath`"" }
if ($WorkerExe)       { $extra += " -WorkerExe `"$WorkerExe`"" }

# conhost --headless keeps it windowless, same trick as the WSL keepalive task.
$action = New-ScheduledTaskAction -Execute "$env:SystemRoot\System32\conhost.exe" `
    -Argument ("--headless powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden " +
               "-File `"$RunScript`" -SinglePort $SinglePort -LogFile `"$LogFile`"$extra")

$trigger  = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME
# RestartCount/RestartInterval supervise the RUNNER -- not the worker. run-arcade-glworker.ps1 already
# restarts a crashed worker.exe in its own `while ($true)` loop, but nothing watched that script: with a
# logon-only trigger and RestartCount 0, a runner that died was never coming back, and its worker.exe
# went on serving rooms ORPHANED. That looks perfectly healthy right up until the process stops, at which
# point the zone vanishes from the coordinator with nothing left to rebuild it. Observed 2026-07-25: the
# capture worker had been orphaned for ~13.7 h. The task only counts a restart when the runner exits
# non-zero, which is the only way that infinite loop ever terminates.
$settings = New-ScheduledTaskSettingsSet `
    -ExecutionTimeLimit  (New-TimeSpan -Seconds 0) `
    -StartWhenAvailable `
    -MultipleInstances   IgnoreNew `
    -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries `
    -RestartCount        999 `
    -RestartInterval     (New-TimeSpan -Minutes 1)

if (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue) {
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
}

Register-ScheduledTask `
    -TaskName    $TaskName `
    -Action      $action `
    -Trigger     $trigger `
    -Settings    $settings `
    -Principal   $principal `
    -Description "Runs Windows-native arcade GL worker #$WorkerId (WGL/NVIDIA GL; UDP mux $SinglePort) in the interactive session so it survives logoff/reboot. One worker = one concurrent room. Managed by register-arcade-glworker-task.ps1." | Out-Null

Start-ScheduledTask -TaskName $TaskName
Write-Host "Task '$TaskName' registered and started (port $SinglePort, log $LogFile)."
Write-Host "Confirm it boots + registers zone=main:  Get-Content '$LogFile' -Tail 40"
Write-Host "Router must UDP-forward $SinglePort -> Ziggy, or this worker's rooms will fail ICE from outside the LAN."
