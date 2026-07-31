# Arcade — Just-in-time (JIT) ROM cache

**Problem.** Some systems have a master collection far too large to pre-stage. The PS1 set is ~448 GB
of `.7z` disc images (`L:\4 - Software\PSX Master Collection`, one archive per game). We want the whole
catalog *browsable* without copying it all onto the arcade box, and without hand-curating a subset.

**Approach.** Catalog every game as a normal `ArcadeGame` row, but instead of a staged ROM each row
records its **source archive** (`ArcadeGame.SourceArchivePath`). The first time someone plays a game,
the **ArcadeGateway** extracts that archive into the workers' read-only ROM mount, then LRU-evicts cold
games to hold total extracted disk under a cap. A game is browsable and launchable even though its
`RomPath` isn't on disk until first play.

## Why the gateway does the extraction

In the go-live topology the control-plane API (`/API/Arcade/*`, room create/bind/join) runs in the
k8s pod, which has **no access to the ROM disk**. The only site component co-located with the disk on
the media host (Ziggy) is the **ArcadeGateway**, and it already gates every stream connection — so it is
the one place that can (a) see the archive on L:, (b) write into the ROM mount, and (c) materialize
*before* forwarding the connection, then pin the game for the life of the session. It stays DB-free:
the site exports a manifest and the gateway reads that file.

## Data flow

```
arcade-jit-ingest   L:\…\PSX Master Collection\*.7z  ─►  ArcadeGame rows (SourceArchivePath set)
arcade-romcache-export   ArcadeGame (JIT rows)        ─►  docker/arcade/arcade-romcache.json
                                                             (gameId → gameKey, system, folder, archive)
play:  browser ──/w/{token}(gameId)──►  ArcadeGateway
                                          ├─ RomCache.EnsureMaterialized(gameId)   (7z x → /roms/psx/…)
                                          ├─ Pin(gameId)
                                          ├─ forward WS ─► CloudRetro coordinator ─► worker
                                          │     worker FindAppByName → miss → Scan() → found (patch)
                                          └─ (WS closes) Unpin(gameId);  later: LRU evict if over cap
```

## The three ways CloudRetro learns about a just-extracted ROM

A freshly-extracted ROM postdates the worker's boot-time library scan, so CloudRetro must be made to
see it. In order of reliability:

1. **Scan-on-miss patch** (the guarantee) — `docker/arcade/patches/0001-jit-scan-on-miss.patch` makes
   the worker's `FindAppByName` run one library `Scan()` and retry on a lookup miss. `Scan()` re-walks
   the directory tree, so it needs **no** filesystem event. This is why materializing the file before
   forwarding is sufficient.
2. **fsnotify `WatchMode`** (`library.watchMode: true` in `config.yaml`) — picks up new files instantly
   *when it fires*, but fsnotify does **not** reliably cross a Windows-host → WSL2 bind mount, so it's a
   bonus, not the mechanism.
3. Neither requires a worker restart.

## Saves are unaffected by extract/evict

CloudRetro keys save files by **room id**, not by ROM (`SetSessionId(uid)` → `<roomId>.dat` / `.srm`
in the separate `/saves` mount), and `/roms` is mounted read-only so a core can never write beside a
ROM. Re-extraction is byte-identical, so deleting an extracted ROM and re-materializing it later has
zero effect on saves — a save reattaches by room id. (Separate, pre-existing: a brand-new room starts
fresh unless the site reuses room ids.)

## Destructive-safety (global bulk-job rule)

Eviction only ever deletes files it extracted itself (the per-game whitelist derived from the archive
listing), each re-verified at delete time to live **under the ROM mount**. The source archive sits on
the library drive, outside the mount, so it can never match the guard — it is never touched. Pinned
(in-use) games are never evicted; if everything over the cap is pinned, it logs and waits.

## Operating it

1. **Catalog** (dry-run first, then `--apply`; bounded/resumable):
   ```
   arcade-jit-ingest --archives "L:\4 - Software\PSX Master Collection" --system ps1        # dry run
   arcade-jit-ingest --archives "L:\4 - Software\PSX Master Collection" --system ps1 --apply --limit 500
   # re-run with --after "<nextCursor>" until remaining: 0
   ```
2. **Export the manifest** the gateway reads (re-run whenever the catalog changes):
   ```
   arcade-romcache-export --out docker/arcade/arcade-romcache.json
   ```
   `--dat` defaults to `data/arcade/fbneo-arcade.dat` and is now found by searching up from the working
   directory, so it resolves no matter where you run from (`dotnet run --project src/MovieTheater/…` sets
   the CWD to the project dir, which used to miss it). The command **fails closed**, writing nothing and
   exiting non-zero, if either
   - the FBNeo DAT cannot be loaded (`--allow-no-dat` to override), or
   - the export carries fewer dependency-archive references than the manifest already on disk
     (`--allow-fewer-deps` to override).

   Both guards exist because the old behaviour was a warning: a missed DAT still published a manifest,
   just with **every** FBNeo `romof` closure gone, so arcade games failed at launch with "missing romset"
   and nothing in the output said so. It recurred three times before being fixed. The healthy numbers to
   look for are `4269 game(s) carry a romof dependency closure (4566 dep archive reference(s))`.
3. **Point the gateway at it** (gateway `appsettings` / env). Both keys must be set to enable the cache;
   leave empty to disable (gateway = pure signaling proxy):
   ```
   RomCache:ManifestPath = /path/to/arcade-romcache.json
   RomCache:RomsDir       = D:\Arcade\roms          # same host path as compose ROMS_DIR
   RomCache:MaxBytes      = 32212254720             # 30 GB cap (tune to taste)
   RomCache:SevenZipPath  = C:\Program Files\7-Zip\7z.exe
   ```
4. **Rebuild the worker image** so the scan-on-miss patch is baked in (see
   `docker/arcade/patches/README.md` + the build steps in `docker/arcade/README.md`).
5. **PS1 BIOS**: place `scph5501.bin` in the workers' libretro `system` dir (HLE fallback works but is
   lower-compat). Sources: `C:\Network Share\bios`.

## Latency note

Extraction of a PS1 `.7z` (~300–500 MB) into ~600 MB of `.cue`+`.bin` takes ~10–40 s, and it happens
during the WebSocket upgrade before the room connects (the room page shows "Connecting…/Negotiating…").
Popular games stay warm in the cache, so only a cold first-play pays this. Multi-parallel extraction is
capped (`MaxParallelExtractions`, default 1) since it's disk-bound.

## Scope / not-yet

- **Multi-disc** games are catalogued as separate `(Disc 1)` / `(Disc 2)` rows for v1; `.m3u` disc-swap
  grouping is a future nicety.
- **Rating ceilings** default to 0 (unrestricted) at ingest — hand-raise mature titles.
- The design generalizes to any archived system (drop the `--system` + folder), but PS1 is the driver.
