# Arcade — Go-Live Runbook (LAN dev → theater.carpouzis.com)

Take the arcade from the current localhost/LAN setup to **playable by friends over the internet on
the real site**. Do the phases IN ORDER — Phase 0 is a genuine go/no-go, and everything after it is
wasted work if Phase 0 fails.

**Topology reminder:** the site (control plane) runs in the k8s pod behind `theater.carpouzis.com`.
The emulator stack + the signaling **gateway** run on **Ziggy**, and the gateway is exposed at
`arcade.carpouzis.com`. Media (video/audio/input) is **WebRTC/UDP straight from the browser to
Ziggy** — it never touches the pod, which is why the router must forward UDP to Ziggy.

**Already true (no action):** all 11,585 games are catalogued in the SHARED prod DB, so
`theater.carpouzis.com/arcade` will list them the moment the site is configured. The JIT ROM cache,
the manifest, the worker cores, and `config.yaml` are all in place on Ziggy.

**The one shared secret** (generate once, use in TWO places — must be byte-identical):
`arcade-prod-c7442ab1bb782ecec1a8d76838f979ebc8b87517310f958d27e6acc2fcd6700e`
Store it in your password manager. It goes in (a) the gateway's prod config on Ziggy and (b) the
site's prod secret. A mismatch = every join 403s.

---

## Phase 0 — Does inbound UDP reach Ziggy? (GO / NO-GO — do this FIRST)

CGNAT-for-UDP is the one thing that kills this project dead. Prove UDP arrives from the outside
BEFORE any other work.

1. **Router:** forward **UDP 8443, 8444, 8445 → Ziggy's LAN IP (192.168.68.69)**.
2. **Windows Defender:** inbound allow rule for UDP 8443–8445 on Ziggy.
3. **Test from OUTSIDE your network** (phone on cell data, NOT wifi):
   - On Ziggy, listen: `ncat -u -l 8443` (or PowerShell UDP listener).
   - From the phone (a UDP test app, or `ncat -u <your-public-ip> 8443`), send a packet.
   - **Packet arrives on Ziggy → PASS.** Nothing arrives → your ISP is likely CGNAT'ing UDP.
     STOP: internet play needs a public UDP path; the fix is a TURN relay (not built) or a real
     public IP from your ISP. LAN play still works meanwhile.

Find your public IP: `curl -s https://api.ipify.org`. If it's not stable, set up DDNS and use the
hostname everywhere `<public-ip>` appears below.

---

## Phase 1 — DNS + Caddy (the HTTPS ingress for signaling)

1. **DNS:** add `CNAME arcade.carpouzis.com → books.carpouzis.com` (same target as your other Ziggy
   subdomains). Wait for it to resolve: `nslookup arcade.carpouzis.com`.
2. **Caddy (on Ziggy):** append the block from `docker/arcade/Caddyfile.arcade.snippet` to Ziggy's
   Caddyfile:
   ```
   arcade.carpouzis.com {
       reverse_proxy localhost:2303
   }
   ```
   Reload Caddy (`caddy reload` / restart the service). Caddy auto-provisions the TLS cert.
3. *Verify after Phase 3:* `curl https://arcade.carpouzis.com/healthz` → `ok`.

---

## Phase 2 — Productionize the gateway on Ziggy

Today the gateway runs as a dev `dotnet run` from the repo (localhost origin, dev secret, no
supervisor). For prod it needs prod settings + to survive reboots — mirror how **StreamGateway**
runs (`C:\StreamGateway\app` with its own `appsettings.Production.json`).

1. **Config** — create `appsettings.Production.json` for the gateway (gitignored) with:
   ```json
   {
     "CoordinatorBaseUrl": "http://127.0.0.1:8000",
     "SiteOrigin": "https://theater.carpouzis.com",
     "ArcadeTokenSecret": "arcade-prod-c7442ab1bb782ecec1a8d76838f979ebc8b87517310f958d27e6acc2fcd6700e",
     "RomCache": {
       "ManifestPath": "F:/Work/MovieTheater/docker/arcade/arcade-romcache.json",
       "RomsDir": "D:/ArcadeStorage/roms",
       "MaxBytes": 64424509440
     }
   }
   ```
   (`SiteOrigin` is the SPA origin — `theater.carpouzis.com`, NOT `arcade.*`.)
2. **Run it as Production, supervised** so it restarts on crash/reboot. Options, cleanest first:
   - Publish to a stable dir like StreamGateway (`dotnet publish -c Release` → `C:\ArcadeGateway\app`,
     drop the `appsettings.Production.json` there), run with `ASPNETCORE_ENVIRONMENT=Production`,
     and register it as a Windows service or a logon Scheduled Task (same pattern as the WSL
     keepalive task `scripts/register-arcade-wsl-task.ps1`).
   - Quick interim: `$env:ASPNETCORE_ENVIRONMENT="Production"; dotnet run --project
     src/MovieTheater.ArcadeGateway` in a persistent session — but it won't survive a reboot.
3. **Verify:** `curl https://arcade.carpouzis.com/healthz` → `ok`, and the gateway log prints
   `Arcade JIT ROM cache enabled: 11567 game(s)`.

> ⚠ Going prod switches the gateway's secret to the prod one → the **dev** site (localhost) will
> then 403 on arcade (secret mismatch). That's expected; dev and prod can't share the one gateway
> with different secrets at once.

---

## Phase 3 — Configure the site (the prod secret)

Follow the **movietheater-secret** skill rules exactly (one malformed char crash-loops prod).
`MOVIETHEATER_APPSETTINGS_JSON` IS the whole `appsettings.Production.json`.

1. Take the **current** secret value (the whole blob — never paste just a few keys).
2. **Add** these five arcade keys (keep every existing key):
   ```
   "ArcadeGatewayBaseUrl":"https://arcade.carpouzis.com",
   "ArcadeTokenSecret":"arcade-prod-c7442ab1bb782ecec1a8d76838f979ebc8b87517310f958d27e6acc2fcd6700e",
   "ArcadeMaxConcurrentRooms":3,
   "ArcadeJoinTokenTtlSeconds":300,
   "ArcadeStunServers":"stun:stun.l.google.com:19302"
   ```
   `ArcadeTokenSecret` MUST equal the gateway's from Phase 2. `MaxConcurrentRooms` = worker count (3).
3. Keep it **one minified line, no duplicate keys**. Validate locally first (from Ziggy, which can
   reach the DB):
   ```powershell
   $cfg = Get-Content cand.json -Raw | ConvertFrom-Json   # throws if invalid JSON
   $c = New-Object System.Data.SqlClient.SqlConnection($cfg.DbConnectionString); $c.Open(); 'DB OK'; $c.Close()
   ```
4. Save it as the GitHub secret `MOVIETHEATER_APPSETTINGS_JSON`.

---

## Phase 4 — Point ICE at the public IP + restart workers

The workers advertise `ZIGGY_PUBLIC_IP` as their WebRTC candidate. For internet play it must be your
**public IP / DDNS**, not the LAN `192.168.68.69`.

1. Edit `docker/arcade/.env` (on Ziggy): `ZIGGY_PUBLIC_IP=<public-ip-or-ddns>` and
   `SITE_ORIGIN=https://theater.carpouzis.com`.
2. Restart the stack: `wsl -d Ubuntu-24.04 -u root -- docker compose -f
   docker-compose.gpu.yml up -d` (from `docker/arcade`).
3. **Same-host/LAN caveat:** ICE advertises ONE IP. With a public IP set, a browser *on your LAN*
   reaches the workers only if your router hairpins (LAN→public-IP→back inside). Most do; if LAN
   play breaks after this, that's why. STUN (`ArcadeStunServers`) helps external peers discover the
   mapping. Internet peers are the target here.

---

## Phase 5 — Deploy + verify

Updating the secret does **nothing** until a new image is built and rolled.

1. **Deploy:** push a commit to `master` → GH Actions builds the API image (bakes the secret) and
   deploys to MicroK8s. The Ziggy arcade stack is untouched by the pod roll.
2. Confirm the CI `validate-config` job prints `VALID JSON, no duplicate keys`, and the site still
   serves: `curl -s -o /dev/null -w "%{http_code}\n" https://theater.carpouzis.com/API/GetGenres`
   → `200` (a crash-loop from a bad secret would keep the old pod and this would look unchanged).
3. **End-to-end:** log into `theater.carpouzis.com`, open `/arcade` (games list — 11,585), start a
   room (first play of a JIT game shows "Connecting…" ~10-40 s while it extracts), confirm it
   streams and plays.
4. **The real test:** have a friend (or your phone on cell data) join from OUTSIDE your network and
   play. If the video connects, Phase 0 was honest and you're live.

---

## After go-live

- **Saves are still room-scoped** (the durable user-save store S1 is built but its live wiring is
  S2 — see `docs/arcade-saves-plan.md`). Tell friends saves don't persist across rooms yet.
- **Concurrency:** 3 simultaneous rooms (3 workers); a 4th gets "arcade is full."
- **Age gate:** every game is `RatingCeiling 0` (visible to all). Raise ceilings on mature titles by
  updating `ArcadeGame.RatingCeiling` if you have age-restricted users.
- **JIT cache:** first play of any game extracts it (~1 s for a 2D zip, ~10-40 s for a PSX disc off
  L:); popular games stay warm under the 60 GB cap.
- **Rollback:** to take arcade offline, remove the arcade keys from the prod secret + redeploy (the
  endpoints return 501 "not set up"); the rest of the site is unaffected.

## What I can prep for you vs. what's yours
- **Yours (infra/secrets):** router UDP forward + the CGNAT test, DNS CNAME, Caddy reload, updating
  the GitHub secret, the deploy push, editing `.env` with your public IP.
- **I can do on request:** write the gateway `appsettings.Production.json`, publish + register the
  gateway service, prepare the exact merged secret JSON (you supply the current blob), and run the
  end-to-end test once the ingress is up.
