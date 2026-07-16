# ICE priority separation — LAN must win, WAN must work (2026-07-15)

## The problem (measured, Eric's webrtc-internals)

The worker advertises BOTH arcade IPs as **host** candidates with **identical priority** (`2130706431`
on both, confirmed in a live session): `pkg/network/webrtc/factory.go:160` —
`SetNAT1To1IPs(ips, ICECandidateTypeHost)` over the comma list from `.env ZIGGY_PUBLIC_IP`.
ICE pair priority therefore cannot distinguish them, and "ORDER IS LOAD-BEARING — LAN FIRST"
(the .env comment) has never actually influenced selection. Result: a LAN browser can select the
WAN pair — media hairpins through the router's WAN edge — which throttles the GCC estimate to
~3 Mbps and starves the stream (measured: 24 fps @ 3 Mbps hairpinned vs 59.8 fps @ 5.8+ Mbps on the
LAN pair, same machine, same minute). This was the original 2026-07-06 "constant audio concealment"
bug re-manifesting; the 07-06 fix (advertise both, LAN first) treated the symptom, not the priority.

**Interim state (live now): `.env ZIGGY_PUBLIC_IP=192.168.68.69` — LAN-only. Remote friends are
BROKEN until this plan ships.** Rollback of the interim = restore
`192.168.68.69,98.15.249.217` (backup at `docker/arcade/.env.bak-hairpin-test`).

## Implementation status (2026-07-15) — DEPLOYED to the retro pool; remote leg still Eric's to verify

**LIVE on both retro workers (8446, 8447) as of ~22:54 2026-07-15.** Built `worker.exe` from fork
7af7f03 (+ the live nanoarch instrumentation), swapped in (rollback kept as
`bin\worker.pre-srflx-20260715.exe`), restored `.env` to `192.168.68.69,98.15.249.217`, restarted both
retro runners (kill runner → `.stop` sentinel → swap → `Start-ScheduledTask`), watchdog re-enabled.
Both workers log the split and dropped STUN, and are registered + free:
```
ICE map: 192.168.68.69 is a local interface — riding natural host candidates (not mapped)
ICE map: [98.15.249.217] advertised as srflx (lower priority than host — chosen only when host is unreachable)
ICE map: srflx NAT1To1 active — dropping worker STUN servers (WAN advertised directly)
```
**Verified same-host** (`test-roms/arcade-diag`, Super Mario World): room reached Playing, 60 fps, 0
freezes, 0 concealed audio, rttMs≈0 — i.e. pion's **natural host gathering produces a working host
candidate** (the change's key assumption; a same-host room can only connect via that candidate).
**STILL OPEN — Eric only:** the genuinely-remote leg (a browser on cellular/external network selecting
the WAN **srflx** pair). Cannot be run from Ziggy. Do not consider the WAN half proven until it is.

**Capture worker (8448) — now ALSO on srflx (~23:23 2026-07-15).** The capture `worker.exe` turned out
to be a byte-identical copy of the general `./cmd/worker` build (its capture behavior is the patched
`libgstd3d12.dll` + config, NOT a build tag), so the same new binary was copied to
`D:\ArcadeStorage\worker-capture\bin\worker.exe` (rollback `worker.pre-srflx-20260715.exe`) and its
runner restarted to re-read `.env`. Log confirms `capture mod enabled (heavy browser lane)`,
`libgstd3d12 is the patched build`, and the srflx split — so the capture DLL/config survive the swap.
The old (7/14, pre-stop-file) capture binary ignored the `.stop` sentinel and was force-killed; it did
not zombie (no WEDGED flag) and the new binary honors the sentinel going forward. The whole pool
(8446/8447/8448) is now on the fix.

<details><summary>Original pre-deploy notes (kept for the record)</summary>

**Done (committed, compile-proven, NOT yet live):**
- Fork change `movietheater-fork` **7af7f03** (`pkg/network/webrtc/factory.go`): splits `IceIpMap`
  by role — a list IP that matches a local interface is dropped from the mapping (natural host
  gathering covers it); every non-local IP is advertised via `SetNAT1To1IPs(…, Srflx)`. Added
  `isLocalInterfaceIP` (walks `net.InterfaceAddrs`). ~83 lines.
- `docker/arcade/patches/fork.patch` regenerated + **compile-proven** on a pristine `13852a7` tree
  (`scripts/export-arcade-fork.ps1`: "builds from the patch alone"). Repo commit **b4f16fc**.

**Two findings that refine the plan:**
1. **The pion STUN caveat is real but not fatal here — I dropped worker STUN anyway.** The
   `SetNAT1To1IPs` doc warns "you cannot give STUN server URL at the same time. It will result in an
   error otherwise," but in the pinned **webrtc v4.2.16 / ice v4.2.7** the legacy srflx path became an
   *append-mode address-rewrite rule* that coexists with STUN — no error (traced the gatherer, no
   guard). Stock config still ships `stun:stun.l.google.com:19302`, though, and leaving it would gather
   a REDUNDANT srflx candidate (same port under this endpoint-independent NAT, but a wrong/unreachable
   one under a symmetric NAT). Since the worker now hardcodes its WAN, STUN self-discovery is pointless
   → the factory **drops worker STUN whenever it maps a srflx IP**. The browser keeps its own STUN via
   the site's `ArcadeStunServers`. srflx emission does NOT depend on STUN (related addr = the mux host
   addr), so this is safe.
2. **There are THREE GL workers now** ("Arcade GL Worker", " 2", " 3" — 8446/8447/8448), not two. The
   restart step must cover all three runners.

**Not done — GATED, do NOT do blind:** build+deploy `worker.exe`, restore `.env` to both IPs, restart
the worker tasks, and verify. Blocked right now because **a live Stuntman session was on worker 9000
when this landed** (the round-3 instrumented run) — restarting would cut the player and lose the
instrumentation. The fork checkout also holds that instrumentation as **uncommitted** `nanoarch.*`
WIP, so a live rebuild would bake it in — decide that separately before building. Runbook below.

### Deploy runbook (run when no room is live — `curl -s localhost:8000/status`)
1. Decide the nanoarch instrumentation: commit it to the fork first if it should ship, or `git stash`
   it if the deploy should be clean. `worker.exe` is built from the working tree, not the patch.
2. Build (UCRT64): `PATH=D:\msys64\ucrt64\bin`, `CGO_ENABLED=1`, `GOPATH=D:\Arcade\build\go`,
   `GOCACHE=D:\Arcade\build\gocache`, `go build -o bin/worker.next.exe ./cmd/worker` in
   `D:\Arcade\build\cloud-game-gl`. Keep the live `bin/worker.exe` as `worker.pre-srflx.exe`.
3. `.env`: set `ZIGGY_PUBLIC_IP=192.168.68.69,98.15.249.217` AND rewrite the comment — order is NO
   LONGER load-bearing (host vs srflx type preference does the work); "do NOT re-add the WAN as host."
4. Restart per the plan's step 4 (kill runner powershells, drop `.stop` sentinels, `Start-ScheduledTask`)
   for **all three** GL workers. Confirm each logs `ICE map: … advertised as srflx` + the local-interface
   line for 192.168.68.69, and NOT "NAT mapping is active" (the old host-mode message).
5. Verify per the matrix — **including the remote/cellular leg, which is Eric's to run.**

</details>

## The fix — advertise the WAN as srflx, not host

ICE type preference is the priority mechanism that actually works: host = 126, srflx = 100, baked
into candidate priority (RFC 8445 §5.1.2), which dominates pair priority. A browser that can reach
the LAN host candidate will always prefer host-host over host-srflx; a remote browser fails the LAN
checks fast and lands on the srflx pair. This is exactly the semantics we want, with zero custom
priority math.

`SetNAT1To1IPs` takes ONE candidate type for the whole list — so split the roles:

- **LAN (192.168.68.69) is Ziggy's real interface IP** — it needs no mapping at all: pion's natural
  host gathering already produces it. (Today's Host-mode mapping *replaces* the natural host IPs,
  which is why the list had to carry the LAN IP explicitly.)
- **WAN (98.15.249.217)** becomes the only mapped IP, advertised as **srflx**.

### Implementation (fork `movietheater-fork`, ~10 lines)

In `pkg/network/webrtc/factory.go` (replacing the current block at ~152–160):

1. Split `conf.IceIpMap` as today.
2. Partition the list: IPs that match a local interface address → DROP from the mapping (natural
   host candidates cover them); the rest → `SetNAT1To1IPs(rest, ICECandidateTypeSrflx)`.
   Auto-detection keeps the `.env` format unchanged and is robust to the list being reordered.
3. Comment the semantics at the .env and factory: "local IPs ride natural host candidates;
   non-local IPs are advertised as srflx = lower ICE priority = chosen only when host is
   unreachable. Do NOT re-add the public IP as host: equal-priority host candidates let LAN
   browsers hairpin (2026-07-15)."

Note: verify against the pinned pion version that srflx-mode NAT1To1 emits the srflx candidate
with the mux port and a sane related address; pion documents this mode for exactly this use.

### Rollout

1. Fork branch commit → `scripts/export-arcade-fork.ps1` → commit `fork.patch` → push (the
   fork-discipline from [[arcade-cloudretro-fork]]).
2. Build worker (msys2 UCRT64 `go build ./cmd/worker`), stage as `worker.exe` via rename-swap.
3. Restore `.env` to `ZIGGY_PUBLIC_IP=192.168.68.69,98.15.249.217`.
4. Restart the GL worker tasks — ⚠ the runners CACHE IceIpMap at task start, and
   `Stop-ScheduledTask` does NOT kill the process tree (proven 2026-07-15): kill the runner
   powershell processes explicitly (filter `run-arcade-glworker\.ps1` AND exclude `$PID` — the
   filter string itself matched and killed my own session once), drop `.stop` sentinels for the
   orphaned workers, then `Start-ScheduledTask`.
5. Same change consideration for the capture worker's runner if it advertises ICE the same way.

### Verification matrix

| Client | Expected pair | How to check |
|---|---|---|
| LAN browser (Eric) | host(192.168.68.69) ↔ host | webrtc-internals candidate-pair; video ≥ 6 Mbps, ~60 fps delivered |
| Ziggy-local harness | host ↔ host | arcade-diag.mjs stats (concealed static, fps 60) |
| Remote (phone on cellular, or Neil) | srflx(98.15.249.217) ↔ * | room reaches Playing; webrtc-internals on the remote shows remote candidate = WAN srflx |

Remote verification is the one that needs a genuinely external network — a phone on cellular is
sufficient. Do not declare this shipped without it: the whole point is not to trade LAN quality
for remote breakage.

### Rollback

`.env` back to LAN-only (the current interim) — one line + runner restart. The fork change is
inert with a single-IP list (nothing non-local to map → no srflx candidates).

## Context: what this closes and what it doesn't

Closes: the client-side starvation family — 24 fps delivery, ABR throttling, and the transport
share of audio chop for LAN players, permanently and without config discipline.

Does NOT close: the **level-start audio sag** (Eric: chop early in each level, every restart —
menus clean). That is server-side: ~10–15 s at 44–55 ticks/s with forgiven audio after each level
load, surviving priority/affinity/clamp/renderer/MTVU fixes. The round-3 instrumented core
(wall/ee_cpu/gs_cpu channels, live on both workers) attributes it on the next real session — see
docs/arcade-stuntman-plan.md and the arcade-stuntman-audio-skip memory.
