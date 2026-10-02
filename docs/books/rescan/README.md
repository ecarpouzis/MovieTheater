# Comics rescan — keeping identity when files move, get replaced, or get collected

A full rescan of `\\Library\Public\5 - Comics` after a large reorganisation (the 2026-09/10 store sync:
folders renamed, issues re-ripped, `(F)` fixes, runs of floppies replaced by their trades). The stock
`books-scan` keys items by PATH, so on its own it would index every moved file as a NEW item and mark the old
one missing — throwing away everything the identity pass wrote against the old item id (provider links,
judged spans, hand-read issue numbers, split-lane keys, format reads, reader state, insights, covers).

The rule this pipeline enforces: **every removal is explained** — as a MOVE (same bytes elsewhere), a
VERSION (another rip / fix / printing of the same book), an EDITION (a different edition of the same work),
CONTAINED (a collection now holds it), or GONE — and **a moved or re-ripped book keeps its item id**.

## The pipeline (run dir: `data/books/rescan/<yyyymmdd>/`, gitignored; tools here)

| Step | Tool | Writes |
|---|---|---|
| 1. Inventory the share (read-only, chunked, resumable; `state.json` frontier) | `tools/inventory_walk.py --out <run>` | `inventory.tsv` |
| 2. Diff against `books.db` root 1 | `tools/diff_inventory.py --run <run>` | `unchanged / inplace / vanished / back / new / casechange .tsv` |
| 3. Mechanical pairing (name+size = move; size+issue = rename; name+folder = replace; `(F)` tag; issue+folder = version (READ)) | `tools/pair.py --run <run>` | `pairs.tsv`, `contained.tsv`, `unexplained.tsv`, `newfiles.tsv` |
| 4. Reading packet for everything not mechanically proven, grouped by old folder | `tools/review_packet.py --run <run>` | `review_packet.txt` |
| 5. READ it; write one verdict per group/item, with evidence | (by hand) `verdicts.txt`, `overrides.txt` (pairs the reading overturned) | |
| 6. Expand + check (exactly one decision per vanished item, targets exist, no target claimed twice) | `tools/expand.py --run <run>` | `relocate.tsv`, `replaced.tsv` |
| 7. Backup, then re-point items (dry run, then `--apply --journal`) | `books-relocate` | `Item` paths/folders, new `Folder` rows, refreshed file facts, dropped covers |
| 8. Re-diff — `vanished` must equal the replaced count | `tools/diff_inventory.py` | |
| 9. `books-scan --root 1 --apply` — indexes the genuinely new files, marks the replaced ones missing | | |
| 10. thumbs → signatures → `books-resolve --series` → collected-editions → reading-order → containment → `books-resolve` | | |

Evidence helpers for step 5: `tools/peek.py` (page count + ComicInfo of an archive), `tools/pagegrab.py`
(extract a few pages to read a contents page / indicia), `tools/cvcollects.py` (a trade's "Collects #…" from the
LOCAL ComicVine rip), and the held trades' own judged spans in `CollectedEditionSpan` (a volume pattern).

## Rulings made in the 2026-10-02 run

- **Edition vs version.** Same book, other rip / `(F)` / a 2nd printing with the same title → `version` (the item
  moves onto the new file and keeps its identity). A differently titled or re-packaged edition — Deluxe, Omnibus,
  Library Edition, `Book N` replacing `Vol. N`, a new-year edition with no printing marker and a different size —
  is a DIFFERENT book (its provider record is a different issue) → `edition`: the old item is marked missing and the
  new file is indexed as its own item, so no identity is mislinked.
- **A bigger book that collects the old one is `contained`, never `version`**, even when the name matches
  (`Gantz v01` → `Gantz v01 (Omnibus Edition)`, `Sunstone Vol. 01` → `Sunstone Book 01`). Size ratio and
  "Omnibus/Book/Deluxe" in the new name are the tell.
- **`contained` vs `contained?`.** A range proven by the book itself (contents page, indicia, ComicInfo
  "Collects"), the CV rip's description, a held sibling trade's judged span pattern, or page arithmetic that only
  one split satisfies → `contained`. A per-volume pattern assumed without proof, or a manga chapter→volume mapping
  → `contained?`. Only `contained` ranges feed `books-curated-spans-import`.
- **Our own second copy that disappeared** while the other copy moved → `gone` (nothing to re-point).
- **1-page variant-cover extracts** removed with nothing replacing them → `gone`.
- **The scanner keeps identity on a changed file.** `LibraryScanner.FileBatchAsync` now re-indexes an existing
  item's file facts with `keepIdentity` — the curated `ComicDetail` row is left whole (a parse-rule change reaches
  existing items through `books-reparse`). Pinned by `RelocationTests` and `ScanTests.AModifiedFileIsSeenAsChangedAndReRead`.

## After the scan — shelves, keys, containment (the order that worked)

11. `tools/post_scan_check.py` — every mark the scan made is a replaced item; no relocated item marked; no dup paths.
12. `tools/sibling_check.py` — a NEW file in a folder whose existing books carry a curated (split-lane) key gets that
    key (strict: siblings ≥ 80% on one key AND the file names the same run) → read the proposals →
    `books-series-split --apply` (key-only, reversible). Reject a proposal whose sibling key is a different BOOK.
13. `books-resolve --series`. **v2 had no producer of a Series for a NEW parsed key** — `SeriesRebuildJob.
    CreateSeriesForNewKeys` is it now, and it skips a STRANDED key (no alias, items already on a shelf); a shelfless
    file joins its stranded siblings' shelf. Verify ANY resolver change with `tools/shelf_stability.py` against the
    pre-change backup: the only pre-existing items allowed to move are ones that had no shelf.
14. `tools/shelf_drift.py` — each replacing collection vs the shelf of the issues it replaced. A trade of ONE run joins
    that run (use the run's LINKED key spelling — the split verb creates a Series for any other spelling); a book
    spanning several runs keeps its own key; a garbage parse (`04,`, `Copra, Round`) gets a proper key.
15. `tools/prefix_check.py` — a reading-order prefix (`02 Hellboy in Love - Black Eyes`) read as an issue number drops a
    one-shot onto a run as "#2": give the book its own key.
16. Containment: `tools/owed_containment.py` (every decision file that now owes a line) → `tools/write_owed_lines.py`
    (S only where proven, else `u`) → `tools/retire_missing_lines.py` (lines for replaced items become comments, the
    range kept as evidence) → `wave_fix.ps1` (no -Resolve) until every gate is green. An armed container (LIVE EXPOSURE)
    gets a `u` in its shelf's file; a newly opened file must decide every edition on the shelf.
17. Ranges for the remaining replacing collections: `tools/span_proposals.py` → read → only full ranges the evidence
    proves, only where the collection shares the replaced issues' shelf, never for an item a decision file decides →
    `books-curated-spans-import`. A book flagged `IsCollection=0` that now nests → `apply_read_iscollection.py`.
18. Identity: `tools/undecided_sheet.py` → `next_batch.py --revisit-file` (R- batches; the counter now skips names
    already on disk) → readers (≤ 2 at a time, ~1,500 packet lines each) → `check_identity` → `wave_land.ps1`.
18b. **The coverage gate — before ANY removal is reported lost.** `tools/removal_coverage.py --verdicts gone,contained?`
    searches the WHOLE live library (not the new files) for collections that could hold each removed item — its
    shelf, title tier, franchise, folder/parent folder, title stem, judged spans — and every candidate is READ
    (`trade_probe.py`, the book's indicia / chapter covers via `pagegrab.py` + a contact sheet) before the verdict.
    The first pass of this run skipped it and reported 72 lost files; 52 were held by collections already on the share
    (corrected in `replaced.tsv`, the pre-fix copy kept as `replaced.before-coverage-fix.tsv`). `missing.tsv` lists only
    what survives the gate.
19. **Same title, different run.** A new run whose key matches an old run's title (Royals 2026 → Marvel's 2017 Royals,
    IDW Sonic trades → Archie's Sonic, Titan Conan → Marvel's Conan) lands on the old shelf. Four reads catch it,
    because none of them catches everything alone: `tools/year_drift.py` + `year_drift_packet.py` (the new book is outside
    the shelf's era), `tools/folder_drift.py` (its folder's older books sit on another shelf), `tools/publisher_drift.py`
    (it sits under a publisher folder the shelf never used — the one that found the Sonic and Conan trades, whose
    years fell inside the old run's span), `tools/number_clash.py` (it repeats an issue number the shelf already holds
    from another era). Read every row: reissues, minis filed under a parent line and screenplays are NOT drift.
    Fix = a split jsonl (`books-series-split --apply`; join an existing run by its LINKED key spelling), resolve,
    `shelf_stability.py` (0 pre-existing items moved), `merge_refusals.py --from <snapshot of the pre-split backup>`
    (the verb's own snapshot step runs only inside wave_land), an R- batch for the shelves it created, `wave_land.ps1`,
    then `wave_fix.ps1` when check_decisions stops on origin lines (`retire_moved_lines` runs only there).
    A new shelf whose cv= is a stored link on an EMPTY row needs `F <sid> merge-with=<row>`; a cv= another shelf holds
    for the same comic is a merge-with too.
20. Ranges for the rest: `tools/containment_priority.py` (which unranged collections sit on shelves with live issues) →
    `tools/range_evidence.py` (stored links' collects clauses) → `tools/trade_probe.py` (the TRADE's own GCD/ComicVine
    record found by title + volume number) and `tools/gcd_series_reprints.py` (a GCD collected series' reprint
    roll-up) → a ranges TSV → `tools/apply_ranges.py --apply --spans-out` (a `u` becomes `S` in the shelf's decision
    file; a shelf with no file gets a curated span line) → `books-curated-spans-import --apply` → `wave_fix.ps1`.
    **Check the record's run against the shelf's run** before writing: Geiger Vol. 01 collects the 2021 series and sits
    on the 2024 shelf; TMNT Color Classics trades collect the 2012 series, the shelf is the 2015 one; Classic G.I. Joe
    reprints Marvel's 1982 run, the shelf is IDW's 2010 continuation — all refused. A record that disagrees with a
    judged neighbour (Gunslinger Spawn Vol. 06 "#31-36" vs the judged Vol. 07 #31-35) is not written.

## Run of 2026-10-02 (numbers)

Inventory 123,392 files / 24,862 dirs / 12.4 TB (0 errors). Diff: 114,584 unchanged, **3,853 vanished**,
**7,561 new**, 3 in-place, 602 excluded-and-present (dedup losers, untouched).
Decisions: **3,013 relocated** (2,911 byte-identical, 102 new bytes; 197 refreshed incl. mtime-only) — refused 0,
369 folders created, backup `data/books/v2/backup-20261002-041138`, journal `relocate-journal.tsv`.
**840 replaced**: 569 contained, 174 contained?, 4 edition, 93 gone. Re-diff after relocation: 840 vanished /
4,548 new — exact.
Scan: added 4,548 / changed 3 / removed 843 (840 replaced + 2 dedup losers whose files were deleted + 1 already
missing), failed 0; thumbs generated 4,742 (3 new files undecodable: Summer Blonde (corrupt), an .azw3 novel, Bartkira).
Keys: 218 sibling-key fixes, 34 run joins for replacing trades, 2 Epitaphs spelling fixes, 1 Europe Comics Thunderbolts
off the Marvel shelf, 8 prefix-misparse books given their own runs. Resolver: 1,089 shelves created for new keys; the
first version also pulled 934 stranded items off curated shelves — repaired (`stranded_repair.py`,
`data/books/rescan/20261002/stranded-repair-undo.json`), all pre-existing shelved items verified back.
Containment: 172 owed lines (10 S, 162 u) + 15 for armed containers, 23 replaced-item lines retired, 69 judged ranges +
Saga Vol. 11/12, 5 IsCollection flags; landing green (audit_containment 0, check_decisions 2,190/0, armed 0/0,
overlap 0, audit_identity 0). Saga changed by design (floppies #49-54/#61-71 replaced by Vol. 09/11/12); WD identical.
Identity: 969 new undecided shelves (2,624 files) → R-192..R-200.

Same-title drift (step 19): split6 126 files (32 new runs + 6 joins of the real run), split7 the 2016 DC Space Ghost
OGN off the Dynamite run, split8 56 files (Sonic/Conan/Flash Gordon trades and issues to their runs, Kai-Sei #1-5,
Godzilla Library Book 04-06, The Beauty #1-2, four minis' stray issues, One Piece v106 colored, Star-Crossed and
Silver off unrelated books), split9 The Harbinger (2021) #5-8 + Book 01 off the 1992 run; Spawn: Omega read as a
collection. Identity waves 208 (R-202, 33 shelves, 2 merge-withs), 209 (R-203), 210 (R-204) — all gates green,
0 pre-existing items moved, undecided 0. Ranges (step 20): 82 written (ranges-r1..r4: 38 into decision files, 44 as
curated spans) from ComicInfo, ComicVine and GCD trade records; one wrong range (Geiger Vol. 01) caught and refused.
New collections without a judged range: 692 → 611; on shelves with live numbered issues 253 → 172 (Spawn: Omega joined the count when read as a collection) — the remainder
have no record that states their contents and stay refused until their pages are read.
