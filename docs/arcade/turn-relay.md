# Arcade TURN relay — guest / remote play

## Why this exists

A client on a **guest/isolated SSID** (Deco guest networks are walled off from the wired LAN by
design and can't be selectively opened) — or on a **hostile remote network** — can reach Ziggy on the
**public-IP TCP hairpin** but not via direct or hairpinned **UDP** to a worker. WebRTC then stalls at
"negotiating": the browser reaches the worker (`Peer connection:` in the worker log) but ICE never
completes, so the game never starts.

The TURN relay is the last-resort ICE path. The client reaches it over **TURNS (TLS/TCP)** — the one
route that works from such a network — and it forwards to the worker over the LAN.

- **It does not slow down normal play.** ICE ranks relay candidates lowest, so LAN and cellular clients
  (which connect directly today via host/srflx) never touch it.
- **turns/TCP only, on purpose.** A UDP TURN listener would hit the exact UDP-hairpin wall the isolated
  client already fails on. The relay converts the dead `client→worker UDP` leg into
  `client→relay (TLS/TCP hairpin, works)` + `relay→worker (LAN, localhost-ish)`.
- The honest cost when it *does* relay: real-time media over TCP → head-of-line blocking under loss.
  Fine for a fallback; a notch below a direct UDP path.

### TURNS is also served on **443** (2026-07-28) — and that is the port that matters

5349 is the IANA turns port, but a genuinely locked-down network doesn't care: **a FortiGuard-filtered
public wifi permits basically 80/443 and nothing else**, so the relay was unreachable from exactly the
networks that need a relay. (The same venue also blocked `arcade.carpouzis.com` outright — rated
**Phishing**, a miscategorisation — and MITM'd the TLS to inject its block page, which is why the
browser complained about the cert. See [[arcade-public-wifi-signaling]].)

Ziggy has one WAN IP and Caddy already owns 443, so the two share it by **SNI demux**: Caddy's
`layer4` app listens on TCP :443 and hands `SNI = turn.carpouzis.com` to the relay as a **raw TCP
passthrough**, everything else to the normal HTTP app (moved to `https_port 8443`).

- **Never put a `tls` handler on the turn route.** The relay terminates TURNS itself with its own copy
  of the cert; terminating at Caddy would hand pion plaintext.
- **`acme-tls/1` is matched FIRST and sent to the HTTP app.** A TLS-ALPN-01 challenge for the turn
  hostname carries `turn.carpouzis.com` as SNI, so without that route the relay would answer the
  challenge and fail it — and the cert could never renew.
- Requires the `github.com/mholt/caddy-l4` plugin in the Caddy build (alongside `caddy-dns/godaddy`).
  Get one from `https://caddyserver.com/api/download?os=windows&arch=amd64&p=github.com/mholt/caddy-l4&p=github.com/caddy-dns/godaddy`.

The Caddyfile lives at `C:\caddy\Caddyfile` (outside this repo), so the load-bearing part is
reproduced here — global options block:

```caddyfile
{
	https_port 8443          # the HTTP app moves off :443; layer4 owns it

	layer4 {
		:443 {
			@acme tls alpn acme-tls/1     # MUST come first (cert renewal)
			route @acme { proxy 127.0.0.1:8443 }

			@turnweb tls {           # HTTP clients -> web app, so the host isn't a black hole
				sni turn.carpouzis.com
				alpn h2 http/1.1
			}
			route @turnweb { proxy 127.0.0.1:8443 }

			@turn tls sni turn.carpouzis.com
			route @turn { proxy 127.0.0.1:5349 }

			route { proxy 127.0.0.1:8443 }
		}
	}
}
```

### Why `@turnweb` exists (added 2026-07-28)

Sending *all* of `SNI=turn.carpouzis.com` to pion made the hostname a black hole over HTTP — it
completes TLS then swallows the request and times out, which reads as evasive to a reputation
scanner. Bad, given FortiGuard had just auto-rated `arcade.carpouzis.com` as **Phishing** (see
[[arcade-public-wifi-signaling]]); the fingerprint it scores is "valid cert + residential IP + zero
human content + opaque token paths". `arcade`, `stream` and `turn` now each serve a real landing
page, `robots.txt` and `favicon.ico` from `C:\caddy\site\<host>\` at exactly those three paths;
every functional path still proxies through untouched.

Browsers and crawlers always negotiate `h2`/`http/1.1`, and a browser's TURN/TLS client negotiates
neither — **verified**, not assumed: headless Chromium with `iceTransportPolicy:'relay'` and only the
:443 url offered gathered `typ relay` candidates.

⚠ **Do not test this with a .NET/PowerShell HTTP client** — it sends no ALPN, so it falls through to
the relay and *appears* to hang, which looks exactly like the bug this fixed. Use `curl` (sends ALPN
by default) for the web path, and a no-ALPN raw TLS socket + STUN Allocate for the relay path.

Deploy it with `scripts/deploy-caddy-turn443.ps1` (elevated) — it backs up the binary and config,
swaps, restarts, then verifies each web host still serves its own cert on 443, that SNI=turn reaches
the relay, that 5349 still answers, and that arcade `/healthz` is 200. Any failure auto-rolls-back.
`-Rollback` undoes it later.
- Known trade-offs: the HTTP app sees `127.0.0.1` as the client IP (nothing depends on client IP
  today — the old `remote_ip` books redirect was removed), and HTTP/3 no longer runs on UDP 443
  (layer4 is TCP-only), so browsers use HTTP/2 over TCP.
- 5349 stays open and listed as a second ICE url, so this is safe to roll back.

## Pieces

| Piece | Path |
|---|---|
| Relay server (native Go, pion/turn) | `docker/arcade/turn/main.go` (+ `go.mod`) |
| Runner (restart loop) | `scripts/run-arcade-turn.ps1` |
| Task registration | `scripts/register-arcade-turn-task.ps1` |
| Credential minter (site side) | `src/MovieTheater.Core/ArcadeTurnCredential.cs` |
| Site config | `MovieTheaterConfiguration.ArcadeTurnUrls` / `ArcadeTurnSecret` / `ArcadeTurnCredentialTtlSeconds` |

## Security model (both are load-bearing — a default TURN install is an open internet proxy)

1. **Ephemeral auth.** The **site** mints a credential per join with the coturn/REST scheme:
   `username = "<expiryUnix>:<userId>"`, `password = base64(HMAC-SHA1(secret, username))`. The relay
   recomputes the same HMAC from the shared secret and rejects once the expiry passes. Nothing
   per-credential is stored. Only password-verified `StreamingUser` sessions ever receive one.
2. **Peer allowlist.** TURN permissions are IP-scoped, so the relay permits relaying **only** to the
   worker/Ziggy addresses (`-allowed-peers`, default `192.168.68.69,98.15.249.217`) and denies
   everything else — otherwise a credential holder could relay into the LAN.
   - **Residual risk (accepted):** TURN permissions can't be scoped to a *port*, so allowing
     `192.168.68.69` technically lets a credentialed user relay UDP to any UDP port on Ziggy. Blast
     radius is limited to authenticated arcade users reaching Ziggy's own UDP ports; the coordinator
     (`:8000`) is TCP so it's not reachable this way. Acceptable for a home server; revisit if the trust
     model changes.

## Go-live runbook (Ziggy)

Everything below is Ziggy-side. The **site code is already done** — with the config keys unset the
site stays STUN-only; setting them turns the feature on.

### 1. Build the relay
```powershell
cd F:\Work\MovieTheater\docker\arcade\turn
go build -o arcade-turn.exe .
New-Item -ItemType Directory -Force D:\ArcadeStorage\turn | Out-Null
Copy-Item arcade-turn.exe D:\ArcadeStorage\turn\
```

### 2. Shared secret (same value on both sides)
Generate a fresh 32-byte base64 secret. **Never commit the real value** — it lives only in
`secret.txt` on Ziggy and in the site's `ArcadeTurnSecret` (the prod appsettings k8s secret):
```powershell
$bytes = New-Object byte[] 32
[System.Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
$secret = [Convert]::ToBase64String($bytes)
Set-Content -NoNewline D:\ArcadeStorage\turn\secret.txt $secret
```
The **same** value goes in the site's `ArcadeTurnSecret` (step 5).

### 3. DNS + TLS cert for the turn hostname
- **Public AAAA record** `turn.carpouzis.com → 2607:2040:110:19ff:1ec2:1284:b022:1876` (GoDaddy),
  **plus an A record → the VPS** (docs/site-ipv4-door.md) since the house went behind CGNAT on
  2026-09-01. The AAAA is the direct door; the A is the VPS's blind TCP forward (443 **and** 5349
  both ride it), which is what restored the relay for IPv4-only public wifi — exactly the
  networks a relay exists for. The AAAA is kept current by the DDNS task
  (`scripts/update-godaddy-aaaa.ps1`).
  The Deco needs an IPv6 firewall rule for **TCP 5349** to that address, and the address is the
  SLAAC *Public* one — never the rotating Temporary address the router's device picker offers. turns needs a publicly-trusted
  cert, and the browser validates the hostname.
- **Do NOT add an AdGuard split-horizon rewrite for `turn.carpouzis.com`.** Guest/remote clients must
  resolve it to the *public* IP; the whole point is to reach Ziggy the WAN way.
- **Cert via Caddy** (it already does LE for the other hostnames — `C:\caddy`, run as the `Caddy`
  NSSM service under **LocalSystem**). This Caddy has **no DNS-provider plugin**, so it uses the same
  default HTTP-01/TLS-ALPN-01 challenge as the other blocks — do **not** add a `tls { dns … }` stub
  (it would fail to load). Just add a plain block whose only job is to make Caddy manage the cert:
  ```
  turn.carpouzis.com {
      respond 404
  }
  ```
  Then `caddy validate` + `caddy reload` (graceful, no downtime for the other sites). Caddy issues the
  cert immediately since :80/:443 already hairpin to Ziggy for the sibling hostnames.
- **Getting the PEMs to the relay.** Because Caddy runs as LocalSystem, its store
  (`C:\Windows\System32\config\systemprofile\AppData\Roaming\Caddy\certificates\…\turn.carpouzis.com\`)
  is **not readable** by the interactive user the TURN task runs as. `scripts/sync-arcade-turn-cert.ps1`
  copies the issued `.crt`/`.key` into `D:\ArcadeStorage\turn\turn.crt` / `turn.key`, and restarts the
  relay task when the cert actually changes. **It must run elevated** (to read the SYSTEM store).
  - **Once now** (elevated PowerShell):
    ```powershell
    powershell.exe -ExecutionPolicy Bypass -File F:\Work\MovieTheater\scripts\sync-arcade-turn-cert.ps1
    ```
  - **Copy-on-renew** — register it as a daily SYSTEM task so a renewed cert (Caddy renews ~30 days
    before expiry) is redeployed automatically (elevated, one time):
    ```powershell
    $a = New-ScheduledTaskAction -Execute "powershell.exe" `
         -Argument "-NonInteractive -ExecutionPolicy Bypass -File F:\Work\MovieTheater\scripts\sync-arcade-turn-cert.ps1"
    $t = New-ScheduledTaskTrigger -Daily -At 3:30am
    $p = New-ScheduledTaskPrincipal -UserId "SYSTEM" -LogonType ServiceAccount -RunLevel Highest
    Register-ScheduledTask -TaskName "MovieTheater - Arcade TURN cert sync" -Action $a -Trigger $t -Principal $p -Force
    ```
  (pion/turn reads the files once at boot; that's why the sync restarts the relay task on change.)

### 4. Deco WAN port-forward
**Deco app → More → Advanced → NAT Forwarding → Port Forwarding**: add **TCP 5349 → 192.168.68.69**.
(TCP 443 is already forwarded for the web hosts — that is the port the 443 SNI demux above rides, so
nothing extra is needed for it.)
While you're there, set an **Address Reservation** for Ziggy at `192.168.68.69` (Advanced → Address
Reservation) so the LAN IP the relay/forwards depend on can't drift.

### 5. Site config (the shared prod/dev appsettings + the prod secret)
Add to the arcade config block:
```json
"ArcadeTurnUrls": [ "turns:turn.carpouzis.com:443?transport=tcp", "turns:turn.carpouzis.com:5349?transport=tcp" ],
"ArcadeTurnSecret": "J73r8022wh/jPexI32+DX9kOdnOJcaC5jpoI/aERRY8=",
"ArcadeTurnCredentialTtlSeconds": 43200
```
- Local/Ziggy dev: `src/MovieTheater/appsettings.Development.json`.
- Prod: the `MOVIETHEATER_APPSETTINGS_JSON` GitHub secret (see the `movietheater-secret` skill).

### 6. Start the task
```powershell
powershell.exe -ExecutionPolicy Bypass -File F:\Work\MovieTheater\scripts\register-arcade-turn-task.ps1
Get-Content D:\ArcadeStorage\logs\turn.log -Tail 10   # expect: "turns listening on :5349 ..."
```

## Verify

End-to-end verification needs the **isolated device** (the relay is skipped whenever a direct path
exists, so you can't prove it from Ziggy or a normal LAN client):

1. Put the phone back on the **guest SSID** and launch a game — it should now connect instead of hanging.
2. In the relay log (`D:\ArcadeStorage\logs\turn.log`) you should see auth succeed and **no**
   `perm: deny` lines for the worker IP. `perm: deny` for other IPs is the allowlist doing its job.
3. Direct networks (main SSID, cellular) should behave exactly as before — confirm one still connects
   directly (they will not appear in the relay log at all).

## Rollback

Clear `ArcadeTurnUrls` (or `ArcadeTurnSecret`) in the site config → instant return to STUN-only, no
redeploy of the relay needed. Stop the relay task if desired.
