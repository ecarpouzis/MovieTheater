<#
.SYNOPSIS
    Runs the ArcadeGateway (signaling proxy + JIT ROM cache + durable save store) in a restart loop.

.DESCRIPTION
    The gateway is the media-host side of the arcade: it validates capability tokens, confines joiners
    to their room, JIT-extracts ROMs, and (docs/arcade-saves-plan.md) seeds/harvests user-scoped saves.
    It is a plain ASP.NET service (no GPU), but it had NO keep-alive — a manual start that didn't survive
    reboot. This loop runs the built exe with the Production environment (so it loads the on-disk
    appsettings.Production.json holding the token secret + RomCache/SaveStore config), and restarts it if
    it exits. scripts/register-arcade-gateway-task.ps1 registers the logon task that keeps this running.

.PARAMETER Exe      Path to the built MovieTheater.ArcadeGateway.exe.
.PARAMETER LogFile  Append log (real OS stream merge via cmd, same as the GL worker runner).
#>
param(
    [string]$Exe     = "F:\Work\MovieTheater\src\MovieTheater.ArcadeGateway\bin\Debug\net8.0\MovieTheater.ArcadeGateway.exe",
    [string]$LogFile = "D:\ArcadeStorage\logs\gateway.log"
)

$env:ASPNETCORE_ENVIRONMENT = "Production"

New-Item -ItemType Directory -Force (Split-Path $LogFile) | Out-Null
# Run from the exe's project dir so it resolves appsettings.json / appsettings.Production.json.
Set-Location (Split-Path $Exe | Split-Path | Split-Path | Split-Path)

function Write-LogLine([string]$msg) {
    [System.IO.File]::AppendAllText($LogFile, ("{0}  [runner] {1}`r`n" -f (Get-Date -Format o), $msg), [System.Text.Encoding]::UTF8)
}

while ($true) {
    if ((Test-Path $LogFile) -and ((Get-Item $LogFile).Length -gt 25MB)) {
        Move-Item $LogFile "$LogFile.1" -Force -ErrorAction SilentlyContinue
    }
    Write-LogLine "starting ArcadeGateway ($Exe)"
    & cmd.exe /c "`"$Exe`" >> `"$LogFile`" 2>&1"
    $code = $LASTEXITCODE
    Write-LogLine ("ArcadeGateway EXITED exitcode={0} - restarting in 4s" -f $code)
    Start-Sleep -Seconds 4
}
