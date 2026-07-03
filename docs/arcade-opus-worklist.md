# Arcade — Opus Worklist (everything except GPU rendering)

**For:** an autonomous coding agent (Opus) picking up the arcade vertical. **Scope:** all remaining
arcade work EXCEPT GPU-accelerated rendering/encoding (that's being researched separately —
`docs/arcade-gpu-research.md`). Read `docs/arcade-plan.md` (architecture) and `docs/arcade-next-steps.md`
(the strategic plan) first, and the memories `arcade-cloudretro-vertical` + `arcade-rom-bios-locations`
for the hard-won gotchas — several details below are counter-intuitive.

## Current state (what already works — DON'T re-derive)
- CloudRetro stack LIVE on Ziggy (`docker/arcade/`): coordinator + 3 workers + Xvfb, image
  `movietheater/cloud-game:pinned` (built from commit `13852a7`). Gateway on `:2303`, site API `:3001`,
  Vite `:3000`, coordinator `:8000`.
- **End-to-end verified:** SNES (pristine), N64 (correct orientation + audio + input; GL artifacts are the
  GPU story, not yours), **multiplayer** (2 browsers, 1 invite link, both seats, shared game).
- Catalog: 6 N64 + 2 SNES games ingested to the live DB; `ArcadeGame`/`ArcadeSession` tables applied.
- Test account for multiplayer: `ClaudeStreamTest` / `ArcadePlayer2`.
- Config/renderer: N64 uses `coreAspectRatio: true` (so the flip flag reaches the client) + default
  gliden64. angrylion PANICS CloudRetro — do not use it.

## HARD RULES (from memory — violating these has burned this project)
- **Live DB is shared prod+dev** (`appsettings.Development.json` → home.neilb.dev/MovieSite). Migrations
  manual via `dotnet ef`; generate → READ the SQL → apply deliberately.
- **Never `git add -A`.** Stage explicit paths; `git status` first. A blanket add once swept WIP/dev config
  into prod.
- **Never touch the NAS (L:) or source ROM drives destructively.** Copy OUT only. Never recursively scan
  the whole `L:\` root — subtrees only.
- **`ArcadeTokenSecret` must be identical** on the site and the gateway. Prod config rides the
  `MOVIETHEATER_APPSETTINGS_JSON` GitHub secret — follow the `movietheater-secret` skill (a malformed
  secret has taken prod down).
- Dry-run-first for any bulk/destructive job; chunk + resume (the global bulk-job rule).

---

## P0 — Commit the working arcade (prerequisite; protects everything)
The ENTIRE vertical is currently uncommitted in the working tree. Lock it in before building more.
- Stage EXPLICIT paths (not `-A`): `src/MovieTheater.Core/ArcadeCapabilityToken.cs`,
  `src/MovieTheater.ArcadeGateway/**`, `src/MovieTheater.Db/{ArcadeGame,ArcadeSession}.cs` +
  `MovieDb.cs` + the `AddArcadeTables` migration, `src/MovieTheater.Services/Arcade/**` +
  `MovieTheaterConfiguration.cs` + `MovieTheaterServiceExtensions.cs`,
  `src/MovieTheater/Arcade/**` + `Controllers/{ArcadeController,ArcadeImageController}.cs` +
  `Web/Startup.cs`, `src/MovieTheater.Tests/ArcadeCapabilityTokenTests.cs`,
  `src/ui/src/Pages/Arcade/**` + `App.js` + `MovieAPI.js` + `NavBar/NavBar.js` + `index.js` (if changed),
  `docker/arcade/**` (but NOT `.env` — it has the LAN IP/secret; keep `.env.example`), `MovieTheater.sln`,
  `docs/arcade-*.md`.
- Do NOT stage `appsettings.Development.json` (has live secrets + the dev arcade secret) or `.env`.
- Build the solution + run the token tests before committing; `git status` and eyeball the diff.
- *Acceptance:* clean build, tests pass, a focused commit that contains ONLY arcade files, prod unaffected.

## P1 — Per-system input profiles (quick, high-QoL)
**Problem:** gamepad/keyboard button placement follows the raw libretro RetroPad layout, which feels
"funky" per system (N64 accelerate lands on the "east"/right button; different systems want different
layouts). There's no single universal mapping, and CloudRetro exposes no RetroArch remap menu.
**Design:** a per-system input profile that reorders the physical→RetroPad mapping, selected by the game's
`system`.
- Shim (`src/ui/src/Pages/Arcade/cloudRetroClient.js`): make `GAMEPAD_BUTTONS` and `KEYMAP` a function of
  a `profile` (e.g. `n64`, `snes`, `nes`, `genesis`, `arcade`, default). N64 profile: put N64 A
  (accelerate) on the physical bottom button; map C-buttons to the right stick / face buttons; Z to a
  trigger. Keep the analog-stick-from-arrows fix (already in — pumpInput derives lx/ly from arrows; N64
  steers with the stick).
- Thread the `system` through: `ArcadeController` join/create descriptors already know the game; add
  `system` to the descriptor JSON (and the `ArcadeJoinDescriptor` record + `CloudRetroHost`). The room
  page passes `descriptor.system` into `createCloudRetroSession`.
- Reference the RetroPad→native mapping per core (mupen64plus-next, snes9x) to choose sensible defaults.
- *Acceptance:* N64 Mario Kart accelerate is the bottom face button; SNES stays Nintendo-correct; documented
  in the on-screen hint.

## P2 — Stage-2 breadth (more games; all on the current CPU stack)
### P2a — The zipped-2D spike (GATES the 2D systems — do this first)
Does CloudRetro load **zipped** 2D ROMs? Drop one zipped SNES ROM into `D:\Arcade\roms\snes\`, restart a
worker, and see if it appears in the game list (t=4) and boots. (We currently use BARE `.sfc` — extracted
from the zip.)
- If YES → 2D can point at the zipped collections directly.
- If NO → serve bare ROMs (extract, as done for the SNES test) or zip-normalize at ingest.
### P2b — 2D systems (NES, SNES, Genesis, GB/GBC/GBA)
- ROM sources: `R:\Roms\Games` (full-name folders, `.zip`) or the cleaner No-Intro set
  (`L:\4 - Software\No-Intro ROM Collection…`). Prefer No-Intro for box-art match quality.
- **Every 2D `.zip` is indistinguishable by extension → each core MUST pin `folder`** (full name, e.g.
  `folder: "Super Nintendo Entertainment System"`) or NES-zips load as SNES.
- `mgba` covers gb/gbc/gba (3 folders, one lib) — verify multiple core entries sharing `lib:
  mgba_libretro` with different `folder`s work. Its buildbot core download failed once ("bad content
  length") — retry / pin.
- Update `ArcadeIngestCommand.cs` `ArcadeSystems.All` to the real folder names + `.zip` (currently short
  codes + native ext) IF mounting collections; OR keep curating into `D:\Arcade\roms\<short>` (the safe,
  working approach). Decide per the curate-vs-mount tradeoff in `arcade-next-steps.md` §C4 (recommend
  curate for v1).
### P2c — PlayStation 1 — DONE via JIT ROM cache (docs/arcade-jit-cache.md)
Built: the whole `L:\4 - Software\PSX Master Collection` (~448GB of `.7z` discs) is browsable without
pre-staging — the ArcadeGateway extracts a game's ROM on first play and LRU-evicts cold ones.
- **Code shipped + verified:** `ArcadeGame.SourceArchivePath` column (+migration APPLIED to shared DB);
  `arcade-jit-ingest` (catalog the master, dry-run-first/chunked/resumable) + `arcade-romcache-export`
  (writes the gateway manifest); gateway `RomCache` (7z extract + LRU + pin + guards); CloudRetro
  scan-on-miss patch (`docker/arcade/patches/`) so a just-extracted ROM launches without relying on
  fsnotify across WSL2; `config.yaml` `watchMode: true`; `pcsx` `roms: ["cue","chd"]`. 3 RomCache unit
  tests + full suite green (23/23).
- **Saves are safe** (keyed by room id in `/saves`, `/roms` is read-only) — proven from CloudRetro source.
- **User steps to go live with PS1:** run `arcade-jit-ingest --apply` for the PSX master (curate rating
  ceilings as desired) → `arcade-romcache-export` → set gateway `RomCache:ManifestPath`/`RomsDir` →
  rebuild the worker image with the patch → drop **PS1 BIOS** `scph5501.bin` (`C:\Network Share\bios`)
  in the worker libretro `system` dir. (`F:\Emulation\roms\psx` still has 147 ready `.chd` if you'd
  rather seed a curated set directly instead of JIT.)
### P2d — Arcade (FBNeo)
- ROMs: `R:\Roms\Games\Arcade` / `MAME`. BIOS: the Neo-Geo/CPS pack at
  `L:\1 - Movies\!Downloads\!Software\Mega_Bios_Pack_Ver1.1` (extract `neogeo.zip` etc. into the arcade ROM
  folder). fbneo is finicky (parents/versions) → per-title enablement, curated shortlist first.

## P3 — Box art (the plumbing exists)
- `/ArcadeImage/{id}` route (`ArcadeImageController.cs`) and `arcade-boxart` CLI already built. Run
  `arcade-boxart --apply` (chunked, `--after-id`) to pull libretro-thumbnails for the catalog; it stores
  under the posters mount + sets `BoxArtPath`. Cleaner No-Intro names → better hit rate.
- *Acceptance:* game cards show box art instead of the labeled placeholder.

## P4 — Go-live (internet play — multiplayer already works on LAN)
- **Caddy + DNS:** the block is written (`docker/arcade/Caddyfile.arcade.snippet`) — add it to Ziggy's
  Caddyfile; CNAME `arcade.carpouzis.com → books.carpouzis.com`.
- **Router (USER hardware step — document, can't automate):** forward **UDP 8443–8445** → Ziggy + Windows
  Defender inbound rule. **Confirm inbound UDP actually reaches Ziggy** (CGNAT-for-UDP is the one true
  project-killer — a phone on cell → a UDP listener answers it).
- **Prod config:** put `ArcadeGatewayBaseUrl`, `ArcadeTokenSecret` (same on gateway), `ArcadeMaxConcurrentRooms`,
  `ArcadeJoinTokenTtlSeconds`, `ArcadeStunServers` into `MOVIETHEATER_APPSETTINGS_JSON` (movietheater-secret
  skill — validate the JSON!). Tighten `.env` `SITE_ORIGIN` from `*` to the real origin, `ZIGGY_PUBLIC_IP`
  to the public IP/DDNS. Gateway prod appsettings gets the matching `ArcadeTokenSecret`.
- **Deploy:** push to `master` → GH Actions builds + deploys to MicroK8s (the pod is thin control-plane
  only; the arcade stack lives on Ziggy, unaffected by the deploy).

## P5 — Ops hardening
- Compose `restart: unless-stopped` (already set) + Docker Desktop start-at-login so the stack survives a
  Ziggy reboot. Verify recovery.
- Gateway `/healthz` watched alongside StreamGateway's. `arcade-rooms` CLI (built) for live-room admin.
- `D:\Arcade\saves` into Ziggy's backup routine. Lock `coordinator.origin.userWs` to the real site origin.

---

## Recommended order
P0 (commit) → P1 (input profiles) → P2a (zip spike) → P2c (PS1, easy win) → P2b (2D) → P3 (box art) →
P2d (arcade) → P4 (go-live) → P5 (ops). Stop and get a human decision at go-live (P4) — it exposes the
service to the internet and needs the router step.

## NOT your scope (being researched separately)
GPU-accelerated **rendering** (fixing N64 GL artifacts / upscaling) AND GPU **encoding** (NVENC) — both
share the same GPU-in-container plumbing and are covered by `docs/arcade-gpu-research.md`. Leave the CPU
worker image as the default; a GPU worker variant will slot in beside it without changing your work.
