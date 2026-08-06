# Arcade — Challenge Mode ("Nick Arcade")

Status: **IDEA + PLAN. Nothing implemented.** Filed for the future 2026-08-01. No branch, no
migration, no worker change yet. Everything below marked ✅ was verified against live code/config
during the design session; everything marked ⚠ is an assumption that must be measured before it is
built on.

Companion skills: `arcade` (architecture, deploy dance, fork rules), `arcade-retroachievements`
(the RA engine this rides on), `test-roms` (verification harness).
Companion docs: `arcade-clean-start-plan.md` (shares the Savescum-taint problem),
`arcade-saves-plan.md` (the save store this writes into).

---

## 1. The product

Drop a player into a **random game**, at a **random point in that game**, with a **difficult but
achievable objective** — score X, beat this boss, reach that distance, survive the next 40 seconds.
WarioWare pacing over the real library. Rounds are short; you rotate through many games in a sitting.

### 1.1 The load-bearing goal — read this before proposing anything

**The point is making lesser-known games visible, and the fun is not knowing what the hell to do.**
The objective text *is* shown to the player. The mystery is the *game*: unfamiliar controls,
unreadable sprites, no idea what kills you. That single sentence drives every decision in this
document.

Consequences that are easy to get backwards:

| Instinct | Why it's wrong here |
|---|---|
| Curate good states by hand | Only covers games the curator already knows — exactly inverted from the goal |
| Prefer famous games with rich data | Famous games are the *least* valuable content in this mode |
| Reach deep, late-game content | An early-game state in an obscure shmup is ideal content |
| Guarantee a fair, winnable position | Dropped in at 1 HP, confused, is the product |

### 1.2 HARD RULE — no hand-curated save states

Eric ruled this out explicitly and asked that it not be raised again. State generation must be
**fully automated across the whole catalog**. If a design requires a human to play each game, the
design is wrong. This is a product constraint, not a budget one.

---

## 2. What already exists (✅ verified)

This mode is mostly assembly of shipped parts. In the fork
(`D:\Arcade\build\cloud-game-gl`, branch `movietheater-fork`):

| Capability | Where | Note |
|---|---|---|
| rcheevos vs live RAM, per frame | `pkg/worker/cheevos/` (32 C TUs + `cheevos.go`) | ✅ vendored, builds, live |
| Rich presence engine | `z_rc_rcheevos_richpresence.c`; script activated `rc_client.c:2397` | ✅ compiled in, **not surfaced** |
| Leaderboard trackers / measured progress | `rc_client.c` (`leaderboard_tracker_list`, `measured_progress`) | ✅ compiled in, **not surfaced** |
| RA's own ROM hashing suite | `z_rc_rhash_hash.c`, `hash_rom.c`, `hash_disc.c`, `hash_zip.c`, `md5.c` | ✅ vendored, **unused** |
| Emulator frontend API | `frontend.go:259` LoadCore, `:1230` LoadGame, `:1729` Tick, `:1740` Input, `:1700`/`:1703` Load/Save | ✅ |
| Raw input + memory access | `nanoarch.go:892` InputRetropad, `:1133` MemoryRegion, `:1139` MemoryMap | ✅ |
| Media path is a *decorator* over the pacing loop | `caged.go:79` | ✅ — headless is reachable |
| Rewind ring (rolling savestate buffer) | `frontend.go:161-172`, armed `:324-340`; default step 6 frames, cap 512 MB | ✅ live on the 2D tier |
| Fast-forward (4x pacing floor) | t=114, `frontend.go:154+` | ✅ live |

On the site (this repo):

| Capability | Where |
|---|---|
| Per-game RA set fetched + cached (title, desc, points, badge) | `ArcadeController.cs:2276`, mapped `:2288-2311` |
| Tiered RA API cache (14d defs / 2d boards) | `ArcadeRaApiCache`, `ArcadeController.cs:1136` |
| `ArcadeGame.Ra{GameId,AchievementCount,HasScoreLeaderboard,HasTimeLeaderboard,Supported}` | from `arcade-ra-enrich` |
| Save store: multi-slot snapshots, `?seedslot=N` boot-from-snapshot | gateway `SaveStore.cs`, `ArcadeSaveId` |
| Unlock/leaderboard mirror + `Clean` computed legitimacy | `ArcadeAchievementUnlock`, `ArcadeLeaderboardEntry` |

Coverage from the last `arcade-ra-enrich` run: **5,875** cards with achievement sets, **2,555** with
score leaderboards, **1,733** with time leaderboards, **4,378** RA-recognised dumps.

---

## 3. Rejected approaches — do not re-derive these

### 3.1 Hand-curated states — rejected (§1.2)

### 3.2 TAS replay as the *primary* source — rejected on catalog bias

Not primarily a sync problem. **TASVideos' catalog is famous-game biased**, so it would
preferentially fill the mode with Mario and Mega Man — inverted from the goal. Demoted to
*opportunistic tier 1* (§5.4): nice where it lands, never the backbone.

Secondary problems, still real if it is ever revived:
- Source emulators are not libretro. Formats (`.bk2/.fm2/.lsmv/.gmv/.smv/.vbm/.dtm`) differ in input
  encoding *and* lag-frame semantics; getting lag frames wrong desyncs on contact.
- Mitigation worth remembering: **BizHawk imports most formats and can export a uniform input log**,
  and its Lua can dump a per-frame RAM-hash trace. That makes sync a *bisectable diff* rather than a
  mystery, and lets a bot brute-force the small knob space (region, BIOS rev, initial RAM fill, core
  options) until the trace holds.
- Reframe that de-risks it: **desync is an early stop, not a failure.** Snapshot continuously, verify
  each snapshot against the oracle, keep everything banked before divergence.
- TAS states are often hostile seeds (damage-boosting, glitch routes, 1 HP, wrong-warps) — but per
  §1.1 that matters far less here than it would elsewhere.

### 3.3 Lifting save states out of the TAS's native emulator — rejected, impossible ✅

A save state is `retro_serialize` output: CPU registers, PPU/APU internal timing, mid-scanline
position — not just RAM. It does not cross emulators, and no converter exists. Checked the live core
list against where TAS movies actually come from:

| System | Our core | TAS source | Transfer? |
|---|---|---|---|
| SNES | `snes9x_libretro` (default, `pkg/config/config.yaml:305`) | Snes9x-rr, lsnes/bsnes | Maybe — same family, 1.4x/1.5x → 1.6x gap |
| NES | `nestopia_libretro` (`pkg/config/config.yaml:298`) | FCEUX, BizHawk | No |
| Genesis | `genesis_plus_gx_libretro` | Gens-rr | No |
| GBA | `mgba_libretro` | VBA-rr, BizHawk | No |
| N64 | `mupen64plus_next` / `parallel_n64` | mupen64-rr, BizHawk | No |
| PS1 | `mednafen_psx_hw`, `pcsx_rearmed` | PCSX-rr | No |
| GC/Wii | `dolphin_libretro` | Dolphin | No — states are build-locked |

One arguable near-miss out of seven. If anyone ever wants to spend an afternoon: try a snes9x-rr
freeze file in `snes9x_libretro`. Do not plan around it.

### 3.4 "Boot the game and start the challenge" — rejected

A cold boot lands in a logo, an attract loop, or a menu tree. The challenge window gets spent
mashing Start. Every challenge needs a state; there is no cold-start shortcut.

---

## 4. Architecture

Three artifacts, produced offline, consumed at runtime:

1. **A state** — a `retro_serialize` blob at a verified-playable moment, plus its labels.
2. **An objective** — RA achievement, RA leaderboard, or a generic primitive (§6).
3. **A verdict** — did the player satisfy the objective inside the time limit.

The harvester (§5) produces 1 and 2 offline and unattended. The runtime (§7–8) consumes them.

### 4.1 Data model sketch

New tables, all additive:

- **`ArcadeChallengeState`** — `Id`, `ArcadeGameId` (the version row, matching the existing
  save-keying convention), `System`, `Core`, `CoreVersionHash`, `StateBlobPath`, `StateBytes`,
  `HarvestTier` (tas/bot/cheat-assisted), `RichPresenceAtCapture`, `FramesIntoRun`,
  `ThumbnailPath`, `HarvestedUtc`, `HarvesterVersion`, `Retired` (bit).
- **`ArcadeChallengeObjective`** — `Id`, `ArcadeChallengeStateId`, `Kind`
  (ra-achievement / ra-leaderboard / survive / raise-counter), `RaAchievementId?`,
  `RaLeaderboardId?`, `CounterAddress?`, `TargetValue?`, `TimeLimitSec`, `PromptText`,
  `DifficultyBand`, `ObservedWinRate`, `ObservedAttempts`.
- **`ArcadeChallengeAttempt`** — `Id`, `UserId`, `ObjectiveId`, `StartedUtc`, `Outcome`
  (win/loss/abandon), `ElapsedMs`, `FinalValue`.

`CoreVersionHash` is load-bearing: **a state is only valid for the core build that wrote it.** When a
core is rebuilt, its states must be re-validated or retired. Without this column a core upgrade
silently poisons the whole library.

`ObservedWinRate` is what actually calibrates difficulty over time — RA unlock rates (§6.1) are only
the cold-start prior.

---

## 5. The harvester

An offline Go binary in the fork, reusing `Frontend` with the media decorator omitted (`caged.go:79`
confirms the split ✅). No WebRTC, no encode, no pacing floor — spin `Tick` as fast as the core runs,
N instances in parallel on Ziggy.

⚠ Throughput is unmeasured. The first job of C1 is to produce a real number, not to guess one.

### 5.1 The oracle — why this is automatable at all

RA rich presence scripts are per-game, public, and nearly all distinguish menus from gameplay
("At the title screen" vs "Zone 1 Act 2, 3 lives, 2450 points"). Combined with achievement trigger
*priming*, that is a machine-checkable answer to **"is the game live right now?"**

This converts state generation from a curation problem into a **search problem with an automated
success check** — the whole plan rests on it.

### 5.2 The bot

Weighted random input policy: Start-heavy first, then A/B, then directions. Boot sequences are
shallow (2–5 inputs) on the systems that matter, and the failure mode is just "reset and retry."

Per §1.1 the bot's shallow reach is **fine** — depth is not a requirement. Games whose front-ends
defeat a random policy (RPG, strategy, sports) are also bad content for this mode, so
**bot failure is the genre filter.** Budget N minutes of emulated time; on failure, record the reason
and move on.

### 5.3 Rewind-backdating — the trick that makes objectives land

The rewind ring already holds a rolling window of serialized states (`frontend.go:161-172`). So:

1. Bot clears the boot sequence; rich presence confirms gameplay.
2. Bot keeps playing badly. Ring keeps rolling.
3. An achievement fires / a leaderboard start-trigger fires / a measured value crosses a threshold.
4. Reach **back** into the ring and dump the state from N seconds *before* that moment.

The result is a state positioned a tunable distance in front of a known, RAM-verified objective —
and **the rewind depth is the difficulty knob.** ~10 s is a tense beat; ~45 s is a real challenge.

### 5.4 Tiers

| Tier | Source | Yield |
|---|---|---|
| 1 (opportunistic) | TAS replay where it exists and syncs (§3.2) | Deep, set-piece; narrow, famous-biased |
| 2 (**primary**) | Bot bootstrap | Shallow, broad, uniform across the catalog |
| 3 | Cheat-assisted bot — level-select / invincibility to push deeper, **cheats off before the state ships** | Depth on tier-2-only games |

All tiers share one oracle, one snapshot mechanism, one quality gate, one output contract.

### 5.5 The quality gate — deliberately loose

Per §1.1, only three checks:

1. Not a menu, cutscene, or attract loop (rich presence says gameplay).
2. Not already game-over / zero lives.
3. The attached objective is still reachable from here.

Nothing about fairness, health, or item loadout. Being dropped in confused and under-equipped is the
product.

### 5.6 Chunk contract (per `bulk-jobs-must-chunk-and-resume` HARD RULE)

Bounded per invocation, resumable, idempotent, deterministic stop:

- Input: `--limit N --cursor <gameId> --system <s> --tier <t> --dry-run`.
- Per game, one verdict row: `gameId, tier, framesRun, statesKept, objectivesAttached, outcome,
  failureReason`.
- Output per call: `{ processed, remaining, nextCursor, counts }`.
- Idempotent: skip games already harvested at the current `HarvesterVersion` + `CoreVersionHash`.
- The driver loop lives in the CLI caller with a no-progress safety break — never inside one call.
- **No silent caps.** If a run bounds coverage, log what was dropped.

---

## 6. Objectives

### 6.1 RA-derived (the ~5,875 cards with sets)

RA is the right dataset for this goal specifically because **RA set authors over-index on obscure
titles** — the opposite bias from TASVideos.

- **Achievements** = objectives with human-written prompts already attached.
- **Leaderboards** = literally start / cancel / submit / value — a challenge definition, pre-authored.
- **Difficulty is empirical.** `API_GetGameExtended` carries `NumAwarded` / `NumAwardedHardcore` /
  `NumDistinctPlayers` / `Points` / `TrueRatio` / achievement `type`. That payload is **already
  fetched and cached** (`ArcadeController.cs:2276`); the site currently maps only Title / Description
  / Points / Badge (`:2288-2311`), so the difficulty fields are free. Cold-start band: unlock rate
  roughly 10–40%, then let `ObservedWinRate` take over.
- **Prompt rewriting is needed.** RA descriptions are written for someone playing normally, not for a
  45-second cold drop. Filter to short / measurable / non-cumulative, then rewrite into imperative
  challenge prompts (the AI-insights path is the obvious tool).

### 6.2 Generic primitives (the fringe systems with no RA coverage)

RA thins out badly on Pokémon Mini, Supervision, Channel F, Arcadia 2001 — which is exactly where
the best content lives (§9). Objectives with no authored data:

- **Survive N seconds** — needs only a death/reset detector.
- **Raise the number** — discover the score counter by **differential RAM analysis**: run the bot
  repeatedly, flag addresses that climb monotonically during play and zero on restart; discard
  addresses that move while paused or in menus. **Validate the heuristic against RA-authored
  addresses on games where both exist**, then trust it where they don't.

"Make this number go up, we won't tell you how" is arguably more on-theme than a polished
achievement description.

---

## 7. Worker delta

Small, and everything needed is already compiled in.

`bridge.c:69` currently handles exactly two events — `RC_CLIENT_EVENT_ACHIEVEMENT_TRIGGERED` (`:71`)
and `RC_CLIENT_EVENT_LEADERBOARD_SUBMITTED` (`:81`). Add:

- `LEADERBOARD_STARTED` / `LEADERBOARD_FAILED` — the objective's start and fail edges.
- `LEADERBOARD_TRACKER_UPDATE` — live score during the attempt (the HUD number).
- `ACHIEVEMENT_CHALLENGE_INDICATOR_SHOW/HIDE` — "you are in the situation right now."
- Measured progress (`measured_progress`, `measured_percent`) — the "37/100" readout.
- Poll `rc_client_get_rich_presence_message()` — the oracle *and* the state label.

⚠ **Threading:** `rc_client` is not thread-safe. Every new call stays on the emulator goroutine, per
the existing rule at `cheevos.go:8`. The harvester must respect this too.

New wire packet for challenge HUD updates alongside the existing `t=160`. Note the transport trap
already solved once: worker→browser pushes ride the negotiated data channel, and
`cloudRetroClient.js` needs the `dc.onmessage` route (fixed for `t=160`; reuse it).

---

## 8. Runtime — playing a challenge

1. Site picks a state + objective (weighted by system, unseen-by-this-user, difficulty band).
2. Room boots seeded from the challenge state — reuses the existing `?seedslot`-style path, but from
   the challenge store rather than the user's save store.
3. Overlay: the prompt, a countdown, and the live value from the tracker / measured progress.
4. Win on the objective's trigger; lose on timeout or fail edge. Record an `ArcadeChallengeAttempt`.
5. Next round: new game, new state. Rounds should be seconds apart, not minutes — **boot latency is
   the main UX risk** and should be measured early (⚠ pre-warming a worker pool may be required).

### 8.1 Legitimacy — challenge runs need their own kind

Every challenge boots from a save state, and today `frontend.go` tags **any** restored session
`Savescum` before the player touches a button — it cannot distinguish "resume my game" from "seeded
by the system." Challenge attempts must be their own run kind, excluded from `Clean` scoring and
from the RA mirror entirely.

This is the **same open item** as distinguishing `SeedSlot > 0` from auto-continue in
`arcade-clean-start-plan.md`. Fix it once, there.

Also: challenge rooms should not write to the player's casual Continue slot — the competitive-room
harvest suppression in the gateway (`SaveStore.SetCompetitive`/`IsCompetitive`) is the pattern to
copy.

---

## 9. Eligibility

**Hard gate: the core must serialize.** That is the 2D / rewind-armed tier — no PSP, no PS2, no LibCo
cores, flaky N64 (`arcade-save-persistence-by-core`). ⚠ Confirm per core with real `rewind-diag`
numbers before including it.

That gate is *aligned* with the goal rather than fighting it. The live core list is already stacked
with fringe systems: `amiarcadia`, `freechaf`, `freeintv`, `gearcoleco`, `o2em`, `pokemini`,
`potator`, `prosystem`, `stella`, `vecx`, `mednafen_lynx`, `mednafen_ngp`, `mednafen_pce`,
`mednafen_vb`, `mednafen_wswan` — Channel F, Intellivision, ColecoVision, Odyssey², Pokémon Mini,
Supervision, Lynx, Neo Geo Pocket, Virtual Boy, WonderSwan, Vectrex.

Shallowest boot sequences, cheapest serialize, maximum "what is this." **Start here, not with the
famous systems.**

---

## 10. Phases

| Phase | Deliverable | Exit criteria |
|---|---|---|
| **C0** Oracle spike | Surface rich presence + trigger priming from the worker; log them for one known game | Rich presence string appears in the log and flips menu→gameplay at the right moment |
| **C1** Headless harness | Offline binary: load core + ROM, drive `Tick`, inject input, snapshot | One game booted, N frames run, a state written and reloaded. **Produces the real throughput number.** |
| **C2** Bot + gate | Input policy, gameplay detection, quality gate, chunked CLI | 20-game pilot on 3 fringe systems, honest hit-rate reported |
| **C3** Objectives | RA attach + difficulty bands; rewind-backdating; generic primitives | Every kept state has ≥1 objective; prompts read sanely |
| **C4** Runtime | Seeded boot, HUD, timer, verdict, attempt rows, run-kind exemption (§8.1) | One challenge playable end to end in a real room |
| **C5** Scale | Full sweep, retirement on core rebuild, `ObservedWinRate` recalibration | Library across every eligible system, difficulty self-tuning |

C0 and C1 are the honest go/no-go. If the oracle is unreliable or the harness is slow, this dies
cheaply and on purpose.

---

## 11. Traps

- **⚠ TAS replay needs power-on with NO SRAM.** The whole save pipeline exists to seed `.srm`/`.dat`
  into `/saves`. A harvester that inherits a seeded save desyncs on frame one, and it will look like
  a sync bug rather than a config bug. Clear the save dir before any replay.
- **A state is bound to its core build.** Rebuild a core → re-validate or retire its states
  (`CoreVersionHash`, §4.1).
- **`config.yaml` vs `config.worker-gl.yaml`** — check which file the workers actually read before
  changing core options; there is prior history of editing the dead one.
- **ROM hashing does not exist yet** — matching is name-based (`NormalizeDump`). RA's full rhash
  suite is vendored and unused; doing it properly would improve RA matching *and* any TAS ROM check.
- **Never age-gate the arcade** (`arcade-never-gate-by-age-rating`) — random selection must not grow
  a rating filter.
- **Prompt text is user-visible content.** An LLM rewrite pass needs a review path, not a blind bulk
  write (`mark-bulk-inserts-for-review` pattern).

---

## 12. Open questions

1. **Bot hit rate per system.** Unknown until C2. The whole plan's value scales with it.
2. **Round-to-round latency.** Is a fresh seeded room fast enough for WarioWare pacing, or does this
   need a pre-warmed worker pool?
3. **Objective reachability.** How often is an attached objective actually satisfiable from a
   backdated state? Needs measurement, then a tuning pass on rewind depth.
4. **Multiplayer shape.** Same state to N players simultaneously (race), or pass-the-controller? The
   original show was head-to-head; the room model supports both.
5. **Generic score discovery accuracy.** Does differential RAM analysis hold up on 8-bit fringe
   systems, or does it produce too many false counters to be usable?
6. **Do states go stale for other reasons** — JIT ROM cache eviction re-materialising a different
   dump, per-game config changes altering core options?
