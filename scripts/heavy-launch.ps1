# heavy-launch.ps1 — the heavy lane's single launch contract (docs/arcade-heavy-lane-plan.md §4).
#
# Apollo runs this as every synced app's cmd:  powershell -File heavy-launch.ps1 -AppId <id>
# and as the app's undo prep-cmd:              powershell -File heavy-launch.ps1 -AppId <id> -Finish
#
#   1. POST gateway /heavy/prepare/<id>  → takes the one-session lane lock (409 = lane busy → the
#      stream fails fast with a logged reason), returns the resolved emulator command. Prepare NEVER
#      stages (trap #8) — the card's Prepare flow made the ROM local long before this runs.
#   2. Launch the emulator and WAIT ON THE PROCESS — Sunshine app lifetime = cmd lifetime (trap #2):
#      the stream ends when this script exits, so it exits when the emulator does.
#   3. POST /heavy/attach (the lock's liveness becomes the emulator PID — crash-safe), and on any
#      exit path POST /heavy/finish to release the lane. The undo prep-cmd repeats finish as belt-
#      and-suspenders: it runs even if Apollo hard-kills this script's tree.
#
# Runs headless under the Apollo service: no console, no prompts; everything lands in the log file.
# Deployed copy: D:\ArcadeStorage\heavy\heavy-launch.ps1 (this repo file is the source of truth).
# Ziggy-local config: D:\ArcadeStorage\heavy\launch-config.json
#   { "gatewayUrl": "http://localhost:2303", "secretFile": "D:\\ArcadeStorage\\heavy\\gateway-secret.txt" }

param(
    [Parameter(Mandatory = $true)][string]$AppId,
    [switch]$Finish
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

$heavyRoot = "D:\ArcadeStorage\heavy"
$logDir = Join-Path $heavyRoot "logs"
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
$log = Join-Path $logDir ("heavy-launch-{0:yyyyMMdd}.log" -f (Get-Date))
function Write-Log([string]$msg) {
    ("{0:yyyy-MM-dd HH:mm:ss} [{1}] {2}" -f (Get-Date), $AppId, $msg) | Add-Content -Path $log -Encoding utf8
}

# ── Config + auth ────────────────────────────────────────────────────────────────────────────────
$cfgPath = Join-Path $heavyRoot "launch-config.json"
$gatewayUrl = "http://localhost:2303"
$secret = $null
try {
    if (Test-Path $cfgPath) {
        $cfg = Get-Content $cfgPath -Raw | ConvertFrom-Json
        if ($cfg.gatewayUrl) { $gatewayUrl = $cfg.gatewayUrl }
        if ($cfg.secretFile -and (Test-Path $cfg.secretFile)) { $secret = (Get-Content $cfg.secretFile -Raw).Trim() }
    }
} catch { Write-Log "config read failed: $_" }
if (-not $secret) { Write-Log "FATAL: no gateway secret (launch-config.json → secretFile)"; exit 1 }
$headers = @{ "X-Arcade-Internal-Secret" = $secret }

function Invoke-Gateway([string]$method, [string]$path, $body) {
    $args = @{
        Method = $method; Uri = ($gatewayUrl.TrimEnd('/') + $path); Headers = $headers
        TimeoutSec = 30; UseBasicParsing = $true
    }
    if ($null -ne $body) {
        $args.Body = ($body | ConvertTo-Json -Compress)
        $args.ContentType = "application/json"
    }
    Invoke-RestMethod @args
}

# ── Finish mode (the undo prep-cmd, and our own exit path) ──────────────────────────────────────
if ($Finish) {
    try { $r = Invoke-Gateway "POST" "/heavy/finish/$AppId" $null; Write-Log "finish → ok=$($r.ok)" }
    catch { Write-Log "finish failed: $_" }
    exit 0
}

# ── 1. Prepare: take the lane lock, get the resolved command ─────────────────────────────────────
$clientName = $env:SUNSHINE_CLIENT_NAME  # Apollo exports the paired device's name to app commands
$prepareUri = "/heavy/prepare/$AppId"
if ($clientName) { $prepareUri += "?client=" + [uri]::EscapeDataString($clientName) }

try {
    $prep = Invoke-Gateway "POST" $prepareUri $null
} catch {
    # 409 = lane busy (or title unstaged) — fail the stream fast with the reason in the log.
    $detail = ""
    try { $detail = $_.ErrorDetails.Message } catch {}
    Write-Log "prepare REFUSED: $detail"
    exit 1
}
if (-not $prep.ok) { Write-Log "prepare not ok: $($prep | ConvertTo-Json -Compress)"; exit 1 }
Write-Log "prepare ok (client='$clientName') exe=$($prep.exe) args=$($prep.args)"

# ── 2. Launch the emulator and hold the lane while it lives ─────────────────────────────────────
$exitCode = 0
try {
    $startArgs = @{ FilePath = $prep.exe; PassThru = $true }
    if ($prep.args) { $startArgs.ArgumentList = $prep.args }
    if ($prep.workingDir) { $startArgs.WorkingDirectory = $prep.workingDir }
    $proc = Start-Process @startArgs
    Write-Log "launched pid $($proc.Id)"

    try { Invoke-Gateway "POST" "/heavy/attach/$AppId" @{ pid = $proc.Id } | Out-Null }
    catch { Write-Log "attach failed (lock stays time-based): $_" }

    # Trap #2: the emulator was launched directly (descriptors point at the emulator exe, never a
    # spawn-and-exit launcher), so waiting on THIS process is waiting on the game.
    $proc.WaitForExit()
    $exitCode = $proc.ExitCode
    Write-Log "emulator exited with $exitCode"
}
catch {
    Write-Log "launch failed: $_"
    $exitCode = 1
}
finally {
    # ── 3. Release the lane. The undo prep-cmd will repeat this harmlessly (idempotent). ────────
    try { Invoke-Gateway "POST" "/heavy/finish/$AppId" $null | Out-Null; Write-Log "lane released" }
    catch { Write-Log "finish failed (PID self-heal will release): $_" }
}
exit $exitCode
