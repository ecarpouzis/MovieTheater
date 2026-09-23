# The identity map: making every comic in the collection knowably the same thing another collection calls it

**Status:** v2, 2026-09-09. Rewritten by the lead session after the containment pass. v1 is beside this file
(`PLAN-v1-20260909.md`) and is still worth reading for its §2 legs inventory; everything else here supersedes it.
**Audience:** a worker with none of the lead's context. Everything you need is here or named here.
**Goal:** a *confidently-correct* identity for every file-holding comic shelf (a `Series` row = one publishing
run) and, through it, for every comic file — so the collection can be diffed against another collection, and
so every provider leg we already paid to collect actually feeds the site.

---

## 0. The one rule that shapes everything below

Eric, 2026-09-09, verbatim:

> This is not a job for Regex or Oracles. Every single attempt you make to use those tools will fail because our
> data set is far too varied and filenames not standard, with multiple years and numbers in each name in varied
> positions to make those general decisions difficult. I anticipate most of this can be figured out by just
> reading the filenames and filepaths and containment details for each group of files, checking our data or the
> web until you're fully confident in our identities and data.

So: **the decision is a reading, made per shelf, with all the evidence in view. Code assembles evidence and
refuses to let a shelf be skipped. Code never decides.** v1 of this plan proposed a scoring model that
"accepts on ≥ 2 positives and 0 vetoes" — that is an oracle and it is gone. The same features are still
computed, but they are *printed in the packet* as lie detectors for the reader, never applied as a rule.

The containment pass got 976 shelves to zero defects exactly this way (`docs/books/containment/PLAN.md` §1 —
six mistakes, read it — §8 the method, §14.13 the population lesson). This plan is the same discipline applied
to identity, at ~20× the shelf count, which is why §7 spends most of its words on making each reading cheap.

Read before touching anything, in this order:

| | |
|---|---|
| `.claude/skills/books-library-ops/SKILL.md` | The map. Its table's **third column** names which of MyBooks' 26 skills each reference file replaces — never answer "do we have X" by listing directories. |
| `.claude/skills/books-library-ops/references/series-identity.md` | `Series` is DERIVED from `SeriesKeyLink` + `ComicDetail.ParsedSeriesKey` by `books-resolve --series`; the three tiers Franchise → Title → Run; the CV match; the split. |
| `.claude/skills/books-library-ops/references/providers.md` | The legs, their caches, the enum values, the rate limits. |
| `docs/books/containment/PLAN.md` §1, §8, §10.3, §14 | The doctrine and the traps. |
| `docs/books/containment/decisions/S10002.txt`, `S19353.txt` | Two real decision files — the voice, the evidence sentences, the refusal shape. |

---

## 1. Why this exists

We collected ComicVine, LOCG, GCD, MangaUpdates and Open Library, cached 100,886 provider responses, scraped
1,293,131 LOCG creator rows and 625,396 LOCG containment edges — and wired a fraction of it in. The data is not
missing; it is **collected and unused**, which is worse, because nothing reports it. And the part that *is*
wired in was matched by v1 name-scoring that nobody has read: 106,804 per-file ComicVine links with `Method =
NULL`, and 13,586 shelf-level ComicVine volumes of which 137 disagree with us on the start year by more than a
year and 853 hold fewer than two-thirds of the issues we own.

---

## 2. The measured baseline (2026-09-09, re-measured by the lead — `identity_coverage.py` must reproduce it)

### The population — and the correction to v1

```
comic items, not excluded ............................ 118,440   (243,702 items in all; 125,262 are novels/ebooks)
file-holding comic Series (shelves) ..................  20,385   ← THE POPULATION OF THIS PASS
   v1 said 41,092: that counted the 20,707 `book:` (novel) series, which are a separate key space (§6.7)
shelf size:  1 file 11,606 · 2-5 files 5,111 · 6-20 2,703 · 21-100 853 · >100 112
```
**82% of shelves hold five files or fewer.** Most of those are one-shots, OGNs, single trades and stray
issues, and their identity is the *file's* identity as much as the run's. That is what makes the job tractable:
the reading is dense where the shelves are small and full-packet where they are large.

### What each shelf already carries (the tiers of §7 are cut from this table)

```
                                    shelves    files
CV volume set  + one GCD series      5,051    25,527    ← two legs already agree on the run
CV volume set  + NO GCD               7,621    15,944    ← one leg (mostly small shelves)
CV volume set  + SEVERAL GCD            914    22,915    ← contradiction: the shelf is several runs, or GCD is
no CV          + one GCD series      1,782    15,155    ← one leg
no CV          + SEVERAL GCD            561    24,940    ← contradiction (the big conflated shelves live here)
no CV          + no GCD              4,456    13,876    ← of which 2,101 carry a UNANIMOUS v1 per-file CV volume,
                                                             9 carry several, and 2,346 (5,595 files) have NO LEG AT ALL
```
(Phase 0 re-measured 2026-09-09 evening: the first three rows read 5,053 / 7,623 / 910 and the no-leg split above is
the corrected one — the lead's first 3,852 / 2,323 pair summed past the bucket. **83 comic items sit on no comic
shelf at all** (`SeriesId` NULL or a novel shelf's stray) and are the fifth bucket of the item partition.)
"one GCD series" = every file on the shelf that has a `Provider.Gcd` Matched link names the same
`SecondaryKey` (= `GcdSeriesId`). 8,354 shelves have at least one such link; 63,299 of them were matched by
issue NUMBER inside a GCD series v1 chose by name, so they are **not independent of the name** — the
independence comes from GCD's ISBN/barcode rows and from the two bridges below.

### Series-level identity columns and links
```
Series.CvVolumeId ....... 13,586 of 20,385 shelves   (12,813 have a CvVolume row; 773 point at a volume we never fetched)
CvVolume rows ........... 14,357;  CvIssue lists cached for 10,348 volumes (70,591 issues); 7,787 linked shelves have theirs
Series.TitleId .......... 2,489 file-holding (6%) — see Phase T
Series.MuSeriesId ....... 410;  MuSeriesLink 813;  Series.ExternalWorkId 656
SeriesKeyLink ........... Cv: 16,356 Matched · 4,047 NoMatch · 1,219 Multiple · 1,029 Skip · 150 Manual
                          External: 700 Matched · 3,074 NoMatch · 562 Multiple
GCD ..................... NO shelf-level link exists. `SeriesResolver` reads only Provider 0 (Cv) and 1 (External)
                          from SeriesKeyLink (SeriesResolver.cs ~L55-59), so Provider=3 rows are INERT to the
                          resolver and can hold the GCD link in the existing table shape without a migration.
```

### Per-file provider links (`ItemProviderLink`, 321,521 rows)
```
Cv  (0)  108,607 Matched, Method NULL (v1). ProviderKey = CV ISSUE id, SecondaryKey = CV VOLUME id.
         63,148 agree with their shelf's CvVolumeId · 1,224 disagree · 44,235 sit on shelves with NO CvVolumeId
Gcd (3)  72,323 Matched. ProviderKey = GcdIssueId, SecondaryKey = GcdSeriesId. Methods: num 63,299 ·
         fulldump-rematch 3,445 · round2-series 3,111 · round2-folder 1,453 · page-rematch 774 · barcode 88 · …
Locg(2)  83,430 Matched.   Barney(5) 2,332.
`Applied` is a v1 artefact — never gate on it.
```

### The collection's OWN assertions (independent of every provider — the strongest evidence there is)
```
ComicEmbedded.Web ....... 23,764 of 27,727 ComicInfo rows carry a URL; 5,757 mention comicvine (5,737 carry the
                          `/4000-<issueId>/` shape), on 595 shelves
BarcodeScan (LEGS db) ... 349 items with codes read off the page
CollectedEditionSpan(Source=3) — 8,511 judged ranges on 1,778 shelves; 976 read off the copyright page
ContainmentFlag ......... conflated-series 110 (108 shelves) · label-ambiguous 420 · overlap-in-series 117 ·
                          provider-disagrees 29 · duplicate-edition 26
```

### The legs file (`data/books/v2/books-legs.db`) — pre-computed and unconsumed
```
LinkCandidates(Scope, Key, Provider, CandidatesJson)
   Scope 1 = series (19,917 Cv · 1,678 External · 414 Mu), Scope 0 = item (5,805 Cv). Provider is the enum int.
   Key = ComicDetail.ParsedSeriesKey (raw spelling, e.g. `#SAD!`) — 18,527 of our 22,316 distinct keys;
   17,249 of the 20,385 shelves have candidates through at least one of their keys; 5,274 of those have no CvVolumeId.
   CandidatesJson = [{VolumeId, IssueId, VolumeName, StartYear, Publisher, CountOfIssues, Score}, …]
LocgSeriesInference(GcdSeriesId, LocgSeriesId, SeriesName, Support)      11,830 (1,285 with Support ≥ 10)
OlSeriesInference(GcdSeriesId, OlWorkKey, SeriesString, SubjectsJson, IsbnSupport)   7,250 (662 with IsbnSupport ≥ 5)
GcdSeries 17,152 · GcdIssue 76,869 (with Isbn/Barcode/PageCount) · CvVolumeRaw 14,357 (characters/teams/…)
CvVolumeDescription 119,879 · LocgContainment 625,396 · LocgCreatorRaw 1,293,131 · ProviderResponseCache 100,886
```
**GCD is the hub leg**: both bridges are keyed on `GcdSeriesId`, so a shelf→GCD link buys LOCG and Open Library
*with a support count* — the independent second opinion §4 wants — for free.

### Whole-provider reference dumps (read-only, `data/books/archive/mybooks/`)
`GrandComicsDatabase-06-01-06.db` (the full GCD dump `export_packets_v3.py` already reads),
`comicdb_comicvine_20260122.db` + `cvref.db` (the ComicVine rip). **These are what "checking our data" means
before the web**: a name/year lookup against them is a lookup whose results the reader sees — not an oracle.
The packet tool prints up to six local hits per shelf so the web is rarely needed.

---

## 3. What the containment pass gives you as EVIDENCE (use it; do not re-derive it)

**3.1 — 8,511 judged issue ranges** (`CollectedEditionSpan` where `Source = 3` and `IssueStart IS NOT NULL`).
Each says *this book collects issues #a-b of the run on this shelf* — a testable prediction about the run's
numbering. A candidate CV volume whose cached issue list contains #a-b is evidence for; one that tops out below
#a is evidence against. The 976 with confidence ≥ 0.95 were read off the book's own copyright page — ground truth.

**3.2 — 4,200 tombstones with reasons** (`Source = 3`, `IssueStart IS NULL`) and the six ways a provider lies.
Each is a shape the reader must recognise on *any* claim about *any* entity:

| shape | what it looks like | what it means |
|---|---|---|
| title-wide stamp | the same record on every run of a title; a one-issue volume on a 300-issue shelf | keyed on the TITLE, returned the first record |
| hull | `#1-578` on a 326pp book; a CV volume with `CountOfIssues` ≈ every relaunch summed | a list flattened into a span |
| wrong coordinate system | legacy numbering, `#1000000`, a chronology SORT PREFIX, a YEAR read as an issue | answering about a different numbering |
| off-by-one-volume | the neighbour's record | volume index misaligned |
| matches our holdings | the claim equals exactly what we happen to own | a coincidence — a reason to **distrust** |
| crossover / anthology | Godhead, JLU digests, War of the Realms | contents are a LIST; no run describes them |

**3.3 — 110 `conflated-series` flags.** A conflated shelf holds several runs that all number from #1. Those are
precisely the shelves where one run identity is WRONG by construction: the reader must write `F split-needed`
(or refuse) rather than an `S` line, unless the flag was resolved by a split since.

**3.4 — corrected keys.** 2,090 issue numbers were read by hand (`IssueSource = 8`, provenance in `ParseNotes`),
547 partial rips normalised (`217 (GL only)` → #217 + note), `IsCollection`/`Format` fixed both ways. Use them.

**3.5 — the invariant style.** `overclaim_check.py`, `overlap_check.py`, `audit_containment.py`,
`coverage_ledger.py`, `armed_unjudged_check.py` find defects no queue can, because each is defined over a
*population* and forced to sum. Phase 0 writes the identity equivalents before any reading starts.

---

## 4. Doctrine: how an identity earns trust — applied by the READER, enforced by the checker

1. **Under-linking is safe; wrong-linking is not.** An unlinked shelf costs a row in a report. A wrongly linked
   one silently misattributes credits, tags, a cover, a rating, and corrupts any diff built on it. When in doubt,
   refuse and record why. Refusal is a first-class outcome and is counted (§7 S, the `R` line).
2. **A single uncorroborated provider claim is not an identity.** Write an `S` line only when **two independent
   legs agree** (CV and GCD; CV and a bridge-supported LOCG/OL; GCD and MU) **or one leg agrees with something the
   collection itself asserts** (a judged range from §3.1 that fits the volume's issue list; a ComicInfo `Web` id;
   a barcode/ISBN that GCD carries; the filenames' own numbering, years and publisher folder).
3. **Corroboration must be independent.** LOCG and our LOCG-derived spans are one source. A v1 GCD link made by
   issue NUMBER inside a name-chosen series is not independent of a CV link made by the same name. The
   independence has to be visible in the evidence sentence: *what* agreed, not *how many*.
4. **Arithmetic beats assertion.** Year gap, issue-count ratio, page count, judged-range fit. A CV volume with 4
   issues is not our 50-issue run whatever the name says. The packet prints these; the reader reads them.
5. **Record the evidence with the decision, in words a person can check.** The `|` clause of every line.
6. **The folder is context, never a rule.** Neighbours and batch dirs inform a reading; nothing is "applied
   across the folder". Every shelf is judged on its own line.
7. **Trust granted by label is not trust.** A `Matched` status, a `Score` of 83, `Applied = 1`, `Quality = High`
   — all of these are v1 saying something once. They are candidates, not answers.

### Confidence vocabulary (write one of these; the checker rejects anything else)
```
1.0   the collection asserts it (ComicInfo Web id / ISBN / judged copyright-page range) AND a leg agrees
0.95  two independent legs agree and the arithmetic holds
0.9   one leg + the collection's own naming/years/count agree with nothing contradicting        ← apply floor
0.7   plausible, one leg, something unexplained → REVIEW QUEUE (SeriesMatchReview), not applied
```

---

## 5. Hard rules (non-negotiable — global rules + this project's scar tissue)

1. **The dev connection IS the live production database.** `F:\Work\MovieTheater\data\books\v2\books.db` is
   what the site serves. There is no staging copy.
2. **Back up before any write pass**: `python docs/books/containment/tools/backup_live.py` (SQLite online backup;
   ~36 s; writes `data/books/v2/backup-<stamp>/`). Last: `backup-20260909-033059`.
3. **Every bulk job is chunked, observable and resumable.** Bounded work per call, a cursor, a printed
   `{ processed, remaining, nextCursor, counts }`. The driver loop lives in the caller (§12). The reading pass
   itself is chunked by construction: one batch file in, one decision file out.
4. **Dry run by default.** Nothing writes without `--apply`. Print counts and a sample first.
5. **Never destructive without a guard.** Skip when unsure. Never delete/rename on the NAS (DB-only). Never a
   full `L:\` scan. Never `sync-jellyfin`.
6. **At most TWO parallel agents** without Eric saying otherwise in the same session.
7. **Commit by path, never `git add -A`.** ⚠ **Nothing from the containment pass is committed** (the whole
   `src/MovieTheater.Books*` diff in `git status`). This pass adds Python under `docs/books/identity/` and
   decision files only; it adds NO C# until Phase C, and Phase C waits for Eric's commit decision (§10).
8. **Do not touch the parsers.** `ComicTitleParser` / `ReadingOrderParser` are off limits — fix data, not code.
9. **Never edit `Item.SeriesId`, `Series.CvVolumeId` or `Series.Name` directly** — `Series` is derived
   (`books-resolve --series` undoes it). The inputs are `SeriesKeyLink`, `ComicDetail.ParsedSeriesKey`,
   `Series.DisplayNameOverride`. `apply_identity.py` writes only `SeriesKeyLink` + `SeriesInferenceDecision`.
10. **Present, do not resolve, an instruction-vs-code conflict.** If this plan contradicts what you find, stop and
    surface it in your report.
11. **A Stop hook exists and can repeat the same finding many times.** Answer it once with facts; do not loop on
    it. If it blocks a batch, report that and move on.

---

## 6. Gotchas that will cost you a day each

**6.1 The Bash tool halves backslashes.** Windows/UNC paths and `\n` in `sed` lose a level; a Python heredoc
with `'\M'`-style paths warns or silently misbehaves (the lead's own folder histogram returned `''` for this
reason). **Write a `.py` file with the Write tool and run it**, or use the PowerShell tool. Verify any
path-carrying job by a prefix histogram.

**6.2 `Item.Path` ends in the FILE NAME.** Folder = `os.path.dirname`. A manga volume file is named like a run
folder; search `segs[:-1]` only. All comics sit under `\\Library\Public\5 - Comics\` — strip that prefix.

**6.3 Page-count arithmetic**, with the exceptions that are not lies: digital-mobile / Infinity Comic / Webtoon
rips inflate ~5× (one panel per page); prestige issues are 48pp; an omnibus legitimately exceeds the run.

**6.4 `LinkCandidates.Key` is the RAW `ComicDetail.ParsedSeriesKey`**, not `Series.Id` and not a normalised key;
`Scope`/`Provider` are ints. Join through the shelf's parsed keys (`SeriesAlias.ParsedKey` for the shelf).

**6.5 Linking two shelves to the same CV volume MERGES them** at the next `books-resolve --series` (the
canonical key becomes `cv:<id>` for both). That is correct for a duplicated shelf and catastrophic for two
different runs. The checker refuses a batch in which two `S` lines share a `cv=` unless one carries
`F merge-with=<sid>`; across batches `audit_identity.py` reports every shared volume before a wave lands.

**6.6 Linking renames the shelf** — the name chain is `DisplayNameOverride` → `CvVolume.Name` → `ExternalWork.Title`
→ raw parsed key. For the 773 volumes we never fetched, the name stays; after Phase C's fetch it changes. Expected.

**6.7 Novels are a separate key space** (`CanonicalKey LIKE 'book:%'`, `SeriesResolver.NotBookSql`). Never in
scope here; their identity is ISBN + Calibre and already exists.

**6.8 `Provider.Mu` is MangaUpdates (4), Marvel is 6.** Enums:
`Provider Cv=0 External=1 Locg=2 Gcd=3 Mu=4 Barney=5 Marvel=6 Inducks=7` ·
`LinkStatus Pending 0 Matched 1 NoMatch 2 Multiple 3 Error 4 Manual 5 Cleared 6 Skip 7` ·
`EditionSource Locg 0 Gcd 1 Cv 2 Curated 3` · `TagSource ComicInfo 0 Cv 1 Calibre 2 Locg 3 Gcd 4 External 5 Mu 6 AI 7` ·
`ParseSource.Manual = 8`.

**6.9 A batch verb must never compute one identity per batch** (containment §14.4 filed `G.I. Joe v2` under
`30 Days of Night`). Every line names its own shelf; the apply tool takes ids from lines, never from context.

**6.10 The undo log is flushed per batch, beside the write it records** (same section). `SeriesInferenceDecision`
has `UndoJson` for exactly this.

**6.11 The containment guards in `ContainmentJob.cs`** were added deliberately. Do not simplify them. Any chain
rebuild is followed by the §8 standing checks.

---

## 7. The phases

Order: **0 → S (the reading pass, in waves) → G → C → T → B → R → P → D**. Each ends with an acceptance criterion
phrased over the **population**, never the queue.

### Phase 0 — instrument, then package (no writes to books.db)

Build `docs/books/identity/tools/` in the style of `docs/books/containment/tools/` (small scripts, read
`books.db` read-only via `file:…?mode=ro`, attach the legs db and the archive dumps, hard-coded paths at the
top, dry-run by default).

| tool | contract |
|---|---|
| `identity_coverage.py` | Regenerates every table in §2 from the live DB; prints the tier partition of §7-S forced to sum to the shelf population, and the item partition forced to sum to 118,440. Zero unaccounted or it fails. |
| `audit_identity.py` | Invariants over the population, each a count that must be 0: `CvVolumeId` on two file-holding shelves; a `CvVolumeId` THIS PASS wrote on a `conflated-series` shelf still flagged open (v1's 80 such shelves are a counted LEAD until the pass reaches them); an `ItemProviderLink` on an item that no longer exists (links on EXCLUDED items — 966 — are a lead); `SeriesTitle` orphan; `RunCount` disagreement; title whose runs disagree on franchise; a Provider=Gcd `SeriesKeyLink` whose `GcdSeriesId` is not in legs `GcdSeries`; a `SeriesInferenceDecision(Class='identity')` whose target shelf no longer exists. Plus LEADS (reported, not failures): shelves whose CV year gap > 1, count ratio outside 0.5–2, CvVolumeId with no CvVolume row. |
| `identity_packet.py <sid> …` | The per-shelf evidence packet, §7-S below. Reduction-free in the v3 sense: every filename, folded only as specified. |
| `next_batch.py --tier A\|B\|C\|D [--lines N]` (tier required; `--help` / unknown flags / a bare run emit nothing; `--out DIR` / `--dry-run` write elsewhere and leave state.json alone) | Reads `state.json`, emits `batches/<tier>-<nnn>.txt` (the packets) and `batches/<tier>-<nnn>.ids`, advances the cursor, prints `{tier, batch, shelves, remaining}`. Ordering within a tier: by the shelf's dominant folder path, so neighbours sit together. Resumable: a batch already emitted is never re-emitted; `--redo <batch>` regenerates one. |
| `check_identity.py [batch …]` | The coverage contract (§7-S grammar). Every shelf in the batch's `.ids` has exactly one `S` or `R`; every id named exists in our tables/dumps; confidence is from the §4 vocabulary; no two `S` share a `cv=`/`gcd=` without `merge-with`; an `S` on an open `conflated-series` shelf is a failure; an `S` whose evidence clause is shorter than 40 characters is a failure. Reports every file, exits non-zero on any failure — like `check_decisions.py`. |
| `apply_identity.py [batch …] [--apply]` | Writes accepted lines (conf ≥ 0.9): `SeriesKeyLink(ParsedKey, Provider=0, ProviderKey=vol, Status=5 Manual, Score=conf×100)` for **every parsed key aliased to the shelf**; `SeriesKeyLink(Provider=3, ProviderKey=gcdSeriesId, Status=5)` for `gcd=`; one `SeriesInferenceDecision(Class='identity', Action, Target, Confidence, EvidenceJson={batch,line,evidence}, UndoJson={previous rows}, DecidedBy='identity-pass', State)` per line; 0.7 lines → `SeriesMatchReview(Scope='series', Key=ParsedKey, State='review', Note=evidence)`; `R` lines → a `SeriesInferenceDecision(Action='refuse')` so refusals are counted in the DB, not only in files. `I` lines → `ItemProviderLink(Status=5, Method='identity-read', Confidence)`. Prints per-batch counts; refuses a batch that fails `check_identity.py`; flushes an undo jsonl per batch. |
| `wave_land.ps1` | §12's landing recipe as one script, each step printing and stopping on non-zero. |

> **Accept when:** `identity_coverage.py` reproduces §2 (or the diff is explained), `audit_identity.py` prints 0
> failures on today's data with its leads listed, three packets rendered by hand (one singleton, one 20-file
> shelf with a CV volume and a GCD series, one `conflated-series` shelf) are judged readable and complete by the
> lead, and `check_identity.py` rejects a hand-written bad batch for each of its rules.

### Phase S — THE READING PASS (the bulk of the job)

**Unit:** one shelf. **Population:** 20,385. **Output:** one decision file per batch,
`docs/books/identity/decisions/<tier>-<nnn>.txt`.

#### The packet (`identity_packet.py`), per shelf
```
== S<sid> <name>  [tier X]  keys: <parsed keys, aliases>  years <YearStart>-<YearEnd>  <n> files / <c> collections
   flags: <ContainmentFlag rows on this shelf with ReviewState>        (omit line if none)
   folders: [<count>] <path relative to \\Library\Public\5 - Comics\>   (all of them, most files first)
   files:   every filename with page count.  FOLD RULE: a run of ≥ 6 consecutive files whose names have the SAME
            non-digit skeleton (every digit group may vary — `Crisis 017 [1989-04-29]` / `Crisis 018 [1989-05-13]`
            fold; `Baltimore 016 - The Infernal Train 01 (of 03)` never folds because its text differs) and whose
            page counts lie within 2× of each other prints as "<first name> … <last name>  (n files, pp lo–hi)".
            First and last are verbatim, so the pattern stays visible. Collections always print in full.
   ours:    ladder of numbered issue files; judged ranges (Source=3) as "item <id> #a-b conf <c>"; ComicInfo
            Series / Volume / Count / Publisher / Web distinct values with counts; barcodes/ISBNs on the shelf
   CV linked:      vol <id> "<Name>" <StartYear> <Publisher> <CountOfIssues>; cached issues #lo–hi (n) or NOT CACHED
                   arithmetic: yearΔ <n>, count ratio <ours/theirs>, judged-range fit <k of m fit / none cached>
   CV per-file (v1): vol <id> "<Name>" <year> <pub> <count> on <k>/<n> files   (each distinct volume)
   CV candidates:  up to 6 from LinkCandidates (id, name, year, pub, count, their score) — marked "(v1 search)"
   CV local rip:   up to 6 name/year hits from comicdb_comicvine / cvref — marked "(rip lookup)"
   GCD per-file:   series <id> "<Name>" <YearBegan>-<YearEnded> <Publisher> <Format> <IssueCount> on <k>/<n> files, methods
   GCD dump:       up to 6 name/year hits from GrandComicsDatabase — marked "(dump lookup)"
   bridges:        for each GCD id above: LOCG series <id> "<name>" support <n>; OL work <key> isbnSupport <n>
   MU / External:  MuSeriesLink, ExternalWork title                                   (omit if none)
```
A singleton shelf is ~12 lines; a 300-file run with clean numbering is ~25 lines; a conflated 1,700-file
shelf is as long as it needs to be and gets a batch of its own.

#### The tiers (assigned by `idbase.py` — the ONE copy of the rule, shared by the coverage tool and the packet tool)
```
A  CV volume + unanimous GCD, yearΔ ≤ 1, count ratio 0.5–2 (tested only when the shelf holds ≥ 1 numbered
   issue file — a shelf of trades has no ratio), no open conflated-series flag
B  exactly one leg (CV only / GCD only / unanimous v1 per-file CV), arithmetic clean, no open conflated flag
C  contradiction: several GCD series (the minority on ≥ 2 files or ≥ 10% of the shelf), yearΔ > 1, ratio outside
   0.5–2, CV↔GCD disagree on the start year, an OPEN conflated-series flag, or shelf > 100 files
D  no leg at all
```
Other ContainmentFlag kinds (label-ambiguous, overlap-in-series, provider-disagrees, duplicate-edition) are
PRINTED on the packet but do not demote — they are containment facts, not identity contradictions.
First measurement (before the ratio/flag refinements above): A 3,933 · B 9,848 · C 4,277 (68,263 files — 58% of
all files) · D 2,327. `identity_coverage.py` prints the live numbers and they MUST sum to 20,385.

**Batches are sized by packet LINES, not shelf count** — lines are what a reader pays for. `next_batch.py`
targets ~1,400 packet lines per batch (`--lines`), with a per-tier shelf ceiling (A 150 · B 100 · C 60 · D 120); a
shelf whose packet alone exceeds ~300 lines gets a batch of its own. Expect roughly 350–450 batches.
Tier A is not "auto-accept": every A shelf still gets a read line. Its density is what makes it cheap.
Work A first (it calibrates the reader on shelves where the answer is plainly visible), then B, D, C — C last
because by then the reader has seen the neighbours of every hard shelf.

#### The decision grammar (`decisions/<tier>-<nnn>.txt`)
```
# free comment — say what the batch was, what surprised you, what the folder's convention turned out to be
S <sid> cv=<volumeId>|- gcd=<gcdSeriesId>|- <conf> | <evidence sentence: WHAT agreed, and what arithmetic held>
R <sid> | <why no identity can be stored — which candidates were seen and why each was rejected>
F <sid> <flag> | <detail>
I <itemId> cv=<issueId>|- gcd=<issueId>|- isbn=<isbn>|- <conf> | <evidence>     (item-level: a standalone book)
N <sid> <note worth keeping>
```
Flags: `conflated-series` (several runs number from #1 → needs `books-series-split` before identity),
`split-needed` (same, with the proposed runs in the detail), `merge-with=<sid>` (this shelf IS that shelf),
`wrong-cv-link` (the stored `CvVolumeId` is wrong; the `S` line carries the right one or `-`),
`needs-fetch cv=<id>` (identity is clear but the volume/issue list is not cached — Phase C fetches it),
`not-a-run` (an OGN / one-shot / art book / magazine — identity belongs on the `I` line),
`provider-missing` (no CV or GCD record exists anywhere for this run: name it in words for Phase D),
`misfiled` (the file's folder contradicts the file), `duplicate-shelf`, `partial-rip`, `mobile-rip`.

Rules the checker enforces: every shelf in the batch has exactly one `S` or `R`; an `S` needs at least one
non-`-` id; conf from §4's vocabulary; an `S` on an open `conflated-series` flag is a failure (write `F` + `R`);
two `S` lines sharing an id need `merge-with`; the evidence clause names the agreement.

#### The reading, per shelf (the method — containment §8 restated for identity)
1. **What is on this shelf?** Folder, filenames, page counts. A run of floppies, a shelf of trades, one OGN,
   a magazine, a strip reprint, a manga series? What coordinate does it use (issue / volume / year / none)?
2. **What does the collection itself assert?** Years and volume markers in the names, the publisher folder,
   ComicInfo `Web` ids and `Count`, judged ranges, barcodes. Write these down first; they outrank every leg.
3. **What do the legs say, and do they agree with (2) and with each other?** Year gap, count ratio, range fit.
   A CV volume whose issue list does not reach a judged range is not this run. A GCD series with a different
   publisher is not this run. A candidate that matches *exactly our holdings* is suspicious (§3.2).
4. **Where they disagree or are silent**: check the local dumps (the packet already did the obvious lookup —
   look again with a different spelling, the alternate title, the year ± 1 via `tools/lookup.py`). **The web
   is closed to reading workers**: `comics.org`, `comicvine.gamespot.com` and `leagueofcomicgeeks.com` all
   answer the fetch tool with HTTP 403 (measured 2026-09-09, both workers, 3 hosts). A shelf that genuinely
   needs an outside answer gets `F <sid> needs-web | <the exact question>` and is decided at 0.7 or refused;
   the lead works the `needs-web` population afterwards in its own lane, SERIALLY, after the fleet: Eric
   connects the Chrome extension then (no browser was connected on 2026-09-09 — `list_connected_browsers`
   returned none), text-only page reads for League of Comic Geeks and for GCD records newer than the local
   dump (2026-06-01); ComicVine questions go through the API with the BooksHost key, cache-first, no browser.
   In the first four batches (398 shelves) no shelf needed it.
   If the shelf is still open, **refuse with the candidates named** — a later pass with the book open
   (containment §10.3) can settle it; this pass does not open books.
5. **Write the line.** Every `S` clause names what agreed. Every `R` names what was seen and rejected.
6. **Flag what you found underneath**: a conflated shelf, a wrong stored link, a mis-filed file, a duplicate.

#### Rulings made on the first four batches (2026-09-09) — the reader's copy is in `READER_BRIEF.md`
- **Several runs on one shelf**: co-equal runs sharing a numbering → `R` + `F split-needed`; a dominant run
  with a minor residue (special, annual, cover packs, duplicate rips, its own trade) → `S` at ≤ 0.9 with the
  residue named. The checker's conflated test is only the OPEN flag; this ruling is the reader's, by proportion.
- **Trades vs run — the RUN wins whenever a numbered run exists** (sharpened after the Sonnet trial split
  exactly on the ambiguity: Backways one way, Echo the other). A trade is an edition of its run, so the shelf
  carries the run's records even when a collected-edition record with a matching volume count exists; the
  collected/volume series is the identity only when no issue run exists (OGN series, digest/anthology lines,
  manga). Decisions written before this sharpening (A-001…A-004) that chose a collected record while a run
  existed are re-read by the lead at the first landing: grep the `S` clauses for "collected".
- **Model trial (2026-09-09, batch A-002, 94 shelves):** Sonnet vs Opus — 76 identical, 16 differ only in
  confidence (Sonnet one step more conservative), 2 differ in ids (both the ambiguity above, one each way);
  Sonnet missed one `misfiled` flag. Time and tokens were NOT lower (474 s / 184k vs Opus ≈ 426 s / 155k per
  batch) because the cost is reading the packets, not deciding. **Verdict: Opus for every tier.**
- **`round2-folder` / `round2-series` GCD per-file links copy a folder-neighbour's series and were wrong every
  time seen** (New Years Prey → "Silent Night Deadly Night vs. Valentine Bluffs Massacre"). They are printed with
  a ⚠ and never count as a leg.
- **Reading workers read `READER_BRIEF.md` only** (≈ 3k tokens) instead of this plan's sections (≈ 15k); the
  brief carries the grammar, the rulings and a conventions ledger the lead curates from every report. Each
  worker takes three batches. Packets are compacted where a block merely repeats the stored volume (per-file
  = linked on 780 of 783 tier-A shelves), which costs no evidence because the agreement is stated in words.

#### S.2 — the second blind reading (the final tier, run after every tier's first reading)
The Sonnet/Opus trial disagreed only on shelves that were already marked as less than certain, so the
uncertain population is selectable by query, not by feel. `identity_coverage.py --second-read` lists it:
every shelf decided at 0.9 or 0.7 · every `R` · every shelf carrying any `F` · every `S` whose linked volume
has zero numbered issue files on the shelf (trade-only) · every `S` at 0.95 on a shelf with ≥ 2 parsed keys.
Expect 10–15% of shelves. Emit them as `R-` batches; a DIFFERENT worker (Sonnet is acceptable here — price,
not time, is the constraint) reads them blind, with no access to the first decision file; the lead runs
`compare_decisions.py` on each pair. Agreement stands. Disagreement gets a third reading by the lead; what is
still open after that is NOT parked — it becomes an entry on the web worklist below. Time budget: ~15% of the
first pass.

#### S.3 — the web lane, fed by S.2 (one serial pass with the browser connected)
The web lane runs ONCE, after S.2, so that everything it touches arrives with a pre-formed question. Its
worklist is built by `web_worklist.py` (tools) from three sources, deduplicated per shelf:
1. every `F needs-web` flag (the reader's exact question, verbatim);
2. every S.2 disagreement the lead could not settle by reading — `compare_decisions.py` writes these with BOTH
   sides' ids and clauses, so the entry reads "A says run CV 20806 / GCD 29889 (30 issues); B says collected
   CV 47310 / GCD 51428 (6 volumes); settle: does GCD 29889 exist as a 30-issue run collected in six trades?";
3. every shelf left at 0.7.
Each entry names: the shelf, the candidate ids on each side, the single fact that would settle it, and the
page most likely to hold it (a `comics.org/series/<id>` URL from the GCD id, a `comicvine.gamespot.com`
volume URL from the CV id, a League of Comic Geeks search for the rest). The lane then works the list top to
bottom with text-only page reads (and the ComicVine API for CV questions), writing `R-` decision lines with
`web:` citations. A shelf the web cannot settle either goes to `SeriesMatchReview` at 0.7 or is refused —
counted either way, never silently dropped. Eric connects the Chrome extension for this step only.

#### Time (measured 2026-09-09)
≈ 8 min and ~130 shelves per batch per Opus worker; two workers. Projected ≈ 290–320 batches (A ~32 · B ~70–105
· C ~160 with 109 solo batches · D ~20) → ≈ 20 h of driving plus ~8 landings of ~10 min. Tier B's ceiling should
rise to 150 (its packets are 5–8 lines).

#### Waves and landing
Decisions accumulate in files; the DB is written **once per wave** (~40 batches), by the lead, with §12's recipe:
backup → `apply_identity.py --apply` → `books-resolve --series` → `run_chain3.ps1` → the §8 checks → Saga → coverage.
Reading continues during a landing because packets are read-only… **except** that a landing can merge or rename
shelves, so `next_batch.py` re-validates each shelf id at emission and skips ids that no longer exist (they
re-enter through the coverage partition, never silently).

> **Accept when:** the shelf partition sums to 20,385 with **zero undecided**: accepted-on-two-legs · accepted-on-
> one-leg-plus-own-assertion · in-review (0.7) · refused-with-reason · flagged-for-split. Report the split by leg
> and **the count resting on two independent legs is the headline number**, not the total. The item partition
> sums to 118,440: on an accepted shelf with a numeric coordinate that the cached CV/GCD issue list contains ·
> on an accepted shelf, coordinate not in any cached list · `I`-identified standalone · on a refused shelf ·
> flagged. And `audit_identity.py` is 0 failures.

### Phase G — GCD as the hub, and the two free bridges
Mostly mechanical once S has recorded `gcd=` on shelves: `SeriesKeyLink(Provider=3)` rows are already written by
the apply. Then a chunked, dry-run-first `bridge_harvest.py`: for every accepted GCD id, take
`LocgSeriesInference` rows with `Support ≥ 10` and `OlSeriesInference` rows with `IsbnSupport ≥ 5` as **corroborated**
LOCG / Open Library links (write `SeriesKeyLink(Provider=2 / 1?)` — check with the lead which provider int LOCG
series links should use, since `External` currently means Open Library *works* via `ExternalWorkScraper`); below
the floor → `SeriesMatchReview`. Record the support count as the evidence.
> **Accept when:** every accepted-GCD shelf has its bridge rows either linked-with-support or in review, and the
> §8 checks still pass. Present to Eric whether a real `Series.GcdSeriesId` column (a migration) should replace
> the inert `SeriesKeyLink` rows — that is a code change and waits for the commit decision (§10).

### Phase C — make ComicVine pay, on ACCEPTED identity only
1. **No API needed (2026-09-10).** The full ComicVine rip is on disk (`comicdb_comicvine_20260122.db`, raw
   volume + issue records; `cvref.db` = every issue of every volume with cover/store dates). `fill_cv_from_rip.py`
   (TOOLS_TODO 13; chunked, dry-run, idempotent, never overwriting a row the site fetched later) upserts
   `CvVolume` + `CvIssue` for every accepted volume and every `needs-fetch` id. This also upgrades every
   collection's reading date to its first issue's real cover date (the reading-order job's rung (a)) and gives
   the packets issue ids for trades. The API client stays for volumes newer than the rip (2026-01-22).
2. The CV fold beside `Resolve/LegsTagFoldJob.cs` (the GCD fold is the template): characters, teams, arcs,
   locations from `CvVolumeRaw` → `ItemTag(TagSource.Cv)`; creators → `ItemCredit`; only for items whose shelf
   has an accepted `cv=` AND whose per-file link's `SecondaryKey` equals that volume. `CvVolumeDescription` is
   the synopsis source — check `SynopsisRules` in `insights-and-kids.md` before any user-visible write.
   **This is C# and waits for Eric's decision on committing the containment diff (§10.3).**
> **Accept when:** `ItemTag`/`ItemCredit` by source show Cv in the same order of magnitude as the accepted links,
> and `audit_identity.py` is still 0.

### Phase T — the Title tier
1. **Ask Eric (§10.1)** whether a single-run work gets a `SeriesTitle` row (the rule in `build_titles.py` that
   holds coverage at 6%). Do not change it unasked.
2. Fill `SeriesTitle.PublisherId` (0 of 1,448) from the runs' unanimous publisher; carry `Franchise` up
   (183 of 1,448 titles vs 6,233 Series) on unanimity only — the rule that produced 0 disagreements.
3. The 6,253 titled-but-empty Series: split residue to prune (`books-series-prune`) or real runs that lost files
   to a fold? `identity_coverage.py` names them; decide by reading a sample of 40, then the population.
> **Accept when:** PublisherId set on every title whose runs agree, 0 franchise disagreements, and the
> empty-titled population is either pruned or explained by name.

### Phase B — book-or-issue, and the partial rips (reconciled with what the last session actually did)
- The **453** 250+pp files flagged as single issues in floppy-publisher folders: ALL READ 2026-09-09, not
  flipped (flipping changes no behaviour; the diff needs the provider record). **They are `I`-line work inside
  Phase S** — when the reader meets one, it writes `I <itemId> …` with the record or `F not-a-run`.
- The **906** strip / OGN / manga files where `IsCollection = 0` is CORRECT: do not "fix".
- The **583** non-numeric issue numbers: **547 were normalised** (`ParseNotes` carries `partial rip: …`);
  **36 remain** (`Annual NN` / `Part NN`) and are Eric's call (§10.4). v1 of this plan still called all 583 a
  schema question; that is stale.
> **Accept when:** every one of the 453 has an `I` line or a flag, and the 36 have Eric's answer.

**B.2 — from "identified as a collected edition" to "its range judged" (added 2026-09-10, after Eric found the
Saga display gap and the Iron Man 2020 (2018) edge).** Identity produces `I` lines that name a collected-edition
record for books whose `ComicDetail.IsCollection` still says single issue. Such a book is a container nobody
judged (containment PLAN §14.13), and the range-nesting guard now keeps it from nesting anything — which is safe
and also means it sits in no book and holds no issue. The identity pass does NOT flip the flag or judge the
range; that is containment reading. So, after tier D and before Phase D:
1. Population, by query, not by feel: every item with an identity `I` line (or `F not-a-run` / `partial-rip`)
   whose provider record is a collected edition, whose `IsCollection = 0`; plus the 3,308 "armed but inert"
   claims `armed_unjudged_check.py` counts; plus every judged range standing on an `IsCollection = 0` item
   (the Iron Man 2020 shape — `audit_containment.py`'s "container that is not flagged as a collection" over the
   RANGE-nesting candidates, not only over live nodes).
2. Flip `IsCollection` only by reading (`apply_read_iscollection.py`, dry-run first; the 453 were read on
   2026-09-09 and their verdicts are in `iscollection-read*.csv`).
3. Judge each flipped book's range with the containment discipline (decision file `S`/`u` lines,
   `check_decisions.py`, `pass2.py`, `books-curated-spans-import`, chain, §8 checks). Refusal is a verdict.
> **Accept when:** no judged range stands on an `IsCollection = 0` item, `armed_unjudged_check.py`'s inert
> population has fallen to the books that genuinely hold nothing we own, and `audit_containment.py` is 0 with
> the range-nesting guard still in place.

**B.3 — a collected edition whose CONTENTS have no coordinate on its shelf (added 2026-09-10, after Eric
spot-checked B.P.R.D.).** S1715 "B.P.R.D." holds 25 trades and omnibuses and one issue file; S1717 / S1718 (Plague
of Frogs, Hell on Earth) are the same shape. The containment pass REFUSED every one of the 25 (`u`, decisions
S1715.txt) — correctly, because B.P.R.D. is a chain of separately-numbered minis that each start at #1, so "#1-5"
on twenty different books names nothing on that shelf. Two things close it, in order:
1. Identity (tier D, the shelves are unlinked): each trade gets its own CV/GCD collected-edition record on an
   `I` line, and the reader names in an `N` which mini(s) it collects (GCD's reprint links carry this).
2. Containment then needs a coordinate the minis do not have as shelves: a file-less seed `Series` row per mini
   (6,259 file-less rows already exist, so the shape is legal), linked to the mini's CV volume, and the trade's
   `CollectedEditionSpan.SeriesId` pointing at it. An omnibus that collects several minis needs several spans —
   the table's key is (ItemId, Source), ONE curated span per book. **Eric's ruling (2026-09-10, §10.7): record the
   omnibus against the TRADE shelf it re-collects ("collects Vol. 1-3"), no key widening.** The trades and the
   omnibus share a shelf, so the shelf's coordinate for that span is the trade volume number; when B.3 lands,
   prove the range-nesting pass reads `VolumeNo` for that shape before judging (it nests by `IssueNo` today).
> **Accept when:** every B.P.R.D.-shaped shelf (a run of collected editions over minis we do not shelve) has its
> editions identified AND either a judged span against a seed coordinate or a refusal that names the missing
> coordinate — "we have not read it" is no longer an answer.

### Phase R — the stale reading order (low severity)
4,685 `ReadingOrderEntry` rows with NULL `SeriesId` (all `ComputedAt` 2026-06-13); 134 entries pointing at
excluded items; 22 series with a duplicate `ReadIndex`. `books-reading-order` over the affected shelves, then
an orphan sweep.
> **Accept when:** every non-excluded comic with a shelf has an entry naming that shelf; no entry outlives its item.

### Phase P — publishers
105,024 items have no `PublisherId`. `LibraryScanner.BackfillPublishersAsync` is the producer; find out why it
stopped (no ComicInfo? folder publishers absent from the 2,745-row `Publisher` table?) before adding anything.
Phase S's `S` lines carry the provider's publisher, which is a second source.
> **Accept when:** every comic item has a publisher or sits in a named, counted residue.

### Phase D — the diff surface (the actual goal)
Only after S/G/C/P. Export a stable per-item identity record — CV volume + issue id, GCD series + issue id,
ISBN/barcode, title, run, issue coordinate, format, publisher, page count, content hash — and a per-run rollup
with its evidence class. The FTP-diff work (`Downloads\!New Comics\tools`, memory `comics-ftp-diff`) is the
consumer shape: **diff against their file list, never against the NAS.**
> **Accept when:** two collections can be diffed and every difference is "we lack it", "they lack it", or "same
> work, different edition" — never "unknown".

---

## 8. Standing checks — after ANY landing, treat a non-zero as a stop

```
python docs/books/containment/tools/audit_containment.py     → 0 problems (6 invariants + 1 lead)
python docs/books/containment/tools/overclaim_check.py       → 0 over-claiming containers
python docs/books/containment/tools/overlap_check.py --all   → only the 2 documented exceptions (GL v4 S94612, MMPR S98522)
python docs/books/containment/tools/check_decisions.py       → 976 files, 0 failing
python docs/books/containment/tools/audit_issue_details.py   → 0 defects, 3 understood leads
python docs/books/containment/tools/armed_unjudged_check.py  → 0 with LIVE EXPOSURE
python docs/books/containment/tools/coverage_ledger.py       → 0 unaccounted
python docs/books/identity/tools/audit_identity.py           → 0 failures            (new)
python docs/books/identity/tools/identity_coverage.py        → partitions sum        (new)
python docs/books/containment/tools/runsql.py data/books/v2/books.db docs/books/containment/tools/sql/series_report.sql ":sid=14966"
                                                              → Saga UNCHANGED (compare with tools/sql/saga_reference.txt)
```
The chain, in order — never `books-containment` without the two before it:
`books-series-split → books-resolve --series → books-curated-spans-import → books-collected-editions →
books-reading-order → books-containment`; `run_chain3.ps1` (repo root) runs the last three. The host CLI is
`MovieTheater.BooksHost.exe <verb>` (the verbs are listed in `docs/books/README.md`).

**Caveat specific to this pass:** `books-resolve --series` MERGES shelves that share a `cv:` key and RENAMES
linked shelves (§6.5, §6.6). After a landing, `identity_coverage.py` must show the shelf count fell by exactly
the number of `merge-with` flags landed, and by nothing else.

---

## 9. What already exists — do not rebuild it

| | |
|---|---|
| Verbs | `books-gcd-match` (ISBN/barcode → GCD), `books-locg-*`, `books-cv-spans`, `books-cv-descriptions-import`, `books-gcd-spans`, `books-mu-import`, `books-isbn-enrich`, `books-signatures`, `books-series-namefix` / `-override` / `-clearlink` / `-prune` / `-split` / `-split-overmatch`, `books-curation-import`, `books-parse-audit`, `books-fix-issue-numbers`, `books-reparse`, `books-resolve --series` |
| Packet ancestry | `containment/tools/export_packets_v3.py` (attaches legs + the GCD dump; the reduction-free doctrine), `render3.py`, `brief.py`, `read_shelf.py` — copy their DB-access shape |
| Checker ancestry | `containment/tools/pass2.py` + `check_decisions.py` (the coverage contract), `emit_dec.py` (transcribe, don't retype) |
| Write-by-hand ancestry | `apply_read_issue_numbers.py` (derives nothing, prints the name beside each write, refuses unknown ids) |
| Title tools | `build_titles.py`, `title_layer.py`, `title_queue.py`, `repair_title_links.py`, `restore_franchise.py` |
| Evidence | `containment/decisions/*.txt` (976), `pass2_spans.jsonl`, `pass2_flags.csv`, `pass2_notes.csv` |
| Read-a-book tools (NOT used in Phase S) | `peek_pages.py`, `contact_sheet.py`, `cover_sheet.py` |
| Backup | `backup_live.py` |
| Review sink | `SeriesMatchReview(Scope, Key, State, Note, DecidedBy, DecidedAt)` — 7 rows today; the admin Series tab's `review` view reads it |
| Decision sink | `SeriesInferenceDecision(SeriesKey, Class, Action, Target, Confidence, EvidenceJson, State, UndoJson, DecidedBy, DecidedAt)` — 2,923 rows today |

---

## 10. Ask Eric (the lead carries these; workers do not block on them)

1. **Phase T.1** — should a single-run work get a `SeriesTitle` row? Triples the table; changes what the tier means.
   **ANSWERED 2026-09-10: yes — every work gets a title row.**
2. **Scope** — novels are out of this pass (their identity is ISBN/Calibre). **CONFIRMED 2026-09-10.**
3. **The uncommitted containment diff** — commit it (by path) before Phase C adds C#, or keep Phase C on hold?
   Also the containment admin tab still needs the elevated `deploy-books-host.ps1`. **ANSWERED 2026-09-10: commit
   and push (done by path the same day); the host deploy stays with Eric.**
4. **The 36** `Annual NN` / `Part NN` issue numbers — numeric with a note, or leave as text?
   **ANSWERED 2026-09-10: numeric PLUS a label** (`Annual`, `Part`, …) kept as its own field, not a free note — the
   label is what lets the reading order place an annual between the issues it belongs with (Phase R picks it up).
5. **GCD link storage** — inert `SeriesKeyLink(Provider=3)` rows now (no migration) vs a `Series.GcdSeriesId` column later.
   **ANSWERED 2026-09-10: whichever is most reasonable, but an identified book must NEVER lose its GCD link or
   metadata** — so the inert rows stand through the pass, Phase G promotes them to a first-class column, and the
   Phase D export carries every GCD id regardless of where it is stored.
6. **Re-scraping** — Phase C.1 fetches only the volumes S names; a broader CV/LOCG refresh is a separate cost.
   **ANSWERED 2026-09-10: not now — reconsider only for what the web lane (S.3) still cannot identify.**
7. **One curated span per book** — `CollectedEditionSpan` is keyed (ItemId, Source), so an omnibus that collects
   several separately-numbered minis (B.P.R.D. Omnibus, Phase B.3) cannot record them all. **ANSWERED 2026-09-10:
   record it against the trade shelf it re-collects (Eric: "the more flexible solution"); no key widening.**

---

## 11. How a reading worker session runs (the contract the lead's prompt will restate)

1. Read §0, §3.2, §4, §6, §7-S of this plan and the two decision files named in §0. Nothing else first.
2. Take the batch files the lead names (`batches/<tier>-<nnn>.txt` + `.ids`). Do not emit batches yourself.
3. Read every packet. For each shelf write exactly one `S` or `R` line, plus any `F`/`I`/`N`. Keep the
   `|` clause specific: the ids and the arithmetic, not "matches well".
4. Web: ≤ 15 fetches per batch, only after the local dumps, each cited in the clause.
5. Run `python docs/books/identity/tools/check_identity.py <batch>`; fix until it prints 0 failing.
6. Report back in ≤ 25 lines: per batch `{shelves, S, R, F by flag, I, web fetches}`, the folder conventions
   you learned (one line each), anything that looks like a systemic defect (a wrong-cv-link cluster, a
   publisher folder that is all conflated), and any instruction-vs-code conflict — presented, not resolved.
7. Never write to `books.db`. Never run a verb. Never open a book archive. Never touch another batch's file.
8. If the Stop hook repeats a finding, answer it once with the counts and end the turn.

## 12. The driver (the lead)

`docs/books/identity/state.json`: `{ cursors: {A,B,C,D}, emitted: [...], checked: [...], landed: [...],
waves: [{stamp, backup, batches, counts}] }` — written by `next_batch.py`, `check_identity.py`, `apply_identity.py`.

Loop, two workers at a time, never more: emit 2–4 batches per worker → spawn → on return, run the checker
myself, read the report, spot-read one packet + its lines → mark checked. Every ~40 checked batches, land a wave:

```
python docs/books/containment/tools/backup_live.py
python docs/books/identity/tools/apply_identity.py <batches> --apply      (undo jsonl flushed per batch)
& src/MovieTheater.BooksHost/bin/Debug/net10.0/MovieTheater.BooksHost.exe books-resolve --series --db data/books/v2/books.db
                                                                           (the Debug build; run_chain3.ps1 shows the --db/--legs shape)
pwsh run_chain3.ps1                                                        (repo root)
<the §8 block>                                                             any non-zero → stop, restore, report
python docs/books/identity/tools/identity_coverage.py                      shelf count moved only by merge-with
```
Progress is reported as the coverage partition, never as "batches done". No-progress safety: a tier whose
cursor has not advanced after two worker rounds stops the loop and gets reported.

**S.2 order (2026-09-22, after wave 12; Eric: "defer the 0.9s").** The 0.9s are NOT re-read blind. First the
shelves that are open by construction: every 0.7, every `R`, every `F split-needed`, and the containment gap
(a shelf whose open ContainmentFlag or unjudged span blocks its books). Only then the 0.9s, and only those
`tools/triage_09.py` lists with a signal — (a) its CV volume / GCD series claimed by another live shelf's S or
by an I/C line on another shelf's book (declared merge-with partners excluded), (b) a per-file CV majority
naming another volume, (c) a judged range / own-run `C` / GCD "Collects" note that does not fit the S run,
(d) an open ContainmentFlag, (e) 21+ files, (f) touched by a merge or a split after its wave landed. Measured
2026-09-22: 1,451 of 6,327 0.9 shelves carry a signal; the other 4,876 stay at 0.9 and are DONE.
`python tools/triage_09.py --all --out triage.tsv` (chunked, read-only) → `python tools/next_batch.py
--s2-file triage.tsv` emits them as `R-NNN` revisits (60 shelves / ~1,400 lines a batch, resumable via
state.json `s2`; named `R-` because only an `R-` file supersedes the earlier decision). Every batch file now
opens with its `## Conventions for this batch` block (`tools/ledger.py`, LEDGER.md) — the brief carries the
contract only.
