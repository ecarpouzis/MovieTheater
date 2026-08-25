#requires -Version 7
<#
.SYNOPSIS
  One-time NSSM install of the MovieTheater.BooksHost Windows service on the media host (merge program R5).

.DESCRIPTION
  Mirrors the standalone books site's install (nssm-supervised; the host does not call UseWindowsService):
  the service runs `MovieTheater.BooksHost.exe web` from C:\BooksHost\app, as a REAL USER ACCOUNT (the
  library and the thumbnail cache are on the NAS; LocalSystem cannot see them), with
  ASPNETCORE_ENVIRONMENT=Production as its ONLY environment variable — every path and secret lives in
  C:\BooksHost\app\appsettings.Production.json, which the deploy script never overwrites. (The standalone site
  pinned paths through nssm environment variables and that made its config source order load-bearing and
  confusing; this host reads one file.)

  Run ONCE from an ELEVATED pwsh, AFTER the first `deploy-books-host.ps1 -SkipRestart` has put the binaries in
  place and appsettings.Production.json has been written by hand. Re-running is safe: every `nssm set` is idempotent.

.PARAMETER Account
  The account the service runs as (default: the installing user). Prompts for the password.
#>
[CmdletBinding()]
param(
  [string]$ServiceName = 'BooksHost',
  [string]$AppDir      = 'C:\BooksHost\app',
  [string]$LogDir      = 'C:\BooksHost\logs',
  [string]$Account     = "$env:USERDOMAIN\$env:USERNAME"
)
$ErrorActionPreference = 'Stop'

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
  ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) { throw "Not elevated. Relaunch: Start-Process pwsh -Verb RunAs -ArgumentList '-NoExit','-File','$PSCommandPath'" }

$nssm = (Get-Command nssm -ErrorAction SilentlyContinue).Source
if (-not $nssm) {
  $candidate = Get-ChildItem "$env:LOCALAPPDATA\Microsoft\WinGet\Packages\NSSM.NSSM_*\*\win64\nssm.exe" -ErrorAction SilentlyContinue | Select-Object -First 1
  if ($candidate) { $nssm = $candidate.FullName }
}
if (-not $nssm) { throw 'nssm not found on PATH or under the WinGet packages folder.' }

$exe = Join-Path $AppDir 'MovieTheater.BooksHost.exe'
if (-not (Test-Path $exe)) { throw "No host binary at $exe — run scripts\deploy-books-host.ps1 -SkipRestart first." }
if (-not (Test-Path (Join-Path $AppDir 'appsettings.Production.json'))) {
  throw "No appsettings.Production.json in $AppDir — write it (Books:Urls, SiteOrigin, PublicBaseUrl, IdentityTokenSecret, MediaTokenSecret, DbPath, LegsDbPath, CacheDir, …) before installing."
}
New-Item -ItemType Directory -Force $LogDir | Out-Null

# nssm writes benign chatter to stderr; do not let that read as failure
$ErrorActionPreference = 'Continue'
if (-not (Get-Service $ServiceName -ErrorAction SilentlyContinue)) {
  & $nssm install $ServiceName $exe web
}
& $nssm set $ServiceName Application $exe
& $nssm set $ServiceName AppParameters web
& $nssm set $ServiceName AppDirectory $AppDir
& $nssm set $ServiceName DisplayName 'MovieTheater Books Host'
& $nssm set $ServiceName Description 'The Books vertical host: catalog API behind the site proxy, media plane, offline verbs.'
& $nssm set $ServiceName Start SERVICE_AUTO_START
& $nssm set $ServiceName AppEnvironmentExtra 'ASPNETCORE_ENVIRONMENT=Production'
& $nssm set $ServiceName AppExit Default Restart
& $nssm set $ServiceName AppRestartDelay 3000
& $nssm set $ServiceName AppThrottle 5000
& $nssm set $ServiceName AppStdout (Join-Path $LogDir 'bookshost.out.log')
& $nssm set $ServiceName AppStderr (Join-Path $LogDir 'bookshost.err.log')
& $nssm set $ServiceName AppRotateFiles 1
& $nssm set $ServiceName AppRotateOnline 1
& $nssm set $ServiceName AppRotateBytes 10485760
$builtIn = @('LocalSystem', 'NT AUTHORITY\LocalService', 'NT AUTHORITY\NetworkService')
if ($builtIn -contains $Account) {
  & $nssm set $ServiceName ObjectName $Account
} else {
  $cred = Get-Credential -UserName $Account -Message "Password for the service account ($Account) — it needs NAS access"
  & $nssm set $ServiceName ObjectName $cred.UserName $cred.GetNetworkCredential().Password
}
$ErrorActionPreference = 'Stop'

Start-Service $ServiceName
(Get-Service $ServiceName).WaitForStatus('Running', '00:00:30')
Start-Sleep -Seconds 3
$health = (Invoke-WebRequest 'http://localhost:2204/healthz' -TimeoutSec 10 -SkipHttpErrorCheck).StatusCode
Write-Host "service: $((Get-Service $ServiceName).Status); healthz: $health"
if ($health -ne 200) { Write-Warning "Not healthy — read $LogDir\bookshost.err.log" }
