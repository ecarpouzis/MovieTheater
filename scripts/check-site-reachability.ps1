<#
.SYNOPSIS
    One command that answers "is the whole site reachable, and by whom?" — DNS, both address
    families, the host firewall, the serving processes, and a real media fetch.

.DESCRIPTION
    Written after the 2026-09-01 FiOS cutover, which broke the site in three independent ways at once
    and took hours to unpick because each check had to be invented on the spot:

      1. the fiber line is behind CGNAT, so the published IPv4 addresses could never answer;
      2. Caddy's inbound firewall rules covered only the Public profile while the NIC sat on Private,
         so nothing but this box could reach it — over ANY address family. Loopback is exempt from the
         firewall, which is exactly why the box looked healthy while refusing every real client;
      3. the media plane ended up IPv6-only while the app stayed IPv4-only, so the site silently began
         requiring a dual-stack visitor.

    Every one of those is visible in the output below. Run it after ANY network change — a new ISP, a
    router swap, a Windows network-identity change, a DNS edit — and before concluding that a playback
    complaint is the player's fault.

    READ-ONLY. It resolves names, opens TLS connections, and reads process/firewall state. It changes
    nothing.

.NOTES
    "Healthy from inside proves nothing" is the standing rule here: a PASS on the local checks with a
    FAIL on the DNS/family matrix means the box is fine and the PATH is broken. The one thing this
    cannot test from inside the LAN is whether the router passes unsolicited inbound traffic — for
    that, load the site on a phone with wifi OFF.
#>
param(
    [string[]]$Hosts = @('theater.carpouzis.com','stream.carpouzis.com','books.carpouzis.com',
                         'arcade.carpouzis.com','turn.carpouzis.com','jellyfin-api.carpouzis.com',
                         'longbox.carpouzis.com'),
    [string]$Nic = 'Ethernet 2',
    [string[]]$ServerProcesses = @('caddy','AdGuardHome','MovieTheater.StreamGateway','arcade-turn'),
    # The VPS's public IPv4 (docs/site-ipv4-door.md). Only needed to probe the door BEFORE its
    # A records are published - once they are, the SERVICE section probes via them automatically.
    [string]$VpsIp
)

$ErrorActionPreference = 'Continue'
function Head($t) { Write-Output ""; Write-Output "== $t"; }
function Row($ok, $text) { Write-Output ("  [{0}] {1}" -f $(if ($ok) { 'OK  ' } elseif ($null -eq $ok) { '??  ' } else { 'FAIL' }), $text) }

# ── 1. What the WORLD is told (public resolver, not the LAN's) ───────────────────────────────────
Head "PUBLIC DNS  (what a visitor resolves - asked of 8.8.8.8, never the local resolver)"
$pub = @{}
foreach ($h in $Hosts) {
    $a = @(); $aaaa = @()
    foreach ($t in 'A','AAAA') {
        try {
            $r = Invoke-RestMethod -Uri "https://dns.google/resolve?name=$h&type=$t" -TimeoutSec 10
            foreach ($ans in ($r.Answer | Where-Object { $_.type -in 1,28 })) {
                if ($ans.type -eq 1) { $a += $ans.data } else { $aaaa += $ans.data }
            }
        } catch { }
    }
    $pub[$h] = @{ A = $a; AAAA = $aaaa }
    $desc = "{0,-28} A: {1,-16} AAAA: {2}" -f $h, $(if ($a) { $a -join ',' } else { '-' }), $(if ($aaaa) { $aaaa -join ',' } else { '-' })
    Row ($a.Count -gt 0 -or $aaaa.Count -gt 0) $desc
}

# ── 2. WHO can actually use the site ─────────────────────────────────────────────────────────────
# The failure mode this exists to catch: the app on one family and the media plane on the other, so
# the site works only for a visitor who has BOTH. Nothing else reports that - each half looks fine.
Head "REACH  (which visitors can use the WHOLE site)"
$app = 'theater.carpouzis.com'
$media = @('stream.carpouzis.com','books.carpouzis.com','jellyfin-api.carpouzis.com')
$appV4 = $pub[$app].A.Count -gt 0; $appV6 = $pub[$app].AAAA.Count -gt 0
$mediaV4 = ($media | Where-Object { $pub[$_].A.Count -gt 0 }).Count -eq $media.Count
$mediaV6 = ($media | Where-Object { $pub[$_].AAAA.Count -gt 0 }).Count -eq $media.Count
Row ($appV4 -and $mediaV4) ("IPv4-only visitor  - app {0}, media {1}" -f $(if($appV4){'yes'}else{'NO'}), $(if($mediaV4){'yes'}else{'NO'}))
Row ($appV6 -and $mediaV6) ("IPv6-only visitor  - app {0}, media {1}" -f $(if($appV6){'yes'}else{'NO'}), $(if($mediaV6){'yes'}else{'NO'}))
Row (($appV4 -or $appV6) -and ($mediaV4 -or $mediaV6)) "dual-stack visitor - needs one family to work end to end"

# ── 3. Are we behind CGNAT? (a port forward can never work if so) ────────────────────────────────
Head "PATH OUT  (CGNAT check - if this trips, no IPv4 port forward can ever work)"
try {
    $wan = (Invoke-RestMethod -Uri 'https://api.ipify.org?format=json' -TimeoutSec 10).ip
    $hops = (tracert -d -h 3 -w 900 8.8.8.8 2>$null) -join "`n"
    $cgnat = $hops -match '\b100\.(6[4-9]|[7-9]\d|1[0-1]\d|12[0-7])\.'
    Row (-not $cgnat) ("WAN as the world sees us: {0}{1}" -f $wan, $(if ($cgnat) { '  <- CGNAT detected on the path (100.64/10)' } else { '' }))
    if ($cgnat) { Row $false "IPv4 inbound is IMPOSSIBLE on this line. IPv6, or a relay with a public address, are the only doors." }
} catch { Row $null "could not determine the WAN address" }

# ── 4. This host's IPv6 - and WHICH address belongs in DNS ───────────────────────────────────────
# Windows holds up to three global v6 addresses. Only the SLAAC "Public" one is safe to publish: the
# Temporary one rotates by design, and a DHCPv6 lease dies when the router stops offering DHCPv6.
Head "THIS HOST'S IPv6  (only the 'Public' address may go in DNS or a firewall rule)"
$v6 = Get-NetIPAddress -AddressFamily IPv6 -InterfaceAlias $Nic -ErrorAction SilentlyContinue |
      Where-Object { $_.IPAddress -notlike 'fe80*' -and $_.IPAddress -notlike 'fd*' }
$netsh = (netsh interface ipv6 show addresses $Nic) -join "`n"
foreach ($addr in $v6) {
    $type = if ($netsh -match [regex]::Escape($addr.IPAddress) + '[\s\S]{0,400}?Address Type\s*:\s*(\w+)') { $Matches[1] } else { '?' }
    $published = ($pub.Values | ForEach-Object { $_.AAAA }) -contains $addr.IPAddress
    $note = switch ($type) {
        'Public'    { if ($published) { 'stable, and it IS what DNS publishes' } else { 'stable - THIS is the one to publish' } }
        'Temporary' { if ($published) { 'ROTATES - published by mistake, DNS will go stale' } else { 'rotates (outbound only) - never publish' } }
        'Dhcp'      { if ($published) { 'DHCPv6 lease - dies if the router stops offering it' } else { 'DHCPv6 lease - not published, fine' } }
        default     { '' }
    }
    Row ($type -ne 'Temporary' -or -not $published) ("{0,-42} {1,-10} {2}" -f $addr.IPAddress, $type, $note)
}

# ── 5. The firewall trap that has now bitten twice ───────────────────────────────────────────────
# A rule scoped to the wrong PROFILE refuses every remote client while loopback keeps working, so the
# box passes every local test. AdGuard hit this 2026-08-07; Caddy hit it 2026-09-01.
Head "HOST FIREWALL  (rule profile MUST cover the NIC's profile - see 2026-08-07 and 2026-09-01)"
$nicProfile = (Get-NetConnectionProfile -InterfaceAlias $Nic -ErrorAction SilentlyContinue).NetworkCategory
Row $true "NIC '$Nic' profile = $nicProfile"
foreach ($proc in $ServerProcesses) {
    $rules = @(Get-NetFirewallApplicationFilter -ErrorAction SilentlyContinue |
               Where-Object { $_.Program -like "*$proc*" } | Get-NetFirewallRule -ErrorAction SilentlyContinue |
               Where-Object { $_.Direction -eq 'Inbound' -and $_.Enabled -eq 'True' -and $_.Action -eq 'Allow' })
    if (-not $rules) { Row $null "$proc : no inbound allow rule found"; continue }
    $profiles = ($rules | ForEach-Object { $_.Profile.ToString() }) -join '/'
    $covered = $rules | Where-Object { $_.Profile -eq 'Any' -or $_.Profile.ToString() -match $nicProfile }
    Row ([bool]$covered) ("{0,-28} rule profiles: {1}" -f $proc, $profiles)
}

# ── 6. Are the serving processes actually up? ────────────────────────────────────────────────────
Head "PROCESSES"
foreach ($proc in $ServerProcesses) {
    $p = @(Get-Process $proc -ErrorAction SilentlyContinue)
    Row ($p.Count -gt 0) ("{0,-28} {1}" -f $proc, $(if ($p.Count) { "running (pid $($p[0].Id))" } else { 'NOT RUNNING' }))
}

# ── 7. The IPv4 door (docs/site-ipv4-door.md) ────────────────────────────────────────────────────
# Behind CGNAT the only inbound IPv4 is the VPS: DNS A -> VPS, nftables DNAT -> WireGuard -> Caddy.
# Ziggy CAN test this whole path itself - outbound to the VPS's public address traverses CGNAT
# fine, gets DNAT'd back down the tunnel, and exercises exactly what a v4-only hotel client hits
# (unlike the old direct hairpin, which nothing on the LAN could ever test).
Head "IPv4 DOOR  (WireGuard to the VPS - the only inbound IPv4 path)"
$wg = @(Get-NetAdapter -ErrorAction SilentlyContinue |
        Where-Object { $_.InterfaceDescription -like '*WireGuard*' -and $_.Status -eq 'Up' })
if (-not $wg) {
    Row $null "no WireGuard interface up - the IPv4 door is not running (v4-only visitors get no media)"
} else {
    Row $true ("tunnel interface '{0}' up" -f $wg[0].Name)
    $ping = Test-Connection -ComputerName 10.9.0.1 -Count 1 -Quiet -ErrorAction SilentlyContinue
    Row $ping "VPS wg peer 10.9.0.1 answers ping over the tunnel"
    if ($VpsIp) {
        foreach ($h in 'stream.carpouzis.com','books.carpouzis.com') {
            $code = & curl.exe -4 -s -o NUL -m 12 --resolve "${h}:443:${VpsIp}" -w "%{http_code}" "https://$h/" 2>$null
            Row ($code -ne '000' -and $code -ne '') ("{0,-28} via VPS {1,-16} -> {2}" -f $h, $VpsIp, $(if ($code) { $code } else { 'no answer' }))
        }
    } else {
        Row $null "pass -VpsIp <public v4> to probe the door directly (needed only before DNS is published)"
    }
}

# ── 8. Does each host actually SERVE, over each family it advertises? ────────────────────────────
# A 401/403 is a PASS: the gate answering means TLS terminated and the app is behind it. Only a
# connection failure (000) is a real failure.
Head "SERVICE  (TLS + HTTP per address family; 401/403 = the gate answering, which is a pass)"
foreach ($h in $Hosts) {
    foreach ($fam in 'A','AAAA') {
        foreach ($ip in $pub[$h].$fam) {
            if ($ip -notmatch '^[0-9a-fA-F:.]+$') { continue }   # skip CNAME targets
            $code = & curl.exe -s -o NUL -m 12 --resolve "${h}:443:${ip}" -w "%{http_code}" "https://$h/" 2>$null
            Row ($code -ne '000' -and $code -ne '') ("{0,-28} via {1,-40} -> {2}" -f $h, $ip, $(if ($code) { $code } else { 'no answer' }))
        }
    }
}

Write-Output ""
Write-Output "NOTE: none of the above proves the ROUTER passes unsolicited inbound traffic - only a"
Write-Output "      client off this network can. Load the site on a phone with wifi OFF to prove that."
