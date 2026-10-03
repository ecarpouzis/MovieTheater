<#
.SYNOPSIS
    LAN DNS fix for browser Local Network Access (LNA): make AdGuard ALSO answer AAAA for the media names,
    passing through their PUBLIC record (Ziggy's global IPv6), next to the private A rewrite it already has.

.DESCRIPTION
    The page (theater.*) is public; AdGuard answers arcade/stream/books/jellyfin-api with A 192.168.68.69 and an
    EMPTY AAAA, so every media request from a LAN browser is public -> private and Chrome/Edge (and Firefox with
    network.lna.enabled) block it or hold it behind a permission prompt: the false "nothing will play" banner and
    arcade rooms stuck at "Connecting...". A global IPv6 address is public to LNA and still on-link, so a LAN
    client with IPv6 goes straight to Ziggy with no prompt. IPv4-only devices keep the private A (unchanged).

    The AAAA rewrite uses AdGuard's special answer value "AAAA" = keep the UPSTREAM AAAA records. Upstream is the
    GoDaddy AAAA on books.* (stream/arcade are CNAMEs to it), which update-godaddy-aaaa.ps1 already keeps current
    on a prefix change — so this needs no DDNS of its own and never goes stale independently.

    Must run ELEVATED (the service restart). Backs the config up first, restarts AdGuard, then verifies every name
    from 127.0.0.1: A must still be 192.168.68.69 and AAAA must equal the public answer. Any failure restores the
    backup and restarts again. Idempotent: a second run finds the entries and only re-verifies.
    -Revert removes the AAAA entries. Never prints the config (it holds the admin password hash).

.EXAMPLE
    pwsh -ExecutionPolicy Bypass -File F:\Work\MovieTheater\scripts\adguard-lan-aaaa.ps1
#>
param([switch]$Revert)
$ErrorActionPreference = 'Stop'
$cfg    = 'C:\AdGuardHome\AdGuardHome.yaml'
$names  = 'stream', 'books', 'arcade', 'jellyfin-api'
$lanA   = '192.168.68.69'

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole('Administrators')) {
    throw 'Run this from an ELEVATED PowerShell (it restarts the AdGuardHome service).'
}

function Restart-AdGuard {
    Restart-Service AdGuardHome
    for ($i = 0; $i -lt 30; $i++) {
        Start-Sleep -Milliseconds 500
        if (Resolve-DnsName "books.carpouzis.com" -Type A -Server 127.0.0.1 -DnsOnly -QuickTimeout -EA SilentlyContinue) { return }
    }
    throw 'AdGuard did not answer on 127.0.0.1 within 15 s of the restart'
}

function Test-Answers([bool]$expectAAAA) {
    $ok = $true
    foreach ($n in $names) {
        $fqdn = "$n.carpouzis.com"
        $a    = @((Resolve-DnsName $fqdn -Type A    -Server 127.0.0.1 -DnsOnly -EA SilentlyContinue | ? Type -eq 'A').IPAddress)
        $v6   = @((Resolve-DnsName $fqdn -Type AAAA -Server 127.0.0.1 -DnsOnly -EA SilentlyContinue | ? Type -eq 'AAAA').IPAddress)
        $pub  = @((Resolve-DnsName $fqdn -Type AAAA -Server 1.1.1.1   -DnsOnly -EA SilentlyContinue | ? Type -eq 'AAAA').IPAddress)
        $aOk  = ($a.Count -eq 1 -and $a[0] -eq $lanA)
        $v6Ok = if ($expectAAAA) { $v6.Count -gt 0 -and -not (Compare-Object $v6 $pub) } else { $v6.Count -eq 0 }
        '{0,-28} A={1,-15} AAAA={2,-40} public AAAA={3}  {4}' -f $fqdn, ($a -join ','), ($v6 -join ','), ($pub -join ','), $(if ($aOk -and $v6Ok) { 'ok' } else { 'FAIL' })
        if (-not ($aOk -and $v6Ok)) { $ok = $false }
    }
    return $ok
}

$text = [IO.File]::ReadAllText($cfg)
$nl   = if ($text.Contains("`r`n")) { "`r`n" } else { "`n" }
$has  = { param($n) $text -match "(?m)^\s*- domain: $([regex]::Escape("$n.carpouzis.com"))\s*\r?\n\s*answer: AAAA\s*$" }

$new = $text
if ($Revert) {
    foreach ($n in $names) {
        $new = [regex]::Replace($new, "(?m)^    - domain: $([regex]::Escape("$n.carpouzis.com"))\r?\n      answer: AAAA\r?\n      enabled: true\r?\n", '')
    }
} else {
    foreach ($n in $names) {
        if (& $has $n) { continue }
        $pattern = "(?m)^(    - domain: $([regex]::Escape("$n.carpouzis.com"))\r?\n      answer: $([regex]::Escape($lanA))\r?\n      enabled: true\r?\n)"
        if ($new -notmatch $pattern) { throw "No 'A $lanA' rewrite for $n.carpouzis.com in the expected shape - nothing changed. Add it in the AdGuard UI (Filters > DNS rewrites: $n.carpouzis.com -> AAAA) instead." }
        $new = [regex]::Replace($new, $pattern, "`$1    - domain: $n.carpouzis.com$nl      answer: AAAA$nl      enabled: true$nl")
    }
}

if ($new -eq $text) {
    'Config already in the requested state - verifying only.'
    if (Test-Answers (-not $Revert)) { 'VERIFIED' } else { 'FAIL - see the rows above' ; exit 1 }
    exit 0
}

$backup = "$cfg.pre-lan-aaaa-$(Get-Date -Format yyyyMMdd-HHmmss)"
Copy-Item $cfg $backup
[IO.File]::WriteAllText($cfg, $new)
"Config updated (backup: $backup). Restarting AdGuard..."
try {
    Restart-AdGuard
    Clear-DnsClientCache
    if (-not (Test-Answers (-not $Revert))) { throw 'verification failed' }
    'VERIFIED - LAN browsers with IPv6 now reach the media hosts by their public address (no LNA prompt).'
} catch {
    "FAILED: $($_.Exception.Message) - restoring $backup"
    Copy-Item $backup $cfg -Force
    Restart-AdGuard
    [void](Test-Answers $false)
    exit 1
}
