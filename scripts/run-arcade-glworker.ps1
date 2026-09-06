<#
.SYNOPSIS
    Runs the Windows-native CloudRetro GL worker (roadmap WS-B) in a restart loop.

.DESCRIPTION
    The GL 3D cores (flycast dc/naomi/atomiswave, ppsspp psp) need real NVIDIA OpenGL, which
    the WSL2/WSLg stack can't provide (GLX/EGL context creation fails). This worker is the
    MSYS2/UCRT64-built ./cmd/worker running natively on Windows (WGL on a hidden window → GL 4.6).
    See docs/arcade-windows-worker.md. It joins the SAME WSL coordinator (localhost:8000) in the
    "gl" zone so the coordinator never hands it a 2D room (CloudRetroHost.ZoneForSystem).

    Run from the INTERACTIVE session (not a service): session-0 gets software-only WGL. This is the
    loop body; scripts/register-arcade-glworker-task.ps1 registers the logon task that keeps it up.

.PARAMETER WorkerExe    Path to the built worker.exe.
.PARAMETER ConfDir      Directory holding the worker config (config.worker-gl.yaml renamed config.yaml).
.PARAMETER Ucrt64Bin    MSYS2 UCRT64 bin dir — its GStreamer DLLs must be on PATH for the cgo worker.
.PARAMETER IceIpMap     What ICE advertises — MUST match docker/arcade/.env ZIGGY_PUBLIC_IP.
.PARAMETER SinglePort   The worker's WebRTC UDP mux port. One port per worker (8446, 8447, …) — the
                        split-audio aux PeerConnection (patch 0020) reuses the SAME Pion api/mux, so
                        audio needs no extra port. Router must UDP-forward it; Defender rule
                        "Arcade Site Traffic" already covers 8443-8448.
.PARAMETER LogFile      Per-worker log. MUST be distinct per worker or the rotation below races.

.NOTES
    EACH RETRO WORKER NEEDS ITS OWN ConfDir (worker-gl, worker-gl-2, ...). Workers used to share one
    — same core list, same BIOS junction, same cache — which was fine while everything persistent was
    keyed by room id. It stopped being fine with per-user memory cards (patch 0039): the cores write
    their cards INTO the ConfDir (Dolphin under libretro\legacy_save\User\GC, PCSX2 under
    libretro\system\pcsx2\memcards), and the worker seeds the room owner's card there on boot and
    harvests it on close. Two workers sharing that directory would seed and harvest each other's
    cards — one player's characters handed to another, or overwritten.

    Consequently libretro\system is a REAL per-worker copy of D:\ArcadeStorage\bios, not a junction
    to it (PCSX2 writes its cards into the system dir, so a shared junction is a shared card).
    D:\ArcadeStorage\bios stays the pristine master to copy from.

    Still shared and safe: the ROM library (read-only) and emulator.storage (save-states, keyed by
    room id). Cost of the split is ~280 MB per worker (cores + BIOS) and each worker builds its own
    shader cache.
#>
param(
    [string]$WorkerExe   = "D:\Arcade\build\cloud-game-gl\bin\worker.exe",
    [string]$ConfDir     = "D:\ArcadeStorage\worker-gl",
    [string]$Ucrt64Bin   = "D:\msys64\ucrt64\bin",
    [string]$IceIpMap    = "",   # resolved from docker/arcade/.env ZIGGY_PUBLIC_IP below; never hardcode the IP here
    [int]   $SinglePort  = 8446,
    # Worker network zone. "main" = the retro pool (every non-capture room). "capture" = the H5 browser
    # capture worker (docs/arcade-capture-worker-plan.md). The gateway derives a room's zone from its
    # room id, so these MUST match: retro workers "main", the capture worker "capture".
    [string]$Zone        = "main",
    # Library base. env CLOUD_GAME_LIBRARY_BASEPATH OVERRIDES the yaml, so the capture worker must point
    # it at its .capture stub dir (not the retro roms) or FindAppByName won't resolve capture titles.
    [string]$LibraryBasePath = "D:\ArcadeStorage\roms",
    [string]$LogFile     = "D:\ArcadeStorage\logs\glworker.log"
)

# Resolve the ICE IP from docker/arcade/.env (ZIGGY_PUBLIC_IP) — the SAME source the WSL workers use,
# and it keeps the real public IP out of this committed script (it's gitignored in .env).
if (-not $IceIpMap) {
    $envFile = Join-Path $PSScriptRoot "..\docker\arcade\.env"
    if (Test-Path $envFile) {
        $m = Select-String -Path $envFile -Pattern '^\s*ZIGGY_PUBLIC_IP\s*=\s*(\S+)' | Select-Object -First 1
        if ($m) { $IceIpMap = $m.Matches[0].Groups[1].Value.Trim() }
    }
}
if (-not $IceIpMap) { Write-Warning "IceIpMap unset and ZIGGY_PUBLIC_IP not found in .env - ICE candidates will be wrong." }

# PRIORITY (2026-07-11, ROOT-FIXED 2026-07-15): Task Scheduler starts tasks at BelowNormal, and on
# a hybrid CPU (13700K) Windows steers normal-or-below threads onto E-CORES — the emulator's hot
# thread then plateaus at a uniform ~25-40 ms/tick (F-Zero GX races; the ENTIRE Stuntman
# "audio skip / 60<->21fps oscillation" hunt of 2026-07-15, which survived every emulator-side
# config arm because it was never the emulator).
# ⚠ The original fix raised only OUR priority and assumed worker.exe inherits it. IT DOES NOT:
# Windows propagates a parent's priority class to children only for Idle/BelowNormal — a HIGH
# parent spawns NORMAL children. Every worker ran at Normal for four days while this comment
# claimed otherwise. The class must be set ON THE CHILD, after each spawn (see the loop below).
(Get-Process -Id $PID).PriorityClass = 'High'

# SCHEDULER SIZING (arcade perf program P3, 2026-09-05). Go sizes GOMAXPROCS ONCE, at process start,
# from GetProcessAffinityMask (runtime/os_windows.go getproccount). The affinity below used to be applied
# AFTER the worker had bound its port, so every worker ran 24 Ps (and 6 dedicated GC workers) on the 8
# logical CPUs the mask then confined it to. Two fixes together: the mask + class now ride the LAUNCH
# (`start /affinity /high`, below), so Go sees 8 CPUs from its first instruction, and GOMAXPROCS is
# pinned explicitly so a future mask change can never silently reintroduce the oversubscription.
# ⚠ Runtime-flow env vars are read by the worker at start: a plain `.stop` recycle re-spawns from THIS
# already-running script and does NOT re-read the file — a change here needs the full TASK restart.
$env:GOMAXPROCS = "8"

# GStreamer DLLs (nvcodec, opus, etc.) resolve from the UCRT64 bin dir — must lead PATH.
$env:Path = "$Ucrt64Bin;$env:Path"

# Flat knobs via CLOUD_GAME_* (pkg/config/loader.go prefix). The core list + encoder live in the config.
# zone "main": the Windows-native workers are now the ONLY pool (docker/WSL retired), so they must take
# every room, not just GL 3D cores. (Was "gl" back when the WSL pool served 2D/N64 and this pool only
# handled flycast/ppsspp; the merged worker-gl/config.yaml now carries the full tuned core list.)
$env:CLOUD_GAME_WORKER_NETWORK_ZONE               = $Zone
$env:CLOUD_GAME_WORKER_NETWORK_COORDINATORADDRESS = "localhost:8000"   # WSL coordinator via mirrored net
$env:CLOUD_GAME_WORKER_NETWORK_SECURE             = "false"
$env:CLOUD_GAME_WEBRTC_SINGLEPORT                 = "$SinglePort"        # router must UDP-forward this → Ziggy
$env:CLOUD_GAME_WEBRTC_ICEIPMAP                   = $IceIpMap
$env:CLOUD_GAME_LIBRARY_BASEPATH                  = $LibraryBasePath
$env:CLOUD_GAME_EMULATOR_STORAGE                  = "D:\ArcadeStorage\saves"
# GRACEFUL-STOP sentinel (paired with scripts/recycle-arcade-glworker.ps1 and the watchdog). The worker
# watches this file (pkg/os ExpectTermination) and, when it appears, shuts down CLEANLY — flushing the GS
# shader cache and tearing down GL/NVENC — instead of being force-killed, which dumps the shader cache
# (cold-cache = periodic in-game audio skips for the next player) and can strand a kernel-stuck teardown
# thread (the unkillable zombie that holds this ConfDir's DLL + cache locked). One file per ConfDir.
$StopFile = Join-Path $ConfDir ".stop"
$env:CLOUD_GAME_STOP_FILE                         = $StopFile

# RetroAchievements MIRROR (arcade RA feature): the worker POSTs achievement/leaderboard events to the
# site's secret-gated internal callbacks so the friends board + profile get a durable copy. rcheevos
# itself submits to retroachievements.org under the player's OWN account — this is only OUR copy.
# The mirror URL is the public site (not secret); the SECRET is the shared ArcadeTokenSecret, read from
# a LOCAL file so it is NEVER committed to the repo (only the path is here). Absent file => mirror off
# (RA + the in-room toast still work). CLOUD_GAME_* env overrides the yaml (verified via loader).
$env:CLOUD_GAME_RETROACHIEVEMENTS_MIRRORURL       = "https://theater.carpouzis.com"
$raSecretFile = "D:\ArcadeStorage\secrets\arcade-token-secret.txt"
if (Test-Path $raSecretFile) {
    $env:CLOUD_GAME_RETROACHIEVEMENTS_SECRET      = (Get-Content -Raw $raSecretFile).Trim()
}
# The SITE service RA account (the scoring engine — spectator mode, never earns). Two lines: username,
# then its connect token. Minted by scripts/mint-arcade-ra-site-account.ps1; never in the repo. Absent =
# RA engine off (no achievements/scores/times recorded).
$raSiteFile = "D:\ArcadeStorage\secrets\arcade-ra-site-account.txt"
if (Test-Path $raSiteFile) {
    $raSiteLines = @(Get-Content $raSiteFile | Where-Object { $_.Trim() -ne "" })
    if ($raSiteLines.Count -ge 2) {
        $env:CLOUD_GAME_RETROACHIEVEMENTS_SITEUSER  = $raSiteLines[0].Trim()
        $env:CLOUD_GAME_RETROACHIEVEMENTS_SITETOKEN = $raSiteLines[1].Trim()
    }
}

New-Item -ItemType Directory -Force (Split-Path $LogFile) | Out-Null

# Run FROM the ConfDir: the worker resolves emulator.localPath ("./libretro" → the system/BIOS junction
# to D:\ArcadeStorage\bios) and its core cache ("./assets/cores") relative to cwd. Config also loads
# from "." (LoadConfig searches cwd), so --w-conf is belt-and-braces.
Set-Location $ConfDir

# UTF-8 loop markers so they share the worker's encoding (piped via cmd below) — one grep-friendly file.
function Write-LogLine([string]$msg) {
    [System.IO.File]::AppendAllText($LogFile, ("{0}  [runner] {1}`r`n" -f (Get-Date -Format o), $msg), [System.Text.Encoding]::UTF8)
}

# Restart loop (the WSL worker's `restart: unless-stopped` analogue). worker.exe --w-conf = a DIRECTORY.
#
# CRITICAL — capture the worker's native STDERR. PowerShell 5.1's `*>>`/`2>&1` on a NATIVE exe mangles
# and frequently DROPS stderr (it wraps each line as an ErrorRecord), and a Go panic / C access-violation
# dump goes to stderr — so crashes were landing NOWHERE and were undiagnosable. `cmd /c "... >> file 2>&1"`
# does a real OS-level stream merge, so panics AND C faults are written verbatim to the log. The exit code
# then disambiguates: 2 = Go panic, 0xC0000005/-1073741819 = access violation, 0 = clean exit.
while ($true) {
    # Clear a stale graceful-stop sentinel from a prior recycle, or the fresh worker would see it and
    # exit immediately in a tight respawn loop. (The recycler removes it after the worker exits, but a
    # crash mid-recycle could leave it behind.)
    if (Test-Path $StopFile) { Remove-Item $StopFile -Force -ErrorAction SilentlyContinue }
    # Rotate so a fresh crash isn't buried under hours of prior output.
    if ((Test-Path $LogFile) -and ((Get-Item $LogFile).Length -gt 25MB)) {
        Move-Item $LogFile "$LogFile.1" -Force -ErrorAction SilentlyContinue
    }
    Write-LogLine "starting glworker (zone=$($env:CLOUD_GAME_WORKER_NETWORK_ZONE) port=$SinglePort ice=$IceIpMap exe=$WorkerExe)"
    # Raise the CHILD's priority class once it binds its mux port. This must target the worker
    # process itself — a raised class does NOT inherit (see the PRIORITY note above). Runs as a
    # background job beside the blocking spawn below; identifies OUR worker by its UDP port so
    # concurrent workers (worker-gl-2, capture) are never touched.
    Get-Job -State Completed -ErrorAction SilentlyContinue | Remove-Job -Force -ErrorAction SilentlyContinue
    Start-Job -ScriptBlock {
        param($port, $log)
        for ($i = 0; $i -lt 120; $i++) {
            Start-Sleep -Milliseconds 500
            $ep = Get-NetUDPEndpoint -LocalPort $port -ErrorAction SilentlyContinue | Select-Object -First 1
            if ($ep) {
                $p = Get-Process -Id $ep.OwningProcess -ErrorAction SilentlyContinue
                if ($p -and $p.Name -eq 'worker') {
                    # High class biases the Win11 scheduler toward P-cores; the explicit affinity mask
                    # FORBIDS E-cores outright (13700K: 8P x 2SMT = logical 0-15). Measured 2026-07-15:
                    # at Normal, the emulator's hot thread oscillated P<->E core (60 <-> 21-25 fps, the
                    # entire Stuntman audio-skip hunt); High alone still allowed periodic E-core dips.
                    # 0x5555 (not 0xFFFF): one logical CPU per PHYSICAL P-core — with both SMT siblings
                    # allowed, the EE and GS hot threads sometimes landed on the SAME physical core and
                    # robbed each other ~30% (meanTick 12ms <-> 20ms oscillation on identical content).
                    # 8 physical P-cores >> the worker's 4-5 hot threads; nothing is lost.
                    # Since perf program P3 the launch line sets both at process creation; this is the
                    # belt to that braces. A worker found WITHOUT them here means `start` misbehaved
                    # under Task Scheduler — worth knowing, so it is logged rather than silently fixed.
                    try {
                        $had = ($p.PriorityClass -eq 'High') -and ([int64]$p.ProcessorAffinity -eq 0x5555)
                        if (-not $had) {
                            $p.PriorityClass = 'High'; $p.ProcessorAffinity = [IntPtr]0x5555
                            # Its own file: cmd holds the main log open for the worker's stdout, and an append to
                            # that file from here is a sharing violation the catch below would swallow.
                            [System.IO.File]::AppendAllText(($log + '.poller'), ("{0}  [runner] WARN: worker pid {1} was not launched at High/0x5555 (class={2} mask=0x{3:X}); fixed by the poller`r`n" -f (Get-Date -Format o), $p.Id, $p.PriorityClass, [int64]$p.ProcessorAffinity), [System.Text.Encoding]::UTF8)
                        }
                    } catch {}
                    break
                }
            }
        }
    } -ArgumentList $SinglePort, $LogFile | Out-Null
    # DEBUG AUDIO CAPTURE (opt-in, marker-file gated). Drop a file named `.audiodump` in this worker's
    # ConfDir and the next spawn writes ~10s of the core's RAW PCM (S16LE stereo, pre-resample/Opus/
    # WebRTC) to <ConfDir>\audiodump\. Delete the marker to turn it off — no script edit needed, and
    # nothing is captured unless the marker exists. See nanoarch/audiodump.go for why this exists: the
    # pacer can prove the audio RATE is correct while saying nothing about whether individual buffers
    # contain the discontinuities a crackle is made of.
    $dumpMarker = Join-Path $ConfDir ".audiodump"
    if (Test-Path $dumpMarker) {
        $env:CLOUD_GAME_AUDIO_DUMP_DIR = (Join-Path $ConfDir "audiodump")
        Write-LogLine "AUDIO DUMP ARMED -> $env:CLOUD_GAME_AUDIO_DUMP_DIR (marker $dumpMarker present)"
    } else {
        Remove-Item Env:\CLOUD_GAME_AUDIO_DUMP_DIR -ErrorAction SilentlyContinue
    }
    # `start "" /affinity 5555 /high /b /wait` creates the process WITH its mask and class (no window, same
    # console, so the `>> log 2>&1` merge is inherited exactly as before; proven 2026-09-05: child saw
    # mask=0x5555 class=High, stderr landed in the file). `& exit /b` hands the child's exit code back out
    # of cmd — without it `cmd /c start /wait` reports 0 for every crash and the exitcode line below lies.
    # The port-bind poller above is now only a VERIFY + fallback (it re-asserts the same values; no-op
    # when start did its job). Affinity is hex without 0x for `start`.
    & cmd.exe /c "start `"`" /affinity 5555 /high /b /wait `"$WorkerExe`" --w-conf `"$ConfDir`" >> `"$LogFile`" 2>&1 & exit /b"
    $code = $LASTEXITCODE
    Write-LogLine ("glworker EXITED exitcode={0} (0x{1:X8}) - restarting in 4s" -f $code, ($code -band 0xFFFFFFFF))
    Start-Sleep -Seconds 4
}
