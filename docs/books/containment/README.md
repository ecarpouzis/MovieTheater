# Collected-edition containment — the model pass

> **Superseded in part, 2026-09-08.** Verification against issue-level ground truth found this pass is
> not yet safe to de-duplicate files against: only **43.6%** of its 4,081 ranges are proven, and spot
> checks turned up whole error classes the original audit never tested for (a gap in Bone's ladder, a
> Hellboy omnibus contradicting its own note, hulls flattened over non-contiguous collections such as
> Checkmate `13-19, 26-31` recorded as `13-31`). **`PLAN.md` in this folder is the current plan and
> supersedes the trust classes below.** The numbers here remain accurate as a record of what the first
> pass did.

What a collected edition *contains* decides which single issues are redundant copies. A file
de-duplication reads containment, so a wrong span deletes a file we still want. **Correctness beats
coverage**: a refusal is a first-class answer here, and the pass says "unknown" 17,421 times.

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

`flags.csv` is the human queue: 718 rows the pass wants eyes on, imported into `ContainmentFlag` and
worked through in the admin **Containment** tab.

| flag | n | means |
| --- | ---: | --- |
| `label-ambiguous` | 420 | single issues wearing a `tpb` format, duplicate copies of one volume, cover-only files |
| `overlap-in-series` | 117 | several relaunch ladders, each numbered from #1, share one Series |
| `conflated-series` | 110 | one provider span repeated across a whole shelf |
| `provider-disagrees` | 29 | the legs contradict each other, or one names a different book |
| `duplicate-edition` | 26 | two items in one series claim the same block of issues |
| `arithmetic-odd` | 6 | the page count cannot hold the issue count the range claims |
| `span-retracted` | 10 | the self-audit below withdrew a range the pass had written |

## The pass audits itself

Judgement at this scale needs a check that is not more judgement. `audit.py` (in the pass's working
set) re-reads the packets against the answers and looks only for the shapes that would make a
de-duplication delete the wrong file:

- **partial overlap** inside one series — nesting is fine (an omnibus over its volumes), straddling is not;
- **page arithmetic** — under 12 or over 60 pages per issue. Thick is safe (a book that collects more
  than it claims only ever under-claims); **thin is the dangerous direction**, because it means the
  range covers issues the book does not hold;
- **the same range claimed twice** in one series;
- **both legs agreeing against the pass**.

That found 18 answers to change. Ten spans were **retracted** — a `cv` row that turned out to name a
Conan *Omnibus* rather than the Epic Collection it sat on; `locg #1-191` on an 824-page Daredevil
omnibus; a Visionaries book that is a *selection* inside #164-186 rather than the run; two Conan
omnibus ranges the pass had tiled between neighbours while both legs said otherwise. Eight were
**revised**: Scooby-Doo Team-Up's LOCG ladder numbers digital chapters two to the issue, and GCD's
title match on volume 7 gives the print ladder away — so six volumes moved from `#1-12, #13-24, …` to
`#1-6, #7-12, …`, and volumes 7 and 8 to `#37-43` and `#44-50`.

Retraction is a real verdict, not an edit: an `unknown` line whose item still carries a row **this pass
wrote** deletes it (`Verdict.Retract`), so an audit that corrects the pass can take a span back and not
merely add. Gold is never retracted this way.

## Importing it

```
books-curated-spans-import --in curated_spans.jsonl --db <books.db> --apply
books-containment-flags-import --in flags.csv --db <books.db> --prune --apply
```

Both are chunked by input line, resumable with `--after`, dry-run by default, idempotent. The span
importer **never overwrites gold** — a Curated row it did not write is v1 quoting an edition's own
indicia, or a person typing it in the review screen — and a disagreement goes to the flags CSV instead
of displacing it. The flag importer keys on `(ItemId, Flag)`: re-importing an edited sheet refreshes the
evidence and leaves any verdict a person has already recorded alone.

Then rebuild the derived tables, and produce the overlap groups:

```
books-collected-editions -> books-reading-order -> books-containment -> books-resolve
books-dedup-contained --reset --apply
```

## Reviewing it — `/books/admin?tab=containment`

> The tab and its endpoints ship in the host binary, so they appear after an elevated
> `.\scripts\deploy-books-host.ps1`. The DATA below lands with the CLI run alone.


The flags are a queue, not a spreadsheet. Each row shows the file, the span it currently carries from
every source, and the rest of its shelf, so the answer is usually visible without opening anything.
Two verbs:

- **Rule on the flag.** *Flag stands* keeps the item out of the de-duplication; *False alarm* lets it
  back in.
- **Type the range.** `PUT /admin/containment/spans/{itemId}` writes a `Curated` span at confidence 1.0
  attributed to whoever typed it, with a `ProviderRef` of `admin:<user>` rather than `model:`. That
  makes it gold: the pass will not overwrite it, and the de-duplication trusts it outright. A person who
  can see the shelf outranks anything inferred from it.

## What it changed (measured on the live database, 2026-09-08)

| | before | after |
| --- | ---: | ---: |
| Curated spans | 1,047 | 4,081 |
| containers decided by a Curated span (`CollectionNode.SpanSource = 5`) | 500 | **1,941** |
| … by LOCG / GCD / CV | 365 / 2,063 / 395 | 1,013 / 315 / 317 |
| containers with no span at all | 5,658 | 5,213 |
| containers that resolve to real issues (`ContainsCount > 0`) | 1,390 | 1,698 |
| issue-keyed GCD spans (the volume ordinal read as an issue number) | 2,383 | **0** |
| implausible winning spans (page audit thin / thick) | 49 / 44 | 38 / 32 |
| items ≥100pp with no span from any source | 17,366 | 16,003 |
| collected editions the parser can see at all | 10,904 | 20,498 |

3,041 spans written, 36 kept, **0 invalid, 0 missing items, 0 disagreements with a gold row** — where the
pass and v1's indicia-derived rows both spoke, they agreed every time.

`M5` (winners whose confidence is below a rival's) rises 297 → 474 by construction: Curated outranks every
provider leg regardless of the number, which is the whole point of the precedence.

469 overlap groups over 2,971 members; 344 containers skipped by the trust gates.

### Acceptance: Saga

Saga (series 14966) is the shape everything else is checked against — nested Books over Volumes over
issues. On the live database its report is identical, row for row, to the run proved on the copy — and
identical to the pre-pass shape except that six spans are now sourced from `Curated` instead of `Cv`:

```
Book 1  #1-18  (contains 7)     Vol. 07  #37-42
Book 2  #19-36                  Vol. 08  #43-48
Book 03 #37-54  (contains 6)    Vol. 09  #49-54  (nested in Book 03)
                                Vol. 10  #55-60
```

## De-duplication

`books-dedup` groups files that ARE each other — same bytes, same pages, same cover. It has always
declared a fourth relationship, `ContainedIn`, and never produced one, because no fingerprint can see
that eighteen floppies and one Book 1 are the same reading. **`books-dedup-contained` produces it**,
from containment.

It reads the trust classes rather than the raw table:

1. Only a container whose winning span is `Curated` — a judged answer, not a provider leg the pass
   looked at and declined.
2. Only at confidence ≥ 0.8 — unless the note quotes the indicia naming exactly those issues, or a
   person typed the range, which are the only exemptions.
3. Never a container carrying an undecided `ContainmentFlag`, and never anything in a series flagged
   `overlap-in-series` or `conflated-series` — those are the shelves where three runs each number from
   #1 and "issue 5" names three different comics.

The groups it writes are **flag-only**: `DuplicateDetectionService.ResolveAsync` refuses to bulk-resolve
relationship 3, and the Duplicates tab hides the keeper radio for them. Owning both the floppies and the
collection is legitimate — often wanted. The job's job is to show the overlap with its evidence; the
decision stays a person's.

They also get their own view — `GET /admin/containment/overlaps`, rendered at the foot of the Containment
tab — because among thousands of signature groups in the Duplicates tab they would never be found.

### Trust classes, for anything else that reads containment

**Corrected 2026-09-08.** The original version of this list granted class 1 to any Curated row this pass
did not write — "gold". Measured against issue-level truth, those rows confirm at **47.8%** against
**83.7%** for the pass's own, and only **564 of 988** carry an indicia quotation at all. Provenance earns
nothing; the quotation does.

1. **Safe to act on** — the row's `Note` QUOTES the book's indicia naming exactly the issues the range
   claims (`SpanEvidence.SelfProving`), or a person typed the range in the review screen
   (`ProviderRef = admin:<user>`).
2. **Act on with the filename in view** — `SpanSource = Curated` at confidence ≥ 0.8 without a
   quotation. The `Note` carries the reasoning verbatim; read it.
3. **Evidence only, not grounds** — `Curated` below 0.8, whatever its provenance.
4. **Do not act on** — any container still won by `Locg`, `Gcd`, `Cv` or `Inferred`. The pass looked at
   these and declined; the provider row that survives is the one it did not trust.
5. **Never** — anything with a Pending `ContainmentFlag`.

A book with no span contains nothing *as far as this data is concerned*. That is a refusal, not an
assertion that it collects nothing.
