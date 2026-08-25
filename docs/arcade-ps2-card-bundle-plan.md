# PS2 per-slot memory-card bundling — "proper fix" for the "wrong memory card" eject

Status: ✅ BUILT + DEPLOYED + VERIFIED 2026-08-25 (Option A). Root cause CONFIRMED with reproduction.

## Shipped (Option A — instant in-room load, worker+core+gateway)

- **lrps2 core `f8e9c9b`** — `FileMcd_ApplyPendingSwaps()` at the top of `retro_unserialize`
  (EmuClose → overwrite `Mcd001.ps2` with `<cardfile>.pending` → EmuOpen, before the eject check);
  `FileMcd_FlushChecksums()` at the end of `retro_serialize` (persist the 0x210 checksum so a
  captured card matches the frozen CRC). DLL **11,648,512 B sha `5F5ECEAF…`**, both GL workers.
  Rollback: `pcsx2_custom_libretro.dll.pre-cardbundle` (= `D1A8A2EC…`, the 0.5-blend core).
- **Worker fork `a25c755`** (branch movietheater-fork = R3b acd9bd8 + this) — `exportPs2Card()` on
  Save writes the live card to the mount as `<id>.mcdN`; `stagePs2CardPending()` on Load copies a
  gateway-staged `<id>.mcdN.load` to `<cardfile>.pending`. worker.exe **39,005,530 B**, both GL
  workers. Rollback: `bin/worker.pre-cardbundle.exe` (= pristine R3b). fork.patch regenerated+proven.
- **Gateway `828b9a9`** (SaveStore/Program) — `CaptureSlotCards` vaults `<id>.mcdN` → `slot-NNN.mcdN`
  on snapshot; `StageSlotCards` writes `slot-NNN.mcdN` → mount `<id>.mcdN.load` on `/w-load`, backing
  up the current card to `_cardpreload.mcdN` first. Runs from `bin/Debug`.
- The stage input (`.mcdN.load`, gateway-written on a real load) is deliberately DISTINCT from the
  capture export (`.mcdN`, worker-written every Save) so an autosave/boot-restore never re-applies.

**Verified end-to-end** (ArcadePlayer2/user 33, Stuntman): the captured `slot-NNN.mcd0` CRC equals the
live mount card exactly (the 0x210 flush works); the worker logs `staged bundled ps2` + the core logs
`mt: applied N pending memory-card swap(s)` ONLY on a deliberate slot load (not on boot); the load
resumes cleanly. The visible dialog-prevention under a card-polling state (a save-prompt) is
structurally guaranteed (post-swap mount CRC == frozen CRC) — Eric's real repro is the field check.

⚠ **Pre-bundle snapshots have no card and are NOT fixed** — including Eric's slot-103 ("Press X to
save"). They must be RE-MADE with the new worker to gain a bundle. Continue/quicksave/named snapshots
made from now on are bundled.

Open follow-ups: slot 0 (Continue/autosave) isn't card-bundled (harvest path, not SnapshotToSlot);
orphan `slot-NNN.mcdN` blobs aren't pruned when a slot is deleted (cosmetic disk).

---

## Original design notes (kept for reference)

## The bug (proven)

PCSX2 freezes the memory-card checksum into every save-state (`Sio.cpp sio2Freeze`); on load it
compares frozen-vs-mounted and **ejects on any mismatch** → the game's "incorrect card" dialog.
Stuntman **rewrites its card on boot/play**, so the single per-(user,system) card drifts past any
older state snapshot. In-room Load (`/w-load`) swaps only the state `.dat`, leaving the drifted
mount card → frozen ≠ mounted → eject.

Evidence: slot-103 froze `0xe809…`; the worker mount drifted to `0x7bd9…` during play while the
vault stayed `0xe809…`. Reproduced end-to-end; CRCs read at each step.

## Architecture facts that constrain the fix

- The **PS2 card is worker-side**: seeded/harvested by the worker (patch 0039 `seedCards`/
  `harvestCards`) from `cards/<user>/ps2/` into `<confdir>/libretro/system/pcsx2/memcards/`. The
  gateway CANNOT target a worker mount (the coordinator picks the worker) — but it CAN read the
  vault, and can identify the active worker card dir via the `.owner` stamp.
- The re-plug primitive exists: `FileMcd_EmuClose(); FileMcd_EmuOpen()` re-reads the card from disk
  (`MemoryCardFile.cpp`). `GetCRC` returns the u64 at card offset `0x210`.
- **The state-load compare uses in-memory `m_chksum` (set at boot), NOT the on-disk file.** So
  swapping the card file mid-session does nothing until the core re-opens the card.
- **Windows file lock:** PCSX2 holds `Mcd001.ps2` open `r+b`. An external process (the gateway)
  cannot reliably overwrite it. So an in-room card swap must be done by the WORKER (which owns the
  handle), not the gateway.

## The one real decision — instant vs relaunch

### Option A — instant in-room load (no relaunch), needs worker+core change
- Worker `/w-load` handler: `FileMcd_EmuClose()` → swap slot-N's card into the mount → `EmuOpen()`
  → then restore the state. Owns the handle, so no lock fight; fresh `m_chksum` = slot-N CRC →
  compare matches → no eject.
- Gateway: capture the active card into `slot-NNN.cards/` at snapshot; hand the worker slot-N's
  card on load.
- Core: expose EmuClose/EmuOpen to the worker (tiny) OR a no-flush reload.
- Cost: **worker fork rebuild** + core rebuild. Keeps the instant-load UX. No dependency on the
  PS2 boot-restore timing.

### Option B — relaunch load, gateway-only card handling
- PS2 "Load snapshot"/"Load" RE-CREATES the room with `seedslot=N` (like lobby Resume) instead of
  `/w-load`. Fresh boot re-reads the card, so no lock, no mid-session re-plug.
- Gateway: capture the active card at snapshot; on `seedslot` resume, back up the current vault
  card and seed slot-N's card into `cards/<user>/ps2/` before boot.
- Cost: **gateway-only for the card** (script-deploy, no fork rebuild) + a small client change
  (route PS2 load to relaunch) + **must fix the PS2 boot-restore-stomp** (the known open item:
  deferred restore fires at a fixed 5 s while PS2 fastboot is still in BIOS → boot stomps it), so
  the relaunched state actually restores. Load is no longer instant (~few-second room restart).

## The semantic decision (applies to both)

Resuming snapshot N adopts N's card as the active lineage (correct for a point-in-time bundle).
The prior card is always backed up first (`cards/<user>/ps2/_pre-slot<N>-<ts>/`), so nothing is
lost — but the "current" card becomes N's. Recommended: yes, with the backup.

## Recommendation

**Option A** — it preserves instant load, doesn't depend on fixing the separate boot-restore bug,
and the worker/core changes are small and self-contained. The worker rebuild is the only extra
cost, and the card handling is where it architecturally belongs (the worker owns the card).

## Capture is safe either way (additive)

Reading `Mcd001.ps2` from the active worker card dir into `slot-NNN.cards/` is read-only on the
mount (fine under the sharing lock) and writes only NEW blobs. This half can ship immediately.
