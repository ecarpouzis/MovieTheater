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
    Multiple workers share one ConfDir by design (same tuned core list, same BIOS junction, same
    core cache) — the retired WSL pool shared its `cores` volume across 3 workers the same way.
    Saves are keyed by room id, so a shared storage dir is safe. The one caveat: when a NEW core is
    added, two workers may race to download it into ./assets/cores on first boot; stagger their
    starts (or pre-warm the core once) if you ever add one.
#>
param(
    [string]$WorkerExe  = "D:\Arcade\build\cloud-game-gl\bin\worker.exe",
    [string]$ConfDir    = "D:\ArcadeStorage\worker-gl",
    [string]$Ucrt64Bin  = "D:\msys64\ucrt64\bin",
    [string]$IceIpMap   = "",   # resolved from docker/arcade/.env ZIGGY_PUBLIC_IP below; never hardcode the IP here
    [int]   $SinglePort = 8446,
    [string]$LogFile    = "D:\ArcadeStorage\logs\glworker.log"
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

# GStreamer DLLs (nvcodec, opus, etc.) resolve from the UCRT64 bin dir — must lead PATH.
$env:Path = "$Ucrt64Bin;$env:Path"

# Flat knobs via CLOUD_GAME_* (pkg/config/loader.go prefix). The core list + encoder live in the config.
# zone "main": the Windows-native workers are now the ONLY pool (docker/WSL retired), so they must take
# every room, not just GL 3D cores. (Was "gl" back when the WSL pool served 2D/N64 and this pool only
# handled flycast/ppsspp; the merged worker-gl/config.yaml now carries the full tuned core list.)
$env:CLOUD_GAME_WORKER_NETWORK_ZONE               = "main"
$env:CLOUD_GAME_WORKER_NETWORK_COORDINATORADDRESS = "localhost:8000"   # WSL coordinator via mirrored net
$env:CLOUD_GAME_WORKER_NETWORK_SECURE             = "false"
$env:CLOUD_GAME_WEBRTC_SINGLEPORT                 = "$SinglePort"        # router must UDP-forward this → Ziggy
$env:CLOUD_GAME_WEBRTC_ICEIPMAP                   = $IceIpMap
$env:CLOUD_GAME_LIBRARY_BASEPATH                  = "D:\ArcadeStorage\roms"
$env:CLOUD_GAME_EMULATOR_STORAGE                  = "D:\ArcadeStorage\saves"

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
    # Rotate so a fresh crash isn't buried under hours of prior output.
    if ((Test-Path $LogFile) -and ((Get-Item $LogFile).Length -gt 25MB)) {
        Move-Item $LogFile "$LogFile.1" -Force -ErrorAction SilentlyContinue
    }
    Write-LogLine "starting glworker (zone=$($env:CLOUD_GAME_WORKER_NETWORK_ZONE) port=$SinglePort ice=$IceIpMap exe=$WorkerExe)"
    & cmd.exe /c "`"$WorkerExe`" --w-conf `"$ConfDir`" >> `"$LogFile`" 2>&1"
    $code = $LASTEXITCODE
    Write-LogLine ("glworker EXITED exitcode={0} (0x{1:X8}) - restarting in 4s" -f $code, ($code -band 0xFFFFFFFF))
    Start-Sleep -Seconds 4
}
