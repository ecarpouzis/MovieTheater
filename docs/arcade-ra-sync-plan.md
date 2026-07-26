# Arcade RetroAchievements — account sync (pull + push) + run-legitimacy plan

Status board for the two features Eric asked for on 2026‑07‑26 ("local boards → hardcore flag + why‑icons"
and "RA sync → pull + push, hardcore when legit / softcore otherwise"). Companion to the
`arcade-retroachievements` skill and the `arcade-retroachievements-feature` memory.

---

## 1. SHIPPED this session (built + verified, both repos)

### Run‑legitimacy taints — feature (A), COMPLETE
Local boards now record **why** a run wasn't a clean one, and the UI shows it.

- **Worker (fork `movietheater-fork`):** `cheevos.Client` samples three taints on every unlock /
  leaderboard event: **Cheat** (cheat codes staged at room create — `len(rq.Cheats)>0`), **Savescum**
  (a save‑STATE was loaded mid‑run — `HandleLoadGame`, sticky until a hard reset), **Timeplay**
  (fast‑forward/rewind — plumbed but *dormant*: no speed control is exposed in this stack, so it's
  always false today). A hard reset (`HandleResetGame`) clears savescum/timeplay, mirroring RA's own
  "loading a state drops you out of hardcore until reset" rule. Taints are `atomic.Bool` (written from
  the coordinator command goroutine, read on the emu goroutine — never an rc_client call off‑thread).
  Threaded through the toast (`t=160`) and both mirror POSTs. Files: `pkg/worker/cheevos/cheevos.go`,
  `caged/libretro/frontend.go`, `caged/libretro/caged.go`, `coordinatorhandlers.go`, `racheevos.go`.
  Worker packages `go build` clean; cheevos tests pass.
- **Site:** `ArcadeAchievementUnlock` + `ArcadeLeaderboardEntry` gain `Cheat/Savescum/Timeplay bool`
  (migration `20260726163556_AddArcadeRunLegitimacy` — 6 additive `bit` cols, default 0, **NOT applied**;
  see §4). Both internal callbacks accept + persist them; the leaderboard best keeps its taints in step
  with the value. Read endpoints expose them + a derived `legit = hardcore && !cheat && !savescum &&
  !timeplay`. UI: `ArcadeLeaderboards.js` shows a 🏆 badge for a legit hardcore run and per‑reason
  why‑icons (🔧 cheat / 💾 savescum / ⏩ timeplay) with tooltips; the in‑room toast appends the same.
  Backend builds; 115 arcade vitest pass.

### RA sync — PULL backend, SHIPPED (gated on config)
- `GET /API/Arcade/RetroAchievements/Profile?userId=` — read‑only pull of a user's real RA profile
  (points, rank, recent unlocks) via RA Web API `API_GetUserSummary`, keyed by the **site** Web API
  account (public data, no per‑user token). Degrades to `{configured:false}` / `{linked:false}` — never
  errors. `MovieTheaterConfiguration.ArcadeRaWebApiUser/Key` added.
  **To activate:** add those two keys to the prod secret (`MOVIETHEATER_APPSETTINGS_JSON`) — the site
  account's username + its retroachievements.org Web API key. Until then it's inert, like every other
  arcade config gate.

---

## 2. NOT shipped — remaining work, and WHY it was staged not auto‑built

Two pieces were deliberately **left for a session with Eric present**, because they are outward‑facing
and/or unverifiable offline — the exact category to confirm before shipping:

- **PUSH to real RA accounts** — submitting friends' unlocks/runs to *their* retroachievements.org
  accounts under stored credentials is irreversible and externally visible, has ToS weight (a wrongly
  “hardcore” submission is on that user's real account), and **cannot be verified without a live
  submission under a real linked account**. It also stacks new CGO on the LIVE worker binary that serves
  every room. Design below; build it with Eric able to test.
- **Pull UI** — the display surface. The `Link`/`Status`/`Unlink` endpoints already exist and the
  `Profile` pull endpoint is shipped; what's missing is a lobby panel. Left out of an autonomous session
  only to avoid building React blind (no app run to eyeball layout / the `index.js` antd‑style footgun).
  This one is low‑risk and small — see §3.

---

## 3. PULL — finish the UI (small, low‑risk)

Revive a compact **RetroAchievements** panel in the arcade lobby (`ArcadePage.js`, likely beside the
`ra` filter in `ArcadeNavContent.js`). It:
1. calls `RetroAchievements/Status`; if unlinked, shows a "Link RetroAchievements" button →
   username/password form → `POST RetroAchievements/Link` (endpoint already stores the connect token
   DP‑encrypted, discards the password).
2. when linked, calls `RetroAchievements/Profile` and renders points / rank / recent unlocks, with an
   Unlink action (`DELETE RetroAchievements/Link`).
3. Optionally reuse this on a future user‑profile page for `Profile?userId=` (view a friend's RA).

`MovieAPI.js` helpers: add `getArcadeRaStatus`, `linkArcadeRa`, `unlinkArcadeRa`, `getArcadeRaProfile`
(the link/status/unlink routes exist; only `getArcadeUserAchievements`/`getArcadeLeaderboards` are wired
today). **Footgun:** any new antd component ⇒ import its style in `index.js`.

---

## 4. Apply the migration (Eric's deliberate DDL step)

Shared prod/dev DB; classifier gates prod DDL. Migration `20260726163556_AddArcadeRunLegitimacy` is
additive (6 `bit` cols, default 0). The base `20260726010120_AddArcadeRetroAchievements` may still be
unapplied — generate a SCOPED script from the last‑applied migration and **read the SQL** before running,
per the RA skill:

```
dotnet ef migrations script <last-applied> 20260726163556_AddArcadeRunLegitimacy \
  -p src/MovieTheater.Db -s src/MovieTheater.Db -o ra-legit.sql
```

Do NOT `database update` (live DB has RenderProfile drift that fails a full update). Until applied, the
new columns don't exist — the callbacks would 500 on write, so **apply before the site deploys** (§6).

---

## 5. PUSH — architecture (build with Eric present)

**Goal (Eric's call):** when a room host has a linked RA account, submit their runs to their REAL RA
account — **hardcore** only when genuinely legit (no save‑load, no cheat, no ff/rewind), else
**softcore** (RA accepts both; casual play still counts, just not for hardcore mastery).

**The load‑bearing constraint:** the friends‑first local board must keep working for EVERY room,
including a linked host's casual room — and RA leaderboards are hardcore‑only, so a single
non‑spectator softcore client would stop feeding the local board. Therefore:

### Dual rc_client (the correct design)
Keep the **site‑account spectator client exactly as today** (always hardcore, never submits) — it is the
local scoring engine for every room, and it already carries the feature‑(A) taints. **Additionally**,
when the host is linked, bring up a **second** rc_client logged in as the HOST's account, **spectator
OFF**, that submits to their real RA:
- start hardcore‑enabled **iff the room is competitive** (competitive already guarantees no
  cheats/no save‑seed and hides Load — the only mode we can vouch is legit);
- on a mid‑run save‑STATE load (defence in depth), call `rc_client_set_hardcore_enabled(false)` to drop
  to softcore for the rest of the session (RA's rule);
- casual room ⇒ start softcore (still submits softcore unlocks; RA won't take softcore leaderboard
  entries, which is fine — the local board covers those).
- The push client's events do NOT drive our toast/mirror (the spectator client already does) — wire it
  with log‑only handlers so there are no double toasts / double DB rows.

The bridge's memory map is a process global shared by both clients (same RAM) — verified: `cheevos_set_memory`/
`cheevos_init_memory` take no client ptr. Two `DoFrame`s per frame; one extra login + game‑data fetch.

### Wire + config (mostly already stubbed)
- The descriptor record `ArcadeJoinDescriptor` **still carries** dormant `RaUser/RaToken/Hardcore` — repopulate
  them in `CreateRoom` from `LoadRaCredentialsAsync(host)` (the DP‑encrypted connect token is already
  stored + loaded). `ToJson` must re‑emit `raUser/raToken`; the shim (`cloudRetroClient.js`) re‑adds
  them to `t=104` (the fields existed before the site‑account pivot removed them — see git history).
- Worker `t=104` already has `ra_user/ra_token/hardcore` plumbed through `user.go/worker.go/workerapi.go`.
  Rebuild the **coordinator** too (it silently drops StartGame fields it doesn't know).
- `coordinatorhandlers.go`: if `rq.RaUser != ""` → stage a second push client (host creds, spectator
  off); always keep the site spectator client. `frontend.go` grows a parallel `raPush *cheevos.Client`.
- New config not required for push (host token rides the descriptor); pull needs the Web API key (§1).

### ToS
Submit hardcore ONLY from competitive rooms (guaranteed clean); everything else softcore. Send the
`MovieTheaterArcade/1.0` UA (dorequest 403s without it). Never submit from the site account (spectator).

### Test plan (needs a real linked account)
Link a throwaway RA account as host; competitive room on a known RA title → confirm a hardcore unlock
appears on that RA account + the local mirror; casual room → confirm softcore. Tail `glworker.log` for
two "session started" lines (spectator + push).

---

## 6. Deploy order (all Eric‑driven)

1. Apply the migration (§4) — **first**, so the new columns exist.
2. Deploy the **worker** (feature A already needs this): the RA skill's dance — confirm no live room
   (`curl localhost:8000/status`), Disable the two GL Worker tasks + the Watchdog, Stop‑Process the
   watchdog loop then the `run-arcade-glworker` loops + `worker.exe`, swap `bin/worker.exe` (keep a
   `worker.pre-legit.exe` backup), re‑enable. Build cmd: ucrt64 toolchain
   (`PATH=/d/msys64/ucrt64/bin`, `PKG_CONFIG_PATH=/d/msys64/ucrt64/lib/pkgconfig`, `CGO_ENABLED=1`).
   Capture worker (Worker 3) is untouched by RA changes.
3. Merge site → `master` (CI/CD auto‑deploys). Safe only AFTER step 1.
4. (Pull) add `ArcadeRaWebApiUser/Key` to the prod secret to light up the profile pull.
5. Regenerate `fork.patch` via `scripts/export-arcade-fork.ps1` and commit it with the fork branch.
