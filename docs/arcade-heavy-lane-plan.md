# Arcade — Heavy lane: Moonlight/Sunshine-streamed emulators (Switch, PS3, PS4, …)

**Status:** plan (2026-07-10), research-verified. This is the execution plan for roadmap
**WS-E** (`arcade-roadmap.md` §4), sharpened by two things that changed since the roadmap was
written: (a) the CloudRetro stack is now **all-native-Windows** on Ziggy (WSL retired) with PS2 and
GameCube already live in-browser, so the heavy lane's scope is *only* what CloudRetro/libretro can
never carry — Switch, PS3, PS4, Wii U, X360 class; and (b) Eric's directive (2026-07-10): **once the
Moonlight layer exists, adding any specific application must be cheap** — a descriptor, not an
engineering project. The design below is therefore a *generic app-registration layer* with
per-emulator adapters, not N bespoke integrations.

Companion docs: `arcade-roadmap.md` (WS-E rationale + §2.3/§2.4 research), `arcade-saves-plan.md`
(the save vault this lane attaches to), `arcade-jit-cache.md` (the staging pattern the big-title
cache reuses), the `arcade` skill.

**Honest scope statement (acknowledged by Eric up front):** this lane does NOT give per-browser-seat
internet multiplayer like CloudRetro. It gives **couch co-op** — multiple pads forwarded from one
Moonlight client into one session — plus possibly Apollo's multi-client mode (§6.4, experiment).
Solo/remote play works from any Moonlight device.

---

## 1. What already exists (inventoried 2026-07-10 — build on it, don't rebuild it)

| Asset | Where | State |
|---|---|---|
| shadPS4 (PS4) | `C:\PS4` | Portable install (`user\` dir beside the exe); **Bloodborne v1.09 + DLC + mods verified playing** |
| Yuzu (Switch) | `E:\Yuzu` | Frozen pre-takedown build, configured |
| Ryujinx (Switch) | `E:\Ryujinx` | Frozen pre-takedown build, configured |
| Cemu (Wii U) | `E:\Cemu` | Configured |
| Xenia (Xbox 360) | `E:\Xenia 360` | Configured |
| LaunchBox / Big Box | `E:\LaunchBox` | Curated front-end over the whole estate |
| Switch games, extracted | `E:\Switch Games` | 20 titles, playable today (manual pre-staging) |
| Switch dump library | `L:\4 - Software\Switch` | ~171 `.xci`/`.nsp` incl. updates + DLC (per `data/nas-file-inventory.csv` — never scan L: directly) |
| PS3 (RPCS3) | — | **Not installed yet** — the one new emulator this plan adds |
| Save vault | `D:\ArcadeStorage\savestore` + `ArcadeSave` table | Built (S0–S3 of `arcade-saves-plan.md`) |
| GPU/CPU | RTX 4070 Ti (2× NVENC, AV1; 12-session cap on 591.44+), i7-13700K, 64 GB | Shared with CloudRetro workers + Jellyfin |

Two consequences:
- **v0 exists the day Apollo is installed:** expose **Big Box** as a single Apollo app and the whole
  E:\ estate is streamable to the couch with zero site integration. Everything after that is
  incremental polish (per-title cards, saves, identity), not a prerequisite for playing.
- The pilot uses the *existing* Yuzu/Ryujinx builds. The July-2026 fork landscape (verified): **Eden**
  is the active successor (survived the Feb-2026 DMCA via counter-notice, self-hosted git, daily
  nightlies), **Ryubing** is the stable/maintenance Ryujinx continuation, **Citron is compromised**
  (hijacked Discord distributing fake builds — do not touch). Refresh to Eden/Ryubing **per title,
  only when a game needs it**; a working frozen build beats fork churn. ⚠ Save-data layouts are
  compatible within a fork family (yuzu→Eden/Citron, Ryujinx→Ryubing) but **shader caches are not**
  — treat caches as per-emulator disposables.

---

## 2. Host stack decision: Apollo (Sunshine fork), not stock Sunshine

**Apollo** (`ClassicOldSong/Apollo`, active, pin the current release at build time) over stock
LizardByte Sunshine, for four load-bearing reasons:

1. **SudoVDA virtual display built in** — a virtual monitor is created *per client, with a fixed
   per-client EDID identity*, auto-matched to the client's native resolution + refresh. Windows
   remembers each client's display config natively. No dummy plug, no separate VDD install, no
   resolution-flapping scripts. This also kills the 59.94-vs-60 Hz judder class: the virtual display
   runs at the client's refresh.
2. **Headless mode** — render + encode directly on the 4070 Ti with no physical monitor attached
   (Adapter Name pinned to the dGPU). Ziggy can stay a headless server.
3. **Per-client permission system** — first paired client gets full permissions; every later client
   is *restricted until explicitly granted* Launch-Apps / Mouse / Keyboard / etc. This IS our trust
   model (§10): Eric's devices = full; friends' devices = gamepad + app-launch only.
4. **Artemis** (Apollo's Android Moonlight fork) adds QoL on phones/handhelds; standard Moonlight
   clients (Qt, iOS/tvOS, Deck flatpak, embedded) all work unchanged.

Stock-Sunshine compatibility is retained (same protocol, same `apps.json` shape, same `/api/*`
surface), so nothing below marries us to the fork — if Apollo ever dies, swap in Sunshine + a
standalone VDD and lose only the per-client niceties.

### 2.1 Windows-session hard truths (the part that bites)

- **A stream is the console session.** Sunshine-family hosts capture and inject into the ONE
  interactive Windows session. There is no per-user session isolation on Windows (that's Wolf's
  Linux trick, roadmap §2.3). A client with mouse+keyboard permission **is sitting at Ziggy** — with
  L: mounted and `c$` reachable. All mitigation is at the permission layer, not the session layer.
- **Auto-logon, unlocked, at console** — already true on Ziggy for the CloudRetro worker tasks; the
  heavy lane inherits the requirement. Capture from the lock screen / secure desktop does not work
  from a plain user process; run Apollo **as its Windows service** (installs by default) so UAC
  prompts and the login desktop are reachable, and set the machine to never lock/sleep the console
  session (display-off is fine — headless mode doesn't care).
- **One instance = one active session.** A second *independent* heavy session needs a second Apollo
  instance (`sunshine_2.conf`-style: distinct ports, distinct state dir — documented upstream) AND
  survives two client quirks: Moonlight treats same-IP-different-port hosts as one host unless the
  second is added manually by `ip:port`, and **both sessions share the one Windows desktop** — see
  the input-collision trap in §6.3. **v1 = exactly one heavy session at a time, enforced by our own
  lock (§7.4).** The second instance is an H5 experiment, not a promise.
- **Emulator processes run as the logged-in desktop user**, not as the client. Per-site-user state
  therefore cannot come from Windows profiles; it comes from the save vault seeding emulator save
  dirs per launch (§8) — same philosophy as CloudRetro seed/harvest.

---

## 3. Architecture

```
 /arcade catalog                    ArcadeGateway (Ziggy, Windows service — already exists)
 ArcadeGame rows, Lane='heavy'      ├── HeavyApps registry (descriptors, §4)
        │                           ├── Apollo admin proxy: pairing PIN, app-list sync (§7)
        ▼                           ├── Big-title stager: L: → E: cache, chunked+resumable (§5)
 "Play via Moonlight" card UX ────► ├── Save seed/harvest per (user, title) → savestore (§8)
 pair · status · busy-lock          └── Heavy-lane lock + session status
                                                │ localhost HTTPS :47990 (basic auth)
                                                ▼
                                    Apollo (Windows service, headless, SudoVDA)
                                    apps.json ← generated, one app per heavy title (+ Big Box)
                                    cmd = heavy-launch.ps1 <appId>   (stage→seed→run→harvest)
                                                │ Moonlight protocol (LAN; Tailscale later)
                                                ▼
                              Moonlight / Artemis clients (Deck, TV box, phones, PCs)
                              N gamepads per client → ViGEm virtual pads on Ziggy
```

Principles, mirroring the rest of the vertical:
- **The site/gateway owns catalog, auth, age gate, identity, saves.** Apollo owns transport only.
- **The gateway is the only thing that talks to Apollo's admin API** (it already runs on Ziggy with
  secrets; the k8s pod cannot reach localhost:47990 and never should).
- **Everything a title needs is declared in one descriptor** (§4) — Eric's "adding an app shouldn't
  be a big concern" requirement, made concrete.

---

## 4. The HeavyApp descriptor — the generic app-registration layer

One JSON descriptor per streamable app, stored on Ziggy (gateway-owned:
`D:\ArcadeStorage\heavy\apps\<id>.json`; DB later if we want UI editing). The **app-sync** step
(§7.2) compiles descriptors → Apollo `apps.json`. Nothing else in the system knows emulator
specifics.

```jsonc
{
  "id": "switch-kirby-forgotten-land",
  "title": "Kirby and the Forgotten Land",
  "system": "switch",                       // catalog System key; null for non-catalog apps (Big Box)
  "arcadeGameId": 4123,                     // FK to ArcadeGame when card-integrated; null for v0 apps
  "exe": "E:\\Yuzu\\yuzu.exe",              // or Eden/Ryujinx/rpcs3/shadPS4/Cemu/Xenia/BigBox.exe
  "argsTemplate": "-f -g \"{rom}\"",        // {rom} resolved by the stager
  "workingDir": "E:\\Yuzu",
  "staging": {                              // null → no staging (Big Box, already-local titles)
    "source": "L:\\4 - Software\\Switch\\Kirby and the Forgotten Land [0100...][v0].xci",
    "updates": ["L:\\4 - Software\\Switch\\...[v65536].nsp"],   // installed once, not per-launch
    "cacheTier": "heavy"                    // the E: big-title cache, §5
  },
  "save": {                                 // null → vault not wired (v0); see §8 per-system table
    "kind": "dir",
    "livePath": "E:\\Yuzu\\user\\nand\\user\\save\\...\\{titleId}",
    "titleId": "0100A3D008C5C000"
  },
  "input": { "gamepad": "x360", "maxPads": 4 },   // "ds4" for gyro titles, §6.2
  "boxArt": "kirby-forgotten-land.png",     // reuses the arcade box-art pipeline output
  "ratingCeiling": 6,                        // age gate, same scale as ArcadeGame.RatingCeiling
  "enabled": true
}
```

Adding a new emulator = writing the descriptor's four emulator-specific strings (`exe`,
`argsTemplate`, `save.livePath` shape, staging source) — exactly the "not a big concern" bar.
Adding a new *title* on an existing emulator = one descriptor, or nothing at all if the title is
reachable through the Big Box catch-all.

**`heavy-launch.ps1` — the single launch contract** (Apollo `cmd` for every generated app):
1. `GET gateway /heavy/prepare/<appId>` → gateway takes the heavy-lane lock (409 if busy), stages
   the ROM if needed (§5 — returns immediately if cached; the card UX pre-stages so this is warm),
   seeds the launching user's save (§8), returns the resolved `{rom}` path.
2. Launch the emulator with resolved args; **wait on the process** (Sunshine app semantics: the
   stream's app "quits" when `cmd` exits — quitting the emulator cleanly ends the session).
3. On exit (or script trap on kill): `POST gateway /heavy/finish/<appId>` → harvest saves, release
   the lock. Apollo's per-app `prep-cmd` undo is set to the same finish call — belt and suspenders,
   because undo runs even when the client force-quits the app.

Notes that will bite if skipped:
- Sunshine/Apollo run `cmd` **through the service**; the script must not require an interactive
  console. Log to a file under `D:\ArcadeStorage\heavy\logs\`.
- `exit-timeout` / graceful-quit: emulators want a clean window-close (WM_CLOSE) so they flush
  saves; configure the app with a generous exit timeout and have the script send `CloseMainWindow()`
  before `Kill()` on teardown.
- Emulator must come up **fullscreen and foregrounded** (`-f`-class flags per adapter) — a
  background emulator still receives XInput but the client stares at the desktop.
- `exclude-global-prep-cmd`: keep global prep empty; everything is per-app.

---

## 5. Content staging — big titles are NOT the JIT cache's usual customer

House rules that carry over verbatim: ROM staging goes **through the sidecar/manifest pattern,
never a side path**; L: is read-only to automation; never scan L: (the inventory CSV + explicit
paths only); all bulk jobs chunked/resumable/idempotent with progress.

What's different from the CloudRetro JIT cache: PSX-era JIT extracts ~500 MB in seconds at
first-play. Switch/PS3/PS4 titles are **5–45 GB** — extracting on the play click means minutes of
staring. So the heavy tier is **pre-staged, not just-in-time**:

- **New cache tier** `heavy` rooted at `E:\Games\_heavycache\` (E: is the existing games drive;
  measure free space at build and set the budget then — the 20 hand-staged Switch titles in
  `E:\Switch Games` count as pinned members and get adopted into the manifest, not re-copied).
- Same manifest + LRU discipline as `RomCache`, new size budget and a **chunked copier**: copy in
  ~256 MB segments with a progress row the card UI can poll (`{staged, total, nextOffset}`),
  resumable after interruption, `.partial` suffix until sha-verified complete (harvest-skip
  convention already exists for mid-write files).
- **Trigger = the card.** A heavy card whose title isn't staged shows **"Prepare (12.4 GB)"**
  instead of Play; preparing is allowed while someone else plays (disk copy, not a session). Play
  enables when staged. Admin can bulk-pre-stage the curated set.
- **Switch updates/DLC install into the emulator NAND once** (not per-launch): staging a Switch
  title = stage the base XCI/NSP + run the emulator's headless install for update/DLC files (yuzu
  family installs into `user\nand`; adapter detail per fork — verify flags at H1). The descriptor's
  `updates` list is the record of what was installed; re-running is idempotent (skip if installed).
- PS3: RPCS3 wants a decrypted folder-format game or ISO; PKG (PSN titles) installs into
  `dev_hdd0\game` once. PS4/shadPS4: game folders (Bloodborne's already local on C:; future titles
  stage to the E: tier).
- **Never run a heavy title directly off L:** — 45 GB of random reads over SMB from a spinning NAS
  is stutter city, and it violates the sidecar rule anyway.

---

## 6. Input — couch co-op, gyro, and the two traps

### 6.1 How pads arrive
Moonlight forwards each physical pad on the client; the host creates one **virtual pad per
forwarded pad via the bundled ViGEmBus driver**. Multiple pads on ONE client is the native couch
co-op path (protocol has historically carried up to 4 pads — verify the current cap empirically at
H2 before advertising seat counts). Rumble is forwarded back on supported clients. Emulators see
ordinary XInput/DS4 devices — no emulator-side special-casing.

### 6.2 Gyro / motion (Switch needs this; some PS3/PS4 titles too)
Motion only exists when the host emulates a **DS4** pad (the X360 HID has no motion fields).
Verified current state: Sunshine/Apollo translate accelerometer+gyro+touchpad into the emulated
DS4, and motion-capable clients are **Moonlight PC ≥5.0, Android ≥12.0, iOS current, and the Deck**
(the Deck's own gyro forwards — Zelda shrines from the couch actually work). So:
- Descriptors for motion titles set `input.gamepad: "ds4"`; the sync step writes the per-app
  gamepad-type override (host-wide default stays `auto`, which picks DS4 when the client reports
  motion — pin per-app anyway so a TV client without motion doesn't silently downgrade the pad type
  mid-catalog).
- Emulator side: the Switch forks and shadPS4 read DS4 motion via SDL natively; map once per
  emulator profile at H1 and it holds for every title.

### 6.3 TRAP — ViGEm pads vs the CloudRetro local-multiplayer detector (cross-feature, will fire)
The arcade vertical's local-multiplayer feature has the gateway/site adopt **locally connected
pads** (ClaimSeat/ReleaseSeat, a 16 ms poll + 125 ms detector — see commit `c1b7420`). ViGEm
virtual pads are **indistinguishable from real pads at the XInput layer and use the stock Xbox
VID/PID** — the moment a Moonlight guest connects with two pads, the CloudRetro side sees two new
"local" controllers on Ziggy and may adopt them into an arcade room. Mitigations, in preference
order (decide at H0 with a 30-minute test):
1. Enumerate via a device API that exposes the **device instance path** and skip anything under the
   `ROOT\ViGEmBus` enumerator (robust, needs a small change in the pad-detection code).
2. Gateway broadcast: while the **heavy-lane lock** is held, suspend the local-pad detector
   (coarse but zero device-plumbing).
3. Both. (Recommended — the detector suspend also stops seat-UI noise during heavy sessions.)

**DECIDED + v1 LANDED 2026-07-10.** Option 1 as written is impossible: the pad detector is browser
JS (`cloudRetroClient.js`), and the Gamepad API never exposes the device instance path — a ViGEm
x360 pad is byte-identical to a real Xbox pad (`Xbox 360 Controller (XInput STANDARD GAMEPAD)`).
What makes filtering safe anyway is an invariant we already set at H0: Apollo is `gamepad = x360`
host-wide and the host's physical pads are non-Xbox (Pro Controller / DualSense) — so on the
stream host, **XInput ⇒ streamed**. Landed: machine-local toggle `arcade.ignoreStreamedPads`
(Controllers panel → "Ignore streamed (XInput) controllers"), enabled only on the stream-host
browser. It filters the two AUTOMATIC paths (fluid adoption + press-a-button detector); explicit
panel assignment still works on any pad as a deliberate override. Option 2 (suspend detector
while the heavy lock is held) still layers on at H3 for seat-UI noise.

### 6.4 EXPERIMENT (H5) — Apollo multi-client shared session
Apollo documents multiple clients connected to a single instance (bandwidth stacks per client). If
that mode lets a *second* Moonlight device join the *same* running app with its own pad, that is
LAN couch co-op **across rooms/houses** — a real fraction of the CloudRetro multiplayer magic on
heavy titles. Unverified; treat as a bounded experiment with two clients at H5, never a roadmap
promise.

### 6.5 Client hotkeys / guest ergonomics
Moonlight's local quit combo (Ctrl+Alt+Shift+Q; long-press Select/Back combos on controller-only
clients) ends the stream but **does not kill the app** unless "quit app on disconnect" semantics
are configured — our `heavy-launch.ps1` finish path + Apollo's app-quit handles both orders
(disconnect-then-quit and quit-then-disconnect). Document for guests: quitting the game in-emulator
is the clean path; just disconnecting leaves the session resumable (Moonlight "Resume" works —
that's a feature for the couch, not a bug).

---

## 7. Site & gateway integration

### 7.1 Catalog
- `ArcadeGame.Lane` (nvarchar(20), null = `'cloudretro'`): `'heavy'` rows join the same lobby, same
  box-art pipeline (IGDB→SteamGridDB cascade already built), same age gate (`RatingCeiling`; ESRB
  feeds it), same dedupe/browse UX. New `System` values: `'switch'`, `'ps3'`, `'ps4'`, `'wiiu'`,
  `'x360'`. Migration per house rules (generate → read SQL → apply to the shared live DB).
- Heavy cards render a **status-aware action**: `Prepare (N GB)` → progress → `Play via Moonlight`
  → or `In use by <user> · Big Box session` when the lock is held. Card detail hosts the pairing
  helper (§7.3) and per-client setup crib (which client apps, bitrate hints).
- Curation over bulk: heavy titles enter the catalog **hand-picked** (descriptor authored, staged,
  cache-warmed, tested), not by romset ingest — dozens, not thousands. The `arcade-ingest` CLI is
  not involved; a small `heavy-sync` CLI (below) is.

### 7.2 App-list sync
`heavy-sync` (gateway CLI or admin endpoint): reads descriptors → validates (exe exists, staging
source exists in the inventory, box art present) → writes Apollo's app list via its admin API
(`/api/apps`, HTTPS :47990, basic auth; creds in gateway appsettings, never in the site DB) → dry
run prints the diff first (house rule). Apollo shows per-app box art in Moonlight's grid
(`image-path` → our box-art PNGs) — the couch UI gets real art for free.

### 7.3 Pairing, and mapping Moonlight clients → site users
Moonlight identity = a per-client TLS cert + friendly name; it knows nothing of site users. Bridge:
- New table `HeavyClient { Id, ClientName, ApolloUuid?, UserId FK, Permissions summary, PairedUtc }`.
- Pairing flow: site user (editor-gated at first) clicks **Pair a device** on /arcade → enters the
  4-digit PIN their Moonlight client is showing → site → gateway → `POST /api/pin {pin, name}` →
  on success the gateway records `HeavyClient(name → UserId)`. The person who paired the device
  owns its sessions — **save seeding keys off this mapping** (§8). A shared living-room device can
  be re-owned per session later (v2: "who's playing?" prompt on the card before launch).
- Apollo's per-client permissions are then set once in its web UI (Eric-only, localhost) —
  friends' devices: app-launch + gamepad only, no keyboard/mouse/clipboard.

### 7.4 The heavy-lane lock
A gateway-held mutex (survives site deploys; it's Ziggy-local state) + a DB status row for UI:
`{ heldBy UserId, appId, since }`. Taken by `prepare`, released by `finish` + a watchdog (if the
emulator process is gone and no finish arrived in N minutes, harvest + release — crash-safe).
Apollo would refuse an overlapping stream anyway; the lock exists so the **site can say who/what**
and so saves can't interleave. CloudRetro rooms are unaffected (different pool, different lock).

### 7.5 Launch UX (no deep link yet — verified)
`moonlight://` deep-linking is an open upstream issue, not shipped; Moonlight Qt does have a CLI
(`Moonlight.exe stream <host> "<app>"`). v1 UX = the card says "open Moonlight on your device →
Ziggy → <app name>" (with the app pre-highlighted by exact name), plus a copyable CLI one-liner for
desktop users. Revisit deep links if upstream lands #1874.

---

## 8. Save vault attach (per-site-user saves on shared emulators)

Same vault, same rules as `arcade-saves-plan.md` (blobs under `D:\ArcadeStorage\savestore\<userId>\`,
`ArcadeSave` rows, never-clobber conflict handling, prune-unnamed-first). Heavy titles add one new
artifact kind — **directory saves** — stored as zips with sha256: `Kind='dirzip'`.

Per-system truth table (the four strings each adapter must get right):

| System | Live save location (seed/harvest target) | Granularity | Portability notes |
|---|---|---|---|
| Switch (yuzu family: Yuzu/Eden/Citron) | `<emu>\user\nand\user\save\0000...\<profileUid>\<titleId>\` | per title | Portable **within the yuzu family**; Ryujinx layout differs (`bis\user\save\<saveId>` with internal metadata) — treat Yuzu-family and Ryujinx as two save namespaces; a title's descriptor pins which emulator it runs on, so no cross-family sync is attempted |
| PS3 (RPCS3) | `<rpcs3>\dev_hdd0\home\00000001\savedata\<TITLEID>-*\` | per title (one or more dirs) | Stable, documented layout; **savestates are version-fragile — never vaulted**, savedata only (the `.srm`-vs-`.dat` truth again) |
| PS4 (shadPS4) | `C:\PS4\user\savedata\1\<CUSAxxxxx>\` | per title | shadPS4 is young; verify layout stability at H1 and pin the shadPS4 build per title |
| Wii U (Cemu) | `E:\Cemu\mlc01\usr\save\<titleId hi>\<lo>\user\` | per title | Standard Cemu mlc01 layout |
| X360 (Xenia) | `E:\Xenia 360\content\<TitleID>\` | per title | Coarser; acceptable |

Mechanics (all existing patterns, re-aimed):
- **Seed** (in `prepare`): resolve launching user via `HeavyClient` mapping → unzip that user's
  `dirzip` into the live path (after moving the current content aside to `\_displaced\<ts>\` —
  never delete). No vault entry = leave whatever's live (v1 keeps continuity with today's local
  saves; "fresh start" is an explicit card option later, mirroring the CloudRetro "New game" flow).
- **Harvest** (in `finish` + debounced watcher while running): zip the live path → compare sha256 →
  new `ArcadeSave` row on change. Dir-zips are MBs at most — vault budgets unaffected.
- **Ownership rule**, same as CloudRetro multiplayer: the session owner is the paired device's
  user; couch guests play in the owner's world. Documented, not negotiated per session.
- **EmuDeck / Deck continuity (roadmap G4):** these are the same emulator families EmuDeck ships —
  savedata zips are directly usable on the Deck. The S4 bridge (when built) maps on
  `(system, titleId)` — cleaner than the ROM-basename mapping the retro lane needs. Until then the
  vault's manual download/upload UI covers it.
- v1 = single "Continue" slot per (user, title); named snapshots are pointless for dir saves until
  someone asks.

---

## 9. Emulator adapter notes (the per-system details H1 must verify)

- **Switch:** pilot on the frozen `E:\Yuzu` (it runs the 20 staged titles today); evaluate **Eden**
  nightly beside it for titles that need newer fixes; **Ryubing** where stability wins. Keys +
  firmware come from Eric's own console dump; store under `E:\<emu>\user\keys` per emulator —
  never in the repo, never in the vault. Fullscreen flag + game-path launch (`yuzu.exe -f -g`,
  Eden same family; Ryujinx `Ryujinx.exe -f <path>` class) — verify per fork. **Pre-warm shader
  caches** per title before its card goes live (first-run compilation stutter would read as "the
  stream is bad"); async shader compile ON.
- **PS3 (RPCS3 — the new install):** portable zip to `E:\RPCS3`. Firmware `PS3UPDAT.PUP` (free from
  Sony) via File→Install Firmware; let the PPU-module precompile finish before first launch.
  2026-04 requirements overhaul: CPU is the constraint and the 13700K sits comfortably in spec
  (note: Raptor Lake consumer has **no AVX-512** — RPCS3's ~20% AVX-512 bonus doesn't apply; it is
  NOT required). Vulkan renderer on the 4070 Ti (listed 4K-ready class by RPCS3's own 2026 matrix).
  Per-title quirks go in RPCS3's `custom_configs` (`config\custom_configs\config_<TITLEID>.yml`) —
  the emulator's own per-game system, same philosophy as the arcade per-game-fixes rule ("let the
  emulator's DB do per-ROM fixes"). Launch: `rpcs3.exe --no-gui "<path to EBOOT.BIN or iso>"`.
- **PS4 (shadPS4):** already proven on the flagship (Bloodborne + mods). Young project — pin the
  working build per title; don't auto-update. Launch flags verify at H1 (`shadPS4.exe -g` class).
- **Wii U (Cemu):** mature, easy. Gyro titles need the DS4 path (§6.2) or Cemu's motion source
  setting; Wii-remote-pointer titles are out of scope (same verdict as Wii on Dolphin — pointer
  can't ride Moonlight sensibly).
- **X360 (Xenia):** canary-pinned build, per-title compat is the long pole — curate only known-good
  titles.
- **RetroArch+Vulkan (ParaLLEl-RDP)** joins the lane later as just-another-descriptor — the only
  real fix for Bomberman 64's adventure-mode cinematics (roadmap WS-B §4). RetroAchievements
  arrives free with the standalone emulators that support it (per-user logins via per-user
  configs = a later, small descriptor extension).

---

## 10. Security & trust model (explicit, because the failure mode is total)

- **Threat statement:** a paired client with keyboard+mouse permissions controls Ziggy's desktop —
  which reaches L:, D:, the DB, everything. Pairing is therefore **physical-seat-equivalent trust**.
- Controls: pairing is site-editor-gated and PIN-completed (§7.3); friends' devices get Apollo's
  restricted permission set (gamepad + launch only); Apollo's web UI stays bound to
  localhost (gateway proxies the two calls we need); the admin creds live in gateway appsettings on
  Ziggy (never the site DB, never the repo — `no-name-or-fs-details-in-code` rule applies to
  descriptors too: they live on Ziggy, not in git).
- **Network exposure: LAN only in v1.** Moonlight ports (TCP 47984/47989/47990/48010, UDP
  47998–48000/48010) are NOT port-forwarded, ever. Remote/away play = Tailscale/WireGuard to the
  LAN (H5) — same trust boundary, no public surface. (The AdGuard split-DNS already gives Ziggy a
  stable LAN name for clients.)
- The site's own surface adds: pairing endpoints (editor-gated), status/lock reads (any user),
  staging admin (editor). Nothing new is internet-reachable beyond the site itself.

---

## 11. GPU / CPU budget (heavy session vs CloudRetro rooms vs Jellyfin)

- **Encode:** one heavy stream = one NVENC session (AV1 or HEVC, up to 4K60, ~80–150 Mbps on LAN).
  4070 Ti has 2 encoder engines and a 12-session driver cap; CloudRetro rooms are ~1 low-res
  session each and Jellyfin transcodes are bursty. Session *count* is a non-issue; engine
  *throughput* at 4K60 + several rooms is fine on paper — **measure at H0** (3 live rooms + a 4K60
  desktop stream + one Jellyfin transcode, watch `nvidia-smi encoder` util + room NACK/FPS via
  test-roms).
- **3D/CPU contention is the real risk:** Switch/PS3/PS4 emulation loads both the GPU 3D engine and
  big P-core slices (RPCS3's SPU threads especially). CloudRetro 2D cores are CPU-cheap; the N64/
  PS2/GC cores + their encodes are not nothing. Policy: heavy session runs at normal priority, and
  the acceptance bar is "a heavy session + 2 active rooms with no room regression" (H1
  measurement). If contention shows, the lever order is: cap heavy encode at 1440p60 → set worker
  CPU affinity to E-cores… no — measure first, don't pre-tune (the 13700K has headroom; the
  roadmap's own note: constraint is plumbing, not horsepower).

---

## 12. Phases

**H0 — Host bring-up (a day).** Install Apollo (pinned) as service on Ziggy; headless mode on the
4070 Ti; pair Eric's desktop + Deck (+TV box if present); expose **Big Box + the 20 staged Switch
titles + Bloodborne as hand-written apps.json entries** (no site work). Verify: 4K60 AV1 desktop
stream; a Switch title + Bloodborne play well from the Deck; gyro works via DS4 mode on one Zelda
shrine; **run the ViGEm-vs-local-pad-detector test (§6.3) and pick the mitigation**; encoder
contention measurement (§11). *This phase alone delivers the couch experience.*

**H1 — Emulator estate hardening (days).** Install RPCS3 (+firmware, 2–3 pilot titles incl. a
CPU-heavy exclusive); verify per-fork launch flags, fullscreen/foreground behavior, clean-exit save
flush, and the save-path table (§8) for all six systems; shader-cache pre-warm procedure; write the
first real descriptors + `heavy-launch.ps1`; Eden/Ryubing evaluation for any title the frozen
builds fumble.

**H2 — Couch co-op verify (a day).** 2–4 pads through one client on Switch + PS3 + PS4 titles; pad
order stability + per-emulator pad pinning documented; rumble; empirical pad-count cap; the guest
hotkey/quit story (§6.5).

**H3 — Site integration (the code phase).** `Lane` migration; `HeavyClient` + lock tables; gateway:
descriptor registry, Apollo proxy (pin + apps), heavy tier of the stager (chunked/resumable,
adopts `E:\Switch Games`), lock + watchdog; `heavy-sync` CLI (dry-run first); card UX
(Prepare/Play/busy, pairing helper). CloudRetro pad-detector mitigation lands here too.

**H4 — Save vault attach.** Dir-zip kind; seed/harvest via prepare/finish + watcher;
`HeavyClient→User` ownership; displaced-content safety; manual download/upload works for heavy
saves; Deck round-trip test on one Switch + one RPCS3 title (G4 proof).

**H5 — Experiments & reach (bounded).** Apollo multi-client shared session (§6.4); second Apollo
instance for a second concurrent lane (§2.1); Tailscale remote play; `moonlight://` deep links if
upstream shipped; Wii/pointer + RetroArch-ParaLLEl descriptors; RetroAchievements per-user logins.

Sequencing vs the rest of the vertical: H0–H2 are pure-Ziggy and can happen anytime (zero site
risk). H3/H4 are ordinary site+gateway work and should not preempt in-flight CloudRetro efforts —
but nothing here blocks on them either.

---

## 13. Open decisions

- **D-H1:** E: free-space budget for the heavy cache tier (measure at H0; the tier's LRU budget is
  config).
- **D-H2:** per-title Apollo apps for everything vs Big Box for the long tail + cards only for the
  curated headliners. (Leaning: cards for anything with save-vault wiring; Big Box for the rest.)
- **D-H3:** descriptor storage — JSON files on Ziggy (v1, simplest, gateway-local) vs DB rows with
  an admin editor (later, if descriptor churn is real).
- **D-H4:** whether the shared-living-room-device "who's playing?" prompt (per-launch user
  override) is needed before saves attach, or device→user static mapping suffices for the
  household. (Static first; the prompt is additive.)

## 14. Trap appendix (read before building — the ones a fresh pass would miss)

1. **ViGEm pads look local** to the arcade local-multiplayer detector (§6.3) — decide the
   mitigation at H0, land it before H3, or heavy sessions will hijack CloudRetro seats.
2. **Sunshine app lifetime = `cmd` lifetime.** Launchers that spawn-and-exit (LaunchBox launching a
   rom, self-updating emulators) end the stream instantly — `heavy-launch.ps1` must wait on the
   *emulator* process (find the child; `detached` is for fire-and-forget apps like Big Box itself).
3. **Frozen forks are a feature.** Yuzu/Ryujinx builds predate the takedowns and work; auto-update
   anything and you inherit fork-soup churn + shader-cache invalidation. Pin everything; upgrade
   per-title, deliberately. Citron specifically: hijacked distribution channels — never fetch it.
4. **Motion dies silently on X360 pads.** A gyro title on the default pad type just has dead
   shrines — no error anywhere. Descriptor pins `ds4` per title (§6.2).
5. **First-run shader compilation** reads as stream jank. Pre-warm per title before its card ships;
   never judge stream quality during a cold cache (echo of "never judge smoothness in headless
   Chrome").
6. **Lock screen / sleeping console = black stream.** Auto-logon, never-lock, display-off-ok is a
   *deployment checklist item*, and a Windows-update reboot silently resets you into the lock
   screen — add the console-session check to whatever watchdog pings the worker tasks.
7. **Savestates are not saves** (again): only directory savedata is vaulted; RPCS3/fork savestates
   stay machine-local and version-bound.
8. **Never stage per-launch from L: synchronously**; the Prepare flow exists so the play click is
   never a 40 GB copy. Staging is chunked+resumable+manifested (house rule; and adopt, don't
   duplicate, `E:\Switch Games`).
9. **Moonlight same-IP instances collide** in the client host list (manual `ip:port` add for
   instance #2) — don't burn a day "debugging" the second instance not appearing.
10. **Descriptors carry personal paths** — they live on Ziggy (`D:\ArcadeStorage\heavy\apps`),
    never in the repo (`no-name-or-fs-details-in-code`).
