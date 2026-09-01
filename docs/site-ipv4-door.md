# The IPv4 door — VPS front for the media plane

## Why this exists

The 2026-09-01 Archtop fiber cutover put the house behind CGNAT: no unsolicited IPv4 can ever
reach Ziggy again, so the media plane went IPv6-only and an IPv4-only visitor (hotel/public wifi,
mostly) loads the app and plays nothing. The TURN relay — built precisely for locked-down public
wifi — was unreachable from those networks too.

**Decision (2026-09-01): a ~$5/mo VPS, not the $10/mo ISP static IP.** The deciding property:
**Ziggy dials OUT to the VPS**, so the site's public IPv4 face survives CGNAT, an IPv6 prefix
re-delegation, an ISP change, and a house move. The static IP answered only the first of those
and rented its answer from the ISP.

The whole thing is a **blind TCP forwarder**. Ziggy's Caddy already SNI-demuxes TCP :443 (the
layer4 block in `C:\caddy\Caddyfile` — see `docs/arcade/turn-relay.md`), HTTP/3 is disabled, and
TLS-ALPN-01 cert renewal rides the same passthrough. So the VPS terminates nothing, holds no
certs, and needs no SNI logic; **Caddy on Ziggy is untouched by this entire build**.

```
IPv4-only client ──TCP 443/80/5349──▶ VPS (static public v4, Newark)
                                        │ nftables DNAT + masquerade  (NOTHING listens on 443)
                                        │ WireGuard  (Ziggy dials out; keepalive holds the CGNAT mapping)
                                        ▼
                                     Ziggy 10.9.0.2 → Caddy :443  layer4 SNI demux (unchanged)
                                        ├── SNI=turn, no ALPN ▶ pion relay :5349
                                        └── everything else  ▶ HTTP app :8443
dual-stack client ──IPv6──▶ Ziggy direct (full speed; Happy Eyeballs never touches the VPS)
```

Measured basis: total media egress ≈ 206 GB/month *including LAN* (2026-09-01), so the v4-only
remote share fits far inside any 1 TB allowance; WireGuard on the smallest tier pushes gigabits,
well past the ~310 Mbps uplink. Dual-stack clients bypass the VPS entirely, so it can never
regress them — and v4-only clients get nothing today, so it can't regress them either.

## Piece list

| Piece | Where |
|---|---|
| VPS: WireGuard server + nftables DNAT | the VPS (configs reproduced in full below) |
| Ziggy: WireGuard tunnel `mt-vps` | WireGuard for Windows (runs as a service, survives reboot) |
| DNS: `A books` + `A turn` → VPS | GoDaddy (`stream`/`arcade`/`jellyfin-api`/`longbox` are CNAMEs → `books`, so they inherit it) |
| AAAA DDNS updater | `scripts/update-godaddy-aaaa.ps1` + `scripts/register-godaddy-ddns-task.ps1` |
| Instrument | `scripts/check-site-reachability.ps1` (IPv4 DOOR section; pass `-VpsIp` pre-DNS) |

## 1. Provision (once)

Vultr or Linode, **Newark/NJ** region (~5–10 ms from the house — every v4 media request pays this
RTT, so region matters), smallest Debian 12 tier **with a public IPv4**. SSH keys only:

```bash
# as root on the fresh VPS
sed -i 's/^#\?PasswordAuthentication.*/PasswordAuthentication no/' /etc/ssh/sshd_config
systemctl reload ssh
apt update && apt install -y wireguard nftables
```

## 2. WireGuard

Keys — generate each side's pair **on that side**; only public keys cross the wire:

```bash
# VPS
umask 077; wg genkey | tee /etc/wireguard/server.key | wg pubkey > /etc/wireguard/server.pub
```
```powershell
# Ziggy: WireGuard for Windows -> Add empty tunnel (generates the keypair in place)
```

`/etc/wireguard/wg0.conf` on the VPS:

```ini
[Interface]
Address    = 10.9.0.1/24
ListenPort = 51820
PrivateKey = <contents of server.key>
MTU        = 1420

[Peer]
# Ziggy. NO Endpoint on purpose: Ziggy is behind CGNAT and dials out; the VPS
# learns the current mapping from whatever address the handshake arrives from.
PublicKey  = <Ziggy's public key>
AllowedIPs = 10.9.0.2/32
```

```bash
systemctl enable --now wg-quick@wg0
echo 'net.ipv4.ip_forward=1' > /etc/sysctl.d/99-mt-forward.conf && sysctl --system
```

Ziggy's tunnel (`mt-vps` in WireGuard for Windows — installs as a service, active without login):

```ini
[Interface]
PrivateKey = <Ziggy's private key>
Address    = 10.9.0.2/32
MTU        = 1420

[Peer]
PublicKey           = <contents of server.pub>
Endpoint            = <VPS_PUBLIC_IP>:51820
# ONLY the VPS's tunnel address. 0.0.0.0/0 here would route ALL of Ziggy's traffic
# through the VPS: kills the direct v6 path, blows the transfer cap, breaks quietly.
AllowedIPs          = 10.9.0.1/32
# Keepalive is what holds the CGNAT mapping open so the VPS can always reach back in.
PersistentKeepalive = 25
```

After activating: run `scripts\check-site-reachability.ps1` — a new interface can flip Windows
firewall profiles (the trap that has bitten twice; Caddy's rules are `Any` now, but verify).

## 3. Forwarding — `/etc/nftables.conf` on the VPS (replaces the file)

```nft
#!/usr/sbin/nft -f
# Blind TCP forwarder for the MovieTheater media plane (docs/site-ipv4-door.md).
# NOTHING on this box may listen on 80/443/5349 - a TLS terminator here would break
# TLS-ALPN-01 cert renewal on Ziggy AND feed the pion TURN relay plaintext.
flush ruleset

table inet filter {
  chain input {
    type filter hook input priority 0; policy drop;
    ct state established,related accept
    iif lo accept
    iifname "wg0" icmp type echo-request accept   # Ziggy's tunnel-health ping (reachability script)
    tcp dport 22 accept
    udp dport 51820 accept
    icmp type echo-request limit rate 5/second accept
  }
  chain forward {
    type filter hook forward priority 0; policy drop;
    # MSS clamp: without it the signature failure is "TLS handshake fine, large transfer
    # stalls" - the tunnel's 1420 MTU eats 80 bytes the client never learned about.
    tcp flags syn tcp option maxseg size set rt mtu
    ct state established,related accept
    ip daddr 10.9.0.2 tcp dport { 80, 443, 5349 } accept
  }
}

table ip nat {
  chain prerouting {
    type nat hook prerouting priority -100;
    # iifname guard: only traffic arriving from the internet is the door's business.
    iifname != "wg0" tcp dport { 80, 443, 5349 } dnat to 10.9.0.2
  }
  chain postrouting {
    type nat hook postrouting priority 100;
    # Masquerade so Ziggy's replies route back down the tunnel instead of out its own
    # default route (asymmetric and dead). Cost: Caddy sees 10.9.0.1 as the client -
    # no loss, layer4 already made every client look like 127.0.0.1.
    oifname "wg0" masquerade
  }
}
```

```bash
nft -c -f /etc/nftables.conf && nft -f /etc/nftables.conf && systemctl enable nftables
```

Port 5349 rides along so the relay's native TURNS port works over v4 too (it stays listed as a
second ICE url); 80 so the Caddyfile's explicit HTTP→HTTPS redirects answer v4 visitors.

## 4. Prove the door BEFORE touching DNS

All from Ziggy — the hairpin through the VPS's public address exercises the exact path a v4-only
client takes:

```powershell
scripts\check-site-reachability.ps1 -VpsIp <VPS_PUBLIC_IP>
# IPv4 DOOR section: tunnel up, peer answers ping, stream->200 / books->401 via the VPS
```

Then the two proofs curl can't give:

- **Passthrough proof**: SNI=`turn.carpouzis.com` at `<VPS_IP>:443` must present the **relay's own
  cert** — use the `Get-SniCert` helper in `scripts/deploy-caddy-turn443.ps1`. (Never probe the
  turn web path with a .NET/PowerShell HTTP client — no ALPN, falls through to pion and "hangs".)
- **MTU proof**: pull ≥50 MB through the door and record the Mbps — a stall here with a healthy
  handshake means the MSS clamp isn't working:
  `curl.exe -4 --resolve stream.carpouzis.com:443:<VPS_IP> -o NUL <a known big media URL>`

## 5. DNS (GoDaddy)

- `books.carpouzis.com` — **add** `A → <VPS_IP>` (keep the AAAA). Covers stream / arcade /
  jellyfin-api / longbox via their CNAMEs.
- `turn.carpouzis.com` — **add** `A → <VPS_IP>` (keep the AAAA).
- TTL 600 until the phone test passes, then 3600.
- `theater.carpouzis.com` stays untouched (Neil's box, already v4).

Rollback at any time = delete those two A records; one TTL later the site is exactly as it was.

## 6. AAAA DDNS updater

The IPv6 prefix is the ISP's; a re-delegation used to be a media-plane outage and is now only a
slow path (v4 clients detour via the VPS, and the tunnel survives because Ziggy dials out). The
updater keeps dual-stack visitors on the fast direct path regardless:

```powershell
# once: credentials (portal shows the secret ONCE; the DNS-01 webhook proves the account works)
mkdir D:\ArcadeStorage\ddns
Set-Content D:\ArcadeStorage\ddns\godaddy.json '{ "key": "...", "secret": "..." }'

scripts\update-godaddy-aaaa.ps1 -WhatIf    # must name the SLAAC Public address and only it
scripts\register-godaddy-ddns-task.ps1     # 15-minute task, logs to D:\ArcadeStorage\logs\ddns.log
```

It publishes **only** the SLAAC `Public` address (never `Temporary`/`Dhcp`), touches **only** the
AAAA on `books` and `turn`, and writes **only** on difference.

## 7. Final verification

1. `scripts\check-site-reachability.ps1` — REACH: **IPv4-only visitor: app yes, media YES**; the
   SERVICE section now probes every host over v4 (via the new A) *and* v6.
2. Phone with wifi OFF (ideally v6 disabled too): browse, play a movie, play music — entries land
   in `C:\caddy\access-stream.log`.
3. Arcade from a v4-only client: signaling connects; force the relay path with headless Chromium
   `iceTransportPolicy:'relay'` against the `:443` turn URL (verified recipe in
   `docs/arcade/turn-relay.md`) — must gather `typ relay`.
4. DDNS drill: hand-edit one AAAA to a wrong value; within 15 minutes the task restores it and
   the log names the correction.

## Standing rules

- **`docker/arcade/.env` `ZIGGY_PUBLIC_IP` stays LAN-only.** The VPS address there would be
  advertised as srflx, is unreachable for UDP, and kills arcade hole punching. The arcade never
  uses the door: direct play hole-punches CGNAT, and only TURN-over-TCP rides the VPS.
- **Nothing on the VPS listens on 80/443/5349** — raw DNAT only, forever.
- Tailscale stays RDP-only (house policy); this tunnel is plain WireGuard on purpose.
- The VPS is in the media path for v4-only clients ONLY. If it dies, dual-stack visitors notice
  nothing; rollback for everyone else is deleting two A records.
