<#
.SYNOPSIS
    Runs the CloudRetro coordinator natively on Windows, in a restart loop.
    Sibling of run-arcade-glworker.ps1; registered by register-arcade-coordinator-task.ps1.

.DESCRIPTION
    The coordinator does signaling + relay only — no emulation, no GStreamer, no GPU (it builds with
    CGO_ENABLED=0). It was the LAST thing left in the WSL docker stack, and keeping it there cost us
    twice:

      1. The distro idles out and takes the arcade with it ("arcade randomly died").
      2. Image drift. The container image was built 2026-07-06, so it predated patch 0018 — its
         StartGameRequest struct had no video_bitrate/audio_fec fields, and it SILENTLY DROPPED them
         when relaying GAME_START to the worker. Every room ran at the config bitrate; the per-room
         and per-system quality settings never reached the encoder, and nothing anywhere errored.

    Running it natively removes both. The WSL stack is then needed by nothing.

    ⚠ CWD must be the ConfDir: pkg/coordinator/coordinator.go does
      template.Must(template.ParseFiles("./web/index.html")) and PANICS if it's missing. The ConfDir
      therefore holds coordinator.exe, config.yaml AND a copy of the source tree's web/ directory.
#>
param(
    [string]$ConfDir = "D:\ArcadeStorage\coordinator",
    [string]$LogFile = "D:\ArcadeStorage\logs\coordinator.log"
)

$exe = Join-Path $ConfDir "coordinator.exe"
if (-not (Test-Path $exe))                             { throw "coordinator.exe not found at $exe" }
if (-not (Test-Path (Join-Path $ConfDir "web\index.html"))) { throw "web\index.html missing in $ConfDir - the coordinator will panic at boot" }

# Origin check for the browser's signaling WS. Config carries it too; env wins and documents intent.
$env:CLOUD_GAME_COORDINATOR_ORIGIN_USERWS = "https://theater.carpouzis.com"

Set-Location $ConfDir

while ($true) {
    # Rotate at 25MB, same as the worker runner.
    if ((Test-Path $LogFile) -and ((Get-Item $LogFile).Length -gt 25MB)) {
        Move-Item $LogFile "$LogFile.1" -Force -ErrorAction SilentlyContinue
    }
    "=== coordinator start $(Get-Date -Format o) ===" | Out-File -FilePath $LogFile -Append -Encoding utf8

    # Redirect through cmd.exe rather than Start-Process -RedirectStandardOutput: the latter buffers to
    # a temp file that is only flushed when the process EXITS, so a running coordinator would appear to
    # log nothing (and a hang would be invisible). `>>` from cmd streams and appends, and merging stderr
    # there avoids PowerShell 5.1's NativeCommandError wrapping of a native exe's stderr.
    cmd.exe /c "`"$exe`" --c-conf `"$ConfDir`" >> `"$LogFile`" 2>&1"
    $code = $LASTEXITCODE

    "=== coordinator exited (code $code) $(Get-Date -Format o); restarting in 3s ===" |
        Out-File -FilePath $LogFile -Append -Encoding utf8
    Start-Sleep -Seconds 3
}
