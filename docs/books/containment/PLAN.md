# Containment: getting every collected-edition range right

**Handoff document.** Written 2026-09-08 by a session that produced more diagnosis than progress.
Nothing here has been applied to the live database. If you read one section first, read **§1**: it is
the list of mistakes that session made, and everything else is shaped around not repeating them.

---

## 1. Read this before planning anything

Six mistakes, all made in one session, all avoidable:

1. **Every detector was wrong on its first run, and its counts were reported as findings before it was
   debugged.** 312 over-claims → 12 once the comparison was fixed. 169 ladder gaps → 62 once restricted
   to adjacent volumes. 4 note contradictions → 1 real. 105 prose conflicts → a handful. A tiering script
   graded Baltimore as seven over-claims when its ranges were correct.
2. **Building new oracles instead of reading shelves.** After writing a plan that said the work is
   per-series judgement, the very next act was writing a regex to detect issue-number defects.
3. **The evidence packet reduced away the evidence.** It carried `heldIssues: [1,2,3,4,5,13,14,15]` —
   bare numbers — when the answer to Baltimore was in the issue *filenames*
   (`Baltimore 016 - The Infernal Train 01 (of 03)`), which encode two numbering systems at once.
4. **"Read the folder's convention once and apply it across its series" is wrong.** Folders hold mixed
   conventions. A folder is *context* — neighbours, batch directories, sibling ladders — never a
   substitute for judging each edition on its own evidence.
5. **Trust granted by label, not by evidence** — the `gold` exemption (§6.6).
6. **Page count and file size were in the packet the whole time** and settle the "what is this file"
   question outright in most cases. A 2pp file is not a TPB; a 1,203pp file is not a single issue.

**The work is per-series judgement with all the evidence in view. Code exists only to assemble evidence
and to stop you skipping an edition — never to decide.**

---

## 2. Why it matters

`CollectedEditionSpan` records "this book collects issues #a–#b". A file de-duplication reads it. If a
range claims issues the book does not contain, files that are not redundant get hidden or deleted.
**Over-claiming loses data; under-claiming only fails to save space.** Weight the two accordingly.

Live today: **4,081 `Curated` ranges, ~43.6% proven** against issue-level evidence.

---

## 3. The complete data map

Everything is offline. The scrapes are retired; their caches are the ground truth that remains.

### 3.1 Databases

| path | what |
|---|---|
| `data/books/v2/books.db` | the live catalog. `Item`, `ComicDetail`, `CollectedEditionSpan`, `CollectionNode`, `ReadingOrderEntry`, `ItemProviderLink`, `ContainmentFlag`, `DuplicateGroup/Member` |
| `data/books/v2/books-legs.db` | the provider warehouse. `LocgContainment` (625,396 edges), `LocgComicRaw` (156,839), `CvVolumeDescription` (119,879), `GcdIssue`, `GcdSeries`, `OpenLibraryEdition`, … |
| `data/books/archive/mybooks/GrandComicsDatabase-06-01-06.db` | the full GCD dump. `gcd_reprint` (origin_issue_id → target_issue_id), `gcd_issue`, `gcd_series`, `gcd_story` |
| `data/books/archive/mybooks/comicdb_comicvine_20260122.db` | the 14.5 GB ComicVine rip (`cv_volume.raw_api_response` carries the Collected Editions list) |
| `data/books/archive/mybooks/mybooks.db` | v1's database. `CuratedCollectedEditions` (1,047 rows, all `Source='claude'`), `ComicCollectionNodes` (118,322). **v1 ComicId == our Item.Id — ids were preserved by the migration**, so it joins directly |

### 3.2 The LOCG cache — `data/books/archive/locg_cache/`

| dir | files | what |
|---|---:|---|
| `detail_html/<locgComicId>.html` | 83,257 | the full server-rendered comic page. **Carries the `Collects SERIES #a-b` prose** — the only source that states non-contiguity. Also the `#stories` section that produced the edges |
| `reprints/<storyId>.json` | 117,414 | the reverse direction: which editions reprint this story |
| `rich/<locgSeriesId>.json` | 14,238 | series enumeration — slug, title, isEdition per comic. Useful for resolving ids to identities |
| `issues/` | 5,384 | per-issue records |
| `resolve.json` | 1 | series-name → LOCG series id resolution cache |

**Shell-page trap:** the bare `/comic/<id>` URL returned a ~6 KB shell that parses cleanly and yields
`contains: []`. **6,494 of the 83,257 cached pages are these.** An empty `contains` on a
collection-shaped title is UNKNOWN, never "collects nothing". Detector: `filesize < 8192`.

### 3.3 The other caches — all now archived

| dir | files | size | what |
|---|---:|---:|---|
| `_pages/` | 10,523 | 16.7 GB | **v1's curation rig** — see §4.2. `AGENT_INSTRUCTIONS.md`, `RESUME_STATE.md`, `candidates.json`, `slices/` (55), `results/` (29), `work/<cid>/pNNN.jpg` cached book pages |
| `_gcd/` | 73 | 441 MB | GCD matching scripts and audits |
| `inducks_cache/` | 10 | 328 MB | Disney/Duck comics database (`.isv` tables) |
| `barney_cache/prog/` | 2,313 | 42 MB | 2000 AD prog data |
| `marvel_cache/series/` | 128 | 25 MB | Marvel API series |
| `mu_cache/` | 1,220 | 13 MB | MangaUpdates |
| `ol_cache/works/` | 17,756 | 4 MB | Open Library |
| `Bookparser/`, `OLD/` | 13 | 360 MB | parser audits — `comic_parse_audit*.csv`, `all_folders_dump.txt` |
| `mybooks-app/` | 1,699 | 31 GB | v1's application source, **including `.claude/skills/`** — 25 skills, of which `locg-scrape`, `comic-hierarchy`, `reading-order`, `unified-data`, `series-reconciliation` are the load-bearing ones |

### 3.4 The books themselves

The files are on `\\Library\Public\5 - Comics\...` (`Item.Path`), reachable and readable. **No OCR is
needed — extract a page and look at it.**

```bash
# list the archive (works for .cbr and .cbz)
"/c/Program Files/7-Zip/7z.exe" l "<Item.Path>"

# pull the front matter only
"C:\Program Files\7-Zip\7z.exe" e "<path>" -o"<outdir>" -y "*00[0-4].jpg"

# downscale so the read is cheap, then Read the .jpg
python -c "from PIL import Image; im=Image.open(p); im.thumbnail((1700,1700)); im.save(out, quality=85)"
```

v1 also cached page images under `_pages/work/<cid>/p000.jpg …` for books it processed — check there
before extracting.

### 3.5 Untried

**Web search.** Publisher and retailer listings state "Collects #X–Y" for most trade editions. No
session has used it. It may settle much of the no-evidence tail cheaply.

---

## 4. What v1 tried — what worked, what did not

### 4.1 LOCG (the primary source, and it was not good enough)
Locked precedence was `LOCG → GCD → CV → curated`. The edge graph is real and two-directional:

- **forward** `Source="stories"`, 88,429 edges — this edition collects these issues
- **reverse** `Source="reprint"` 302,778 + `"reprints-cache"` 234,189 — this issue is reprinted here

**625,396 edges over 53,887 containers.** v1's own measurement after reduction to spans: **39%
agreement with GCD.** It had to ship `prune_disagreeing_spans.py` to drop rows where GCD and CV agreed
against LOCG (bad identity matches). Its reduction gates were: complete containers need
`resolvedFrac≥0.6 ∧ dominantFrac≥0.6 ∧ coverage≥0.5`; reprint-only containers need
`cluster≥3 ∧ coverage≥0.8 ∧ dominantFrac≥0.8`. Result: 4,689 → **4,606 spans**.

Edition pages link only *some* chapters to source issues; the rest exist only as bold chapter labels in
the HTML, which v1 parsed into stubs (+40,860 → 156,788 records, 91,600 with issue numbers). Even so,
**only 46.3% of our edges resolve to an issue number.**

**Because it is partial, a resolved edge raises a FLOOR and nothing more. Never use it to narrow.**

### 4.2 Reading the books (this is what worked — and it is unfinished)

`_pages/AGENT_INSTRUCTIONS.md` is the method, and it is authoritative. The essentials:

**The coordinate rule — get this wrong and it mis-nests.** A range must be in the same coordinate as the
series' base reading units:

- **base = Issue** → ranges in *issue* coordinates. A manga Viz-HC "Vol. 3" is **not** chapter 3. If the
  chapter range cannot be determined, **skip** — never fall back to the volume number.
- **base = Volume/Book/Omnibus** → ranges in *volume* coordinates. A base volume "Vol. N" → `N,N`; an
  omnibus collecting "volumes X–Y" → `X,Y`.

**Where the indicia lives, by publisher:**

| publisher | location |
|---|---|
| Image (Saga, Walking Dead, creator-owned) | front title spread, **p001–p002**, dense legal text |
| DC / Vertigo | front copyright page **p001–p003** |
| **Marvel** | **the LAST text page** — fetch the closing pages, not the front |
| Dark Horse / IDW / Valiant / Boom | varies — front p1–p3, then the back |
| Archaia / kids HCs / OGNs | often **no issue statement at all** — fall back to chapter dividers or reproduced covers, or skip |

The indicia is the page dense with tiny legal print ("Published by…", "Copyright ©…", ISBN). Skim
p001–p003 for that look rather than reading every page.

**Junk-last-page gotcha:** some archives carry an unrelated image as the very last page (it sorts last
in the zip). If the final page shows a *different series*, it is a packaging artefact — the real indicia
is just before it. **Self-check: the indicia you use must name THIS series.**

**Efficiency, as v1 measured it:** work a whole series together — fully read ONE book to learn the
publisher's indicia location and the numbering pattern, then for siblings fetch just the likely indicia
page and confirm. **1–2 image reads per book.** Always confirm each book's own range from its own
indicia; use the pattern to know where to look, not to skip looking.

**Skip rule:** newspaper-strip reprints, original graphic novels, art/sketch books, magazines and kids'
GN lines have no issue range. Decide a skip from the opening pages only. v1 skipped 159 books this way.

**Confidence:** 0.95–1.0 indicia exact; ~0.8 reproduced covers/chapter dividers; ~0.6 recognised content
plus sibling pattern, and only when the structure confirms it. Below 0.6 → skip.

**How far it got:** 29 of 55 slices, 1,278 results (1,119 ranged, 159 skips) → **1,047 rows imported**.
It stopped because a usage cap cut the agent fan-out mid-flight, and the agents had batched their writes
to the end so in-flight work was lost. `RESUME_STATE.md` records the resume procedure.

**Its import filters** (`insert_results.py`): confidence floor **0.7**, reject **volume-coordinate ranges
on issue-based series** (the JoJo manga collision), reject widths **>200** (legacy dual numbering).

### 4.3 Engine fixes v1 made
- Zero-span containers no longer get a positional parent — cleared 2,140 spurious parent links, kept 191.
- An over-collection span guard cut catastrophic mis-nesting **71 → 7**.

### 4.4 The problem v1 left open, and it is still open
**Cross-run nesting collision.** Big titles — Batman, Wonder Woman, Superman, Flash, Avengers, X-Men,
Spider-Man, the Marvel Epic/Masterworks lines — conflate multiple runs that **restart issue numbering**
under one `Series.Id`. New 52 Vol 1 `[1,6]` and Rebirth Vol 1 `[1,6]` overlap; a `[1,52]` book nests over
volumes from every run. v1's note: *the curated labels are correct; only the nesting is affected.* This
is the same population this project flagged `overlap-in-series` / `conflated-series`.

---

## 5. What this project did

A model read compact per-series renders and judged all 20,498 collected editions: **3,077 ranges, 17,421
refusals, 718 flags**, applied live with the parser fix (`books-reparse`) and the provider recovery
(`books-cv-spans`, `books-gcd-spans`, `books-locg-editions`).

Its packet showed provider spans **reduced to min–max**, no file sizes, and no issue filenames. Bone's
gap was invisible in it by construction. That thinness is the root cause of its errors, not the judging.

---

## 6. Confirmed defects in the live data

### 6.1 Wrong ranges
- **Bone Vol. 04** is `#21-27`; it is **`#20-27`**. Proven by reading two books: Book Three's contents
  page shows 8 chapters and GCD confirms 8 issues (#12-19), so chapters map 1:1 to issues; Book Four has
  a prologue plus chapters I–VIII. Issue #20 is currently in no volume.
- **Hellboy Omnibus Vol. 03** `#8-10` — its own stored note says it collects *The Storm and the Fury*,
  which is Vol. 12. **Omnibus Vol. 04** `#11-12` — its note says *Hellboy in Hell* vols 1&2, a different
  Series entirely. Both are self-contradicting and provable from the note alone.
- **Hulls flattened over non-contiguous collections** — the file-deleting shape:
  Checkmate `13-31` vs indicia `13-19, 26-31`; Superman `33-40` vs `33-36, 39-40`; X-Men `1-21` vs
  `#1-12 & #16-21`; Fables Vol. 04 `19-27` claiming #22; Angel `1-17` vs `#1-#14 … #17`.

### 6.2 Degenerate `#N-N` rows are two different things
- **30 artefacts** — the span equals the **volume ordinal**: Transformers Classics Vol. 01 → `#1-1` over
  318pp; Iron Man Masterworks Vol. 03 → `#3-3` over 440pp; the entire Essential Groo ladder, whose own
  notes name the real contents ("Groo v2 #25 to #36").
- **19 genuine** single-issue editions where the number is *not* the ordinal: Batman #238 (a 100pp
  giant), Flash #800, Fables Vol. 22 "Farewell" which **is** issue #150.

### 6.3 The issue numbers underneath the ranges
Filenames like `Baltimore 016 - The Infernal Train 01 (of 03)` carry **both** a continuous library number
and the arc's own. The parser took the arc's, so issues collide:

| series | issue files | distinct issue numbers |
|---|---:|---:|
| Baltimore | 40 | 8 |
| Lobster Johnson | 31 | 13 |
| Sir Edward Grey, Witchfinder | 26 | 6 |
| Abe Sapien (2013) | 43 | 22 |

**Baltimore's ranges were never wrong — the numbering under them was.** Any grader comparing them to
LOCG/GCD (which report the *arc* numbering) calls them over-claims. They are not. **A range cannot be
judged while the numbering under it is wrong.**

### 6.4 Identity
- **3,458** items the parser calls a collection, LOCG only ever lists as *contained* — 2pp cover-only
  files, 7pp specials.
- **11,252** items the parser calls an issue, LOCG lists as a *container* — 1,203pp `Stray Bullets Uber
  Alles Edition`, 974pp `Loki - God of Stories`.
- LOCG role across our linked items: **11,387 container-only, 50,814 contained-only, 6,810 both, 16,869
  absent.** This is an identity signal independent of filename and ComicInfo.
- The same run is split across series inconsistently — Witchfinder is `S15695` (26 issues) *and*
  `S21752 Gates of Heaven` (5) *and* `S21753 Reign of Darkness` (5).

### 6.5 Regressions from the last live run
- **746 containers** went from honest silence to a provider leg's claim (the LOCG re-point gave the legs
  more material; where the model pass refused, a leg filled the vacuum).
- **12** lost a correct gold win to the degenerate guard.

### 6.6 The shipped trust classes are backwards
`README.md` and `ContainedDuplicateJob` granted class 1 to any `Curated` row the model pass did not
write. Measured: **gold confirms at 47.8%**, model rows at **83.7%**, and only **564 of 988** gold rows
carry an indicia quotation at all. **Trust the quotation, not the provenance.**

### 6.7 v1 work not carried over
73 ranged results were never imported. **44 match v1's own documented filters** (37 volume-coordinate on
issue-based series, 4 below the 0.7 floor, 3 wider than 200) and were rejected deliberately. **29 are
unexplained** — genuinely lost work, clustered in the conflated big titles (Aquaman, Batgirl, Batman,
Catwoman, Green Arrow, Hitman, RASL). All 1,278 results are preserved in
`docs/books/containment/v1_curation.jsonl` with their evidence strings.

---

## 7. Every source, and exactly how it lies

| source | worth | failure modes |
|---|---|---|
| **Indicia quotation** in a curated note | self-proving | only 564 of 988 gold rows have one |
| **The book itself** | decisive | Marvel puts indicia on the last page; junk last pages; OGNs print no issue statement |
| **Page count + file size** | decisive for identity | none — use it first |
| **GCD** `gcd_reprint` | near-exact issue SET | guest stories inject other titles (restrict to the dominant series); indexes **stories, not books**, so silence outside `[min,max]` of what it indexed is not denial; `Method="num"` links point at a single issue (Bone Vol. 03's link is Bone **#3**) |
| **LOCG forward edges** | a FLOOR only | 46.3% resolve; shell pages; bad identity links (Bone Vol. 04's link is a *Heavy Metal* special) |
| **LOCG reverse edges** | membership without trusting a parsed number | same coverage limits |
| **LOCG role** | independent identity signal | absent for 16,869 items |
| **LOCG "Collects …" prose** | the ONLY source stating **non-contiguity** | in the publisher's per-mini numbering, not ours |
| **ComicVine** | the volume's Collected Editions list | title-matched only |
| **ComicInfo.xml** | **do not use** | it *is* the source of the 420 `label-ambiguous` flags — 20pp single issues declaring themselves `tpb` |

---

## 8. The method

**Series by series, every edition judged on its own evidence, in as many passes as the series needs.**
Folders give context — neighbours, batch dirs, sibling ladders — and nothing more.

Per edition, with the whole packet in view (filename, full path, page count, file size, volume ordinal,
format, the sibling ladder, **every issue file's filename**, each leg's issue SET, the LOCG role and
prose, v1's indicia note and skip decision):

1. **What is this file?** Page count and file size answer it before any provider does.
2. **What coordinate does this Series use** — issue or volume? (§4.2. Get this wrong and it mis-nests.)
3. **What numbering do the issue filenames actually use?** Not the parsed numbers.
4. **Does the ladder tile** — no gap, no overlap, no inversion, one numbering throughout?
5. **Does the arithmetic hold** — pages ÷ issues plausible for the format?
6. Where sources agree and the ladder tiles, settle it. Where they disagree or are silent, **open the
   book** (front matter, or the last page for Marvel; the contents page for a chapter count; calibrate
   against a sibling a source confirms). Web search where the book does not say.
7. Record per edition: the range or an explicit refusal, the confidence, and the evidence — and any
   defect found underneath (issue numbers, identity, series splits) as a flag.

`pass2.py` refuses to expand a series unless every edition in it is decided exactly once. That refusal is
the only thing that makes "I did this series" checkable.

**Done for a series** = every edition has a range with named evidence or a refusal with a reason; the
ladder tiles; no source is left contradicting the result unexplained.

**Landing** happens once, at the end, on a backed-up database (the online-backup script is proven), gated
on: Saga (14966) unchanged, Bone `#20-27`, Hellboy Omnibus 03/04 corrected or refused, and the §6.1 hulls
no longer claiming excluded issues. Then re-measure. Only then may the file de-duplication read
containment.

---

## 9. Code state

**Uncommitted in the tree, tested (545 green), not applied, not pushed** — the last commit is `79d909c3`:

1. `SpanEvidence.SelfProving(note, start, end)` — parses a quoted indicia line and compares it to the range.
2. `ContainedDuplicateJob` — gold's blanket exemption replaced by self-proving-or-typed-by-hand.
3. `SpanSelection.IsDiscardable(..., volumeNo)` — a curated `#N-N` is discarded only when it equals the
   volume ordinal (§6.2).
4. `docs/books/containment/v1_curation.jsonl` — all 1,278 v1 results.
5. `README.md` — supersession banner and corrected trust classes.
6. `.claude/skills/books-library-ops/references/containment-provenance.md`.

**Preserved into the repo** (the session scratchpads they were written in are session-scoped and are
gone): `docs/books/containment/tools/`

| file | what |
|---|---|
| `export_packets_v3.py` | builds the complete per-series evidence packet. **Written but never run** — v2 was what the two decided series used |
| `render2.py` | views one series' packet (built for v2; needs its field names updated for v3) |
| `pass2.py` | expands per-series decision files into importer JSONL + flags, and **refuses a series unless every edition is decided exactly once** |
| `backup_live.py` | SQLite online-backup of the live pair — safe with the host serving. Run it before anything writes |
| `runsql.py` | runs the `.sql` measurement files (registers a `REGEXP` function python's sqlite3 lacks) |
| `live_a.ps1`, `live_b.ps1` | the two halves of the full live chain, as actually run on 2026-09-08 |
| `sql/M.sql`, `A1.sql`, `A2.sql`, `A3_A4.sql` | the before/after measurement suite |
| `sql/M_baseline_2026-09-07.txt` | the measurement taken before this project's live run |
| `sql/saga_reference.txt` | the Saga (14966) report to diff acceptance against |
| `../decisions/S1810.txt`, `S68162.txt` | the only two series decided under the new method — Baltimore and The Essential Groo |

**Do not trust or rebuild:** `span_audit.py`, `verify_spans.py`, `prose_scan.py`, `tiers.py`. Each was
wrong on first run. They are listed so they are not mistaken for tools.

---

## 10. Environment, traps, and verified recipes

Written down because each one cost time to find.

### 10.1 The box
- **7-Zip** at `C:\Program Files\7-Zip\7z.exe` — reads `.cbr` (RAR) and `.cbz`. Python has `rarfile`
  and `PIL`. **There is no OCR** (`tesseract` is not installed) and none is needed: extract a page and
  *look* at it.
- The comics live on `\\Library\Public\5 - Comics\…` and are readable. `Item.Path` is exact.
- **Never run a full scan of `L:` / the share**, and never delete or rename on it. Catalog work is
  database-only.

### 10.2 Tool traps
- **The Bash tool halves backslashes.** Anything carrying a Windows or UNC path goes through the
  PowerShell tool or a script *file*, never an inline Bash heredoc.
- **Bash heredocs break on apostrophes** — comic titles are full of them ("Old Man's Cave"). Use the
  Write tool for any file with prose in it.
- **`du`, `ls | wc` on the cache trees time out** (117k+ files). Use PowerShell
  `[System.IO.Directory]::EnumerateFiles(p,'*',AllDirectories)`.
- **robocopy on `locg_cache` takes 30+ minutes just to enumerate** 220k files even when nothing needs
  copying. Order any copy so the valuable trees go first.
- **python `sqlite3`**: named parameters take keys *without* the `:` prefix; `CREATE INDEX` on a TEMP
  table needs the `temp.` schema prefix; and splitting a `.sql` file on `;` then skipping statements
  that *start* with `--` silently drops any statement whose first line is a comment.
- **xUnit `Assert.Same` fails on `SpanSelection.Candidate`** — it is a `readonly record struct`, so
  boxing defeats reference equality. Use `Assert.Equal`.
- **vitest is flaky under I/O load** — 6 failures while a `dotnet test` run was hammering the disk, 0 on
  a clean run. Re-run alone before believing a UI failure.

### 10.3 Reading a book (the decisive move, and it is cheap)
```bash
"/c/Program Files/7-Zip/7z.exe" l "<Item.Path>"                 # list; find the lowest-numbered pages
```
```powershell
& 'C:\Program Files\7-Zip\7z.exe' e "<path>" -o"<outdir>" -y "*00[0-4].jpg"
```
```python
from PIL import Image                       # downscale so the read is cheap
im = Image.open(src); im.thumbnail((1700, 1700)); im.convert('RGB').save(dst, quality=85)
```
Then Read the `.jpg`. Front matter for most publishers, **the last page for Marvel** (§4.2). Check
`data/books/archive/_pages/work/<itemId>/` first — v1 may already have cached the pages.

### 10.4 Measuring
```bash
python tools/runsql.py <books.db> tools/sql/M.sql                       # the census
python tools/runsql.py <books.db> tools/sql/series_report.sql ":sid=14966"   # Saga acceptance
```
Diff the Saga output against `tools/sql/saga_reference.txt`; the only acceptable difference from the
proven run is the `spanSource` column.

### 10.5 The deploy boundary
`git push` to master rebuilds and deploys **the site**. It does **nothing** to the Books host, which
serves `/API/Books/*` — that updates only through an **elevated** `.\scripts\deploy-books-host.ps1`
(the script refuses if not elevated, because it stops an NSSM service). So a change to a controller or a
CLI verb is not live until Eric runs that. The containment review UI
(`/books/admin?tab=containment`, `ContainmentController`, `ContainmentFlag`) is built and committed but
has never been deployed; the tab detects a 404 and says so rather than showing an empty queue.

---

## 11. Where the confirmed defects are

Item ids, so nothing has to be re-hunted.

| defect | items |
|---|---|
| Bone Vol. 04 `#21-27` → **`#20-27`** | `26813` (Vol. 04), `26812` (Vol. 03, the calibration) |
| Hellboy Omnibus 03 `#8-10`, note says vol 12 | `15102` |
| Hellboy Omnibus 04 `#11-12`, note says *Hellboy in Hell* | `15103` |
| Checkmate `13-31` vs indicia `13-19, 26-31` | `63873` |
| Superman `33-40` vs `33-36, 39-40` | `72605` |
| X-Men `1-21` vs `#1-12 & #16-21` | `92979` |
| Fables Vol. 04 `19-27` claiming #22 | `112578` |
| Fables Vol. 22 "Farewell" — genuine `#150-150`, was being discarded | `112596` |
| Angel `1-17` vs `#1-#14 … #17` | `13881` |
| Essential Groo — the whole `#N-N` ordinal ladder | `106160`–`106173` |
| Baltimore — ranges correct, issue numbers wrong beneath | series `1810` |
| B.P.R.D. Omnibus 08 & 10 — silence → junk LOCG `#1-5` | `14938`, `14940` |

**Series carrying the issue-number defect (§6.3):** `1810` Baltimore, `15695` Witchfinder, `10839`
Lobster Johnson, `64085` Abe Sapien (2013) — all in
`Dark Horse\Hellboy, B.P.R.D. & Related`, plus six smaller elsewhere.

### A near-miss worth remembering
The obvious fix for §6.2 — "stop discarding Curated `#N-N`" — would have let all **30** Essential
Groo/Transformers/Masterworks ordinal artefacts win, because they are also Curated `#N-N`. The
discriminator is the **volume ordinal**, and it was only visible by reading a shelf where both kinds sat
side by side. **A fix derived from one example will do damage at the other end of the same population.**

---

## 12. Open questions

1. **Fix §6.3 (issue numbers) and §6.4 (identity/series splits) first?** A range cannot be judged while
   the numbering under it is wrong, and Baltimore proves a correct range gets graded as an error because
   of it.
2. **The cross-run collision (§4.4) has been open since v1.** Splitting conflated series may be a
   prerequisite for the big titles rather than a follow-up.
3. **Scale.** 1,241 series hold ranges; 3,784 hold only refusals needing a cheap confirmation sweep. Two
   series were done properly in a full session. Is a slower correct pass acceptable, or should
   refusal-only series be signed off on the packet alone?
4. **Should `books-dedup-contained` run at all before the pass completes?** It currently produces 469
   groups from data that is 43.6% proven.
5. **Should a refusal suppress a provider leg**, so the shelf says nothing rather than something
   known-wrong (§6.5)?
6. **Web search is untried** and may be the cheapest way to clear the no-evidence tail.

---

## 13. Session of 2026-09-08 (second) — what was found and what is live

Backup taken before the first write: `data/books/v2/backup-20260908-141722/` (both files, SQLite online-backup).

### 13.1 The finding that reframes §6.3

**A range cannot be judged while the numbering under it is wrong — and the numbering was wrong because
three different layers each preferred a provider that numbers by ARC.** The defect is one thing seen three
times, and only the third fix made any shelf resolve:

1. **The parse.** `ExtractIssueNo` reached the arc count before the library index, so
   `Baltimore 016 - The Infernal Train 01 (of 03)` was issue 1. **101 files, ten series.**
2. **The reading order.** Where a ComicVine issue is matched, `ReadNumber` is ComicVine's number — the
   arc's. Ten Baltimore files were all numbered 1, so the shelf READ 001, 006, 011, 016, 019, 021, 024,
   026, 031, 036 and only then 002. This was visibly broken on the site, not just in containment.
3. **The containment ladder.** `ContainmentJob` indexed base books by `ReadNumber`, so once (2) scattered
   them the over-collection guard killed every volume in the series.

Fixing only (1) moved Baltimore from 3 attached issues to 40 and changed nothing else in the library.
Fixing only (1)+(3) made Baltimore *worse* — the guard dropped every volume — because the base ladder was
still ordered by the arc. All three together is what makes a shelf tile.

**LOCG cannot arbitrate any of it.** Its per-comic number is the arc's, and it disagrees with all 23 of the
repaired rows it has an opinion on. Worse, its containment edges cannot reach these shelves at all:
Baltimore's nine volumes carry 61 contained edges between them and **not one points at a file we hold**,
because no Baltimore issue file has a LOCG link. Witchfinder has none whatever. The two-way mapping is
present on the container side and absent on the contained side.

**What settled every one of them was the shelf**: the volume titles name their arcs, and the page
arithmetic confirms it — every Dark Horse trade in that folder carries 15–21pp of matter over the sum of
its issues' pages, and each omnibus is the sum of three trades to within 5pp.

### 13.2 Live now

| what | scope |
|---|---|
| `ComicDetail.IssueNo` repaired | 101 rows, 10 series (`docs/books/containment/issue_numbers_2026-09-08.txt`) |
| item 15002 Witchfinder `000` prequel, `16` → `0` | by hand; 16 was the DHP issue it was printed in |
| Curated spans for Witchfinder (7) and Lobster Johnson (8) | `decisions/S15695.txt`, `decisions/S10839.txt` |
| reading order + containment + resolve rebuilt | full library |

Issue files nested under a collection **11,289 → 11,474**; containers with real contents **1,698 → 1,762**.
Baltimore, Witchfinder and Lobster Johnson now tile exactly. **Saga (14966) is byte-identical to its
pre-write snapshot** at every step.

### 13.3 Code (uncommitted, 572 tests green)

1. `SpanEvidence.ExcludedIssues` — the gap a note's quotation DENIES. Anchor rule: the quote must name both
   endpoints. Over all 4,081 curated rows that is 37 genuine gapped hulls, and it correctly rejects all 56
   notes that quote a *different* numbering (Baltimore's arcs, Groo's source series, a sibling's range).
2. `ContainedDuplicateJob` honours it — **6 containers, 23 issue files** no longer called redundant with a
   book that never printed them (Checkmate #20-25, Hickman X-Men #13-15, Deadpool #8-9, Doctor Strange
   #6-8, Brubaker Batman #589-590, Catwoman #25).
3. `ComicTitleParser` — the library-index rung, gated on an `(of N)` marker.
4. `books-fix-issue-numbers` — **it was unsafe to run.** Unguarded it proposed 17,641 changes; it hands a
   collection its volume number back as an issue number and overwrites ComicInfo-sourced numbers. Now it
   skips both (4,835) and takes `--only <regex>` so a ladder change is applied to the population that rung
   explains (101). **Never run it without `--only`.**
5. `ContainmentJob` — a single issue believes its own span only when the page arithmetic can hold it
   (`PageArithmetic`), and the ladder is in issue numbers, not reading numbers. Fifteen of Lobster
   Johnson's thirty-one floppies carry a LOCG span pointing at the TRADE's record; a 27-page file is not
   five issues.
6. `ReadingOrderJob.ReconcileCollapsedShelf` — when our own issue numbers tell more of a shelf's files
   apart than the numbers about to be stored, the provider is numbering in another coordinate and the
   filename wins. Fires on **7 series, 2,482 files**; everywhere else ComicVine keeps the say it has earned.
7. `pass2.py` takes its coverage check from `books.db` instead of a packet export; `render3.py` reads a v3
   packet; `export_packets_v3.py` writes its prose cache atomically.

### 13.4 Still open, found this session

- **965 items whose stored `IssueNo` is the literal string `01 (of 04)`**, all from ComicInfo, plus 54 more
  like `Annual 04` / `Part 03`. They can never sort, compare or attach. The `NN (of MM)` shape is a clean
  fix; `Annual 04` and `18 (GL I Only)` are not and must not be reduced to a bare number.
- **Witchfinder is one run across four Series ids** — 15695, 21751 (Vol. 06), 21752 and 21753 — so its
  Omnibus Book 02 cannot be described by a range in any single one. Items 15025-15029 are the same rips as
  15038-15042, filed twice. Flagged, not resolved.
- **Abe Sapien (2013) conflates the 2008 and 2013 runs**; its ladder still carries duplicate numbers.
- The omnibuses attach nothing because a base book takes exactly one parent (the narrowest). That is by
  design, but it means an omnibus never shows contents when its trades are present.
- `export_packets_v3.py` has still never completed a run; it died silently after ~40 minutes of the LOCG
  prose sweep.

---

## §14 — The fold: what a run is, and five ways the tooling got it wrong

Written 2026-09-08, after the pass that healed run identity across the whole library. Everything here was
paid for once; none of it should have to be paid for again.

### 14.1 The three tiers, and why `Series` stays run-grained

    Franchise (a string on Series)  ->  Title (the new `SeriesTitle` table)  ->  Run (= a `Series` row)

`CollectionNode`, `ReadingOrderEntry` and `Series.CvVolumeId` are all keyed per RUN, and issue numbers
restart at #1 with every relaunch. A `Series` that spans two runs therefore cannot hold a correct range —
two different #1s share one number space. What was missing sat ABOVE the run: `Harley Quinn` is a title;
`v1 (2000)`, `v2 (2014)`, `v3 (Rebirth) (2016)` and `v4 (2021)` are its runs. `SeriesTitle` is that tier.
Nothing at runtime reads it yet; the Franchise facet still reads `Series.Franchise`.

### 14.2 Containment only nests INSIDE a Series, so a scattered run nests nothing

This is the fact that made the fold necessary. `Green Lantern v3 (1990)` had its files spread over seven
Series and `01 Nightwing v1 (1995) + v2 (1996)` over five, because the filename parser had guessed
differently for different files. A trade on one of those shelves can never nest the issues it collects,
which sit on another. Splitting a shelf per-run WITHOUT also gathering the run's strays makes this worse:
the first split pass dropped nested issues from 11,731 to 10,333 for exactly that reason.

So the unit of work is the TITLE FOLDER, not the shelf. The folder tree is the librarian's own judgement
about which run a file belongs to, and it does not care what the parser guessed. `fold_by_folder.py` /
`fold_all.py` re-key every file under a title folder to the run its folder names, across all Series at
once — 26,439 files, healing 1,070 run/shelf splits.

### 14.3 What is and is not a run folder — four rules, each bought with a defect

  1. **The year is part of a run's identity.** Keying on the volume number alone fused
     `Fantastic Four v1 (1961)`, `v1 (2003)` and `v1 (2015)` — 495 files from three shelves — into one run.
  2. **...except across a `vN.M` subdivision.** `Harley Quinn v4.1 (2021)` and `v4.2 (2024)` are OUR OWN
     subdivision of one run: the suffix marks a creative-team era and the issues run straight through
     (#1-37 then #38-47). `Detective Comics v1.1` through `v1.4` are one #934-1080 numbering.
     `subdivision_merge.py` proves the join by MEASUREMENT — two shelves are one run only when their issue
     numbers are DISJOINT, because a relaunch restarts at 1 and would overlap.
  3. **`Item.Path` ends in the FILE NAME.** The last path segment is a file, not a folder — and a manga
     volume file is named exactly like a run folder (`One Piece v001 (2003) (Digital) (...).cbz` carries a
     volume marker and a year just as `04 Nightwing v4.1 (2016)` does). Falling back to the first segment
     when nothing else qualified handed that file name to the run matcher and made ONE SERIES PER VOLUME:
     103 for One Piece, 73 for Naruto, 67 for Bleach, 49 for Initial D. Search `segs[:-1]` only.
  4. **A folder holding one book is a TRADE folder, not a run.** `Green Lantern - New Guardians v01 (2012)`
     and `Grayson v01 (2015)` match the same shape. A candidate is a run only if it holds an ISSUE file or
     at least three files.

And the guard that keeps a run from swallowing its neighbours: a file is folded only when its own
`ParsedSeriesKey`, with a trailing year or volume marker stripped, EXACTLY equals the run's title stem.
A prefix test is not enough — it folds Nightwing's 1995 one-shot `Alfreds Return`, which carries #1, into
the 1996 ongoing and collides it with that run's own #1.

### 14.4 Two ways a batch verb lied about what it had done

  * **`books-series-split` stamped ONE franchise and ONE `TitleId` on every run it created in a batch** —
    the first non-empty pair it found among all the shelves the batch touched. A 44-run batch filed
    `G.I. Joe v2 (2001)` and `Batman Beyond v6 (2016)` under the title `30 Days of Night`. Worse, those
    wrong values then LOOKED like evidence to `build_titles.py`, whose repair spreads a franchise across a
    title when every run of it that has one agrees — so `Ka-Zar`, a Marvel title whose runs all had NULL,
    acquired 'Batman' from two split-created siblings and passed it to the three originals. Inheritance is
    now per key, and `repair_title_links.py` checks every link against the series' OWN name.
  * **The undo log was written once at the end.** The Series-creation step threw a UNIQUE clash on
    `Series.CanonicalKey` (existence was being checked by `ParsedKey`, which is not the unique index), and
    26,439 already-committed re-keys had no recorded previous key at all. Recovery came from the snapshot
    taken before the pass, not from the log. The log is now flushed per batch, beside the commit it records.

### 14.5 Refutable provider claims, and what they cost

`junk_claims.py` refutes a claim from the file itself, three ways: THIN (fewer than 12 pages per claimed
issue — a 152pp book cannot hold #4-643), MISLINK (LOCG's own cached page says the record is a single
~28pp "Comic", so the span describes a different object — this is the mechanism behind the §6.2
`Vol. 07 -> #7-7` shape), and ORDINAL (the claim is exactly #N-N where N is the volume ordinal).

Library-wide: 325 refutable claims, with 3,419 issue files inside a winning one and 2,574 nested under
one. Those were retracted with tombstones. A retracted claim is not a loss — a claim the book demonstrably
cannot support was never information.

### 14.6 Two more places a wrong container costs files

  * **A one-page file is a variant COVER SCAN, not the issue.** Nightwing v4 alone holds 159 of them. A
    trade that collects #1-8 does not make the cover art ripped out of #3 redundant, so
    `ContainedDuplicateJob` holds back anything under `MinIssuePages` (8) and says so in the evidence.
  * **2,274 single issues carried `IsCollection = 1`.** `Plastic Man v2 008 (1968).cbz`, 36pp — while that
    flag stands the file is a container waiting for a range, and any provider willing to guess one gets to
    nest the shelf under itself. `unflag_floppies.py` clears it where BOTH tests hold: the filename carries
    a plain issue number with no volume/omnibus/collection word, and the file is under 60 pages.

### 14.7 A judgement belongs to the ITEM, not to the shelf

A decision file is named for a Series, but what was judged is a book: "this collects #22-27". When the
fold moves a file to the run its folder names, the judgement is still true and simply filed in the wrong
place. `rehome_decisions.py` follows every line to its item's current shelf, expanding a blanket
`U <seriesId>` into one `u <itemId>` per book FIRST — a blanket refusal is about a shelf, and once its
items are on several shelves the blanket would silently start covering books it never saw.

### 14.8 Where the evidence actually is, by publisher

  * **DC digital trades** print "Originally published in single magazine form in ..." on the copyright
    page — p3 for the Zone-Empire era, p4-p5 for the 2021+ Son of Ultron rips. Many also print a TABLE OF
    CONTENTS naming the source issue of EVERY chapter ("FROM NIGHTWING #35, SEPTEMBER 1999"), which is
    stronger than the indicia line: read the first entry and the last.
  * **Marvel** prints its indicia on the LAST page, and the back cover often carries "Collecting ...".
    Modern Marvel trades also attribute credits PER ISSUE — "TAMRA BONVILLAIN [#22] & ANTONIO FABELA
    [#23-26]" — which names the range as precisely as an indicia line.
  * Where a book states nothing, REFUSE. Six Superior Spider-Man trades and seven Red Hood trades were
    refused rather than guessed; not one issue of either run is held, so the refusal costs nothing, and a
    range taken from the volume label would have been pure invention.

### 14.9 Reading a page is cheap if you crop it

Extract the pages, crop the band the indicia sits in, scale to ~900px wide and STACK four books into one
contact sheet. One look then settles four books. Full pages at readable resolution cost four times as much
and say no more.

### 14.10 A fourth coordinate: the YEAR

§4.2 names three coordinates — issue, volume, chapter. Reading the numberless population turned up a
fourth. Newspaper-strip collections are keyed by YEAR and by nothing else: `Prince Valiant (1980 Sundays)`,
`Ziggy (2004)`, `Popeye (1939) (Dailies)`, `Rip Kirby (1957)`, `The Family Circus (2011)`, `Flash Gordon
(2016)`, `Wallace the Brave (2019)`, `Doonesbury 1970`, `Garfield 1982`. The year is the identifier, not a
publication date and not an issue number, and a shelf of them has no issue ladder at all.

This matters twice. Storing the year as an issue number is what produced the `IssueNo = 2013` fault §14
already describes. And REMOVING a year-shaped number is just as wrong: 207 files on the `2000 AD` shelf are
numbered 1900-2107 because those are its genuine prog numbers, and any rule that "fixed years" would have
destroyed every one of them. Both mistakes are invisible to a pattern and obvious to a reader.

So the numberless population is read, not inferred, and most of it is CORRECTLY numberless: of 10,970 files
with no issue number, 7,654 sit alone on their shelf and are one-shots or graphic novels. The readable
numbers concentrate on shelves holding a run — `What If V2 041 ..The Avengers Had Fought Galactus` states
its number, and reading the run together confirms the coordinate is an issue and not a volume.
`tools/list_unknown_issues.py --min=2` orders the work that way; `tools/apply_read_issue_numbers.py` stores
a hand-written sheet and derives nothing.

### 14.11 Two guards that were measuring the wrong thing

Both were found the same way: a range READ off a book's own copyright page nested nothing, and the question
"why not" had an answer in the engine rather than in the evidence.

**The span guard counted ladder positions, not coordinates.** `ContainmentJob` rejects a container whose
matches are scattered — the comment's own example is a conflated-run collision matching "a handful
scattered far apart, giving a small count yet an enormous span" — by testing `span > rangeSize * 1.3 + 3`.
But it measured span in ladder POSITIONS, and this library holds many issues two and three times over: a
chronology-tree rip beside a run rip. `Green Lantern Vol. 02` collects #7-13, seven issues held as
twenty-one files; twenty-one positions against an allowance of twelve, so a range taken straight from the
book was thrown away. Measured in distinct COORDINATES it is seven against seven.

Library-wide the positional measure was discarding **269 judged ranges over 3,518 files**. Counting
coordinates instead took nesting from 7,218 to 9,487 and left the guard's intent intact — scatter is a
property of the coordinates, so a claim of #1-5 whose matches land at #1 and #300 still covers hundreds of
coordinates between them and is still rejected.

**The parent test matched on position and never re-asked the range.** A container's node range is a
contiguous run of base positions, and a position WINDOW is not a range: the window that holds
`Aquaman Vol. 01`'s #1-8 also holds files numbered outside them. Thirteen containers were nesting
coordinates their own range excludes, one of them thirty-five of them. The gap check on the line above
already re-asks the note's exclusions at assignment time; the declared range now gets asked there too.
That is a tightening, which is the direction that cannot lose files, and it took the over-claiming
containers from 13 to 6 while nesting settled at 9,228.

**Six remain, and they are named rather than hidden**: items 63455, 63456 (Bombshells United Vol. 02/03),
74961 (Titans Vol. 01), 85482 (X-23 Complete Collection Vol. 01, whose own judged range is the suspicious
`#1-1`), 34760 (Savage Dragon Archives Vol. 01) and 92970 (X-23 Omnibus v01). They are container-inside-
container cases, where the child has no point coordinate for the new check to test. `tools/overclaim_check.py`
is the standing check — run it after any containment rebuild, because "did nesting go up" is the wrong
question and "did anything nest more than its book prints" is the right one.

### 14.12 The `IsCollection` flag is wrong in BOTH directions, and page count decides neither

§14.6 records one half: 2,274 single issues flagged as collections, because a page-rip carries the
ComicInfo of the trade it was cut out of. Reading the other half found **1,673 genuine collections flagged
as single issues** — and while that flag stands a trade cannot nest anything, so its contents stay loose
forever and its shelf looks unjudged. That is the larger of the two defects.

**It is the format of the original comic, not the ripper.** The obvious theory — one bad scene group — was
measured and is false: 1,520 of the collections AND 140 of the retractions are "-Empire" rips. The scene
rips everything, so provenance separates nothing. What separates them is what the comic IS:

  the modern digital trade   100-500pp, the filename names a WORK and carries no issue number, and the
                             span is tight and believable (`1-5`, `1-6`, `1-12`). Nearly every wrongly
                             flagged collection is one of these.
  the oversized single issue 60-130pp is its NORMAL size — annuals, "Giant", "Spectacular", digests,
                             Golden Age 68-pagers, Heavy Metal magazine issues. Page count would have
                             mis-called every one of them.

**A reflowed rip is not bigger, it is narrower.** `All-Star Superman 001 (2024) (digital-mobile)` is 123pp
and reads like a six-issue collection by any size test. Opening it settles it in one page: page 2 is a
SINGLE PANEL of the famous four-panel origin page, because the mobile rip puts one panel on each page. It
is issue #1 alone. `digital-mobile`, Infinity Comic and Webtoons rips inflate page count roughly 5x, so on
those files page count carries no information about size at all.

**Arming a container's span can create an over-claim, so flipping the flag is not always safe.**
`ContainmentJob` applies the page-arithmetic "thin" guard only to BASE books; a container's span is
believed as it stands. So setting `IsCollection = 1` on a real collection that carries a junk provider span
ACTIVATES that span. The two `2000AD #61-85 Cursed Earth` rips are genuine collections whose span reads
`#1-368`, which on the 1,754-file Judge Dredd shelf would swallow 861 files; `Guardians of the Galaxy: An
Awesome Mix` reads `#1-181` against a 139-file shelf. About 35 files were left flagged as issues for
exactly this reason — under-claiming only fails to save space (§2). Retract the span first, then flip.

**The junk spans have named causes**, which is worth knowing before dismissing one as noise: 64 of 295
carry the numbering of the title the issue REPRINTS; the rest are second coordinates — digital chapters
(Batman '66), webtoon episodes, Infinity Comic chapters, and French-album numbering on European reprints.
Seventy-six of the 295 would have nested files from the issue's own shelf under it, up to 368 files under a
single 100-Page Super Spectacular.

### 14.13 The coverage check could not see the containers that were doing the damage

The decision-file contract is that a shelf does not expand unless **every collected edition on it is decided
exactly once**, and `pass2.load_packets()` builds that set with `WHERE cd.IsCollection = 1`. So the guarantee
was never about containers — it was about items *flagged* as collections. Thirty books flagged as single
issues were nesting **604 files** and no decision file could reach one of them. The queue read zero and the
work was not done.

**The standing check is therefore not "are all shelves decided" but this query**, run after every rebuild
beside `overclaim_check.py`:

    SELECT p.Id, count(*) FROM CollectionNode n JOIN Item p ON p.Id = n.ParentItemId
    JOIN ComicDetail pd ON pd.ItemId = p.Id
    WHERE n.ParentItemId IS NOT NULL AND pd.IsCollection = 0 GROUP BY p.Id

It must return nothing. A container that is not flagged as a collection is a container nobody judged.

**What they were**, and it is the same list every time: `Fantastic Four by J. Michael Straczynski` holding
Fantastic Four (1961) **#1-5** — the Lee and Kirby originals, on a run that is #527-543; `Archie by Bob
Montana - The Complete Newspaper Comics` holding **353** Archie comic books, because a strip reprint's
coordinate is a date and LOCG gave it #1-494; an ART BOOK (`The Art of Masters of the Universe: Revolution`)
holding the four issues it illustrates; `Gunsmith Cats Revised Edition 02` holding its own siblings *and
itself*. Two of them were also sitting in the BASE ladder and had been swallowed by omnibuses, which is the
same defect pointing the other way.

**Retracting one exposes the next.** The files a retracted book was holding do not disappear; they fall back
to the ladder and the SIBLING volume — flagged the same way, carrying the same hull — picks them up.
`Captain America - Road to Reborn` gave its 86 files to `Captain America - Reborn`, which took 99. Three
rounds were needed: 25, then 4, then 1. Do not stop at the first pass; rebuild and re-run the query until it
returns zero, which is the ordinary shape of §1's "drive it to zero rather than measuring it".
