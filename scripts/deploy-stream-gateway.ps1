#requires -Version 7
<#
.SYNOPSIS
  Hand-deploy MovieTheater.StreamGateway on Ziggy, with a write-once rollback point and probes that
  prove the new binary actually took.

.DESCRIPTION
  `git push` rebuilds the site pods and does NOTHING to this gateway. Every new gateway route stays
  dead until the binary is republished by hand — that is what stalled the music vertical's launch
  (the live exe predated the MusicFile route and every minted URL 404'd), and it is why the cast
  receiver CORS fix needed this script on 2026-08-24.

  The binary carries ALL THREE data planes — movies (Jellyfin /Videos proxy), music (MusicFile /
  MusicTranscode / the universal AAC cache) and photos (PhotoStreamRoutes). A hand redeploy ships
  whatever every one of those verticals has committed since the last one, so check
  `git log -- src/MovieTheater.StreamGateway/ src/MovieTheater.Core/` against the live file dates
  before running. A change to the capability-token format in Core is the one that would break the
  world, because the site pods mint tokens this binary has to validate.

  ── THE LAYOUT TRAP (why this does not "copy ONLY the exe") ─────────────────────────────────────
  The install used to be a single-file self-contained publish (~97 MB exe), and the old runbook said
  to swap just that exe. It stopped being true on 2026-08-13: the live install is a FRAMEWORK-
  DEPENDENT MULTI-FILE publish. MovieTheater.StreamGateway.exe is a 152 KB apphost shim and the code
  lives in MovieTheater.StreamGateway.dll. Copying only the exe today ships NOTHING while looking
  like a clean deploy — service restarts, healthz green, the new route still dead. That is the worst
  failure shape available, so this copies the publish SET (deps.json has to stay in step with the
  DLLs beside it) and verifies behaviour rather than trusting the restart.

  The ~97 MB MovieTheater.StreamGateway.exe.bak-* files still sitting in the app dir are relics of
  that older shape. They are NOT rollback targets for the current layout. Roll back with -Rollback,
  which restores a directory snapshot this script made.

.PARAMETER Rollback
  Path to an app.bak-* directory to restore instead of deploying.

.EXAMPLE
  # From an ELEVATED pwsh (service control needs admin; PS 5.1 will not run this file at all):
  .\scripts\deploy-stream-gateway.ps1

.EXAMPLE
  .\scripts\deploy-stream-gateway.ps1 -Rollback C:\StreamGateway\app.bak-20260824-cast-cors
#>
[CmdletBinding()]
param(
  [string]$AppDir      = 'C:\StreamGateway\app',
  [string]$ServiceName = 'StreamGateway',
  [string]$ProbeUrl    = 'http://localhost:2203',
  [string]$Label       = (Get-Date -Format 'yyyyMMdd-HHmmss'),
  [string]$PublishDir,
  [switch]$SkipPublish,
  [string]$Rollback
)

$ErrorActionPreference = 'Stop'

# Elevation FIRST. Without this the script gets as far as taking a backup and then dies at
# Stop-Service, which reads like a failed deploy when in fact nothing was touched.
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
  ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
  throw "Not elevated. Stopping an NSSM service needs admin — relaunch from an elevated pwsh:`n" +
        "  Start-Process pwsh -Verb RunAs -ArgumentList '-NoExit','-File','$PSCommandPath'"
}

function Restart-Gateway {
  param([scriptblock]$Swap)
  Stop-Service $ServiceName
  (Get-Service $ServiceName).WaitForStatus('Stopped', '00:00:30')
  Write-Host 'service stopped'
  & $Swap
  Start-Service $ServiceName
  (Get-Service $ServiceName).WaitForStatus('Running', '00:00:30')
  Write-Host "service status: $((Get-Service $ServiceName).Status)"
}

# ── rollback ────────────────────────────────────────────────────────────────────────────────────
if ($Rollback) {
  if (-not (Test-Path (Join-Path $Rollback 'MovieTheater.StreamGateway.dll'))) {
    throw "Not a usable snapshot (no app dll): $Rollback"
  }
  Restart-Gateway { Get-ChildItem $Rollback -File | ForEach-Object { Copy-Item $_.FullName -Destination $AppDir -Force } }
  Write-Host "rolled back from $Rollback"
  return
}

# ── publish ─────────────────────────────────────────────────────────────────────────────────────
$repo = Split-Path $PSScriptRoot -Parent
if (-not $PublishDir) { $PublishDir = Join-Path $env:TEMP "streamgateway-publish-$Label" }
if (-not $SkipPublish) {
  $csproj = Join-Path $repo 'src\MovieTheater.StreamGateway\MovieTheater.StreamGateway.csproj'
  Write-Host "publishing $csproj -> $PublishDir"
  # Framework-dependent on purpose: it is what is installed, and .NET 8 is present on this host.
  # Switching back to --self-contained/-p:PublishSingleFile would change the deployment shape and
  # strand the sibling DLLs already in the app dir.
  dotnet publish $csproj -c Release -o $PublishDir --nologo | Out-Null
  if ($LASTEXITCODE -ne 0) { throw 'publish failed' }
}
if (-not (Test-Path (Join-Path $PublishDir 'MovieTheater.StreamGateway.dll'))) {
  throw "Publish looks wrong: no app dll in $PublishDir"
}

# ── rollback point ──────────────────────────────────────────────────────────────────────────────
# WRITE-ONCE. A re-run after a partial or successful deploy must never copy the ALREADY-SWAPPED
# binaries over the only good snapshot — that is exactly how "restore the newest .bak-*" became the
# wrong advice last time.
$backup = Join-Path (Split-Path $AppDir -Parent) "app.bak-$Label"
if ((Test-Path $backup) -and @(Get-ChildItem $backup -File -ErrorAction SilentlyContinue).Count -gt 0) {
  Write-Host "backup already exists, keeping it untouched -> $backup"
} else {
  New-Item -ItemType Directory -Path $backup -Force | Out-Null
  # Skip the ~97 MB single-file relics; they are dead weight and not valid restore targets.
  Get-ChildItem $AppDir -File | Where-Object { $_.Name -notlike '*.bak-*' } |
    ForEach-Object { Copy-Item $_.FullName -Destination $backup -Force }
  Write-Host "backed up $(@(Get-ChildItem $backup -File).Count) files -> $backup"
}

# ── swap ────────────────────────────────────────────────────────────────────────────────────────
Restart-Gateway {
  $copied = @()
  # NEVER config: appsettings.Production.json holds the real secrets (JellyfinApiKey,
  # StreamTokenSecret, MusicRootDir, FfmpegPath) and exists only on this host — it is not in git and
  # the publish output's placeholder version would wipe it.
  Get-ChildItem $PublishDir -File | Where-Object { $_.Name -notlike 'appsettings*.json' } | ForEach-Object {
    Copy-Item $_.FullName -Destination $AppDir -Force
    $copied += $_.Name
  }
  Write-Host "copied: $($copied -join ', ')"
}

# ── verify ──────────────────────────────────────────────────────────────────────────────────────
# Behaviour, not a green service light. -SkipHttpErrorCheck is PS7-only: under 5.1 these all throw
# identically and fake a total outage, which is why this file refuses to run there at all.
Start-Sleep -Seconds 3
$ok = $true

$health = (Invoke-WebRequest "$ProbeUrl/healthz" -TimeoutSec 10 -SkipHttpErrorCheck).StatusCode
Write-Host "healthz: $health"
if ($health -ne 200) { $ok = $false; Write-Warning 'GATEWAY UNHEALTHY' }

# Route-registration probe (no token needed): 405 = the route exists on this binary, 404 = old binary.
$music = (Invoke-WebRequest "$ProbeUrl/s/bogus/MusicFile" -Method POST -TimeoutSec 10 -SkipHttpErrorCheck).StatusCode
Write-Host "POST /s/bogus/MusicFile: $music  (405 = route registered, 404 = OLD BINARY still live)"
if ($music -eq 404) { $ok = $false; Write-Warning 'The swap did not take — still serving the old binary.' }

# CORS allow-list: an allow-listed origin is echoed, anything else falls back to the site origin.
# Load-bearing for BOTH the browser (crossOrigin=anonymous on every player) and cast receivers,
# which fetch playlist/segments/VTT themselves from their own origin.
function Get-Acao([string]$origin) {
  $r = Invoke-WebRequest "$ProbeUrl/healthz" -Headers @{ Origin = $origin } -TimeoutSec 10 -SkipHttpErrorCheck
  , @($r.Headers['Access-Control-Allow-Origin'])
}
$siteAcao = (Get-Acao 'https://not-allowed.example')   # unlisted → whatever the fallback site origin is
$castAcao = Get-Acao 'https://www.gstatic.com'         # the Default Media Receiver
Write-Host "ACAO fallback (unlisted origin): $($siteAcao -join '|')"
Write-Host "ACAO for the cast receiver:      $($castAcao -join '|')"

if ($castAcao.Count -ne 1 -or $siteAcao.Count -ne 1) {
  $ok = $false; Write-Warning 'Expected exactly ONE ACAO header — duplicates break playback outright.'
}
if ($castAcao -join '' -ne 'https://www.gstatic.com') {
  $ok = $false; Write-Warning 'Cast receiver origin is NOT allow-listed — casts will fail on the TV.'
}
if ($siteAcao -join '' -eq 'https://not-allowed.example') {
  $ok = $false; Write-Warning 'The allow-list is echoing ANY origin — roll back.'
}

if ($ok) {
  Write-Host 'DEPLOY VERIFIED.' -ForegroundColor Green
} else {
  Write-Warning "Roll back with:`n  $PSCommandPath -Rollback $backup"
}
