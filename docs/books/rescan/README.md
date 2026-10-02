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

## Run of 2026-10-02 (numbers)

Inventory 123,392 files / 24,862 dirs / 12.4 TB (0 errors). Diff: 114,584 unchanged, **3,853 vanished**,
**7,561 new**, 3 in-place, 602 excluded-and-present (dedup losers, untouched).
Decisions: **3,013 relocated** (2,911 byte-identical, 102 new bytes; 197 refreshed incl. mtime-only) — refused 0,
369 folders created, backup `data/books/v2/backup-20261002-041138`, journal `relocate-journal.tsv`.
**840 replaced**: 569 contained, 174 contained?, 4 edition, 93 gone. Re-diff after relocation: 840 vanished /
4,548 new — exact.
