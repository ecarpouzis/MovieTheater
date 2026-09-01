<#
.SYNOPSIS
    Keep the GoDaddy AAAA records for the media plane pointed at THIS host's stable IPv6 address.
    The DDNS updater the site-reachability skill flagged as NOT BUILT — built 2026-09-01.

.DESCRIPTION
    The house's IPv6 prefix is DELEGATED by the ISP: a re-delegation silently invalidates every
    AAAA record and takes the whole media plane's direct path with it. Since the VPS IPv4 door
    went in (docs/site-ipv4-door.md) that is no longer an outage — v4 clients detour via the VPS
    and the WireGuard tunnel survives a prefix change because Ziggy dials out — but a stale AAAA
    still costs every dual-stack visitor the full-speed direct path. This closes that gap.

    Guards (all standing rules from the site-reachability skill):
      - publishes ONLY the SLAAC 'Public' address. The 'Temporary' one rotates by design and the
        'Dhcp' one dies when the router stops offering DHCPv6 — publishing either is the trap the
        reachability script exists to catch.
      - touches ONLY AAAA records, and ONLY the names that carry one (books + turn). The other
        media names are CNAMEs and GoDaddy would refuse an AAAA on them anyway (RFC 1034).
      - writes ONLY on difference (a no-op run makes two GETs and zero PUTs), and preserves the
        record's existing TTL.
      - -WhatIf shows the decision without writing.

    Credentials: D:\ArcadeStorage\ddns\godaddy.json -> { "key": "...", "secret": "..." }
    (same convention as the turn relay's secret.txt — lives only on Ziggy, never committed).
    The cluster's cert-manager DNS-01 webhook proves this account's API access works.

.EXAMPLE
    powershell.exe -ExecutionPolicy Bypass -File update-godaddy-aaaa.ps1 -WhatIf
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$Domain     = 'carpouzis.com',
    [string[]]$Names    = @('books', 'turn', 'mediav6'),   # the ONLY names that carry an AAAA; the rest are CNAMEs. mediav6 is the browser's v6-path probe (AAAA and NEVER an A - see docs/site-ipv4-door.md)
    [string]$Nic        = 'Ethernet 2',
    [string]$SecretFile = 'D:\ArcadeStorage\ddns\godaddy.json',
    [string]$LogFile    = 'D:\ArcadeStorage\logs\ddns.log'
)

$ErrorActionPreference = 'Stop'

function Log($msg) {
    $line = '{0}  {1}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $msg
    Write-Host $line
    # -WhatIf:$false: a dry run should still LOG that it was a dry run, not what-if the logging.
    try { Add-Content -Path $LogFile -Value $line -WhatIf:$false -Confirm:$false } catch {}
}

# -- 1. Which address is safe to publish? Only the SLAAC 'Public' one (see the reachability
#       script's THIS HOST'S IPv6 section, whose parse this reuses). --------------------------
$v6 = @(Get-NetIPAddress -AddressFamily IPv6 -InterfaceAlias $Nic -ErrorAction SilentlyContinue |
        Where-Object { $_.IPAddress -match '^[23]' })   # global unicast only - no fe80/fd/lease junk
$netsh = (netsh interface ipv6 show addresses $Nic) -join "`n"

$public = @(foreach ($addr in $v6) {
    if ($netsh -match ([regex]::Escape($addr.IPAddress) + '[\s\S]{0,400}?Address Type\s*:\s*(\w+)')) {
        if ($Matches[1] -eq 'Public') { $addr }
        else { Log "skip $($addr.IPAddress) - labelled '$($Matches[1])', never publishable" }
    } else { Log "skip $($addr.IPAddress) - netsh gave it no label, refusing to guess" }
})

if (-not $public) { Log "FAIL: no SLAAC 'Public' address on '$Nic' - nothing safe to publish. Did RAs stop, or did the prefix just change?"; exit 1 }
# During a prefix transition both old and new prefixes can be live; the freshest lease wins.
$target = ($public | Sort-Object ValidLifetime -Descending | Select-Object -First 1).IPAddress
Log "stable address on '$Nic': $target"

# -- 2. Credentials ---------------------------------------------------------------------------
if (-not (Test-Path $SecretFile)) { Log "FAIL: $SecretFile not found - see docs/site-ipv4-door.md for the shape"; exit 1 }
$cred = Get-Content $SecretFile -Raw | ConvertFrom-Json
if (-not $cred.key -or -not $cred.secret) { Log "FAIL: $SecretFile must hold { key, secret }"; exit 1 }
$headers = @{ Authorization = "sso-key $($cred.key):$($cred.secret)" }

# -- 3. Compare, and write only on difference -------------------------------------------------
$fail = $false
foreach ($name in $Names) {
    $url = "https://api.godaddy.com/v1/domains/$Domain/records/AAAA/$name"
    try {
        $current = @(Invoke-RestMethod -Uri $url -Headers $headers -TimeoutSec 15)
    } catch {
        Log "FAIL: GET $name AAAA: $($_.Exception.Message)"; $fail = $true; continue
    }

    if ($current.Count -gt 0 -and $current[0].data -eq $target) {
        Log "$name.$Domain AAAA already $target - no-op"
        continue
    }

    $ttl = if ($current.Count -gt 0 -and $current[0].ttl) { $current[0].ttl } else { 3600 }
    $was = if ($current.Count -gt 0) { $current[0].data } else { '(none)' }
    if ($PSCmdlet.ShouldProcess("$name.$Domain", "AAAA $was -> $target (ttl $ttl)")) {
        try {
            $body = ConvertTo-Json @(@{ data = $target; ttl = $ttl })
            Invoke-RestMethod -Uri $url -Method Put -Headers $headers -ContentType 'application/json' -Body $body -TimeoutSec 15 | Out-Null
            Log "$name.$Domain AAAA $was -> $target (ttl $ttl) - UPDATED"
        } catch {
            Log "FAIL: PUT $name AAAA: $($_.Exception.Message)"; $fail = $true
        }
    } else {
        Log "$name.$Domain AAAA $was -> $target - would update (WhatIf)"
    }
}

if ($fail) { exit 1 }
