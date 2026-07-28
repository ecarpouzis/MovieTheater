# Deploy the TURNS-on-443 front door (docs/arcade/turn-relay.md).
#
# Swaps Caddy for a build carrying the caddy-l4 plugin and installs the Caddyfile whose
# layer4 app demuxes TCP :443 by SNI -- turn.carpouzis.com to the pion relay (raw TCP
# passthrough), everything else to the HTTP app on :8443.
#
# WHY a script and not a few manual commands: this is the front door for books, stream,
# jellyfin-api and arcade all at once. Every step is checked, and ANY failed verification
# rolls the binary AND the config back and restarts the service before exiting.
#
# Run ELEVATED (stopping/starting the service needs admin):
#   powershell.exe -ExecutionPolicy Bypass -File F:\Work\MovieTheater\scripts\deploy-caddy-turn443.ps1
# Undo at any later time:
#   ... -File ...\deploy-caddy-turn443.ps1 -Rollback

[CmdletBinding()]
param(
    [switch]$Rollback,
    [string]$CaddyDir = 'C:\caddy'
)

$ErrorActionPreference = 'Stop'

$live       = Join-Path $CaddyDir 'caddy.exe'
$liveConf   = Join-Path $CaddyDir 'Caddyfile'
$newExe     = Join-Path $CaddyDir 'caddy-l4.exe'
$newConf    = Join-Path $CaddyDir 'Caddyfile.l4'
$bakExe     = Join-Path $CaddyDir 'caddy.pre-turn443.exe'
$bakConf    = Join-Path $CaddyDir 'Caddyfile.pre-turn443'

function Say($m) { Write-Host "[deploy] $m" }
function Die($m) { Write-Host "[deploy] FAILED: $m" -ForegroundColor Red; exit 1 }

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
        ).IsInRole([Security.Principal.WindowsBuiltinRole]::Administrator)) {
    Die 'must run elevated (Caddy service stop/start needs admin).'
}

# --- TLS probe: connect with a given SNI and report the cert subject we are handed. ---
# This is how we prove the demux: SNI=turn must yield the relay's own cert.
function Get-SniCert([string]$computer, [int]$port, [string]$sni) {
    $c = $null
    try {
        $c = New-Object Net.Sockets.TcpClient
        $c.SendTimeout = 6000; $c.ReceiveTimeout = 6000
        $c.Connect($computer, $port)
        $s = New-Object Net.Security.SslStream($c.GetStream(), $false,
                ({ $true } -as [Net.Security.RemoteCertificateValidationCallback]))
        $s.AuthenticateAsClient($sni)
        return $s.RemoteCertificate.Subject
    } catch { return "ERROR: $($_.Exception.Message)" }
    finally { if ($c) { $c.Close() } }
}

function Restore-Previous {
    Say 'rolling back...'
    Stop-Service caddy -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
    if (Test-Path $bakExe)  { Copy-Item $bakExe  $live     -Force }
    if (Test-Path $bakConf) { Copy-Item $bakConf $liveConf -Force }
    Start-Service caddy
    Start-Sleep -Seconds 4
    Say 'rolled back to the previous binary + Caddyfile.'
}

if ($Rollback) {
    if (-not (Test-Path $bakExe)) { Die "no backup at $bakExe" }
    Restore-Previous
    Say 'done.'
    exit 0
}

# ---------------------------------------------------------------- pre-flight (no changes yet)
Say 'pre-flight...'
foreach ($f in @($newExe, $newConf, $live, $liveConf)) {
    if (-not (Test-Path $f)) { Die "missing $f" }
}
if (-not (& $newExe list-modules 2>$null | Select-String -Quiet '^\s*layer4\s*$')) {
    Die "$newExe does not carry the layer4 plugin."
}
$validateOut = & $newExe validate --config $newConf --adapter caddyfile 2>&1
if ($LASTEXITCODE -ne 0) {
    $validateOut | ForEach-Object { Say "  $_" }
    Die 'new Caddyfile does not validate.'
}
if (-not (Get-NetTCPConnection -LocalPort 5349 -State Listen -ErrorAction SilentlyContinue)) {
    Die 'the TURN relay is not listening on 5349 -- start "MovieTheater - Arcade TURN" first.'
}
Say 'pre-flight OK (plugin present, config valid, relay up).'

# ---------------------------------------------------------------- backup + swap
Copy-Item $live     $bakExe  -Force
Copy-Item $liveConf $bakConf -Force
Say "backed up to $bakExe / $bakConf"

Say 'stopping caddy...'
Stop-Service caddy -Force
Start-Sleep -Seconds 2

try {
    Copy-Item $newExe  $live     -Force
    Copy-Item $newConf $liveConf -Force
} catch {
    Say "swap failed: $($_.Exception.Message)"
    Restore-Previous
    Die 'could not swap files (is caddy.exe still locked?).'
}

Say 'starting caddy...'
Start-Service caddy
Start-Sleep -Seconds 6

# ---------------------------------------------------------------- verify (any failure = rollback)
$problems = @()

if ((Get-Service caddy).Status -ne 'Running') { $problems += 'caddy service is not Running' }

foreach ($h in @('books.carpouzis.com', 'stream.carpouzis.com', 'arcade.carpouzis.com')) {
    $subj = Get-SniCert '127.0.0.1' 443 $h
    if ($subj -notlike "*$h*") { $problems += "web host $h did not get its own cert on 443 (got: $subj)" }
}

$turn443 = Get-SniCert '127.0.0.1' 443 'turn.carpouzis.com'
if ($turn443 -notlike '*turn.carpouzis.com*') { $problems += "SNI turn on 443 did not reach the relay (got: $turn443)" }

$turn5349 = Get-SniCert '127.0.0.1' 5349 'turn.carpouzis.com'
if ($turn5349 -notlike '*turn.carpouzis.com*') { $problems += "relay stopped answering on 5349 (got: $turn5349)" }

try {
    if ((Invoke-WebRequest 'https://arcade.carpouzis.com/healthz' -TimeoutSec 10).StatusCode -ne 200) {
        $problems += 'arcade /healthz did not return 200'
    }
} catch { $problems += "arcade /healthz failed: $($_.Exception.Message)" }

if ($problems.Count -gt 0) {
    Say '--- VERIFICATION FAILED ---'
    $problems | ForEach-Object { Say "  * $_" }
    Restore-Previous
    Die 'rolled back; nothing changed. Send the list above to Claude.'
}

Say '--- VERIFIED ---'
Say '  web hosts serve their own certs on 443 (via layer4 -> :8443)'
Say '  SNI turn.carpouzis.com on 443 reaches the pion relay'
Say '  relay still answers on 5349 (fallback intact)'
Say '  arcade /healthz 200'
Say ''
Say 'Live. Remaining step: add turns:...:443 to ArcadeTurnUrls in the prod'
Say 'MOVIETHEATER_APPSETTINGS_JSON secret, then redeploy the site pod.'
