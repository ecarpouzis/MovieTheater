# Arcade — Durable, User-Scoped Saves (and the road to cross-system sync)

**Status:** design/plan (not yet built). Supersedes the current room-scoped save behavior.
**Goal (near term):** every save belongs to a **user + game**, not a room. Whoever starts a
room can leave and later resume *their* save (or pick among several). **Goal (ultimate, NOT built
yet):** the same in-game saves sync to/from other emulation setups (EmuDeck / RetroArch) so a game
started on a Steam Deck can be continued online and vice-versa.

Read alongside the `arcade` skill and `docs/arcade-jit-cache.md` — the save layer reuses the same
gateway seam (materialize state before play, clean up after) that the JIT ROM cache uses.

---

## 1. The load-bearing facts (verified from CloudRetro source, pinned `13852a7`)

Everything below is anchored in the emulator's actual behavior — the design is shaped by these,
not around them.

- **Save files are just two files named by a session id**, in the `/saves` mount
  (`emulator.storage`): `<MainSave>.dat` (full save **state**) and `<MainSave>.srm` (**SRAM** /
  battery / cartridge memory). `storage.go:39-40`.
  **⚠ `saveCompression` is currently ON** — the live `/saves` dir holds `*.dat.zip` / `*.srm.zip`.
  We **set `saveCompression: false`** in config.yaml so files are raw `.dat`/`.srm` (a zip-wrapped
  `.srm` can't be handed to EmuDeck/RetroArch). The existing `.zip` test-saves are room-scoped
  throwaways; they're abandoned by the new scheme, no migration needed.
- **`MainSave` = the room id.** At room start the worker sets the session id to `rq.Rid`
  (the client-supplied room id) or, if empty, a generated `<randomhex>___<gameName>`.
  `coordinatorhandlers.go:106-130`, `launcher.go:70-72`.
- **★ I can choose the save id.** A **non-empty** `room_id` supplied at GAME_START that has no live
  room **creates a session with exactly that id** — *provided* it's formatted `<myid>___<gameName>`
  (it must contain `___`, and the suffix must resolve to a real game via the library scan; a
  non-`___` id is **rejected**, and when `Rid` is set `rq.Game` is ignored — the game comes from the
  suffix). `coordinatorhandlers.go:85-113`, `userhandlers.go:28-79`. **This is the whole mechanism:
  it lets the gateway know the save filename before the game boots — no new emulator patch needed.**
- **One save file per id — no slots.** SAVE (t=106) / LOAD (t=107) always hit the single
  `<id>.dat`/`.srm` for the current room; the packets carry no slot index. Multi-save is *our*
  concern, in *our* store. `cloud.go:15`, `coordinatorhandlers.go:255-277`.
- **Boot auto-restores iff the file exists** (`HasSave()` = `os.Exists(<id>.dat)`);
  `frontend.go:306-314`. So **seeding `<id>.dat` before boot ⇒ the game resumes it automatically.**
- **Autosave is on in our stack** (`autosaveSec: 60`) and **save-on-close** fires when the last
  player leaves — but only if a save already exists (`HasSave` guard). `frontend.go:294-301,529-549`,
  `room.go:130-134`. Because we **seed**, `HasSave` is true from t=0, so close always flushes.
- **Seed/harvest is pure file I/O**, independent of the ROM: the name depends only on the id + dir.
  `storage.go`. ✅ confirms the gateway can shuttle files in/out.
- **Two gotchas to honor:**
  - **PS1 memory cards may escape the session dir.** `.srm` (SAVE_RAM) is session-scoped and
    harvestable, but `pcsx_rearmed` lacks `uniqueSaveDir`, so any card files it writes itself land in
    a **shared, global** `libretro/legacy_save` dir. `nanoarch.go:147`, `frontend.go:187-191`.
    → We set **`uniqueSaveDir: true` for pcsx** (config.yaml) and verify empirically that PS1 cards
    then land per-session; otherwise fall back to harvesting the `.srm` only.
  - **Never enable CloudRetro's own cloud provider.** A configured S3 `storage.provider` overwrites
    a locally-seeded state at boot (`cloud.go:16-35`). Our stack keeps `provider: ""` — **we** are
    the durable layer, not CloudRetro's cloud.

### Portability truth (drives the whole sync story)
- **`.srm` (SRAM / battery / memory-card) is the portable artifact.** It's raw cartridge memory,
  effectively core-agnostic — RetroArch/EmuDeck read it. **This is the sync target.**
- **`.dat` (save *state*) does NOT port** across systems: it's a `retro_serialize` snapshot, valid
  only for the *same core + version*. Great for "resume the exact frame online"; useless for
  cross-device sync. **States stay online-scoped; only SRAM syncs.** The plan bakes this in rather
  than pretending states are portable.

---

## 2. Architecture

**DECIDED** (Eric, 2026-07-03): metadata lives in the **app DB** (small — handled like movie
posters / boardgame images), blobs live on **disk** under `D:\ArcadeStorage\savestore`. Budgets:
≤100 GB on disk (saves are KB–MB, so effectively unbounded — a safety cap, not a real limit),
≤~200 MB in the DB (thousands of tiny metadata rows). Responsibilities:

```
Site (k8s pod)                          Gateway (Ziggy, next to /saves + the ROMs + EmuDeck's world)
──────────────                          ─────────────────────────────────────────────────────────
mints capability tokens                 seeds a chosen save into /saves BEFORE the game boots
  (join + save-scoped)                  harvests /saves → D:\ArcadeStorage\savestore DURING + after
enforces auth / game age gate           records each harvest as an ArcadeSave row (see below)
renders the save-browser UI             serves the blob for download (tokened); reads its local store
reads ArcadeSave rows for the dropdown  this is where EmuDeck sync attaches later
```

**Where the metadata write happens.** Harvest runs on the gateway (only it can read `/saves` on
Ziggy), but the `ArcadeSave` row lives in the shared app DB. Two ways to bridge, to finalize in S1:
(a) **gateway writes the row directly** via a narrow `DbContext` — the shared DB
(home.neilb.dev/MovieSite) is network-reachable from Ziggy, and the gateway already holds a secret;
simplest. (b) gateway **POSTs an authenticated callback** to a new internal site endpoint that
upserts the row — keeps the gateway app-DB-free. Recommendation: (a), unless we want to preserve the
gateway's DB-free purity. Blobs stay on Ziggy either way and are downloaded through the gateway
(the k8s pod can't read Ziggy's disk).

### The deterministic save id (user + game + slot)
- A save is identified by **user + game + slot** (Eric's model). Internally the CloudRetro session id
  is `sv-<userId>-<slotId>___<gameName>`: the `___<gameName>` suffix is **mandatory** (CloudRetro
  rejects a non-`___` id and derives the game from the suffix), and `<userId>-<slotId>` is the opaque
  prefix. `slotId` is a **stable int**; the user-facing **slot name is an editable label** on the
  `ArcadeSave` row, so renaming a slot never churns filenames or the id.
- The site already carries `cloudRetroRoomId` inside the capability token. Today it's **empty** for
  creators ("create on a free worker"). We change creators to carry this deterministic id instead
  (chosen by the resume dropdown — see §4). The room-confinement check (query `room_id` must equal
  the token's id) keeps working; Bind becomes confirmation of a known id rather than discovery.
- Because the id is in the token, the **gateway knows the exact save filename before the game boots**
  and can seed/harvest it with zero races.

---

## 3. Data model

**Blobs on disk** (`D:\ArcadeStorage\savestore`, NVMe, backed up — same pattern as movie posters):
```
savestore/
  <userId>/<gameId>/
    sram.srm                    # canonical in-game save (THE sync artifact)
    slot-000.dat                # "Continue" slot (latest state)
    slot-001.dat                # named snapshot slots (label lives in the DB, not the filename)
    ...
```

**Metadata in the app DB** — new `ArcadeSave` table (tiny rows; the poster-pattern the user chose):
`{ Id, UserId, ArcadeGameId (FK), System, Kind ("sram"|"state"), SlotId (int), Label?, CoreName?,
CoreVersion?, StorageRelPath, SizeBytes, Sha256, Source ("online"|"imported"), IsAutosave,
CreatedUtc, UpdatedUtc }`.
- `CoreName/CoreVersion` gate whether a *state* is safe to load (mismatch ⇒ offer the SRAM instead).
- `System + <ArcadeGame>.CloudRetroGameKey + RomHash` are the future EmuDeck mapping keys — persist
  them from day one so sync needs no migration. (Add `RomHash` to `ArcadeGame` or the save row.)
- Migration is deliberate against the shared live DB (read the SQL, apply) — the standing hard rule.

**Config keys (gateway `appsettings` → `SaveStore`, added):**
`{ StoreDir (= D:\ArcadeStorage\savestore), SavesMountDir (= SAVES_DIR = D:\ArcadeStorage\saves),
MaxBytes (~100 GB safety cap), HarvestDebounceMs, MaxStatesPerGame }`. Enabled only when StoreDir +
SavesMountDir are both set (empty = disabled = today's room-scoped behavior).

---

## 4. Session lifecycle

**Create room (site) — the resume dropdown.** When a game is selected, a dropdown lists that user's
existing **save slots** for it (from `ArcadeSave` rows) plus **"New game"**. Picking a slot resumes
it; "New game" starts fresh (and creates a new slot on first save). The site mints the token with the
deterministic `cloudRetroRoomId` for the chosen `slotId` and a flag for which save to seed.

**Gateway `/w/{token}` (before forwarding — same spot as JIT ROM extract):**
1. JIT-extract + pin the ROM (existing).
2. **Seed:** copy the chosen `slot-NNN.dat`→`/saves/<id>.dat` and `sram.srm`→`/saves/<id>.srm`.
   For **New game**, instead *delete* any stale `/saves/<id>.*` so `HasSave` is false and the game
   boots clean. (Optionally have the client fire one t=106 shortly after boot so even a <60 s fresh
   session leaves a harvestable file.)
3. Forward the WS. The worker's GAME_START uses `<id>` → auto-restores the seeded state.

**During play:** autosave (60 s) and manual saves write `/saves/<id>.*`. A **harvest watcher** in the
gateway (debounced on file mtime) copies changes back into the store, updating `sram.srm` and the
active `slot-NNN.dat`. This captures progress continuously, so an unclean disconnect still persists.

**Room close:** save-on-close flushes (guaranteed, since we seeded), then a final harvest sweep runs.

**Mid-session "load a different snapshot":** gateway swaps that slot's bytes into `/saves/<id>.dat`,
the client sends t=107 LOAD → the running core restores it. **"Snapshot current":** gateway copies
the live `/saves/<id>.dat` into a new `slot-NNN` with the user's label.

**Multiplayer:** the room id encodes the **creator's** (user, game) — guests join the creator's world
and the harvest updates the **creator's** saves. Document this as the intended ownership rule.

---

## 5. UI/UX
- **Resume picker** on the game card / room-create dialog: Continue • pick a snapshot • Fresh.
- **"My Saves"** management (per game): list snapshots with labels/timestamps, rename, delete
  (guarded — never bulk-delete without confirmation), and **download / upload** a save file. Upload
  (`source: imported`) is the **manual MVP of sync** and ships in the near-term phases.
- In-room: Save / Load already exist (t=106/107); add **Snapshot** (name it) and a **Load snapshot…**
  chooser wired through the gateway file-swap.

---

## 6. The EmuDeck / cross-system sync endgame (designed, NOT built now)

Everything above is built so that sync is an *attachment*, not a rewrite:
- **Sync only SRAM/memory-cards** (`sram.srm`), never states (see §1 portability truth). The store
  already keeps SRAM as a first-class, per-(user,game) canonical file.
- **Mapping** to EmuDeck/RetroArch is `(system, rom basename/hash, core family) ↔ <rom>.srm` in
  RetroArch's `saves/`. We already persist `system` + `gameKey` + (add) `romHash`, so the mapping is
  deterministic when we build it. No-Intro naming on the R: sets makes basenames line up.
- **Transport options to evaluate then** (not now): (a) a folder synced by Syncthing/rclone between
  the store's `sram/` view and EmuDeck's `saves/`; (b) an import/export REST pair + a small
  Deck-side script; (c) the manual upload/download UI (already in §5) as the zero-infra fallback.
- **Conflict rule** (honor the global "never destructive without a guard" rule): last-writer-wins by
  `sha256 + updatedUtc`, **surface conflicts**, keep the losing copy as a `.conflict` backup — never
  silently clobber a save.
- **Reality to set expectations:** SRAM ports across core families (it's cartridge memory); *states*
  do not, and even SRAM won't help if one side uses a wildly different emulator. Sync promises
  "continue your in-game save," not "resume the exact frame."

---

## 7. Per-system specifics
| System | In-game save (portable) | State (online-only) | Notes |
|---|---|---|---|
| NES/SNES/GB/GBC/GBA/Genesis | `.srm` (battery) | `.dat` | SRAM only exists for games with battery saves; password games have none. |
| N64 | `.srm` (EEPROM/SRAM/FlashRAM) | `.dat` | mupen exposes SAVE_RAM → harvestable. |
| PS1 | memory card via `.srm` (verify) | `.dat` | **Set `uniqueSaveDir: true` for pcsx** and confirm cards land per-session; else harvest `.srm` only. Multi-disc later. |

---

## 8. Phased implementation

- **S0 — verify (cheap, do first).** Empirically confirm: (a) a deterministic `sv_..._..___<game>`
  id boots and names the save file as expected; (b) seeding a `.dat` before boot auto-resumes;
  (c) PS1 `.srm`/`uniqueSaveDir` behavior. Use the `test-roms` harness + a look at `/saves`.
- **S1 — gateway save store + seed/harvest.** Store + index, the harvest watcher, seed/delete in
  `/w/{token}`, save-scoped capability tokens, the save API (list/download/upload/snapshot). Config
  keys + off-by-default. Unit tests mirroring `RomCacheTests` (seed→boot→harvest→resume; fresh clears;
  snapshot/rename/delete guards).
- **S2 — site wiring + deterministic id.** Creators mint the deterministic `cloudRetroRoomId`; Bind
  becomes confirm; save-scoped token minting; MovieAPI wrappers.
- **S3 — UI.** Resume picker, My Saves, in-room Snapshot / Load-snapshot, manual import/export.
- **S4 — sync (separate future project).** Pick a transport, build the mapping + conflict layer per
  §6. Explicitly out of scope now.

## 9. Decisions — RESOLVED (Eric, 2026-07-03)
1. **Store:** metadata in the app DB (poster-pattern), blobs on disk under `D:\ArcadeStorage\savestore`
   (D: is the 990 PRO NVMe; F: the repo drive is a spinning HDD → the ROM/save mounts want the SSD).
2. **Save id:** deterministic room id, **no new emulator patch**; id = user + game + **slot**.
3. **Retention:** don't lose saves — ≤100 GB on disk (effectively unbounded for KB–MB saves) and
   ≤~200 MB in the DB; `MaxStatesPerGame` default 20, prune oldest *unnamed* auto-slots first, never
   a labeled snapshot or the canonical SRAM.

**Still open (finalize in S1, low-stakes):** whether harvest writes the `ArcadeSave` row via a
gateway `DbContext` (simplest) or an authenticated site callback (keeps the gateway app-DB-free);
and where `RomHash` lives (ArcadeGame vs the save row).

## 10. Fit with the go-live sequence
Saves touch the same gateway `/w/{token}` path as the **R: JIT-zip** ROM work (approved) and the
**PSX JIT** go-live. Recommended order: land PSX JIT go-live (already built) → build R: JIT-zip →
build the save store (S0–S3, this doc) → then internet go-live → sync (S4) as its own effort.
