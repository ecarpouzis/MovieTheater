# Arcade Plan — Retro Games in the Browser, Together

**Status:** Proposed (2026-07-01; wire-level detail added 2026-07-02 from a CloudRetro source dive —
master branch, commits current as of 2026-07-01). Prerequisite: none in the DB — this is a new
vertical. Phase 0 is a hardware/network spike on Ziggy and gates everything else.
**Scope:** A new `/arcade` page on theater.carpouzis.com: browse a curated ROM library, open a game
room, send friends a link, and play together in the browser — Mario Kart 64, Snowboard Kids,
four-player SNES. Emulation runs on Ziggy; the site stays the product. Movies/TV/boardgames
unaffected.

> **How to read this doc:** §1–§14 are the plan. Appendices A–E are the implementation-grade
> reference — CloudRetro's actual wire protocol, config keys, the gateway spec, the exact repo
> patterns to mirror (with file paths), and the Windows networking runbook. Facts in the
> appendices were extracted verbatim from CloudRetro master source unless flagged **[unverified]**.
> When implementing, trust the appendices over memory or intuition — several details here are
> counter-intuitive (see the callout boxes).

---

## 1. Goals and non-goals

Goals, in priority order (from Eric):

1. **Online multiplayer is the point** — send a friend a link and race. Not "netplay for
   enthusiasts": no client installs, no port forwarding on the friend's side, no desync
   troubleshooting. A browser tab and a gamepad (or keyboard).
2. **System breadth** — the more consoles the better. NES, SNES, Genesis, GB/GBC/GBA, N64, PS1,
   arcade (FBNeo) as the launch set; more later.
3. **The site stays the product** — same login, same look. Arcade is a section beside Movies/TV,
   gated to **password-verified sessions** (the existing `StreamingUser` policy) and to each user's
   age restriction. Nobody ever sees the emulator stack.
4. **Ziggy does the heavy lifting, like Jellyfin** — the cluster pod stays thin (control plane
   only); game video/input never traverses the cluster.

Non-goals (v1): mobile/touch controls, spectator-only mode, per-user cloud save sync,
tournaments/leaderboards, systems needing GPU-class emulation (GameCube/PS2/Dreamcast), voice chat
(friends already use Discord). All possible later; none constrain this design.

## 2. Decision: server-side emulation streamed over WebRTC (CloudRetro)

Two ways to put emulators in a browser:

| | A. Server-side ("cloud gaming") | B. Client-side (WASM, e.g. EmulatorJS) |
|---|---|---|
| Where the emulator runs | One instance per room on Ziggy | Each player's browser |
| Multiplayer model | Everyone's inputs go to the same instance; video streamed out. **Desync is impossible** | Netplay: N instances kept in lockstep over the network |
| Multiplayer status (mid-2026) | Works today (CloudRetro's core feature) | **EmulatorJS netplay still doesn't work** — FAQ says "not currently supported"; RetroArch's web build has had an open netplay request since 2018 |
| N64 quality | Native x86 dynarec core on Ziggy — full speed | WASM N64 is demo-quality: audio underruns, ~30fps in heavy titles, per-game glitches |
| Cost | Ziggy CPU per room + upload bandwidth | Free for the server |
| ROM exposure | ROMs never leave Ziggy | ROM is downloaded to every player's browser |

**Recommendation: architecture A, using [CloudRetro](https://github.com/giongto35/cloud-game) as
the engine — the same role Jellyfin plays for video.** It is purpose-built for exactly this: a Go
service (Pion WebRTC) that runs libretro cores server-side, gives each *room* one emulator
instance, binds each joining player to a controller port, and streams VP8/Opus to every member.
Its README use case is literally "play Pokémon together." Actively developed (commits July 2026),
Docker-deployable, and its default core set already covers NES, SNES (with multitap — 5 players),
GBA/GBC, **N64 (mupen64plus-next)**, PS1 (PCSX-ReARMed), DOS, and FBNeo arcade. Genesis is a small
config addition (cores auto-download from the libretro buildbot, `repo.sync: true`).

Why not the shinier alternatives: **neko** (shared-desktop WebRTC) has no gamepad support at all;
**Sunshine/Moonlight** and **Wolf** are one-controlling-client-per-session by design and need a
native client app; **Nestri** is experimental and wants an Nvidia GPU; **Piepacker/jam.gg** — the
commercial product that proved this exact concept — is dead with no usable open-source remnant.
EmulatorJS is lovely for *solo* play and stays on the bench as a hedge (§13).

**Hedge, same shape as the Jellyfin one:** the backend talks to the emulator stack through one
small seam (`IArcadeHost`: resolve game key → launch descriptor; account for live rooms; kill a
room). CloudRetro is the v1 implementation. If it stagnates (it cut its last tagged release in
2021 — we pin a commit and build our own image anyway), the room model, DB schema, page, and auth
don't change; only the host adapter and the in-browser client shim do.

Latency reality: WebRTC streaming adds roughly 20–40 ms of capture/encode/decode on top of RTT, so
friends at 20–60 ms see ~60–120 ms motion-to-photon. That's the "slightly floaty but everyone has
fun" zone kart racers were commercially streamed at (Stadia, Piepacker); it also means **nobody is
"the host"** — every player has symmetric latency and there is never a desync. Competitive
time-trial purists will notice; Snowboard Kids nights will not.

> **⚠ Two source-dive facts that shape everything downstream — do not design against intuition:**
>
> 1. **CloudRetro has NO server-side room API.** The coordinator's entire HTTP surface is `/`
>    (its own web UI), `/ws` (browsers), `/wso` (workers). There is no REST endpoint to create,
>    list, or kill a room; room lifecycle is 100% driven by browser WebSocket messages
>    (Appendix A). Consequence: **our backend cannot create rooms.** The site mints its *own*
>    room code, the **creator's browser** creates the CloudRetro room (t=104 with empty
>    `room_id`) and reports the returned CloudRetro room id back to the site, which binds it to
>    the room record (§8). Any design where `ArcadeController` "calls CloudRetro to make a room"
>    is wrong and will dead-end.
> 2. **One worker = one concurrent room.** Each CloudRetro worker process has a single game slot;
>    the coordinator rejects a start on a busy worker with `ErrNoFreeSlots` (t=112). Concurrency
>    is scaled by running **N worker containers** against one coordinator (they self-register
>    over `/wso`). `ArcadeMaxConcurrentRooms` is therefore *descriptive* (it must equal the
>    number of deployed workers), not a knob the backend enforces alone — CloudRetro's t=112 is
>    the authoritative backstop and the client shim must surface it as "arcade is full."

## 3. Architecture

```
                       public internet
  friend ──HTTPS──► theater.carpouzis.com (ingress, unchanged)
                            │
                    MovieTheater API pod (thin — control plane only)
                    └── /API/Arcade/*   library, room records, seats, presence,
                                        age gate; mints signed join tokens (§3.1)
                            │  (nothing else — no game bytes, no WS, no calls to Ziggy at all in v1)
                            │
  friend ──WSS──► arcade.carpouzis.com (CNAME → books, Caddy on Ziggy)
                            │  /w/{token}?room_id=…   (WebSocket signaling)
                       ArcadeGateway on ZIGGY (Appendix C)
                       validate signed join token → rewrite path to /ws → forward
                            ▼ localhost:8000
                       CloudRetro coordinator ─── /wso ──► worker-1 :9000 ┐
                       (never exposed raw;                 worker-2 :9001 ├─ one room each
                        its / web UI stays private)        worker-N …    ┘
                            │                                   │ libretro cores + Xvfb (llvmpipe GL)
                            │                                   │ ROMs: read-only mount, Ziggy local disk
                            │                                   ▼
  friend ◄──WebRTC/UDP──► Ziggy :8443/udp  (video/audio + input DataChannels; singlePort)
```

- **Control plane** — the site owns the game catalog, room records, invites, seats, presence, and
  age gating. Note the asymmetry with Jellyfin: because CloudRetro has no server API (§2 box),
  **the site pod never talks to Ziggy for arcade in v1** — no tunnel-key control channel needed.
  The browser is the intermediary (§8 bind flow). If we later add server-driven room kill, it
  will be a small WS client in the gateway, not the pod (Appendix C §C4).
- **Signaling plane** — the browser's WebSocket to the CloudRetro coordinator. This is the one
  genuinely new kind of traffic in the stack (everything today is HTTP range/HLS + polling). It
  terminates on **Ziggy's Caddy → ArcadeGateway**, never the cluster ingress — so the cluster
  needs zero WebSocket config, mirroring how stream bytes already bypass the pod.
- **Media plane** — WebRTC flows directly browser ↔ Ziggy over one forwarded UDP port
  (`webrtc.singlePort` = 8443, `webrtc.iceIpMap` = Ziggy's public IP so ICE candidates advertise
  the right address instead of the Docker-internal one). Video VP8, audio Opus, inputs on WebRTC
  **DataChannels** (they never touch the WS after setup — Appendix A §A4). No TURN server in v1;
  add coturn later only if some friend's NAT proves hostile (§11 R4).

### 3.1 Authorization — signed join tokens, same trust template as streaming

CloudRetro has **no auth of any kind** (confirmed: zero auth/token keys in its entire config; the
only gate is a WS `Origin` check, `coordinator.origin.userWs`). Anyone who can reach the
coordinator can join any room and take over any controller. So the coordinator is never exposed
raw:

- The pod's `POST /API/Arcade/Room/{code}/Join` enforces `StreamingUser` + the game's age ceiling,
  then mints a short-lived HMAC capability token — `ArcadeCapabilityToken`, a clone of
  `StreamCapabilityToken` (`src/MovieTheater.Core/StreamCapabilityToken.cs`), payload
  `userId|gameId|roomCode|playerSlot|expiresUnixSeconds` (Appendix D §D1).
- The browser opens `wss://arcade.carpouzis.com/w/{token}?room_id=…&zone=…`. The **ArcadeGateway**
  (sibling of `MovieTheater.StreamGateway`) validates the token, rewrites the path to the bare
  `/ws` CloudRetro expects, preserves the query string, and forwards the upgraded connection to
  `localhost:8000`. Everything else 403s. It holds no state. (Full spec: Appendix C. The path
  rewrite is mandatory — the stock client hardcodes `/ws`, and the coordinator only serves WS
  there; a prefix-preserving proxy would 404.)
- Gating the signaling gates the media: SDP/ICE exchange happens only inside that authorized
  WebSocket (t=100/101 packets), so the open UDP port is useless without a valid token.
- Age gate: each game carries a rating ceiling on the same scale the TV channels use; rooms
  inherit the game's ceiling and are hidden from (and unjoinable by) users whose `AgeRestriction`
  is below it — checked in `Games`, `Rooms`, and `Join`, mirroring
  `ChannelController.cs` (list-time cache + per-call re-check).
- **Known soft spot, accepted at friends-scale:** seat assignment (controller port) is enforced by
  *our* client shim honoring the seat the site assigned, not by CloudRetro — any connected client
  can send t=108 (`ChangePlayer`) and grab a different port. All clients are our shim, and every
  connection already passed the password gate. If it ever matters, the gateway can be upgraded
  from a byte-pump to a packet-inspecting proxy that drops t=108 packets not matching the token's
  `playerSlot` (Appendix C §C4) — designed-for but not built in v1.

### 3.2 Connectivity — one new Caddy block, one UDP forward

Reuses the Ziggy trust template wholesale: new CNAME `arcade.carpouzis.com → books.carpouzis.com`,
new Caddy site block proxying to the ArcadeGateway on localhost (TLS is Caddy's job; the gateway
binds plain HTTP; Caddy proxies WebSocket upgrades natively with no extra config). The single
genuinely new network requirement is the **UDP 8443 forward on the home router** for WebRTC media.

The Windows-host networking picture (researched, details in Appendix E): **Docker Desktop
publishes UDP ports on the Windows host directly** — the repo compose's `8443:8443/udp` mapping
is reachable from outside with just a Windows Defender firewall allow rule; no `netsh portproxy`
(TCP-only — cannot forward UDP at all) and no WSL2 mirrored-networking mode needed. Mirrored mode
(`.wslconfig`, Win11 22H2+, Hyper-V firewall rules) is the fallback only if we ever run the worker
natively in a WSL2 distro instead of Docker Desktop. What remains genuinely unknown is whether
Docker Desktop's userspace port proxy handles sustained WebRTC media rates cleanly — nobody has
benchmarked it publicly — which is exactly what Phase 0 measures.

### 3.3 Data plane details

- CloudRetro runs on Ziggy in Docker: **one coordinator + N workers + Xvfb**, built from a
  **pinned commit** (the project doesn't cut releases; we own the image like we own the API/UI
  images). The repo's own compose runs coordinator and worker from one image with different
  commands — we extend that to N worker services (compose draft: Appendix B §B3). ROM directory
  mounted read-only; save directory (`emulator.storage`, default `{user}/.cr/save`) read-write
  and included in Ziggy's backups.
- Encoding is software VP8 (GStreamer `vpxenc`; `encoder.video.codec: vp8`, `keyframeInterval:
  300`) at CloudRetro's retro-resolution render presets — cheap. The stock image has **no x264**
  (GStreamer built `-Dgpl=disabled`); H.264 exists only as a commented-out config example. VP8 is
  fine for every target browser here; don't rebuild for H.264 in v1.
- N64 renders via mesa-llvmpipe software GL in the container (`DISPLAY=:99` Xvfb,
  `MESA_GL_VERSION_OVERRIDE=4.5`) — budget 2–4 CPU threads per N64 room; 2D cores are trivial.
  Worker count (= room cap) is sized to Phase 0's measured headroom, N64-rooms-worst-case.
- Games are matched to cores **by file extension** against each core's `roms` list, within
  `library.basePath` (default `assets/games`); a core can pin a subfolder via its `folder` key.
  The site catalog's launch key is the game's **title as CloudRetro's library scan derives it**
  (filename-based) — stored per game as `CloudRetroGameKey` so a filename change on Ziggy can't
  silently orphan catalog rows (§5).

## 4. Ziggy setup checklist

- [ ] Docker Desktop on Ziggy (WSL2 backend). Confirm `com.docker.backend` publishes on
      `0.0.0.0`, not loopback (Appendix E §E2).
- [ ] CloudRetro built from **pinned commit** into a registry image we own; compose stack
      (Appendix B §B3) with restart policy `unless-stopped` so it survives reboots, like Caddy
      (NSSM) and Jellyfin.
- [ ] Core config (Appendix B §B2): defaults cover `nestopia`, `snes9x` (multitap `hid` mapping
      pre-configured — 5 players), `mgba`, `mupen64plus_next` (`isGlAllowed: true`),
      `pcsx_rearmed`, `fbneo`, `dosbox_pure`; **add `genesis_plus_gx`** (entry drafted in B2 —
      exact lib name must be verified against the libretro buildbot core list during Phase 0
      **[unverified]**).
- [ ] ROM library directory on **Ziggy local disk** (e.g. `D:\Arcade\roms\<system>\…`) — **not
      the L: NAS**; ROMs are tiny, and this keeps the arcade entirely outside NAS rules.
      Read-only bind mount to the workers' `library.basePath`. Per-system subfolders matching
      each core's `folder` key (`psx`, `n64`, …).
- [ ] PS1 BIOS (`scph5501.bin`) in the worker's libretro system dir (PCSX-ReARMed has an HLE
      fallback with reduced compatibility). GBA BIOS optional. NES/SNES/Genesis/N64 need none.
- [ ] Save/save-state dir mounted read-write + added to Ziggy's backup routine. Set
      `emulator.autosaveSec` (e.g. 60) so crashes lose ≤1 min.
- [ ] Caddyfile: `arcade.carpouzis.com` block → ArcadeGateway localhost port (Appendix C §C3);
      DNS CNAME → books.carpouzis.com.
- [ ] Router: forward **UDP 8443** → Ziggy. Set `CLOUD_GAME_WEBRTC_SINGLEPORT=8443` and
      `CLOUD_GAME_WEBRTC_ICEIPMAP=<Ziggy's public IP or DDNS-resolved address>`.
- [ ] Windows Defender firewall: inbound allow UDP 8443 (+ TCP for the gateway port from
      localhost/Caddy only).
- [ ] `coordinator.origin.userWs` set to `https://theater.carpouzis.com` (the page origin that
      opens the WS) — defense-in-depth on top of the token gate.
- [ ] ArcadeGateway deployed beside StreamGateway (same box, new localhost port), config:
      `ArcadeTokenSecret`, `CoordinatorBaseUrl` (`http://localhost:8000`), `SiteOrigin`.

## 5. Data model and config

Additive only. The arcade catalog is deliberately its own small table — games are not Movies, and
none of the movie plumbing (posters pipeline, OData, viewings) applies at v1.

```sql
CREATE TABLE ArcadeGame (
    Id                INT IDENTITY PRIMARY KEY,
    Title             NVARCHAR(200) NOT NULL,
    SortTitle         NVARCHAR(200) NOT NULL,   -- article-inverted, same convention as SimpleTitle
    System            NVARCHAR(20)  NOT NULL,   -- 'nes','snes','genesis','gb','gbc','gba','n64','ps1','arcade'
    RomPath           NVARCHAR(400) NOT NULL,   -- relative to the workers' rom mount (audit/ingest key)
    CloudRetroGameKey NVARCHAR(200) NOT NULL,   -- the launch key: game name as CloudRetro's library scan
                                                -- exposes it (t=104 game_name / InitSession games[].title)
    MaxPlayers        TINYINT       NOT NULL DEFAULT 1,
    RatingCeiling     INT           NOT NULL DEFAULT 0,  -- same scale as the channel age gate
    BoxArtPath        NVARCHAR(400) NULL,       -- served via a new /ArcadeImage route, files on posters mount
    Year              INT           NULL,
    IsEnabled         BIT           NOT NULL DEFAULT 1,
    Notes             NVARCHAR(MAX) NULL,
    CONSTRAINT UQ_ArcadeGame_SystemRom UNIQUE (System, RomPath)
);

CREATE TABLE ArcadeSession (                     -- durable log; live room state is in-memory (§6)
    Id                 INT IDENTITY PRIMARY KEY,
    ArcadeGameId       INT NOT NULL REFERENCES ArcadeGame(Id),
    RoomCode           NVARCHAR(16) NOT NULL,    -- OUR invite code (short, URL-safe)
    CloudRetroRoomId   NVARCHAR(300) NULL,       -- bound after creator's browser makes the room (§8);
                                                 -- format "<int64-hex>___<game title>" — contains '___'
                                                 -- and spaces, so ALWAYS URL-encode it
    CreatedByUserId    INT NOT NULL,
    CreatedUtc         DATETIME2 NOT NULL,
    EndedUtc           DATETIME2 NULL
);
```

Migration notes (this repo's specifics — see memory/streaming history): the dev connection string
**is the live shared prod/dev DB**; migrations are applied manually via `dotnet ef` (no auto-migrate
on deploy), and the live DB has drifted from the EF snapshot before — generate the migration,
*read the SQL* (`dotnet ef migrations script`), and apply deliberately. Nothing here touches
existing tables, so drift risk is nil, but follow the ritual anyway. No filtered indexes or
computed columns in these tables → no `QUOTED_IDENTIFIER`/`sqlcmd -I` trap.

```jsonc
// MovieTheaterConfiguration additions — flat fields, bound via rawConfig.Bind(this)
// (src/MovieTheater.Services/MovieTheaterConfiguration.cs); prod values arrive through the
// MOVIETHEATER_APPSETTINGS_JSON GitHub secret (follow the movietheater-secret checklist!).
{
  "ArcadeGatewayBaseUrl": "https://arcade.carpouzis.com", // what join descriptors point browsers at
  "ArcadeTokenSecret": "…",                               // shared with ArcadeGateway; HMAC key
  "ArcadeMaxConcurrentRooms": 3,                          // MUST equal deployed worker count (§2 box)
  "ArcadeJoinTokenTtlSeconds": 300,                       // token covers WS *connect*, not session length
  "ArcadeStunServers": ["stun:stun.l.google.com:19302"]   // echoed to the client shim (iceConfig)
}
```

Note there is deliberately **no** `ArcadeCoordinatorBaseUrl`/`ArcadeTunnelKey` on the pod — the
pod never calls Ziggy for arcade (§3 asymmetry). If C4's kill channel is ever built it belongs to
the gateway's config, not the pod's.

Catalog ingest (ROM dir → `ArcadeGame` rows) is a CLI command in the existing command style
(`arcade-ingest`), and per the project's bulk-job rule it is chunked, cursor-resumable, idempotent
(upsert keyed on the `System+RomPath` unique constraint), reports `{processed, remaining}` per
batch, and never deletes — rows for vanished files are flagged (`IsEnabled=0` + note), not
dropped. It derives `CloudRetroGameKey` from the filename the same way CloudRetro's library scan
does (name sans extension) and box art comes from libretro-thumbnails by name match; misses are
listed in the output for hand-fixing.

## 6. Backend work (site pod)

- **`ArcadeController`** (`[Authorize(Policy = "StreamingUser")]` at class level — the policy is
  `RequireAuthenticatedUser().RequireClaim("amr","pwd")`, registered in `Startup.cs`; age gate is
  separate and per-endpoint, mirroring `ChannelController`):
  - `GET  /API/Arcade/Games` — enabled games the user's age restriction allows.
  - `GET  /API/Arcade/Rooms` — live rooms (game, players by name, seats free), age-filtered.
  - `POST /API/Arcade/Room` — body `{gameId}`. Checks live-room count < `ArcadeMaxConcurrentRooms`
    (best-effort; CloudRetro t=112 is the backstop), creates `ArcadeSession` + our room code,
    registers the room in `ArcadeRoomService` with the caller in seat 0, returns the creator's
    join descriptor (§8).
  - `POST /API/Arcade/Room/{code}/Bind` — body `{cloudRetroRoomId}`. Creator-only, once-only,
    room must be unbound: stores the CloudRetro room id the creator's browser got back from
    t=104. Joins are refused with a "room still starting" status until bound.
  - `POST /API/Arcade/Room/{code}/Join` — age gate + seat assignment (lowest free seat <
    `MaxPlayers`; full → 409) → join descriptor.
  - `POST /API/Arcade/Room/{code}/Heartbeat` — presence touch; returns room status (who's here,
    seats). The room page polls this every 12 s, like TvPage's Now poll.
  - `POST /API/Arcade/Room/{code}/Leave` — explicit leave (also sent via `navigator.sendBeacon`
    on page hide, like `beaconStopStream` in `MovieAPI.js`).
- **Join descriptor** (returned by Room create and Join):
  ```jsonc
  {
    "roomCode": "K7QX2M",
    "wsUrl": "wss://arcade.carpouzis.com/w/<token>?room_id=<urlencoded CloudRetroRoomId or empty>&zone=",
    "playerSlot": 1,                    // 0-based controller port for t=108 / t=104 player_index
    "gameKey": "Mario Kart 64",         // t=104 game_name
    "iceConfig": [{ "urls": "stun:stun.l.google.com:19302" }],
    "isCreator": false                  // creator: send t=104 with empty room_id, then Bind
  }
  ```
- **`ArcadeRoomService`** — singleton, `Dictionary<string, RoomState>` under a `lock`, TTL'd
  per-user heartbeats: a direct structural copy of `ChannelSkipService`
  (`src/MovieTheater/Channels/ChannelSkipService.cs` — see Appendix D §D3 for exactly which parts
  transfer). Owns seat assignment, bind state, and lifecycle; a timer (or the reaper pattern from
  the channel maintenance service) ends rooms whose viewers all aged out (TTL 30 s vs the 12 s
  heartbeat, same margin rule as the channels), stamping `ArcadeSession.EndedUtc`.
- **`IArcadeHost` + `CloudRetroHost`** — the §2 seam, registered via an `ArcadeServiceExtensions`
  that **tolerates missing config** (arcade unconfigured = site runs fine, controller returns
  503/hidden), mirroring `JellyfinServiceExtensions.AddJellyfinServices`
  (`src/MovieTheater.Services/Jellyfin/JellyfinServiceExtensions.cs` — note its
  `"http://jellyfin-not-configured.invalid"` placeholder-BaseAddress trick). In v1 this service
  is small: mint tokens, build join descriptors, hold the STUN list. It exists so the CloudRetro
  specifics never leak into the controller.
- **`MovieTheater.ArcadeGateway`** — new small project, sibling of `MovieTheater.StreamGateway`:
  token validation + WS-upgrade forwarding to the coordinator (full spec Appendix C).
- **CLI**: `arcade-ingest` (§5); `arcade-rooms` (list live rooms from `ArcadeSession` + room
  service state; `--kill <code>` marks the record ended — actual emulator kill is C4/v2, until
  then a wedged room dies when its players disconnect or via `docker restart` of the worker).

## 7. Frontend work

- **Route**: React Router **v5** — `Switch`/`Route`/`useHistory` (NOT v6's `Routes`/`useNavigate`;
  the repo pins v5). In `src/ui/src/App.js`, mirror the existing pattern:
  `const ArcadePage = lazy(() => import("./Pages/Arcade/ArcadePage"));` with
  `<Route path="/arcade" exact>` and `<Route path="/arcade/room/:code" exact>` beside the TV
  route. Arcade entry in the section-switcher nav, hidden for users the policy/age gate rejects.
- **antd footgun** (repeats every time a page is added — see frontend-load-perf memory): antd CSS
  is imported on-demand; any antd component used on the new pages **must have its style import
  added in `src/ui/src/index.js`** or it renders unstyled in prod builds only.
- **Library page**: game grid reusing the existing card look (box art via `/ArcadeImage/{id}`),
  filter by system, "live rooms" rail at top (who's playing what — join in one click). 12 s
  polling for the rail; no realtime plumbing in the lobby.
- **Room page** (`/arcade/room/:code`): the player. **Vendor CloudRetro's web client rather than
  iframe it** — the stock client is dependency-free ES modules with no build step, but it derives
  its WS URL from `window.location` and force-overwrites the path to `/ws`
  (`web/js/network/socket.js` `buildUrl`), so it cannot be pointed at the tokened gateway URL
  without a patch anyway. The client shim we own implements the small protocol in Appendix A:
  connect → INIT (t=4, gives ICE servers) → WebRTC setup (t=100/101) → GAME_START (t=104) →
  seat (t=108) → inputs over the DataChannel. Adapted from the vendored `webrtc.js`/`api.js`;
  budget it as a real subtask, not "drop in a script tag."
- **Input**: Gamepad API (any USB/Bluetooth pad) with keyboard fallback. Joypad state goes over
  the pre-negotiated WebRTC DataChannel (label `"data"`, `negotiated: true, id: 0, ordered:
  false, maxRetransmits: 0` — must match exactly or the channel never opens; Appendix A §A4).
  Per-player "you are P2" seat badge from the join descriptor.
- **Invite flow**: copy `https://theater.carpouzis.com/arcade/room/{code}`. A friend opening it
  hits the normal login wall if needed, then Join seats them. No guest access: arcade rides the
  same password-verified policy as streaming. Room codes are ours (short base32); the CloudRetro
  room id (which embeds the game title and `___`) never appears in user-facing URLs.
- The Watch/TV player hooks (hls.js engine, ABR, capabilities) are irrelevant here — WebRTC
  replaces all of it — but reuse `useWakeLock` so long races don't sleep the screen, and follow
  the route-code-splitting discipline so the arcade chunk costs nothing on other pages.

## 8. Rooms, seats, and lifecycle — the exact flow

**One room = one CloudRetro worker = one emulator = one shared game.** Everyone sees the same
stream; this is couch multiplayer at a distance, not matchmaking.

Creation and join, end to end (because the backend cannot create rooms — §2 box):

1. Creator clicks a game → `POST /API/Arcade/Room` → site mints room code `K7QX2M`, seat 0,
   returns descriptor with **empty `room_id`** and `isCreator: true`.
2. Creator's browser connects `wss://arcade…/w/{token}` (gateway → coordinator `/ws`), receives
   INIT (t=4) with ICE servers + game list, completes WebRTC setup (t=100/101), sends
   **GAME_START** (t=104) `{game_name: gameKey, room_id: "", player_index: 0}`. Coordinator picks
   a free worker, starts the emulator, responds with the generated CloudRetro `roomId`
   (`"<hex>___<title>"`).
3. Creator's browser calls `POST /API/Arcade/Room/K7QX2M/Bind {cloudRetroRoomId}`. Room is now
   joinable. (If the browser dies between steps 1 and 3, the unbound room ages out via the
   reaper; the CloudRetro side dies with the creator's disconnect.)
4. Friend opens the invite URL → logs in → `Join` → descriptor with `room_id=<bound id>` and
   seat 1. Their WS connect carries `room_id` in the query, so the coordinator routes them to
   **the same worker** (deeplink joins share the worker without consuming its game slot); their
   t=104 with the same `room_id` joins rather than starts; t=108 confirms the seat (worker
   answers the accepted index, `-1` = rejected → surface an error, don't silently play on the
   wrong port).
5. Everyone heartbeats `POST …/Heartbeat` every 12 s (also the room-status poll). Leave = beacon
   + t=105 (QuitGame `{room_id}`) from the shim.
6. Room ends when all seats age out (TTL 30 s) or the creator ends it: reaper stamps
   `EndedUtc`. Save-state via t=106 before quit (autosave covers crashes); CloudRetro records the
   session id (first half of the room id) for its own resume feature — our "resume last session"
   v2 can ride that (`PrevSessions`), but v1 just relies on in-room save/load.

Seats: creator P1 (slot 0); joiners take the lowest free slot up to `MaxPlayers` (N64: 4, SNES
multitap: 5, GBA: 1). Extra joiners beyond seats get "room full" (spectators are v2 — CloudRetro
already streams to every room member, so spectating is seat-logic + UI, not new plumbing).
Pauses/resets are in-game (the game's own start menu); no vote machinery at this scale — the
creator gets an "end room" button and that's it for v1.

**Phase-0/2 verification items for this flow** (behaviors read from source but not yet exercised —
each gets a scripted check): (a) a room with zero connected users closes on the worker and frees
the slot [expected from `CloseRoom`/quit handling, **unverified** timing]; (b) t=104 into a busy
worker's *existing* room never yields t=112; (c) the 3-second grace loop on a closing room doesn't
bite quick end-and-recreate cycles; (d) what the worker does with two clients claiming the same
`player_index` (last-write-wins expected, **unverified**).

## 9. System support matrix (v1)

| System | Core (`emulator.libretro.cores.list` key) | Players | Notes |
|---|---|---|---|
| NES | `nestopia` | 2 | default config |
| SNES | `snes9x` | up to 5 | multitap via `hid` port→device map (device 257 on port 1), pre-configured |
| Genesis | `genesis_plus_gx` **[add]** | 2 | not in defaults; entry drafted in Appendix B §B2, verify lib name on buildbot |
| GB/GBC/GBA | `mgba` | 1 | solo only — link cable out of scope |
| **N64** | `mupen64plus_next` | **4** | the headline; `isGlAllowed: true`, llvmpipe GL, CPU-priciest (§3.3); config comments flag N64 frame-time inconsistency → per-core `vfr` flag exists if pacing looks off |
| PS1 | `pcsx` (pcsx_rearmed) | 2 | needs `scph5501.bin`; roms `["cue","chd"]`, `folder: psx`, dynarec on (`pcsx_rearmed_drc: enabled`) |
| Arcade | `fbneo` | 2 | |
| DOS | `dosbox_pure` | kb/mouse | stretch — CloudRetro has kb/mouse passthrough channels (`kbMouseSupport`), park unless wanted |

Out of scope v1: GameCube/PS2/Dreamcast (GPU-class emulation, different streaming problem),
Saturn/Sega CD (BIOS + heavier cores — revisit after launch).

## 10. Phases

**Phase 0 — Spike: prove the pipe (gates everything).**
Stock CloudRetro on Ziggy via Docker Desktop, repo compose + one extra worker. Forward UDP 8443,
firewall rule, `ICEIPMAP` to public IP, temporary Caddy block straight at the coordinator
(basic-auth gated, no site integration — the stock web UI at `/` is the test client; share rooms
with its native `?id=` deeplink). Load Mario Kart 64 and Snowboard Kids.
*Acceptance:* two people on remote home connections complete a 4-lap Mario Kart 64 GP together in
a browser at playable latency; Ziggy's CPU is measured for {1 N64 room, 1 N64 + 1 SNES room,
during a Jellyfin transcode} and the numbers + chosen worker count are recorded **in this doc**;
the §8 verification items (a)–(d) are exercised; Docker Desktop's UDP proxy shows no
loss/latency pathology at media rates (else fall back per Appendix E §E3).

**Phase 1 — Gate the door.**
`ArcadeCapabilityToken` in Core (clone D1); `MovieTheater.ArcadeGateway` project (Appendix C);
Caddy block + CNAME for `arcade.carpouzis.com`; pinned-commit CloudRetro image in `docker/`;
`coordinator.origin.userWs` locked to the site origin.
*Acceptance:* WS connect through the gateway with a valid token reaches the coordinator (verified
with a script speaking t=4); expired/garbage/missing token → 403 before any upstream traffic; the
coordinator and its web UI are unreachable from the internet; StreamGateway still serves video
(shared Caddy config regression check).

**Phase 2 — Site backend.**
EF migration for `ArcadeGame`/`ArcadeSession` (§5 ritual for the shared live DB);
`ArcadeRoomService`; `IArcadeHost`/`CloudRetroHost`; `ArcadeController` with the §8 flow;
`arcade-ingest` CLI; config through `MOVIETHEATER_APPSETTINGS_JSON` per the movietheater-secret
checklist (a malformed secret has taken prod down before — validate JSON before saving).
*Acceptance:* scripted walk-through — list games (age-filtered), create room, bind, join from a
second account, seats assigned, join refused pre-bind and when full, heartbeat-starve a room and
watch the reaper end it, cap enforced at `ArcadeMaxConcurrentRooms`.

**Phase 3 — `/arcade` frontend.**
Library grid + live-rooms rail; room page with the vendored client shim (WS → WebRTC → inputs),
gamepad + keyboard, seat badges, invite-copy; nav entry; antd style imports in `index.js`; wake
lock.
*Acceptance:* the Phase 0 scenario end-to-end through the real site — two users, real logins, one
invite link, no manual URLs — on desktop Chrome and Firefox (Firefox has burned this project
before; test it explicitly, especially DataChannel behavior).

**Phase 4 — Library + polish.**
Ingest the real ROM collection; box art; Genesis core verified; PS1 BIOS + a test title;
in-room save/load buttons (t=106/107); "arcade full" (t=112) and "room full" UX; autosave config.
*Acceptance:* every enabled game in the catalog boots to gameplay from the site — a scripted,
chunked, resumable smoke-run over the catalog per the bulk-job rule; each system verified with at
least one title.

**Phase 5 — Ops.**
Compose stack auto-starts with Docker Desktop; gateway `/healthz` watched alongside
StreamGateway's; `arcade-rooms` CLI; save dir in backups; worker-count/CPU headroom documented;
status callouts added to this doc.
*Acceptance:* Ziggy reboot → arcade recovers with no manual steps; a wedged room is clearable
(worker restart) without touching the other rooms.

## 11. Risks and mitigations

- **R1 — Docker Desktop's UDP proxy under WebRTC media load is unbenchmarked** (the Windows story
  is otherwise solved — Appendix E). Phase 0 measures; fallbacks in order: WSL2 mirrored
  networking with Hyper-V firewall rules, or a Hyper-V Linux VM with its own bridged IP (cleanest
  long-term home if Docker Desktop disappoints).
- **R2 — N64 CPU cost** (llvmpipe GL + software VP8): mitigated by worker-count cap, retro render
  resolutions, and Phase 0's measured headroom. If Ziggy has a usable GPU, pointing workers at
  real GL instead of Xvfb+llvmpipe is the first optimization, not a prerequisite.
- **R3 — CloudRetro release hygiene** (no tags since 2021, master-driven): pin a commit, own the
  image in `docker/`, upgrade deliberately. The `IArcadeHost` seam + vendored client shim are the
  exit hatch; the wire protocol (Appendix A) is small enough to re-implement against a fork.
- **R4 — A friend behind hostile NAT/CGNAT can't pass UDP**: v1 ships STUN-only; fallback is
  coturn on Ziggy (same Caddy/DNS pattern, `webrtc.iceServers` takes username/credential
  entries). Known work, deferred until someone actually hits it. Confirm Ziggy's own uplink isn't
  CGNAT for UDP specifically (video streaming working proves TCP 443, not UDP).
- **R5 — Invite leaks outside the friend group**: links carry only our room code; joining still
  requires a password-verified site session plus a fresh short-lived token per join. Same trust
  boundary as streaming. Coordinator additionally origin-locked; raw coordinator unreachable.
- **R6 — Upload bandwidth**: VP8 at retro resolutions ≈ 1–3 Mbps per room member (each member
  gets their own stream). A full N64 room ≈ one modest HLS viewer. The worker cap bounds it.
- **R7 — Seat squatting via t=108** (§3.1 soft spot): accepted at friends-scale; packet-filtering
  gateway upgrade (C4) if ever needed.
- **R8 — Coordinator/worker WS drops** (Ziggy reboot, image upgrade): rooms are ephemeral by
  design; the site's room records reap cleanly (heartbeats stop). Autosave (`autosaveSec`) bounds
  lost progress. Don't attempt session migration.

## 12. Open questions

1. Does Ziggy have a usable GPU for the workers (R2), and how much headroom alongside typical
   Jellyfin transcode load? (Phase 0 measures; decides worker count.)
2. ROM sourcing/curation — start with the ~20 game-night titles or bulk-ingest per system and
   curate with `IsEnabled`? (Plan assumes start small; ingest is built either way.)
3. Zone routing: CloudRetro supports worker zones + a ping-based selector. Single-box v1 ignores
   it (`zone=""` everywhere) — confirm nothing breaks with the param empty. **[low risk]**
4. Is DOS (dosbox_pure) wanted? kb/mouse passthrough exists but it's a different input UX.
5. Per-game core options (`options4rom`, N64 pak config, FBNeo dips) — punt to core defaults for
   v1, revisit per-title complaints?
6. Recording (`recording.*` config exists — CloudRetro can capture sessions): fun for "clips of
   game night," but storage + privacy questions. Parked.

## 13. Alternatives considered

- **EmulatorJS (client-side WASM) as the whole answer** — rejected for v1: netplay has been "in
  progress" for years and is still officially unsupported (mid-2026), and browser N64 is
  demo-quality. **Kept as the planned Phase 6 for solo play** — fully specified in its own doc,
  **`docs/emulatorjs-plan.md`** (2D systems solo in-browser at zero worker cost; N64/PS1 solo
  route to 1-player CloudRetro rooms). It stays out of v1 and out of this doc.
- **RetroArch native netplay / gopher64** — the enthusiast path (desktop apps, per-player setup;
  N64 netplay lives in gopher64 since simple64 was archived Feb 2025). Violates goal 1.
- **neko / Sunshine / Wolf / Selkies / Nestri** — see §2; each fails on
  multi-player-one-instance, gamepads, browser delivery, or maturity.
- **iframing CloudRetro's stock web UI instead of a client shim** — rejected: the stock client
  hardcodes WS path `/ws` off `window.location`, so it can't carry the gateway token path without
  patching anyway; iframing also gives up seat control, our chrome, and the login-integrated
  invite flow. Vendoring its ES modules (no build step, no deps) into our shim is barely more
  work and we own the result.
- **Running the emulator stack on the cluster instead of Ziggy** — rejected: realtime UDP media +
  CPU-heavy encode on the cluster, ROM storage there, and it breaks the "Ziggy does the heavy
  lifting" symmetry that already works for video.
- **Backend speaking the CloudRetro WS protocol to pre-create rooms** — considered (it's plain
  JSON, a .NET `ClientWebSocket` could do it); rejected for v1: a room's lifetime is tied to its
  connected users, so a backend-created room would need the backend to hold a WS open as a
  phantom member, and the emulator would start before any human is connected. The browser-bind
  flow (§8) is simpler and matches CloudRetro's grain.

## 14. Post-launch follow-ups

- Spectator seats (view-only members — streams already go to every member; seat-logic + UI).
- Per-user save states / "continue from last game night" (ride CloudRetro's `PrevSessions`
  session-id mechanism or copy save files per user on Ziggy).
- EmulatorJS solo mode (§13) — **Phase 6, planned in `docs/emulatorjs-plan.md`** (adds one ROM
  file-serve route `/r/{token}/{filename}` to the ArcadeGateway; otherwise independent).
- TURN (coturn) if R4 materializes; GPU GL for workers; H.264 rebuild only if a target browser
  ever struggles with VP8.
- Packet-inspecting gateway (C4): seat enforcement + server-side room kill.
- "Game night" scheduling — a TV-guide-style "Mario Kart, Friday 8pm" slot that pre-creates the
  room record (not the CloudRetro room — §8) and features it on the arcade page.
- Genesis/other cores beyond the launch set; Saturn/Sega CD (BIOS chain).

---

# Appendix A — CloudRetro wire protocol (browser ↔ coordinator ↔ worker)

Source: `pkg/api/api.go`, `pkg/api/user.go`, `pkg/api/coordinator.go`,
`pkg/coordinator/{coordinator,hub,userhandlers}.go`, `web/js/{api.js,app.js}`,
`web/js/network/{socket.js,webrtc.js}`, `web/js/room.js` — CloudRetro master, fetched 2026-07-02.

## A1. Transport + envelope

- Browser WS endpoint: **`/ws`** on the coordinator (workers use `/wso` — never exposed).
  Query params on connect: `room_id` (empty = will create), `zone` (worker zone, empty ok),
  optional `wid` (pin a specific worker id).
- Every message is JSON: `{"id": "<optional call id>", "t": <uint8>, "p": <payload>}`.
- The stock client builds the WS URL from `window.location` and **force-overwrites the path to
  `/ws`** — the reason the gateway must rewrite `/w/{token}` → `/ws` (Appendix C) and the reason
  we vendor the client rather than iframe it.

## A2. Packet types

| t | name (Go / JS) | direction | payload |
|---|---|---|---|
| 3 | CheckLatency / LATENCY_CHECK | server→client→server | list of worker ping URLs / results (multi-worker selection) |
| 4 | InitSession / INIT | server→client on connect | `{ice: [{urls,username?,credential?}], games: [{alias,title,system}], wid}` — **ICE config and the game list arrive here; there is no HTTP game-list API** |
| 100 | InitWebrtcStream / INIT_WEBRTC_STREAM | client→server | `{initiator: bool, sdp?: string}` — kicks off WebRTC; server replies with its SDP offer |
| 101 | WebrtcSignal / WEBRTC_SIGNAL | both | `{ice?: string, sdp?: string}` — **values are JSON-*stringified* strings**, e.g. client sends `packet(101, {ice: JSON.stringify(candidate)})`; parse accordingly |
| 104 | StartGame / GAME_START | client→server | `{game_name, room_id, player_index, record?, record_user?}`. Empty `room_id` ⇒ **create** room on a free worker; non-empty ⇒ **join** that room. Response: `{roomId, av?, kb_mouse}` |
| 105 | QuitGame / GAME_QUIT | client→server | `{room_id}` — only honored if it matches the worker's current room |
| 106 | SaveGame / GAME_SAVE | client→server | none; replies "ok"/error. Successful save records the session id (first half of room id) for later resume (`PrevSessions`, t=206) |
| 107 | LoadGame / GAME_LOAD | client→server | none |
| 108 | ChangePlayer / GAME_SET_PLAYER_INDEX | client→server | **bare int** (`packet(108, i)`), 0-based controller port. Worker replies with the accepted index, **`-1` = rejected** — check it |
| 110 | RecordGame / GAME_RECORDING | client→server | recording toggle (config-gated) |
| 111 | GetWorkerList / GET_WORKER_LIST | client→server | worker list; includes each worker's current room id **only when `coordinator.debug: true`** |
| 112 | ErrNoFreeSlots / GAME_ERROR_NO_FREE_SLOTS | server→client | all workers busy — surface "arcade is full" |
| 113 | ResetGame / GAME_RESET | client→server | reset current game |
| 150 | AppVideoChange / APP_VIDEO_CHANGE | server→client | video geometry change (aspect handling in shim) |
| 201/202/204/205/206 | RegisterRoom/CloseRoom/TerminateSession/LibNewGameList/PrevSessions | worker↔coordinator | internal (2xx block); listed so nobody "discovers" them as a control API — they ride `/wso`, not `/ws` |

## A3. Canonical session flows

**Create (seat 0):** connect `/ws?room_id=&zone=` → receive t=4 (stash `ice`) → t=100
`{initiator:false}` → receive SDP offer via t=100/101 → answer + trickle ICE via t=101 →
media + DataChannels up → t=104 `{game_name, room_id:"", player_index:0}` → response `roomId` →
**report `roomId` to the site (Bind)** → play.

**Join (seat k):** connect `/ws?room_id=<bound id>&zone=` (routes to the room's worker;
deeplink joins share the worker without consuming its game slot) → t=4 → WebRTC setup as above →
t=104 `{game_name, room_id:<bound id>, player_index:k}` → verify t=108-style ack ≠ -1 → play.

**Leave:** t=105 `{room_id}` → close WS. **Save/Load:** t=106/t=107 (wire to room-page buttons).

Room id format (from `pkg/games/launcher.go`): `strconv.FormatInt(rand.Int64(),16) + "___" + title`
— e.g. `6e2f01ab9c3d___Mario Kart 64`. Contains `___` and typically spaces ⇒ **always
URL-encode** when placed in the `room_id` query param. The game title is embedded so a room id
alone can relaunch the right game; the stock UI's shareable deeplink is `?id=<roomid>` (ours is
the site room code instead — the raw id stays server/descriptor-side).

## A4. Input path — DataChannels, not the WebSocket

After WebRTC setup, **all inputs bypass the WS**:

- **Joypad**: the client creates one channel with **exactly**
  `pc.createDataChannel("data", {negotiated: true, id: 0, ordered: false, maxRetransmits: 0})`.
  Negotiated means the server assumes it exists — wrong label/id/flags = channel silently never
  works. Gamepad/retropad state is serialized and sent on it (binary frame format lives in
  `web/js/input/retropad.js` — **[not extracted; read it when writing the shim]**).
- **Keyboard/mouse** (dosbox etc.): channels opened **by the server**, caught via
  `pc.ondatachannel`, addressed by label `"keyboard"` / `"mouse"`. Binary formats:
  keyboard = 7 bytes `[u32 keycode][u8 pressed][u16 mod]`; mouse move = 5 bytes
  `[u8 0][i16 dx][i16 dy]`; mouse button = 2 bytes `[u8 1][u8 button]`.
- Video/audio arrive as regular WebRTC media tracks (attach to a `<video>` element).

## A5. Identity/auth surface

None. No token/secret config exists for user connections; the only knob is
`coordinator.origin.userWs` (WS `Origin` header check: `""` = same-origin, `"*"` = any, or an
explicit origin — set it to the site origin). All real auth is ours, in front (Appendix C).

# Appendix B — CloudRetro configuration for Ziggy

Source: `pkg/config/config.yaml` (the embedded default — note it is *not* at `configs/`),
`pkg/config/loader.go`, root `docker-compose.yml`.

## B1. Env-var override rule (how compose configures everything)

Prefix `CLOUD_GAME_`, then: single `_` → `.` (case-insensitive match against camelCase keys);
the **first `__`** becomes one `.` and everything after it is literal (for keys containing
underscores); `name[0]` → indexed entry. Applied last (over file config). Examples:
`CLOUD_GAME_WEBRTC_SINGLEPORT` → `webrtc.singlePort`; `CLOUD_GAME_ENCODER_VIDEO_CODEC` →
`encoder.video.codec`; `CLOUD_GAME_COORDINATOR_ORIGIN_USERWS` → `coordinator.origin.userWs`.
Deep-nested core-list keys are awkward as env vars — put core config in a mounted YAML file
(loader search order: explicit path → cwd → `configs/` → `~/.cr`) and use env only for the flat
knobs, as the repo compose does.

## B2. Key settings (exact key paths)

```yaml
coordinator:
  server.address: ":8000"
  origin.userWs: "https://theater.carpouzis.com"   # WS Origin check — the page's origin, not arcade.*
  selector: ""            # "" = any free worker; "ping" matters only multi-zone
  debug: false            # true adds room ids to GetWorkerList — handy in Phase 0, off in prod
library:
  basePath: /roms         # mount point of the read-only ROM volume (default assets/games)
  # supported: [...]      # optional extension override; default = union of cores' roms lists
worker:
  network.coordinatorAddress: "coordinator:8000"   # service name in compose
  server.address: ":9000"
emulator:
  autosaveSec: 60
  storage: /saves         # save-state dir (default {user}/.cr/save) — mount + back up
  libretro.cores.repo.sync: true    # auto-downloads listed cores from buildbot.libretro.com/nightly
  libretro.cores.list:
    # defaults already include: nestopia, snes9x (multitap hid: port 1 → device 257),
    # mgba, mupen64plus_next (isGlAllowed: true, vfr available), fbneo, dosbox_pure, and:
    pcsx:
      lib: pcsx_rearmed_libretro
      roms: ["cue", "chd"]
      folder: psx
      options: { pcsx_rearmed_drc: enabled }
    gen:                                    # OUR addition — verify lib name on buildbot [unverified]
      lib: genesis_plus_gx_libretro
      roms: ["md", "gen", "smd", "bin"]
      folder: genesis
encoder:
  video: { codec: vp8, threads: 0, keyframeInterval: 300 }   # no fps key — rate comes from the core
  audio: { codec: opus }
  # h264 exists only as a commented example (x264enc, speed-preset=superfast tune=zerolatency);
  # the stock image has NO x264 (gstreamer -Dgpl=disabled) — a rebuild, not a config flip
webrtc:
  singlePort: 8443        # all ICE over one UDP port — the one we forward
  iceIpMap: "<ziggy public IP>"   # exact key name iceIpMap — overrides advertised candidate IP
  iceServers: [{ urls: "stun:stun.l.google.com:19302" }]   # entries take username/credential for TURN later
storage:
  provider: ""            # s3 only needed for multi-BOX save sharing — not us
```

Per-core knobs that exist when needed: `scale`, `scaleMethod`, `maxThreads`, `ratio`,
`isGlAllowed`, `usesLibCo`, `hid` (port→device map), `kbMouseSupport`, `nonBlockingSave`, `vfr`,
`options`, `options4rom`, `hacks`, `uniqueSaveDir`, `saveStateFs`.

## B3. Compose shape for Ziggy (deltas from the repo's docker-compose.yml)

The repo compose runs coordinator+worker from **one image** (`bash -c "./coordinator & … ./worker"`)
plus a `kcollins/xvfb` service (`Xvfb :99 -screen 0 320x240x16`) sharing an `x11` volume. Ours:

- Split into `coordinator` + `worker-1..N` services (same image, explicit commands) — **N services
  = room cap** (§2 box). Workers share the Xvfb `x11` volume and each sets `DISPLAY=:99`,
  `MESA_GL_VERSION_OVERRIDE=4.5`.
- Ports: publish **only** `8443:8443/udp` (media) and coordinator `8000` on **127.0.0.1** (for the
  gateway). Worker :9000s stay internal. The stock web UI at `/` is thereby localhost-only.
- Volumes: `D:\Arcade\roms` → `/roms` (ro), `D:\Arcade\saves` → `/saves` (rw),
  `D:\Arcade\config\config.yaml` → mounted into the loader's search path; cores cache volume so
  `repo.sync` doesn't redownload on every restart.
- `restart: unless-stopped`; Docker Desktop set to start at login.
- **[verify in Phase 0]** whether all workers can share one `singlePort` 8443 through the
  coordinator's muxing or need distinct ports each (`8443-844N/udp`) — the repo compose only
  demonstrates one worker; the singlePort muxer is per-worker-process, so per-worker ports are
  the likely answer. Budget router rules accordingly.

# Appendix C — ArcadeGateway spec

A sibling of `src/MovieTheater.StreamGateway/Program.cs` (~150-line minimal API, no state, YARP
forwarder, localhost behind Caddy). Differences from StreamGateway, which is the template for
everything not listed here:

## C1. Route + rewrite

- Single route: `app.Map("/w/{token}", …)` (+ `/healthz`). Everything else 403.
- Validate `ArcadeCapabilityToken` (D1). On success, forward to `http://localhost:8000` with the
  path **rewritten to `/ws`** and the original query string (`room_id`, `zone`) preserved —
  CloudRetro's client and server both hardcode `/ws`, and static-prefix proxying 404s.
- Enforce the token's room: for a joiner token (`roomCode` bound), require the `room_id` query
  param to equal the bound CloudRetro room id (the gateway gets it from the token payload — put
  the *CloudRetro* room id, not our code, in the token for joiners; creators carry empty).
  This is the arcade analog of StreamGateway confining `/Videos/{itemId}` to the token's item.
- WebSocket forwarding: YARP's `IHttpForwarder.SendAsync` handles the Upgrade end-to-end.
  **Delta from StreamGateway:** its `SocketsHttpHandler` sets `ConnectTimeout` only — fine — but
  the `ForwarderRequestConfig.ActivityTimeout` of 100 s must be raised/disabled for WS (a quiet
  signaling socket would be severed; the WS carries little traffic after setup). Set
  `ActivityTimeout = Timeout.InfiniteTimeSpan` on this route and let the room TTL do lifecycle.
- No CORS middleware needed (WebSockets don't use CORS; the coordinator's own Origin check +
  our token do the work). Keep `/healthz`.

## C2. Config

`ArcadeTokenSecret` (shared with the site via `MOVIETHEATER_APPSETTINGS_JSON` on one side and
gateway appsettings/env on the other), `CoordinatorBaseUrl` (`http://localhost:8000`),
`SiteOrigin` (optionally re-check the `Origin` header at the gateway too).

## C3. Caddy block (shape — matches the stream.carpouzis.com block)

```
arcade.carpouzis.com {
    reverse_proxy localhost:2303   # ArcadeGateway; Caddy handles TLS + WS upgrade natively
}
```

## C4. Designed-for upgrades (not v1)

Replace the byte-pump with a message-level WS proxy (two `WebSocket` receive/send loops, ~100
lines): parse the `{id,t,p}` envelope and (a) drop t=108 whose payload ≠ token `playerSlot`
(seat enforcement, R7), (b) drop t=104 whose `room_id`/`game_name` ≠ token scope, (c) expose a
local admin endpoint that injects t=105 to kill a room server-side (`arcade-rooms --kill`
becomes real). The envelope is tiny JSON; this is mechanical when wanted.

# Appendix D — Repo patterns to mirror (exact)

## D1. `ArcadeCapabilityToken` ← clone of `src/MovieTheater.Core/StreamCapabilityToken.cs`

Keep every mechanic: payload `'|'`-joined UTF-8 → `base64url(payload) + "." +
base64url(HMACSHA256(secret, payload))`; validation uses
`CryptographicOperations.FixedTimeEquals`, parses strictly (`parts.Length` check + `TryParse`),
checks expiry via `DateTimeOffset.UtcNow.ToUnixTimeSeconds()`, returns `false` on any defect.
New payload record: `Payload(int UserId, int GameId, string RoomCode, string CloudRetroRoomId,
int PlayerSlot, long ExpiresUnixSeconds)` — `CloudRetroRoomId` empty for creators (C1 uses it for
room confinement). Note the room id contains `|`-free text (hex + `___` + title) so the `'|'`
join stays parseable — but titles could theoretically contain `|`; **base64url the room id inside
the payload** to be safe (small, deliberate delta from the clone). Lives in `MovieTheater.Core`
so site + gateway share one implementation, exactly like the original.

## D2. Gateway anatomy ← `src/MovieTheater.StreamGateway/Program.cs`

Reuse verbatim: required-config throws at startup; single pooled `HttpMessageInvoker`
(`SocketsHttpHandler`, `UseProxy=false`, `AllowAutoRedirect=false`, `UseCookies=false`);
`app.Map` + validate + `forwarder.SendAsync(context, base, client, requestOptions, transformer)`;
`HttpTransformer` subclass for path rewrite (StreamGateway's strips `/s/{token}`, ours swaps to
`/ws` and re-appends `QueryString`); `Headers.Host = null` after rewrite; `/healthz` returning
bare "ok". Drop: CORS middleware and response-header de-duping (HLS-specific). Change:
`ActivityTimeout` per C1.

## D3. `ArcadeRoomService` ← `src/MovieTheater/Channels/ChannelSkipService.cs`

Transfer the skeleton: singleton registered in `Startup.ConfigureServices` (beside
`ChannelSkipService`, `Startup.cs:152`); one `private readonly object gate` + `Dictionary`;
`ViewerTtl = 30s` with the comment's rule — **TTL must exceed the client poll interval (12 s)
with margin so an active member is never pruned between polls**; every mutation method also
touches presence (`state.Viewers[userId] = now` — see `VotePoll`/`TogglePause` doing this);
`Prune` drops quiet members *and their side-effects* in the same pass. Skip the vote/poll
machinery (no votes in arcade v1). Add: `Seats` (slot → userId), `CloudRetroRoomId` bind state
(null until Bind; single-set), creator id, and a `ReapExpired()` swept by a hosted timer —
follow `ChannelScheduleMaintenanceService`'s hosted-service registration (`Startup.cs:158`) for
where that loop lives.

## D4. Optional external-service registration ← `JellyfinServiceExtensions.AddJellyfinServices`

Tolerate missing config (arcade unconfigured ⇒ site boots and runs, arcade endpoints 503/hidden);
placeholder `BaseAddress` trick (`"http://…-not-configured.invalid"`) if a typed HttpClient ever
appears; `services.Configure<ArcadeOptions>` from the flat `MovieTheaterConfiguration` fields;
registered from `AddMovieTheaterServices` in `MovieTheaterServiceExtensions.cs`.

## D5. Controller conventions ← `ChannelController.cs`

`[Authorize(Policy = "StreamingUser")]` at class level; `AgeRestriction` from `UserSettings` with
default 100; gate applied on list *and* re-checked on every mutating call; server computes all
timing (client clocks untrusted); JSON bodies on state-changing endpoints (CSRF posture relies on
SameSite=Lax + JSON content type, same as the stream endpoints).

## D6. Frontend conventions

Router v5 idioms only (`Switch`, `Route`, `useHistory`, `useParams`); lazy page import in
`App.js`; antd on-demand style imports in `src/ui/src/index.js` (the recurring footgun); API
wrappers in `MovieAPI.js` following `startStream`/`beaconStopStream` (incl. `navigator.sendBeacon`
for leave-on-page-hide); `useWakeLock` from the shared hooks; session/user from the existing
localStorage pattern.

# Appendix E — Windows host networking runbook (Ziggy)

Facts verified against MS docs + tracked issues, 2026-07:

## E1. What does NOT work

`netsh interface portproxy` is **TCP-only** — it cannot forward UDP, period (microsoft/WSL#11194).
Any plan that says "portproxy UDP 8443 into WSL2" fails silently. Do not use it for media.

## E2. Primary path — Docker Desktop publishes UDP itself

Docker Desktop's backend proxy publishes `-p 8443:8443/udp` **on the Windows host directly**
(its own `docker-desktop` VM, not your WSL2 distro) — reachable from LAN/WAN given:
(a) an inbound Windows Defender rule for UDP 8443, (b) the publish binding 0.0.0.0 (default;
don't write `127.0.0.1:8443:8443/udp`), (c) router forward UDP 8443 → Ziggy. The
WebRTC-specific gotcha is ICE advertising container-internal IPs — solved by
`webrtc.singlePort` + `webrtc.iceIpMap=<public IP>` (that pairing is exactly what those keys
exist for). Unknown: proxy throughput/jitter under sustained media — Phase 0's measurement.

## E3. Fallbacks, in order

1. **WSL2 mirrored networking** (run the stack via docker-ce inside a WSL2 distro):
   `.wslconfig` → `[wsl2] networkingMode=mirrored` (Win11 22H2+). Inbound UDP works but needs
   **Hyper-V firewall** rules (`Set-NetFirewallHyperVVMSetting -Name
   '{40E0AC32-46A5-438A-A0B2-2B479E8F2E90}' -DefaultInboundAction Allow`, or targeted
   `New-NetFirewallHyperVRule`). Known quirks: UDP multicast broken (irrelevant — WebRTC is
   unicast), UDP 68 never forwarded, some VPN conflicts, open regressions (WSL#13459). Plain
   Windows firewall rules alone are NOT sufficient in mirrored mode — the Hyper-V layer has its
   own table.
2. **Hyper-V Linux VM with bridged (external) vSwitch** — own LAN IP, zero NAT weirdness,
   forward UDP straight to the VM. Most moving parts, most predictable result; also the
   cleanest long-term home if Ziggy's Docker Desktop is busy with other duties.
