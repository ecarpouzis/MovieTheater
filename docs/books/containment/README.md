# Collected-edition containment — the model pass

What a collected edition *contains* decides which single issues are redundant copies. A file
de-duplication reads containment, so a wrong span deletes a file we still want. **Correctness beats
coverage**: a refusal is a first-class answer here, and the pass says "unknown" 17,411 times.

## What this is

`curated_spans.jsonl` is one line per collected edition in the comics library — 20,498 of them across
5,027 series — judged by a model that looked at each series folder whole: every filename, page count,
year, volume label and the provider legs' own claims side by side.

```jsonc
{"itemId":32823,"seriesId":2,"start":1,"end":4,"editionTitle":"'68 Vol 1 - …","confidence":0.85,
 "rationale":"the original 4-issue mini; gcd title match; 161p/4=40","batch":"b000"}
{"itemId":32824,"seriesId":2,"unknown":true,"why":"identical gcd #1-4 on every volume of the shelf"}
```

The providers (LOCG, GCD, ComicVine) are **cross-checks, not the engine**. Their recurring artefacts —
one span repeated on every volume of a shelf, degenerate `#N-#N`, LOCG shell pages, truncated lower
bounds, cumulative end-points, rows that match a *different* book (Omnibus / Complete Collection /
Masterworks / Epic Collection / foreign-language editions) — are exactly what a model reading the shelf
can see and a script cannot.

`flags.csv` is the human queue: 674 rows the pass wants eyes on, with the filename joined back in.

| flag | n | means |
| --- | ---: | --- |
| `label-ambiguous` | 419 | single issues wearing a `tpb` format, duplicate copies of one volume, cover-only files |
| `overlap-in-series` | 114 | several relaunch ladders, each numbered from #1, share one Series |
| `conflated-series` | 110 | one provider span repeated across a whole shelf |
| `provider-disagrees` | 27 | the legs contradict each other on the same book |
| `arithmetic-odd` | 4 | the page count cannot fit the claimed issue count |

## Importing it

```
books-curated-spans-import --in curated_spans.jsonl --db <books.db> --apply
```

Chunked by input line, resumable with `--after`, dry-run by default, idempotent on `(ItemId, Source)`.
It writes `CollectedEditionSpan(Source = Curated)` with `ProviderRef = "model:<batch>"`, and it **never
overwrites gold** — a Curated row this pass did not write is v1 quoting an edition's own indicia, and a
disagreement is written to the flags CSV instead of displacing it.

Then rebuild the derived tables:

```
books-collected-editions  ->  books-reading-order  ->  books-containment  ->  books-resolve
```

## What it changed (measured on a copy of the live db)

| | before | after |
| --- | ---: | ---: |
| Curated spans | 1,047 | 4,090 |
| containers decided by a Curated span (`CollectionNode.SpanSource = 5`) | 924 | 1,947 |
| … by LOCG / GCD / CV | 1,316 / 404 / 801 | 1,010 / 315 / 315 |
| containers with no span at all | 5,354 | 5,212 |
| containers that resolve to real issues (`ContainsCount > 0`) | 1,628 | 1,699 |
| implausible winning spans (page audit thin / thick) | 49 / 44 | 37 / 31 |
| items ≥100pp with no span from any source | 16,363 | 16,003 |

3,049 spans written, 38 kept, **0 invalid, 0 disagreements with a gold row** — where the pass and v1's
indicia-derived rows both spoke, they agreed 38 times out of 38.

`M5` (winners whose confidence is below a rival's) rises 297 → 477 by construction: Curated outranks
every provider leg regardless of the number, which is the whole point of the precedence.

### Acceptance: Saga

Saga (series 14966) is the shape everything else is checked against — nested Books over Volumes over
issues. After the pass its report is identical to before, row for row, except that six spans are now
sourced from `Curated` instead of `Cv`:

```
Book 1  #1-18  (contains 7)     Vol. 07  #37-42
Book 2  #19-36                  Vol. 08  #43-48
Book 03 #37-54  (contains 6)    Vol. 09  #49-54  (nested in Book 03)
                                Vol. 10  #55-60
```

## Trust classes for the file de-duplication

Read `CollectionNode.SpanSource` and the span's `Confidence` before deleting anything.

1. **Safe to act on** — `SpanSource = Curated` at confidence ≥ 0.8, or a Curated row this pass did not
   write (gold: an edition quoting its own indicia). Two independent legs agreed, or the edition says so.
2. **Act on with the filename in view** — `SpanSource = Curated` at 0.6–0.79. One leg, or a gap closed by
   the ladder either side of it. The `Note` column carries the reasoning verbatim; read it.
3. **Do not act on** — any container whose `SpanSource` is still `Locg`, `Gcd`, `Cv` or `Inferred`. The
   pass looked at these and declined; the provider row that survives is the one it did not trust.
4. **Never** — anything named in `flags.csv`. Duplicate copies, ladders sharing a Series, single issues
   labelled `tpb`. These need a person.

A book with no span contains nothing as far as this data is concerned. That is a refusal, not an
assertion that it collects nothing.
