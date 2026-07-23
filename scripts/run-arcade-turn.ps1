<#
.SYNOPSIS
    Runs the native pion/turn relay (arcade-turn.exe) in a restart loop.
    Sibling of run-arcade-coordinator.ps1; registered by register-arcade-turn-task.ps1.

.DESCRIPTION
    The TURN relay is the LAST-RESORT ICE path for arcade clients that can't reach a worker directly
    (guest/isolated SSID, hostile remote network). ICE ranks relay candidates lowest, so LAN/cellular
    clients that connect directly never touch it. See docs/arcade/turn-relay.md for the full runbook.

    turns (TLS/TCP) only — a UDP listener would hit the same UDP-hairpin wall the isolated client
    already fails on. Requires:
      - arcade-turn.exe in $ConfDir (build: docker/arcade/turn, `go build -o arcade-turn.exe .`)
      - a shared secret in $SecretFile (byte-identical to the site's ArcadeTurnSecret; NOT in the repo)
      - a publicly-trusted cert/key for the turn hostname (Caddy already fetches these — see the doc)
#>
param(
    [string]$ConfDir    = "D:\ArcadeStorage\turn",
    [string]$LogFile    = "D:\ArcadeStorage\logs\turn.log",
    [string]$SecretFile = "D:\ArcadeStorage\turn\secret.txt",
    [string]$Cert       = "D:\ArcadeStorage\turn\turn.crt",
    [string]$Key        = "D:\ArcadeStorage\turn\turn.key",
    [string]$Listen     = ":5349",
    [string]$Realm      = "arcade.carpouzis.com",
    [string]$RelayIp    = "192.168.68.69",
    [string]$AllowedPeers = "192.168.68.69,98.15.249.217"
)

$exe = Join-Path $ConfDir "arcade-turn.exe"
if (-not (Test-Path $exe))        { throw "arcade-turn.exe not found at $exe (build docker/arcade/turn)" }
if (-not (Test-Path $SecretFile)) { throw "TURN secret file missing at $SecretFile" }
if (-not (Test-Path $Cert))       { throw "TURN cert missing at $Cert" }
if (-not (Test-Path $Key))        { throw "TURN key missing at $Key" }

$secret = (Get-Content $SecretFile -Raw).Trim()
if (-not $secret) { throw "TURN secret file $SecretFile is empty" }

(Get-Process -Id $PID).PriorityClass = 'Normal'

while ($true) {
    if ((Test-Path $LogFile) -and ((Get-Item $LogFile).Length -gt 25MB)) {
        Move-Item $LogFile "$LogFile.1" -Force -ErrorAction SilentlyContinue
    }
    "=== arcade-turn start $(Get-Date -Format o) ===" | Out-File -FilePath $LogFile -Append -Encoding utf8

    # Secret rides an env var (not argv) so it never lands in a process-list / command line.
    $env:TURN_SECRET = $secret
    # Stream + append via cmd (same rationale as the coordinator runner: -RedirectStandardOutput
    # buffers until exit, hiding a running process's logs).
    cmd.exe /c "`"$exe`" -listen `"$Listen`" -realm `"$Realm`" -relay-ip `"$RelayIp`" -cert `"$Cert`" -key `"$Key`" -allowed-peers `"$AllowedPeers`" >> `"$LogFile`" 2>&1"
    $code = $LASTEXITCODE

    "=== arcade-turn exited (code $code) $(Get-Date -Format o); restarting in 3s ===" |
        Out-File -FilePath $LogFile -Append -Encoding utf8
    Start-Sleep -Seconds 3
}
