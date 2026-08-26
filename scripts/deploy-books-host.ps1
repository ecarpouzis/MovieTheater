#requires -Version 7
<#
.SYNOPSIS
  Hand-deploy MovieTheater.BooksHost on the media host (merge program R5), with a write-once rollback point
  and probes that prove the new binary actually took — the deploy-stream-gateway.ps1 shape.

.DESCRIPTION
  `git push` rebuilds the site pods and does NOTHING to this host. The site's proxied /API/Books route stays
  pointed at whatever binary is live here, so every host-side change ships only through this script.

  Framework-dependent multi-file publish (the ASP.NET Core 10 runtime is on this host): the exe is an apphost
  shim and the code is MovieTheater.BooksHost.dll + its sibling DLLs, so the PUBLISH SET is copied, never just
  the exe. appsettings*.json are never copied — appsettings.Production.json holds the secrets and paths and
  exists only here.

.PARAMETER Rollback
  Path to an app.bak-* directory to restore instead of deploying.
.PARAMETER SkipRestart
  First-time install: copy the binaries without touching a service that does not exist yet.

.EXAMPLE
  .\scripts\deploy-books-host.ps1            # from an ELEVATED pwsh
.EXAMPLE
  .\scripts\deploy-books-host.ps1 -Rollback C:\BooksHost\app.bak-20260826-101500
#>
[CmdletBinding()]
param(
  [string]$AppDir      = 'C:\BooksHost\app',
  [string]$ServiceName = 'BooksHost',
  [string]$ProbeUrl    = 'http://localhost:2204',
  [string]$SiteOrigin,   # default: Books:SiteOrigin from the host's appsettings.Production.json
  [string]$Label       = (Get-Date -Format 'yyyyMMdd-HHmmss'),
  [string]$PublishDir,
  [switch]$SkipPublish,
  [switch]$SkipRestart,
  [string]$Rollback
)
$ErrorActionPreference = 'Stop'

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
  ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin -and -not $SkipRestart) {
  throw "Not elevated. Stopping an NSSM service needs admin — relaunch: Start-Process pwsh -Verb RunAs -ArgumentList '-NoExit','-File','$PSCommandPath'"
}

# Copy a publish tree INCLUDING subfolders. runtimes\win-x64\native\ carries e_sqlite3.dll and pdfium.dll;
# a flat file copy (the StreamGateway script this was cloned from has no native dependencies) left the host
# without SQLite at all on 2026-08-25 - every database touch threw DllNotFoundException while /ping still 200ed.
function Copy-Tree {
  param([string]$From, [string]$To, [switch]$SkipAppSettings)
  Get-ChildItem $From -Recurse -File | Where-Object { -not ($SkipAppSettings -and $_.Name -like 'appsettings*.json') } | ForEach-Object {
    $rel = $_.FullName.Substring($From.TrimEnd('\').Length + 1)
    $dest = Join-Path $To $rel
    New-Item -ItemType Directory -Force (Split-Path $dest -Parent) | Out-Null
    Copy-Item $_.FullName -Destination $dest -Force
    $_.FullName
  }
}
function Restart-Host {
  param([scriptblock]$Swap)
  if ($SkipRestart) { & $Swap; return }
  Stop-Service $ServiceName
  (Get-Service $ServiceName).WaitForStatus('Stopped', '00:00:30')
  Write-Host 'service stopped'
  & $Swap
  Start-Service $ServiceName
  (Get-Service $ServiceName).WaitForStatus('Running', '00:00:30')
  Write-Host "service status: $((Get-Service $ServiceName).Status)"
}

if ($Rollback) {
  if (-not (Test-Path (Join-Path $Rollback 'MovieTheater.BooksHost.dll'))) { throw "Not a usable snapshot (no host dll): $Rollback" }
  Restart-Host { Copy-Tree $Rollback $AppDir }
  Write-Host "rolled back from $Rollback"
  return
}

# ── publish ──
$repo = Split-Path $PSScriptRoot -Parent
if (-not $PublishDir) { $PublishDir = Join-Path $env:TEMP "bookshost-publish-$Label" }
if (-not $SkipPublish) {
  $csproj = Join-Path $repo 'src\MovieTheater.BooksHost\MovieTheater.BooksHost.csproj'
  $runtimes = (& dotnet --list-runtimes) -join "`n"
  if ($runtimes -notmatch 'Microsoft\.AspNetCore\.App 10\.') { throw 'Microsoft.AspNetCore.App 10.x runtime is not installed on this host.' }
  Write-Host "publishing $csproj -> $PublishDir"
  dotnet publish $csproj -c Release -o $PublishDir --nologo | Out-Null
  if ($LASTEXITCODE -ne 0) { throw 'publish failed' }
}
if (-not (Test-Path (Join-Path $PublishDir 'MovieTheater.BooksHost.dll'))) { throw "Publish looks wrong: no host dll in $PublishDir" }

# ── write-once rollback point ──
New-Item -ItemType Directory -Force $AppDir | Out-Null
$backup = Join-Path (Split-Path $AppDir -Parent) "app.bak-$Label"
if ((Test-Path $backup) -and @(Get-ChildItem $backup -Recurse -File -ErrorAction SilentlyContinue).Count -gt 0) {
  Write-Host "backup already exists, keeping it untouched -> $backup"
} elseif (@(Get-ChildItem $AppDir -Recurse -File -ErrorAction SilentlyContinue).Count -gt 0) {
  New-Item -ItemType Directory -Path $backup -Force | Out-Null
  Copy-Tree $AppDir $backup
  Write-Host "backed up $(@(Get-ChildItem $backup -Recurse -File).Count) files -> $backup"
}

# ── swap (never config) ──
Restart-Host {
  $copied = @(Copy-Tree $PublishDir $AppDir -SkipAppSettings)
  Write-Host "copied $($copied.Count) files"
}
if ($SkipRestart) { Write-Host 'binaries in place; install the service next (install-books-host-service.ps1)'; return }

# ── verify by BEHAVIOUR ──
if (-not $SiteOrigin) {
  $prod = Join-Path $AppDir 'appsettings.Production.json'
  if (Test-Path $prod) { $SiteOrigin = (Get-Content $prod -Raw | ConvertFrom-Json).Books.SiteOrigin }
  if (-not $SiteOrigin) { throw 'Pass -SiteOrigin or set Books:SiteOrigin in appsettings.Production.json (the ACAO probe needs it).' }
}
Start-Sleep -Seconds 3
$ok = $true
$health = (Invoke-WebRequest "$ProbeUrl/healthz" -TimeoutSec 10 -SkipHttpErrorCheck).StatusCode
Write-Host "healthz: $health"
if ($health -ne 200) { $ok = $false; Write-Warning 'HOST UNHEALTHY' }

# route-registration probe: 401 = the identity-gated route exists on this binary; 404 = old binary
$ping = (Invoke-WebRequest "$ProbeUrl/ping" -TimeoutSec 10 -SkipHttpErrorCheck).StatusCode
Write-Host "GET /ping without identity: $ping  (401 = route registered, 404 = OLD BINARY still live)"
if ($ping -ne 401) { $ok = $false; Write-Warning 'The swap did not take, or the identity gate is missing.' }

$thumb = (Invoke-WebRequest "$ProbeUrl/m/bogus/thumbs/1.webp" -TimeoutSec 10 -SkipHttpErrorCheck).StatusCode
Write-Host "GET /m/bogus/thumbs/1.webp: $thumb  (403 = media plane refusing a bad token)"
if ($thumb -ne 403) { $ok = $false; Write-Warning 'Media plane is not refusing a bogus token.' }

function Get-Acao([string]$origin) {
  $r = Invoke-WebRequest "$ProbeUrl/healthz" -Headers @{ Origin = $origin } -TimeoutSec 10 -SkipHttpErrorCheck
  , @($r.Headers['Access-Control-Allow-Origin'])
}
$siteAcao = Get-Acao $SiteOrigin
$otherAcao = Get-Acao 'https://not-allowed.example'
Write-Host "ACAO for the site: $($siteAcao -join '|'); for an unlisted origin: $($otherAcao -join '|')"
if ($siteAcao.Count -ne 1 -or $otherAcao.Count -ne 1) { $ok = $false; Write-Warning 'Expected exactly ONE ACAO header.' }
if (($otherAcao -join '') -eq 'https://not-allowed.example') { $ok = $false; Write-Warning 'The allow-list is echoing ANY origin — roll back.' }

if ($ok) { Write-Host 'DEPLOY VERIFIED.' -ForegroundColor Green }
else { Write-Warning "Roll back with:`n  $PSCommandPath -Rollback $backup" }
