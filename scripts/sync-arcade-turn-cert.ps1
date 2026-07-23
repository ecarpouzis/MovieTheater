<#
.SYNOPSIS
  Copy the Let's Encrypt cert/key Caddy issued for the TURN hostname out of Caddy's
  (LocalSystem-owned) storage into D:\ArcadeStorage\turn so the native pion/turn relay
  (arcade-turn.exe, which runs as an interactive user and cannot read the SYSTEM store)
  can load them.

  Idempotent: only copies when the source differs from what's already deployed, and only
  then restarts the relay task. Safe to run on a schedule - Caddy auto-renews the cert
  ~30 days before expiry, and this picks up the new file on its next run.

  MUST run elevated (the Caddy store lives under the SYSTEM profile). See turn-relay.md.
#>
[CmdletBinding()]
param(
    [string]$Hostname = "turn.carpouzis.com",
    [string]$Store    = "$env:SystemRoot\System32\config\systemprofile\AppData\Roaming\Caddy\certificates",
    [string]$Dest     = "D:\ArcadeStorage\turn",
    [string]$TaskName = "MovieTheater - Arcade TURN",
    [string]$LogFile  = "D:\ArcadeStorage\logs\turn-cert-sync.log"
)

$ErrorActionPreference = "Stop"

function Log($msg) {
    $line = "{0}  {1}" -f (Get-Date -Format "yyyy-MM-dd HH:mm:ss"), $msg
    Write-Host $line
    try { Add-Content -Path $LogFile -Value $line } catch {}
}

# Caddy nests the PEMs under <store>\<ca-directory>\<hostname>\<hostname>.crt|.key.
# Search rather than hard-code the CA folder so an ACME-directory change doesn't break us.
$crtSrc = Get-ChildItem -Path $Store -Recurse -Filter "$Hostname.crt" -ErrorAction SilentlyContinue |
          Sort-Object LastWriteTime -Descending | Select-Object -First 1
$keySrc = Get-ChildItem -Path $Store -Recurse -Filter "$Hostname.key" -ErrorAction SilentlyContinue |
          Sort-Object LastWriteTime -Descending | Select-Object -First 1

if (-not $crtSrc -or -not $keySrc) {
    Log "SOURCE NOT FOUND for $Hostname under $Store (crt=$([bool]$crtSrc) key=$([bool]$keySrc)). Has Caddy issued it yet? (elevated?)"
    exit 1
}

$crtDst = Join-Path $Dest "turn.crt"
$keyDst = Join-Path $Dest "turn.key"
New-Item -ItemType Directory -Force $Dest | Out-Null

function Same($a, $b) {
    if (-not (Test-Path $b)) { return $false }
    return (Get-FileHash $a).Hash -eq (Get-FileHash $b).Hash
}

$changed = $false
if (-not (Same $crtSrc.FullName $crtDst)) { Copy-Item $crtSrc.FullName $crtDst -Force; $changed = $true }
if (-not (Same $keySrc.FullName $keyDst)) { Copy-Item $keySrc.FullName $keyDst -Force; $changed = $true }

if (-not $changed) {
    Log "No change (deployed cert already matches Caddy's). Source notAfter unchanged."
    exit 0
}

# Report the freshly-deployed cert's validity window so the log shows real progress.
try {
    $c = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2 $crtDst
    Log "Copied cert/key for $Hostname. Valid $($c.NotBefore.ToString('u')) -> $($c.NotAfter.ToString('u'))."
} catch {
    Log "Copied cert/key for $Hostname (could not parse validity: $($_.Exception.Message))."
}

# pion/turn reads the PEMs once at boot, so a renewed cert only takes effect on restart.
$task = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
if ($task) {
    Restart-ScheduledTask -TaskName $TaskName
    Log "Restarted task '$TaskName' to load the new cert."
} else {
    Log "Task '$TaskName' not registered yet - cert is in place; it will be read at first start."
}
