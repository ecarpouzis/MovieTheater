# Span run refs — "this book collects #a-b OF WHICH RUN, on any leg" (Eric, 2026-09-16)

## Why
A `CollectedEditionSpan` row says what a book collects (`IssueStart`-`IssueEnd`) but only names OUR series
(`SeriesId`, and that copy is denormalised and stale — see `ReadingOrderJob.LoadSpans`). On a shelf whose books each
collect a DIFFERENT run (Hellboy Vol. 01 = Seed of Destruction #1-4, Vol. 02 = Wake the Devil #1-5; B.P.R.D.;
Ether; Pathfinder; Blue Book …) the run a range counts in has no Series row of ours, so the readers' `N` lines held
the provider ids as prose and nothing queryable recorded them. Eric's ruling: identity must persist the correct data
— the shelf takes its collected LINE (one leg suffices), every book keeps its own item records, and each book's range
must name the RUN it counts in. This is also the containment engine's missing fact: two Curated spans "#1-4" and
"#1-5" on one shelf are NOT two editions of the same material when they count in different runs.

## Data (src/MovieTheater.Books.Db, Hot context — EF migration `SpanRunRef`)
NOT two hard-coded columns. A run can be named on ANY leg that has a run concept — ComicVine (volume), GCD (series),
MangaUpdates (series, our manga leg), Barney (the 2000 AD index), Marvel (series-issue ids), Inducks (Disney: the
series code `us/LTSMB` already sits in SecondaryKey of its item links) — and the enum already exists: `Provider { Cv=0,
External=1, Locg=2, Gcd=3, Mu=4, Barney=5, Marvel=6, Inducks=7 }`. LOCG has no usable series identity in our data (3 distinct
LocgSeriesId values) and Open Library is work/edition-level — those stay item legs on the `I` line.

New table, mirroring `ItemProviderLink` / `SeriesKeyLink`:
```
CollectedEditionSpanRun (ItemId, Source, Provider, ProviderKey TEXT NOT NULL, Confidence REAL NULL, CreatedAt)
  PK (ItemId, Source, Provider); FK (ItemId, Source) -> CollectedEditionSpan ON DELETE CASCADE
```
"The range on span (ItemId, Source) counts in the run ProviderKey of that Provider." Several rows per span = the
same run on several legs (CV volume + GCD series for one mini). None = unknown / the shelf's own identity (today's
meaning). Cascade so a retracted or re-imported span cannot orphan its refs; the import verb re-attaches them
(below). Entity in `Hot/Entities.cs` + snapshot + one migration; applied to the live db by `books-db-migrate`
(BooksHost verb) after `docs/books/containment/tools/backup_live.py`.

Also closed in the same change — the `I` line's ISBN was being DROPPED at apply ("ItemProviderLink has no ISBN
column"): store it on `ItemProviderLink(Provider=External)` in the shape the ExternalWork leg already uses (the
worker reads how Provider=1 / ExternalWork rows are keyed and follows it; if they key by OL work id, ProviderKey =
the work id when resolvable and SecondaryKey = the ISBN, else ProviderKey = `isbn:<isbn>`), so an Open Library bridge
is persisted, never only quoted in evidence.

## Decision grammar (identity pass) — new line kind `C`
```
C <itemId> cv=<volumeId>|- gcd=<seriesId>|- [mu=<muSeriesId>] [barney=<key>] [inducks=<seriesCode>] [marvel=<seriesId>] #<a>-<b> <conf> | evidence (≥ 40 chars: the run's name/year/count and why this book is that range)
```
"This book collects issues a-b of that run." One `C` per item at most. `a`/`b` accept `.5` decimals like spans do.
At least one id. Required on every collected edition whose range counts in a run OTHER than the shelf's own S
identity (chains of minis, trade lines, omnibus lines, crossovers); optional when the run IS the shelf's identity.
`I` lines are unchanged (the book's own record); `C` is the book's CONTENT.

`idbase.scan_decisions` parses `C` into `rec["collects"][itemId] = ({provider: key}, a, b, conf)`; `check_identity`
validates: the item belongs to a shelf in the batch's `.ids` (or, in a revisit, to a shelf the file decides); cv is in
CvVolume / the rip; gcd is in legs.GcdSeries / the dump; mu is in MuSeries; barney / inducks / marvel are left as typed; a ≤ b; conf in
the vocabulary; evidence ≥ 40; at most one `C` per item. `check_identity`'s per-file summary gains a `C` count.

## Apply (`docs/books/identity/tools/apply_identity.py`)
For each winning `C` line, on the item's `Source = 3 (Curated)` span row:
- no row → INSERT (ItemId, 3, SeriesId = item's shelf, IssueStart a, IssueEnd b, EditionTitle NULL, ProviderRef
  `identity:<batch>`, Contiguous 1, Confidence conf, Note = evidence, CreatedAt) + one `CollectedEditionSpanRun` row
  per id on the line.
- row whose ProviderRef starts with `model:` or `identity:` → UPDATE range + Confidence + Note + ProviderRef; replace
  its run rows with the line's.
- gold / `admin:` / self-proving row (`CuratedSpanImport.Existing.IsGold` / `IsProven` semantics): write ONLY the run
  rows; if its range differs from a-b, print a CONFLICT line (kept, not written) — Eric's question.
Every write goes into the batch's undo jsonl like the other tables. The dry run prints `C` counts and conflicts.

## The wave pipeline must not erase them
`books-curated-spans-import` (`CuratedSpansImportCommand`) DELETEs then INSERTs the Curated row for a model line: the
cascade would drop its run rows, so the verb must read them first and re-insert them (a model range replacing an `identity:` row is already blocked
by `IsGold`, so only `model:` rows are rewritten — still carry the ids). `SeriesRebuildJob` re-points `SeriesId` on
merge and leaves the ids alone. `restore_live.py` / backups are whole-file — nothing to do.

## Consumers
- `ReadingOrderJob.LoadSpans` returns the span's run refs as a `{Provider: key}` map; `ContainmentJob.Book` carries it.
- Nesting rule (both the positional pass and the range-based parent test): two Curated spans that share a Provider and whose keys DIFFER
  on it are not comparable — neither nests in the other and equal ranges are not "two editions
  of the same material". Unknown on either side = today's behaviour. This closes the "#1-5 of mini A inside #1-4 of
  mini B" defect the conflated-series flags describe.
- `ItemDetail.EditionSpanBlock` + the containment admin payload gain `Runs: [{Provider, Key, Name?}]` (Name from CvVolume /
  GcdSeries / MuSeries when resolvable) so the reader UI can say "collects #1-5 of Wake the Devil" (UI text change
  is a follow-up; the API field ships now).

## Tests
`MovieTheater.Books.Tests`: CuratedSpanImportTests (ids survive a model re-import), a ContainmentJob test (differing
run ids never nest; equal ids / null ids unchanged), a projection test if one exists. `docs/books/identity/tools/
selftest.py`: a `C` line round-trips through check + apply dry run; a bad `C` (unknown id, a > b, two per item) fails.

## Rollout
1. Tools worker builds all of the above, `dotnet build` of BooksHost (Debug — the build `wave_land.ps1` runs),
   `dotnet test` green, selftest green. 2. Lead: backup_live → `books-db-migrate`. 3. Brief + WORKER_PROMPT gain the
   `C` line; the Opus flip pass over R-020..R-022 (Eric's one-leg ruling) writes `C` lines for every book on those
   shelves. 4. Wave 5 lands them. 5. Later: a targeted `C` pass over already-landed chain-of-minis shelves (A/B tiers'
   trade-only shelves) — found by Curated spans with no `CollectedEditionSpanRun` rows on shelves whose S is a
   collected line.
