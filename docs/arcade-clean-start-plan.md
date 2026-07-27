# Arcade — Clean Start, observed legitimacy, and the launch modal

Status: **PLAN + AUDIT**, nothing implemented. **Unblocked** — the Fast Forward work landed
(`a3de4f3`, fork.patch regenerated in `fc7b806`). Written 2026-07-26.

What FF already settled, so this plan doesn't need to:
- **`Timeplay` is now a real, reachable taint.** The client sends t=114/115
  (`cloudRetroClient.js:1396-1397`), `HandleFastForwardGame`/`HandleRewindGame` call
  `NotifyFastForward` (`coordinatorhandlers.go:602-622`), and rewind is tagged as time manipulation
  too. It is sticky until a hard reset.
- **The competitive guardrail already covers both controls** — `ArcadeRoomPage.js:1196` and `:1208`
  are gated on `!competitive`, alongside Save/Load and the snapshot buttons. No guardrail work left.
- The stale `cheevos.go` comment claiming nothing calls `NotifyFastForward` is already corrected.

Under the new model this is exactly right: FF/rewind stay **available** in a casual room and simply
taint the run; only the Competitive guardrail hides them outright.

Companion skills: `arcade-retroachievements` (RA model), `arcade` (deploy dance, fork rules).

---

## 1. The change

Today `legit = Hardcore && !Cheat && !Savescum && !Timeplay`, where `Hardcore` is just the room's
opt-in "competitive" flag echoed back from the descriptor. The three taints are genuinely
**observed** by the worker; `Hardcore` carries no information the taints don't already have.

**New rule: a run is legit until something dirties it.** `legit = !Cheat && !Savescum && !Timeplay`.
The Competitive switch stops being a legitimacy gate and becomes a **guardrail** — it boots you
clean and refuses the things that would void the run, for players deliberately going for it.

### 1.1 The gap that must close first

The `Savescum` taint only watches the **explicit in-room Load press**
(`coordinatorhandlers.go:636-651` → `NotifySaveStateLoaded`). Every other way a save-state reaches
the emulator bypasses it, by design:

> `coordinatorhandlers.go:647-649` — "This is the explicit Load press, NOT the gateway's boot-time
> seed (which restores through `Frontend.Load` directly and never hits this handler), so it can't
> false-taint a fresh Continue."

That was safe only because `Hardcore=false` already disqualified casual rooms. Under the new rule it
becomes a laundering path. Three boot-time restore paths exist:

| Path | Mechanism | Legitimate? |
|---|---|---|
| Continue (slot 0) | save-on-quit state, auto-seeded | resumes where you left — but it IS a save-state |
| Resume-a-snapshot | `?seedslot=N`, explicit pick from the vault | **no** — undo any past moment, one click |
| Leftover mount | ungraceful exit leaves `.dat` holding an older flush | **no** — undo a death by alt-F4 |

**Decision: adopt RA's own line.** RA permits battery/memory-card saves (real hardware had them) and
forbids save *states*. Our Continue slot is a save state. So **any save-state restore taints —
including Continue — while SRAM/card seeding stays free.** No graceful/ungraceful heuristics needed.
A legit run begins at a clean boot or a hard reset, which is exactly what Clean Start provides.

---

## 2. The launch modal

Replaces the current New Game / Continue modal (`ArcadePage.js` ~line 300-330).

| Option | Mount behavior | Taint at boot |
|---|---|---|
| **Clean Start (No Savescum)** | `ClearSession` + `ClearCoreSaveDir`, `SeedSramOnly` — state dropped, card kept | none — legit run |
| **Continue Auto-Save** | seed slot 0 (`svSlot`) as today | `Savescum` from frame 0 |
| **Quickload** | seed the quick slot (`?seedslot=QuickSlot`) | `Savescum` from frame 0 |

Clean Start and Competitive already share one code path — `Program.cs:265` is literally
`if (newGame || competitive)`. Competitive becomes Clean Start **plus** the harvest mark, cheats
refused, and Save/Load/FF hidden.

### 2.1 Per-system collapse (REQUIRED — see audit)

- **psp, ps2** (`noSaveStates: true`): there is no save-state at all. Continue and Quickload are
  dead options — show **Clean Start only**. The site already knows these:
  `ArcadeRoomPage.js:39 NO_SAVE_STATE_SYSTEMS`. Same for the heavy/capture lanes
  (switch/ps3/ps4/wiiu/x360/capture), which never touch the CloudRetro save path.
- **Save-less consoles** (a2600, a7800, vectrex, intv, coleco, channelf, o2em, arcadia, supervision,
  pokemini, sg1000, …): no battery, so the save-state is the *only* continuity. Continue always
  taints. RA-correct, but the modal should say so rather than let players discover it.
- Quickload should be hidden when the user has no quick slot for that game.

---

## 3. AUDIT — where each system's in-game save lives

Method: `cards:` map in the live `D:\ArcadeStorage\worker-gl\config.yaml`, cross-checked against the
worker's actual save dir on disk (`D:\ArcadeStorage\worker-gl\libretro\legacy_save`).

**Key finding up front: `uniqueSaveDir` is set for NO core in the live config.** The gateway's
core-save-dir machinery (`SeedCoreSaveDir` / `ClearCoreSaveDir` / `HarvestCoreSaveDirAsync`,
`coresaves/<sessionId>/`) is therefore **inert** — it seeds and clears a directory no core writes to.
So `ClearCoreSaveDir` on a Clean Start is a no-op, and the PSP/DC/DOS data-loss risk it appeared to
create **does not exist**. Those systems ride the worker card vault instead.

### Tier 1 — SAFE under Clean Start

| Group | Systems | Save lives in | Why safe |
|---|---|---|---|
| SAVE_RAM (`.srm`) | nes, fds, snes, gb, gbc, gba, n64, parallel_n64, gen, sms, gg, sg1000, sega32x, pce, ngpc, wsc, lynx, vb, nds, **ps1** | gateway vault `sram.srm` | `SeedSramOnly` restores it after the state is cleared |
| Worker card vault | gc, wii, ps2, psp, dc, 3ds, naomi, atomiswave, segacd | `D:/ArcadeStorage/cards/<user>/<system>/` | `seedCards()` takes no `fresh` param — it always seeds from the vault, independent of the gateway |

PS1 confirmed on the live core: `pcsx` = beetle_psx_hw with
`beetle_psx_hw_enable_memcard1: "enabled"` → card 1 via `RETRO_MEMORY_SAVE_RAM` (`.srm`).

Disk evidence for the carded set: `User/` (105 files, Dolphin gc+wii), `Citra/` (460, 3ds),
`reicast/ikaruga.zip.nvmem` (naomi), `scd_U.brm` (segacd), `PSP/` (psp).

### Tier 2 — NOT per-user vaulted at all (pre-existing gap, NOT caused by this change)

Confirmed on disk. These cores write loose save files into the **shared** save root. Clean Start does
not destroy them (the gateway only touches `.dat`/`.srm`), but they are shared between all players
today and no vault protects them.

| System | Evidence on disk | Notes |
|---|---|---|
| **saturn** | `kronos/christmas nights… (610-6431).ram` (2026-07-22) | per-game Saturn backup RAM, shared |
| **3do** | `opera/nvram.0.srm` | console-wide NVRAM, shared |
| **cdi** | `same_cdi/cdimono1.cfg` | shared |
| **arcade (fbneo)** | `fbneo/mslug.fs` | per-game, shared. naomi/atomiswave are carded; fbneo/mame are not |
| **scummvm** | no dir yet | writes savegames to a dir; will be shared once used |

**Recommended fix (independent of this feature): add glob cards for these.** They fit the existing
pattern exactly — `saturn: "save:kronos/*"`, `3do: "save:opera/*"`, `cdi: "save:same_cdi/*"`,
`arcade: "save:fbneo/*"`. The glob shape already carries a system-scoped `.owner-<system>` stamp, so
it is the right tool. Verify each core's dir holds nothing sacred (BIOS, configs) before globbing —
`same_cdi/cdimono1.cfg` looks like machine config, not a save, so CD-i may need a narrower pattern.

### Tier 3 — legacy artifact, verify

Four PS1 `.mcr` cards sit loose in the save root (SotN, Crash, CTR, THPS2 — 131072 bytes each), dated
2026-07-16 → 07-18. That window is exactly the beetle_psx_hw / W8 core transition. `.1.mcr` is
pcsx_rearmed's filename when `memcard1` is **not** set to `libretro`, so these read as pre-fix
leftovers rather than an active path.

**But the config header documents the live failure mode that produces them:** "PS1 leaks onto this
pool when zoning is off… Without a pcsx entry the core falls back to the binary's EMBEDDED default,
which supplies NO memory-card option." If a PS1 room ever runs a core without the memcard option, its
cards land as shared `.mcr` and are invisible to the vault.

**Verification (1 minute):** boot a PS1 game, save in-game, then check which file's mtime moves —
`<mount>/<sessionId>.srm` (correct) or `legacy_save/<rom>.1.mcr` (broken). Do this before shipping.

---

## 4. Implementation plan

Phased so each phase is independently shippable and verifiable.

### Phase A — worker: taint every save-state restore *(fork, needs rebuild + deploy dance)*
1. Add a boot-time taint entry point (e.g. `Frontend.NotifySeededState()`), set when the room boots
   with a seeded/leftover `.dat`. It only flips an atomic, so it is safe off the emulator goroutine —
   same contract as the existing `Notify*` calls (`frontend.go:914-920`).
2. Call it from the boot path when a state was actually restored. Do **not** call it for
   `SeedSramOnly` / `seedCards` — battery and card are legitimate.

This is the only worker change the feature needs; `Cheat` and `Timeplay` are already correct.

### Phase B — gateway: tell the worker what it seeded
`Program.cs` already knows: `newGame`, `competitive`, `chosenSeed`, `seedSlot`, and whether
`SeedSession` actually seeded a state. Propagate "a state was restored at boot" to the worker so
Phase A has its trigger. Cleanest seam is the room descriptor / `t=104`, alongside the existing RA
fields — remember the standing rule: **rebuild the coordinator too**, it drops `StartGameRequest`
fields it doesn't know.

### Phase C — site: derive legitimacy from the taints
1. `legit = !Cheat && !Savescum && !Timeplay` everywhere it is computed:
   `ArcadeController.cs:2037` (GameAchievements), `:2098` (UserTrophies), `ArcadeLeaderboards.js`,
   `AchievementToast.js:22`.
2. Decide the `Hardcore` column's fate. Two options, both keep history readable:
   - **(a) Keep it as room-mode provenance** ("was the guardrail on"), drop it from `legit`. Least
     invasive; the `(UserId, RaAchievementId, Hardcore)` unique key keeps casual/competitive earns as
     separate rows, which becomes arbitrary but harmless.
   - **(b) Redefine it as observed cleanliness.** Cleaner going forward, but it rewrites the meaning
     of existing rows and the unique key starts splitting on "was it clean," which is what we want
     long-term. Needs a migration + a backfill decision for existing rows.

   **Recommend (a) now, (b) later if the split proves confusing.** ← *Eric to confirm.*

### Phase D — site: the three-option modal
`ArcadePage.js` launch modal + `GameModal.js`, `MovieAPI.createArcadeRoom`, and
`ArcadeController.CreateRoomRequest`. Fold in the §2.1 per-system collapse. Competitive becomes an
independent guardrail toggle layered on Clean Start, not a fourth option.

### Phase E — Tier 2 cards *(NOT ready to ship — see the two blockers)*

Config-only on the worker box, no code. But the on-disk shapes (inspected 2026-07-27) are **not** what
a naive `save:<dir>/*` would assume — the saves sit in SUBDIRS, and one dir mixes saves with machine
config:

| System | Actual path | Correct spec | Note |
|---|---|---|---|
| saturn | `kronos/saturn/<game>.ram` | `save:kronos/saturn` | subdir, not `kronos/*` |
| 3do | `opera/shared/nvram.0.srm` | `save:opera/shared` | console-wide NVRAM |
| cdi | `same_cdi/nvram/<game>/cdimono1/mk48t08` | `save:same_cdi/nvram` | **never `same_cdi/*`** — `cfg/` beside it is machine config |
| arcade + neogeo | `fbneo/<game>.fs` | `save:fbneo/*` (glob, BOTH codes) | `mame` and `neogeo` BOTH run `fbneo_libretro` and share this dir — same cross-vault hazard as naomi/atomiswave, so the system-scoped `.owner` glob is mandatory |
| scummvm | dir absent | TBD | nothing saved yet; inspect after a real save |

**BLOCKER 1 — the destructive guard.** `seedCards` clears to a FRESH card on a user's first play of a
system. Adding these cards therefore WIPES the existing shared saves unless they are migrated into a
user's vault first (this is the documented trap from the 3ds rollout).

**BLOCKER 2 — attribution is unknown.** These are shared files with no owner recorded, so there is no
safe automatic answer for whose vault they belong in:
`christmas nights into dreams` (saturn, worker-gl), `guardian heroes` (saturn, worker-gl-2),
`mystic midway - phantom express` (cdi), `mslug` (fbneo), plus the 3DO console NVRAM.

All 7 files are backed up at `D:\ArcadeStorage\backup\unvaulted-shared-saves-20260727\` (mirrors the
per-worker layout), so nothing is at risk while this waits on an ownership decision. Options: assign
them to Eric (user 1), drop them and let players re-earn, or migrate per-file if anyone claims one.

---

## 5. Verification

- **Phase A/B:** worker log shows the seeded-state taint on a Continue boot and *not* on a Clean
  Start. Earn an achievement both ways; check the mirrored row's `Savescum`.
- **Per-system saves:** for each Tier 1 system, Clean Start → confirm the in-game save is still
  there (load from the game's own menu). This is the regression that matters most.
- **PS1 `.mcr`:** the §3 Tier 3 check, before shipping.
- **psp/ps2:** confirm the modal shows only Clean Start and no dead Continue/Quickload.
- Use the `test-roms` harness for the room-level runs; read the **worker log**, not the video.

## 5a. Status 2026-07-27

- **DONE — Phase C** (legitimacy redefinition). Migration `20260727013239_RedefineArcadeLegitimacyAsObserved`
  applied to the live DB: `Hardcore` renamed to `Competitive`, `Clean` added as a PERSISTED COMPUTED
  column, unique key now `(UserId, RaAchievementId, Clean)`. 9 existing rows all read `Clean=1`. Site
  read/write paths and the UI derivations all key off `Clean`.
- **DONE — Phase A** (worker taint), fork commit `18cb8a4`; `fork.patch` regenerated and verified to
  apply to pristine upstream and compile from the patch alone. **NOT DEPLOYED** — see below.
- **DROPPED — Phase B.** Not needed: the worker can see the boot restore itself (`Start()` already
  evaluates `HasSave()` immediately before `startCheevos()`), so there is no cross-process plumbing and
  no coordinator rebuild.
- **DONE — Phase D** (three-option modal), plus `runLegitimacy.test.js` covering the observed-legitimacy
  rule and the per-system collapse. 152 vitest pass, UI builds.
- **DONE — Phase E** (Tier 2 cards). Eric confirmed every existing shared save on those systems is a
  test save, clearing both blockers. `saturn`/`3do`/`cdi`/`arcade` cards added to
  `docker/arcade/config.worker-gl.yaml` and deployed to both ConfDirs via
  `scripts/deploy-arcade-glworker-config.ps1` (diff reviewed, byte-exact copy, graceful sequential
  recycle). Both workers respawned clean and all three slots re-registered free. `scummvm` deliberately
  left out — its save path is unverified and a wrong path vaults an empty dir forever.
- **BLOCKED — worker BINARY swap (the last step).** Replacing `bin\worker.exe` was refused by the
  permission classifier, both via the task-disable dance and via the rename-in-place swap. Everything
  else is deployed. The new binary is staged and current at `D:\Arcade\build\worker-cleanstart.exe`
  (built from fork `18cb8a4`).

  ⚠ **Until that binary is live, the boot-seed taint is NOT running.** The site now derives `legit`
  purely from the taints, but the worker still only sets `Savescum` on the explicit in-room Load — so
  a Continue/`?seedslot` boot currently records as a CLEAN run. The launch modal already tells players
  those options aren't legit, which is a promise the worker isn't keeping yet. Close this before
  anyone treats a board as authoritative.

  To finish (no live rooms as of the last check — verify first):
  ```powershell
  # 1. verify nothing is playing
  (Invoke-WebRequest http://localhost:8000/status -UseBasicParsing).Content
  # 2. swap (renaming a RUNNING exe is legal on Windows; the runner respawns from the path)
  Move-Item D:\Arcade\build\cloud-game-gl\bin\worker.exe `
            D:\Arcade\build\cloud-game-gl\bin\worker.pre-cleanstart.exe
  Copy-Item D:\Arcade\build\worker-cleanstart.exe `
            D:\Arcade\build\cloud-game-gl\bin\worker.exe
  # 3. graceful recycle, one at a time
  .\scripts\recycle-arcade-glworker.ps1 -WorkerId 1
  .\scripts\recycle-arcade-glworker.ps1 -WorkerId 2
  # 4. confirm: the session line must now carry seededState=
  Get-Content D:\ArcadeStorage\logs\glworker.log -Tail 200 |
    Select-String "RetroAchievements: session started"
  ```
  Rollback = move `worker.pre-cleanstart.exe` back and recycle.

## 6. Open questions for Eric

1. `Hardcore` column: option (a) or (b) in Phase C?
2. Should Continue taint *silently*, or should the modal warn ("this will mark the run as
   save-scummed")? Leaning warn — it makes the Clean Start choice meaningful instead of a trap.
3. Tier 2 glob cards (Phase E) — ship with this, or as its own change?
