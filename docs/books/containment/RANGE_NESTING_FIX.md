# Range nesting: a judged edition that holds none of our issues must still sit in the reading order and inside its book

**Found 2026-09-10 by Eric on Saga (S14966), the acceptance gate.** The reading list showed `Saga Book 2` (#19-36),
`Vol. 07` (#37-42), `Vol. 08` (#43-48) and `Vol. 10` (#55-60) under "Editions without a known range", flat, while
`Book 03` (#37-54) nested only `Vol. 09`. The ranges ARE known: `CollectedEditionSpan(Source=3)` holds them and
`ReadingOrderEntry` places every one correctly (Book 2 at ReadIndex 9 between Book 1 and Book 3; Vols 7/8/9 at
11/12/13; Vol 10 at 20, `Notes = "collects #19-#36 [Curated]"`).

**Cause — `Resolve/ContainmentJob.cs`, the node pass.** A container's node span is measured in BASE POSITIONS of
owned issue files (`SpanStart = lo + 1 … SpanEnd = hi + 1`). A container whose issues we own only as trades has
no positions, so it gets `SpanStart = SpanEnd = 0, ContainsCount = 0`, and the parent pass skips it outright:

    // A container that collects nothing we own has no position to be contained BY, so it stays a
    // top-level leaf — otherwise every "empty" container nests under any other empty one.
    if (b.SpanEnd <= 0) { b.ParentItemId = null; continue; }

The UI (`src/ui/src/Pages/Books/ReadingList.tsx`, the "Editions without a known range" section) then reads
`SpanEnd <= 0` as "no known range".

**Population (2026-09-10, live):** 8,461 judged editions; **1,427** hold no owned issue file (span 0-0) and are
shown as "without a known range"; 1,411 of those sit on shelves that DO hold issue files (Saga Book 2's shape);
**419 on 75 shelves** have a sibling container whose judged range strictly contains theirs and should nest inside
it (Vol. 07 inside Book 03).

## The fix (two halves)

### 1. `ContainmentJob` — a second parent test, by judged RANGE, for containers without positions
Keep the positional pass exactly as it is (its guards were bought with defects — PLAN §14.11, §6.4). ADD, for a
container with `SpanEnd <= 0` **and a judged range** (`SpanFromStart`/`SpanFromEnd` from a Curated span; never a
provider span — an unjudged claim must not nest anything), a parent chosen among sibling containers that ALSO
carry a judged range, where:
- `p.Level > b.Level` (a Book can hold a Volume; never the reverse), `p.ItemId != b.ItemId`;
- `p.SpanFromStart <= b.SpanFromStart && p.SpanFromEnd >= b.SpanFromEnd` and the parent's range is STRICTLY
  wider (equal ranges are two editions of the same material — never nest one in the other);
- the page test as in the positional pass (`p.PageCount >= b.PageCount` when both known);
- `p.ExcludedCoords` must not wholly exclude the child's range;
- the SMALLEST such parent wins (as now).
"Every empty container nests under any other empty one" cannot happen: both sides need a judged range and strict
containment. `ContainsCount` stays 0 (it counts owned issue files) and `SpanStart/SpanEnd` stay 0 (positions) —
the node gains a `ParentItemId` only. Nothing changes for containers that hold files.

Add a unit test in `src/MovieTheater.Books.Tests` (ContainmentJob tests exist) with Saga's shape: a Book #37-54
holding files #49-54, Vol 7 #37-42 and Vol 8 #43-48 with no files → both nest under the Book; Vol 9 #49-54
(holding the files) nests as before; Book 2 #19-36 with no files nests under nothing (no wider sibling).

### 2. The UI section — "Editions without a known range" must mean that
A container with a judged range but no owned issue file is not "without a known range"; it is "holds none of our
issues". `ReadingList.tsx` should place such editions INLINE in the reading order at their `ReadIndex` (the row
already exists and is correct), rendered with their `collects #a-b` label and an empty children list (or the
nested child editions from half 1), and keep the "without a known range" section for editions that truly have
no `CollectedEditionSpan` row and no `ReadNumber`. Find where the section is populated (likely on
`SpanEnd`/`ContainsCount`) and switch the test to "no range at all".

## Verification
- `dotnet test src/MovieTheater.Books.Tests` green (572+ tests today).
- Re-run `books-containment` only (the chain's last step; `run_chain3.ps1` runs all three and is fine too), then
  the standing checks in `docs/books/identity/PLAN.md` §8 — all zero — and the Saga report
  (`docs/books/containment/tools/runsql.py … series_report.sql ":sid=14966"`): Book 2 / Vol 07 / Vol 08 / Vol 10
  now carry a parent where one exists (Vol 07/08 → Book 03); every other row byte-identical. The saga reference
  then gets re-snapshotted deliberately (`sql/saga_reference.txt`) because this is the one intended change.
- Site: the host must be rebuilt and redeployed (`scripts/deploy-books-host.ps1`, elevated — Eric) for the UI
  half; the DB half is visible as soon as `books-containment` runs.

## Applied — 2026-09-10

Both halves are written; the DB half is live. `ContainmentJob.BuildSeries`'s `SpanEnd <= 0` leaf branch now
takes a parent by judged RANGE exactly as specified above (both sides Curated, `p.Level > b.Level`, strictly
wider, the page and `ExcludedCoords` guards, narrowest wins; `ContainsCount`/`SpanStart`/`SpanEnd` unchanged),
with **one guard beyond this spec**: the parent must be flagged `ComicDetail.IsCollection = 1`
(`ContainmentJob.Book.IsCollection`, loaded in `LoadBooks`). PLAN §14.13 — "a container that is not flagged as
a collection is a container nobody judged" — and without it the first run put `Ultimate Iron Man` #1-5 and
`Iron Man 2020 - Robot Revolution` #1-2 inside item 82200 `Iron Man 2020 (2018)` on **S9575 Iron Man Epic
Collection**, a mixed shelf whose trades are each judged in their own coordinates; that tripped both
`audit_containment` and `overclaim_check`. It costs exactly those two edges. Four tests in
`MovieTheater.Books.Tests/DerivedJobTests.cs` (592 green) pin the Saga shape, equal ranges, an unjudged span
on either side, and the unflagged parent.

**The population correction.** "419 on 75 shelves" above counted `CollectedEditionSpan` rows without the level
and container conditions. Measured by the rule as specified, the live shelf count is **62 editions on 21
shelves** — no looser reading reaches 419 (equal ranges 65, same-or-higher level 105, any level 109, mere
overlap 183, parent range from any source 303). The 1,427 child population is exact. After the run **60 of the
62** carry a parent (the two S9575 edges are the guard's) and **1,367** judged file-less editions remain
top-level, all of them with no wider judged sibling. `sql/saga_reference.txt` was re-snapshotted (both blocks);
its only change is `spanSrc 2 → 5` on six rows.

`ReadingList.tsx` places a container with a `spanLabel` but no position inline at its `ReadIndex`, and
"Editions without a known range" now holds only editions with no range at all. **That half is not live** — it
needs `scripts/deploy-books-host.ps1` (elevated, Eric).

## Tie-break and container dates — 2026-09-10

Two more defects on the same acceptance gate, in the same two files. Backup `backup-20260910-024658`.
`dotnet test src/MovieTheater.Books.Tests` **598 green** (592 + 6). The DB half is live (`books-reading-order`
then `books-containment`, derived tables only); the site still runs the DEPLOYED host binary, so the engine
change needs `scripts/deploy-books-host.ps1` (elevated — Eric) before `/books` shows any of it.

### 1. The nesting tie-break — the INNERMOST container wins an equal window

`ContainmentJob.BuildSeries`, the positional parent loop. A container's window is measured in the base
positions of the issue files we OWN, so two containers of very different size routinely tie: on Saga
`Book 03` (Level Book, judged #37-54) and `Vol. 09` (Level Volume, judged #49-54) both measure 8-13, because
#49-54 is all of Book 03's range we hold. The old line `p.SpanEnd - p.SpanStart < parent.SpanEnd -
parent.SpanStart` keeps the FIRST candidate on a tie — item order — so items 34063-34068 nested under the
Book (34060) and the Volume inside it (34082) showed empty.

`IsInnerThan(candidate, incumbent)` replaces that line. The window is still the first word; **on an equal
window** the order is: a book **flagged `IsCollection`** beats one that is not, then the **lower
`CollectionLevel`**, then the **narrower judged range** (`SpanFromStart..SpanFromEnd`; no range is not
narrow), then the **smaller page count** (a zero on either side is not evidence and leaves the incumbent).
Eligibility — level, window cover, pages, `ExcludedCoords`, the range re-test — is untouched.

**One guard beyond the spec, and why.** `IsCollection` leads because the positional pass has no such test —
it was kept clear of unflagged containers by item order alone, and preferring the inner level took that
accident away. The first run put five files under `Green Hornet - Sky Lights Collection` (S7899, Level
Volume, `IsCollection = 0`) instead of `Green Hornet Omnibus v01`, and three under `The Amazing Spider-Man
(2023) (DCP Webrips)` (S34339) instead of the Spider-Man omnibus; both tripped `audit_containment`'s "a
container that is not flagged as a collection" (2 FAIL). PLAN §14.13 — a container nobody flagged is a
container nobody judged. It costs exactly those two edges.

**Measured, isolated** (the new `books-containment` re-run against a copy of the pre-change backup, so no
date effect is mixed in): **114 edges moved on 18 shelves — 112 issue files and 2 containers**; no other
node field changed anywhere. Nothing was lost: 6,862 files stay nested, over 1,111 → **1,120** distinct
parents. Every shelf is the same shape — files moving off an outer Omnibus/Deluxe/Complete edition onto the
TPB that actually collects them: Watchmen 12, Blue Beetle 12, Star Wars: The High Republic 12, Black
Lightning 11, House of M 10, Red Sonja 9, The Flash v1 8, **Saga 6**, Rat Queens 6, Usagi Yojimbo 5, Green
Hornet 5, Secret War 5, MMPR v1 4, Mouse Guard 3, Skullkickers 3, Ice Cream Man 1, Avengers: Forever 1,
Justice League v4 1.

### 2. A container's reading date — dated by what it COLLECTS

`ReadingOrderJob`. The row SQL joins the item's matched ComicVine issue and takes `cvi.CoverDate`, and v1
matched a collected edition to the issue whose number equals its VOLUME number. So `Saga Vol. 07` carried
Saga #7's 2012-11-01 though it collects #37-42, `Vol. 08` carried #8's, `Book 03` carried #3's, `Book 2`
carried #2's — and since `ReadDate` is the top source of `Item.ResolvedYear`, a shelf of trades printed
2014-2022 read as 2012.

New `ReadingOrderJob.DateContainers`, run after `ReconcileCollapsedShelf` and before `PullInCollections`
(both now share one lazily-loaded `LoadSpans` call per series). For a row the parse calls a collection
(`ComicDetail.IsCollection = 1`) whose SELECTED span is a **judged** `Curated` range — a provider claim is
not enough to re-date a book:

- **(a)** the cover date of the FIRST issue of that range, from `CvIssue` for the shelf's
  `Series.CvVolumeId`, else from the OWNED issue file carrying that number (collections never date each
  other; a trade's number is the volume ordinal);
- **(b)** failing both, the book's OWN date.

**What "the book's own" means — the line this rule had to be drawn on.** The defect is a link to an issue
of THIS shelf's run picked by the volume ordinal. A link into a DIFFERENT ComicVine volume is a match
against the edition's own record in a collected-editions volume, and its cover date is the trade's printing
date: `Terry Moore's Echo Vol. 01` links to CV volume 47310 #1 at 2009-05-31 while the shelf's run is volume
20806, and its filename says 2018 because that is when it was scanned. Read literally ("never a per-file CV
issue link"), the first cut replaced 3,301 such dates with filename years. So the row SQL now also reports
whether the matched `CvIssue.VolumeId` IS the shelf's `Series.CvVolumeId`, and (b) is the local chain
(`ComicEmbedded.PublicationDate` → `ComicDetail.Year` → the insight year) only for a same-run match;
otherwise the row keeps what it had. Live the two halves are **2,611** same-run and **3,301** other-volume.

Only the DATE changes. `ReadIndex`, tier, number and suffix are still `PullInCollections`'s: a judged
edition is placed by its span start and negative suffix, so it reads immediately before the issues it
collects. A row that could not be ordered is not made orderable here.

**Measured, live:** **3,082 `ReadDate` values changed** (1,226 on `TrackRole = Container` editions, 1,856 on
base-level collections — manga/trade shelves where the collection IS the base level; `ReadingOrderJob` runs
before containment and cannot see `TrackRole`, so the population is "judged collection", which is the same
defect wherever it sits). 2,499 of them changed YEAR. Roughly 800 were answered by a cached `CvIssue`, 420
by an owned issue file, and the rest by the book's own year; the resulting precisions are 1,977 Year /
131 Month / 974 Day. **101 `ReadIndex` values moved** and 49 `CollectionNode` span/contains fields shifted
on 13 shelves — all of them base-position swaps where two collections tied on index and number and the date
was the tie-break (Harrow County vs Tales from Harrow County, One Piece vs Ace's Story, X-Files seasons).
No parent moved because of a date.

### Saga (S14966), before → after

| item | | parent before → after | ReadDate before → after |
|---|---|---|---|
| 34063-34068 | Saga 049-054 | **34060 → 34082** (Book 03 → Vol. 09) | unchanged |
| 34082 | Saga Vol. 09 (#49-54) | 34060 (unchanged) | 2012-12-28 → **2018-02-28** (#49's cover date, cached) |
| 34080 | Saga Vol. 07 (#37-42) | 34060 (unchanged) | 2012-11-01 → **2017-07-01** |
| 34081 | Saga Vol. 08 (#43-48) | 34060 (unchanged) | 2012-12-01 → **2017-07-01** |
| 34060 | Saga Book 03 (#37-54) | none (unchanged) | 2012-05-01 → **2019-07-01** |
| 34062 | Saga Book 2 (#19-36) | none (unchanged) | 2012-04-01 → **2017-07-01** |
| 34083 | Saga Vol. 10 (#55-60) | none (unchanged) | 2013-02-01 → **2022-07-01** |
| 34061 | Saga Book 1 (#1-18) | none (unchanged) | 2012-03-01 (#1's cover date — already correct) |

Only Vol. 09 reaches rung (a): **`CvIssue` is a hot SUBSET holding only issues an `ItemProviderLink`
references**, so Saga #19, #37, #43 and #55 are not cached and are not on disk either, and those five fall
to (b), the book's own year. Dating Vol. 07 and Book 03 by #37 (2016) needs `books-cv-issues` to fetch
volume 46568's issue list first — the rule will pick it up on the next run with no code change.

### Verification

`audit_containment` **0 problems**; `overclaim_check` **0 over-claiming** (1,120 containers, 6,862 files
nested); `check_decisions` **977 files, 0 failing**; `audit_issue_details` **0 suspect rows**;
`armed_unjudged_check` **0 with LIVE EXPOSURE**; `coverage_ledger` **0 unaccounted** (118,440);
`overlap_check --all` **only the 2 documented exceptions** (GL v4 S94612, MMPR S98522);
`audit_identity` **0 failures**; `identity_coverage` **both partitions sum**.

**The Saga report is byte-identical to `sql/saga_reference.txt`** (both blocks; only CRLF and a trailing
newline differ), so the reference was NOT re-snapshotted — there is nothing to re-snapshot. That is not a
pass, it is a blind spot: `sql/series_report.sql` prints idx/tier/num/sfx/src/lvl/role/contains/span/label/
spanSrc/pages/file and has **no `ParentItemId` and no `ReadDate` column**, so this gate could not see either
defect and cannot see either fix. Both were verified by direct query instead (the table above). Adding those
two columns to the report — and re-snapshotting the reference once, deliberately — is the obvious follow-up
and was left for Eric to approve.

## Scope note
The identity pass (docs/books/identity) never touched this: it decides which run a shelf IS, and its landing
only re-runs the chain. This gap predates it and is in the containment engine + the reading-list view.
