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
#>
param(
    [string]$WorkerExe  = "D:\Arcade\build\cloud-game\bin\worker.exe",
    [string]$ConfDir    = "D:\ArcadeStorage\worker-gl",
    [string]$Ucrt64Bin  = "C:\msys64\ucrt64\bin",
    [string]$IceIpMap   = "98.15.249.217",
    [int]   $SinglePort = 8446,
    [string]$LogFile    = "D:\ArcadeStorage\logs\glworker.log"
)

# GStreamer DLLs (nvcodec, opus, etc.) resolve from the UCRT64 bin dir — must lead PATH.
$env:Path = "$Ucrt64Bin;$env:Path"

# Flat knobs via CLOUD_GAME_* (pkg/config/loader.go prefix). The core list + encoder live in the config.
$env:CLOUD_GAME_WORKER_NETWORK_ZONE               = "gl"
$env:CLOUD_GAME_WORKER_NETWORK_COORDINATORADDRESS = "localhost:8000"   # WSL coordinator via mirrored net
$env:CLOUD_GAME_WORKER_NETWORK_SECURE             = "false"
$env:CLOUD_GAME_WEBRTC_SINGLEPORT                 = "$SinglePort"        # router must UDP-forward this → Ziggy
$env:CLOUD_GAME_WEBRTC_ICEIPMAP                   = $IceIpMap
$env:CLOUD_GAME_LIBRARY_BASEPATH                  = "D:\ArcadeStorage\roms"
$env:CLOUD_GAME_EMULATOR_STORAGE                  = "D:\ArcadeStorage\saves"

New-Item -ItemType Directory -Force (Split-Path $LogFile) | Out-Null

# Restart loop (the WSL worker's `restart: unless-stopped` analogue). worker.exe --w-conf = a DIRECTORY.
while ($true) {
    "$(Get-Date -Format o)  starting glworker (zone=gl port=$SinglePort ice=$IceIpMap)" | Add-Content $LogFile
    try {
        & $WorkerExe --w-conf $ConfDir *>> $LogFile
    } catch {
        "$(Get-Date -Format o)  glworker threw: $($_.Exception.Message)" | Add-Content $LogFile
    }
    "$(Get-Date -Format o)  glworker exited — restarting in 4s" | Add-Content $LogFile
    Start-Sleep -Seconds 4
}
