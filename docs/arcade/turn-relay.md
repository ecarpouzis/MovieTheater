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
- **Public A record** `turn.carpouzis.com → 98.15.249.217` (GoDaddy). turns needs a publicly-trusted
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
While you're there, set an **Address Reservation** for Ziggy at `192.168.68.69` (Advanced → Address
Reservation) so the LAN IP the relay/forwards depend on can't drift.

### 5. Site config (the shared prod/dev appsettings + the prod secret)
Add to the arcade config block:
```json
"ArcadeTurnUrls": [ "turns:turn.carpouzis.com:5349?transport=tcp" ],
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
