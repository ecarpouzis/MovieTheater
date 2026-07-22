# Arcade multi-disc + metadata cleanup plan (2026-07-22)

Follow-on to `docs/arcade-dedupe-multidisc-plan.md`. That doc designed the (now fully-built)
one-card-per-game + version-dropdown + seamless-disc-swap machinery. This doc covers making it
actually work across **all** systems: the per-system wiring, and the DATA cleanup the grouping
and enrichment depend on.

## What was fixed 2026-07-22 (DONE, deployed, verified)

- **Multi-disc per-system config.** The generalized disc-swap machinery (`ArcadeVersions` grouping,
  worker patch 0005 disk-control, shim `disc` channel, ArcadeRoomPage Swap Disc UI, `descriptor.discCount`)
  only routes a `<name>.m3u` to the right core if that system lists `m3u` in its config `roms:` — else the
  scoped lookup (patch 0037) misses and the global lookup hands `.m3u` to `gc`/Dolphin. Added `m3u` to
  **segacd, ps2, pce, dc, 3do, cdi** in `docker/arcade/config.worker-gl.yaml` (ps1/saturn/gc/wii already had
  it); deployed to both worker ConfDirs. **Verified live:** Penn & Teller's Smoke and Mirrors (segacd, 2-disc)
  boots on Genesis Plus GX with both discs from the M3U, `discCount=2`, Swap Disc control present.
- **Disc-tag parser.** `ArcadeVersions.DiscNumber`/`M3uKey` extended to recognize `cdN` / `cd N` / `(CD N)` /
  French `disque N` (the coded Saturn/PCE sets), not just `(Disc N)`. Tests: `ArcadeVersionsTests.cs` (33).

## Data audit (2026-07-22) — two tracks: RESOLVE vs ENRICH

Enrichment (box-art / LaunchBox rating / IGDB summary) matches by **Title**, so it is near-useless on
coded or lowercase titles (saturn box-art coverage is 2/2522 today). **Normalize names first, then enrich.**

### Track A — name RESOLVE (titles are unreadable codes)

| System | rows | problem | approach |
|---|---|---|---|
| naomi | 287 | 100% MAME shortnames (`18wheelr`,`fotns`) | DAT resolve modeled on `ArcadeFbneoResolveCommand.cs`; region from clone suffix (`j/u/e`). Disable the `awbios` BIOS row. |
| atomiswave | 24 | 100% MAME shortnames (`ggx15`,`kofxi`) | same DAT resolve |
| saturn (coded) | ~41 | `NNNN-<name>-<lang>-cdN` (`0691-atlantis-fre-cd1`) | Saturn Redump DAT rename (or re-derive from a properly-named Redump source); maps title/region/disc |
| arcade long-tail | ~30k | fruit-machine/mahjong clones (`sc5ddosha`) | LEAVE — un-nameable; per-title curation only |

DAT sources: MAME `naomi.xml` / atomiswave driver descriptions (or the FBNeo NAOMI subset); Saturn Redump DAT.

### Track B — ENRICH (names are real, just lowercase / no region / no art / no rating)

Normalize (Title-Case + `, The` inversion via the SimpleTitle convention) + region backfill from the existing
`ArcadeRomTags` filename parse, THEN run `arcade-boxart` + `arcade-launchbox` + `arcade-igdb` (all chunked,
dry-run by default, resumable):

- saturn bulk (~2,481), **segacd (520)**, cdi (669), pce (~491 lowercase) — casing + region + then art/rating.
- intv (171), lynx (82), ngpc (91) — pure region backfill; titles already fine.
- 3do (238) — mostly clean; minor.

LaunchBox (`arcade-launchbox`, Metadata.zip) is the PRIMARY rating source (~83% coverage) and also fills
genres/summary/dev/pub; its per-platform game list + alternate names can also seed Saturn name matching.

### Track C — one-offs

- **Penn & Teller** (segacd 61926/61927): Region=USA ✓ Variant=Proto ✓ already; only Title casing →
  `Penn & Teller's Smoke and Mirrors` and Year → 1995. (Applied 2026-07-22.)
- **PSX L→R stragglers**: 44 ps1 rows still point `SourceArchivePath` at `L:\` (the NAS). ~22 are redundant
  near-dupes of an R: row (disable/repoint); ~22 are the only copy (copy the `.7z` to R: and repoint, or accept
  they 503 when the NAS is offline). Files all exist on L: today. See `[[psx-l-to-r-chd-migration]]`.

## Sequencing

1. **Track A DAT resolve** (naomi/atomiswave first — 311 rows, smallest+highest value; then saturn-coded 41).
2. **Track B normalize** (casing/region) — a dry-run-first bulk pass (chunked, idempotent, guarded).
3. **Track B enrich** — boxart → launchbox → igdb, chunked, over the normalized rows.
4. **Track C** — Penn (done), then PSX stragglers, `awbios`.
5. **Verify** — test-roms multi-disc swap on one title per CD system (segacd done via Penn).

## Hard rules (this is the shared prod DB)

Every bulk step: dry-run + count first, chunked with a cursor, idempotent, guard-skip when unsure, never
`git add -A`, never touch the NAS destructively. Preserve hand-edits on re-ingest.
