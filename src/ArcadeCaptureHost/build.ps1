<#
.SYNOPSIS
    Publish ArcadeCaptureHost as a self-contained single-file win-x64 exe and (optionally) deploy it
    beside the capture worker.

.DESCRIPTION
    ArcadeCaptureHost is the WGC window-capture helper for the arcade capture lane (captureMode:
    window). It is Windows-only and Ziggy-only — NOT in MovieTheater.sln and NOT built by CI — so it is
    published here and deployed by file copy to the capture worker's bin dir. See
    docs/arcade-capture-worker-plan.md ("WGC WINDOW CAPTURE MODE").

.PARAMETER Deploy   Also copy the published exe to $DeployDir (the live capture-worker bin).
.PARAMETER DeployDir  Where -Deploy copies the exe (default the capture worker's bin on Ziggy).
#>
param(
    [switch]$Deploy,
    [string]$DeployDir = "D:\ArcadeStorage\worker-capture\bin"
)
$ErrorActionPreference = "Stop"
$proj = Join-Path $PSScriptRoot "ArcadeCaptureHost.csproj"
$out  = Join-Path $PSScriptRoot "publish"

Write-Host "publishing $proj -> $out (self-contained single-file win-x64) ..."
dotnet publish $proj -c Release -o $out
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }
$exe = Join-Path $out "ArcadeCaptureHost.exe"
if (-not (Test-Path $exe)) { throw "published exe not found: $exe" }
Write-Host ("published: {0} ({1:N0} bytes)" -f $exe, (Get-Item $exe).Length)

if ($Deploy) {
    if (-not (Test-Path $DeployDir)) { throw "deploy dir missing: $DeployDir" }
    Copy-Item -LiteralPath $exe -Destination (Join-Path $DeployDir "ArcadeCaptureHost.exe") -Force
    Write-Host "deployed -> $DeployDir\ArcadeCaptureHost.exe"
    Write-Host "NOTE: recycle the capture worker (task 3) for a running room to pick up a new helper on its NEXT room."
}
