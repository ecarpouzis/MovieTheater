# Reader brief — everything a reading worker needs, and nothing else

You are reading shelves of a comics library and deciding, per shelf, which ComicVine volume and which GCD
series the shelf IS. Every shelf gets a reading. **No rule, regex or score decides anything** — the tools
assemble evidence; you read it and write a line a person can check. The library owner's words: *"figured out
by just reading the filenames and filepaths and containment details for each group of files, checking our
data until you're fully confident."*

## The packet (one per shelf)
```
== S<sid> <name>  [tier]  keys: <parsed keys>  years a-b  n files / c collections     ← the shelf
   flags:   ContainmentFlag rows (only if any)      folders: [count] <path under 5 - Comics\>
   files:   every filename with page count; runs of ≥6 same-skeleton names fold to "first … last (n, pp lo–hi)"
   ours:    ladder of numbered issue files; judged ranges (from the containment pass, read off the books);
            ComicInfo Series/Volume/Count/Publisher/Web; barcodes/ISBNs
   GCD says: per trade with a GCD issue id, GCD's own "Collects X #a-b" note and its gcd_reprint roll-up — the
            C range in the run's own numbering; `⚠ RANGE CONTRADICTED` marks a judged range the note disagrees
            with (count = a different number of issues; offset = same count, other numbers). Read, never obey.
   CV linked:   the STORED volume: id "name" year publisher count; cached issues #lo-hi (n); yearΔ; ratio; range fit;
                "v1 per-file: same volume on k/n" — or a separate "CV per-file (v1)" block when files disagree
   CV candidates / CV local rip / GCD dump: name lookups — ids already shown are marked (= linked)
   GCD per-file: the GCD series v1 linked our files to, with METHODS. `round2-folder` / `round2-series` ⚠ copy a
                 folder-neighbour's series and were WRONG every time in the first four batches. `num` = matched by
                 issue number inside a name-chosen series (not independent of the name).
   bridges:  LOCG / Open Library series inferred from the GCD id with a support count (Phase G uses these; one bad
             one seen: gcd 119253 → "The Revisionist" support 4 — a support count is not a name check)
```

## What counts as evidence (in this order)
1. **The files themselves**: folder, filenames (title, year, `(of NN)` = solicited length — it beat both providers'
   counts repeatedly), page counts (a 160pp file is a trade; 3–4pp files are cover packs), the ladder.
2. **The collection's own assertions**: ComicInfo Web/Count/Publisher, barcodes/ISBNs, judged ranges.
3. **Two legs agreeing with each other AND with 1–2**: CV volume and GCD series with the same publisher, start
   year and count, whose cached issue span reaches our ladder.
Arithmetic beats assertion: a 4-issue volume is not a 50-issue run whatever its name. A candidate that matches
*exactly what we happen to own* is a reason to distrust, not accept.

## How a provider lies (each is a veto on that claim)
| shape | looks like |
|---|---|
| title-wide stamp | the same record on every run of a title; a 1-issue record on a long shelf |
| hull | a count ≈ every relaunch summed; a span that swallows the shelf |
| wrong coordinate | a YEAR, a volume ordinal, a chronology prefix, `#1000000` read as an issue number |
| off-by-one | the neighbour's record (sequel / previous volume) |
| matches our holdings | the claim equals exactly the files we own |
| collected-edition record | a same-title record one year later with count 1 — almost always the trade, not a run |

## The lines you write (`decisions/<batch>.txt`; exactly one S or R per shelf id in the `.ids` file)
```
# comment — what the batch was, the conventions of its folders, what surprised you
S <sid> cv=<volumeId>|- gcd=<gcdSeriesId>|- <conf> | WHAT agreed (which two independent things, or which leg + which of our own assertions) and which arithmetic held
R <sid> | the candidates seen and why each was rejected
F <sid> <flag> | detail        flags: split-needed · wrong-cv-link · needs-fetch cv=<id> · misfiled · not-a-run ·
                               provider-missing · merge-with=<sid> · duplicate-shelf · partial-rip · mobile-rip · needs-web
I <itemId> cv=<issueId>|- gcd=<issueId>|- isbn=<isbn>|- <conf> | evidence     (a BOOK's own record — see the rule below)
C <itemId> cv=<volumeId>|- gcd=<seriesId>|- [mu=<id>] [barney=<key>] [inducks=<code>] [marvel=<id>] #<a>-<b> <conf> | evidence   (what the book COLLECTS)
N <sid> note worth keeping
```
**The `C` line (Eric, 2026-09-16 — "the correct data must be persisted").** `I` is the book's own record; `C` is its
CONTENT: "this book collects issues a-b of THAT run", the run named on any leg that has one (ComicVine volume, GCD
series, MangaUpdates series, Barney, Inducks series code, Marvel). It lands on the book's judged range and tells
the containment engine which run the numbers count in, so `#1-5` of one mini never nests inside `#1-4` of another.
REQUIRED on every collected edition whose range counts in a run OTHER than the shelf's own S identity — a trade
line or omnibus line whose books each collect a different mini (Hellboy Vol. 02 → `C 15089 cv=<Wake the Devil
volume> gcd=<its series> #1-5 0.95 | …`), an archive line collecting a Golden-Age original, a crossover
collection. Optional when the run IS the shelf's identity (the range already exists). At least one id; `a ≤ b`
(`.5` allowed); evidence ≥ 40 chars naming the run's name, year and count and why this book is
that range (`lookup.py --issues <volume>` and the packet's judged range are the sources). Ids are checked against
the catalogs like every other id — never invent one; a run you cannot name gets no `C` and an `N` saying so.

**Several `C` lines per book (2026-09-16, TOOLS_TODO 17).** One `C` states ONE range of ONE run, so a book that
collects TWO runs gets TWO lines — a trade of two minis (Hellboy Vol. 06/12, Hell on Earth Vol. 02/04/05/07,
Baltimore Vol. 03-05, Lobster Johnson Vol. 03/05/06, Abe Sapien Vol. 02) and every omnibus / Library Edition.
Each line carries the range IN THAT RUN'S OWN NUMBERING, which is also how one run named on two legs that count
it differently is stated: `Return of the Master` is CV 51622 #1-5 and GCD 71228 #103-107, so it is
`C <item> cv=51622 #1-5 …` and `C <item> gcd=71228 #103-107 …`, not one line with both ids and one range. Two
legs that DO agree on the numbering still belong on one line (`cv=… gcd=… #1-5`). What is refused is the same
(leg, run) twice — that is two answers to one question. The book's own span takes the FIRST line's range.

**An `X-NNN` batch is an ITEM batch (2026-09-16, TOOLS_TODO 16), and its `.ids` are BOOKS, not shelves.** Each
block is a shelf whose identity is already DECIDED — its `S` line is restated at the top with the volume /
series it was linked to, and the shelf's own `N` notes come with it, because on a chain-of-minis shelf that is
where the reader who decided it wrote down which mini is which. You do not re-decide the shelf: an `X-` file
carries ONLY `I`, `C` and `N` lines, and an `S` or `R` in one is a failure. What you answer is the BOOKS listed
under `books:` — for each, its own record (`I`) and, where it collects a run that is not the shelf's own
identity, what it collects (`C`). The packet gives you the pool those answers come out of: the linked volume's
ISSUE ids, the GCD series' issue rows with page counts and ISBNs (our rips match within ±10pp, and that is the
strongest per-book check there is), the judged ranges and which of them already name a run, the ComicInfo
assertions and the barcodes. **Coverage: every item id in the `.ids` has an `I` line, or an explicit
`N <itemId> no-record | why` saying what was looked for and where it was not found (≥ 40 characters).** A `C`
line is not coverage — it says what a book collects, not what it is.

Confidence — exactly one of: **1.0** the collection asserts it (Web id / ISBN / copyright-page range) and a leg
agrees · **0.95** two independent legs agree and the arithmetic holds · **0.9** one leg plus our own naming /
years / count agree, or two legs with one unexplained residue (a special, a #4 beyond both counts) · **0.7** plausible
but a real doubt → review queue, not applied. An `S` needs at least one id; the `|` clause must name the agreement
(≥ 40 characters, specific ids and numbers — never "matches well").

Three real lines, for the voice:
```
S 4358 cv=28047 gcd=49742 0.95 | CV 28047 (Fleetway 1988, 63) and GCD 49742 (Fleetway 1988-1991, 63) agree on publisher, year and count, our ladder 1-63 sits inside the cached #1-63, ComicInfo says Fleetway on all 63 files
R 16028 | this shelf is two runs that both number #1-4: "Spencer & Locke 001-004 (2017)" and "Spencer & Locke 2 001-004 (2019)" in sibling subfolders, which is why all four numbers name two files and the ratio is 8/4; the stored CV 100666 / GCD 113435 describe only the first run and the sequel is CV 118614 — applying either hands the sequel the first run's identity
F 16797 misfiled | items 82880-82882 ("11 Starlord 1.cbr" … 36-37pp) sit under Marvel\Guardians of the Galaxy\Star-Lord and are Marvel's Star-Lord, not IPC's Starlord — they are why three numbers here name more than one file
```

## Rulings (decided by the lead from the first four batches — apply them)
- **Books get their own line.** An `S` line identifies the RUN; a numbered issue file's identity then follows from
  the run and its number. A collected edition, an OGN, a one-shot or any standalone book does NOT follow from the
  run — so whenever you identify such a book's own record (the GCD issue of the trade, e.g. "GCD's 2018 one-issue
  127388 is that trade's own record"; the CV volume of the TPB with its issue id; an ISBN or barcode the packet
  shows), write the `I` line for that item, not only the sentence. `I <itemId> cv=<CV issue id>|- gcd=<GCD issue id
  or, if only the series is known, gcd=s<GCD series id>> isbn=<isbn>|- <conf> | evidence`. Ids must be ITEM-level
  (an issue, not a volume) except the `s<series>` form. If the book's record is genuinely unknown, say so in the
  `S`/`R` clause ("the trade's own record is not in our data") so the gap is counted. On a shelf of trades this is
  one `I` line per trade; on a singleton shelf it is the shelf's real identity.
- **Several runs on one shelf.** Co-equal runs sharing a numbering (a sequel in a `v2 - <Subtitle>` or `02 …`
  subfolder, three minis each from #1) → `R` + `F split-needed | proposed runs with their records`. A DOMINANT run
  with a minor residue (a special, an annual, cover packs, duplicate rips `36p`/`45p`/`2nd printing`, the run's own
  trade) → `S` at ≤ 0.9 with the residue named in the clause or an `F`/`N`. Never let a sequel's parsed key inherit
  the first run's volume. **Precision (2026-09-09):** residue that does not touch what the run IS — duplicate rips
  of the same issue (`36p`/`45p`/`2nd printing`), 1pp variant-cover packs, a book filed twice under an event folder
  and its own title, **and the run's OWN trade sitting beside its issues** (one comic, two editions) — costs
  NOTHING: 0.95 stands when two legs agree; the trade still gets its `I` line. Residue that is a different comic — a special,
  an annual, a sequel's issues, a one-shot — caps the line at 0.9 and is named.
- **Trades vs run — the RUN wins whenever a numbered run exists.** A shelf is a publishing run, and a trade on it is
  an edition of that run. If our trades collect a numbered run (judged ranges like `#1-3 + #4-7`, a `(of 5)` in a
  name, a 124pp trade of a 5-issue mini, the per-file link naming the run) → the `S` line carries the RUN's CV
  volume and GCD series, even when a collected-edition record also exists and its count equals our volumes
  (Echo: six trades collecting #1-30 → CV 20806 / GCD 29889, the 30-issue run, NOT the 6-volume "Collected
  Edition" record). Only when the work was published as volumes and no issue run exists (an OGN series, a digest
  or anthology line, a manga) is the collected/volume series the identity. A stored link that points at the
  TPB record while a run exists → `F wrong-cv-link`. The trade's own container record ALWAYS goes on an `I` line
  (`I <itemId> cv=<tpb volume's issue id> gcd=<tpb issue id>`) — the `I` line is REQUIRED on every collected
  edition (superseding an older "optional" here, which four readers misread); what is optional is the `C` line
  when the book collects the shelf's own run. Say which case applies in the clause.
- **Foreign files folded onto a shelf** (an IDW one-shot under an AC run, a Dark Horse book under Strangers in
  Paradise): `F misfiled` naming the items, and judge the shelf on the majority run.
- **A stored CV link that is the parent run, the TPB record, or another publisher entirely** → `F wrong-cv-link`
  and put the right id on the `S` line (or `-` if none is known).
- **A right id with no CvVolume row** ("named from the rip") → the `S` line still carries it, plus `F needs-fetch cv=<id>`.
- **The web is closed** (comics.org, comicvine.gamespot.com, leagueofcomicgeeks.com all 403 the fetch tool). Do not
  try. If a shelf genuinely needs an outside answer, write `F <sid> needs-web | the exact question` and decide the
  shelf at 0.7 or `R`. Local lookups with another spelling: `python lookup.py "<name>" [--year YYYY] [--contains]`
  from `docs\books\identity\tools` (a few seconds to load) — and `python lookup.py --batch <file>` answers a file
  of such queries (one per line, same shape, plus `--issues <vol>` / `--collects <gcd issue>`) in ONE call: put
  every spelling you mean to try in the file rather than paying the load once per spelling.

**Promoted from the conventions ledger (2026-09-22) — these hold on every shelf, so they live here, verbatim:**
- `<Publisher>\<Title> (<year>)\<Title> NNN (<year>) (digital) (<ripper>).cbz` is the house shape; the name carries
  title, start year and ladder.
- LOCG bridges named a DIFFERENT comic in six cases so far ("'68", Firebreak, Moon Knight Saga, Penny Dora,
  Quarry's War, Invasion) — a support count is no name check; never cite a bridge as a leg.
- A provider count ABOVE our holdings on a series whose end year is `?` is staleness, not residue (0.95 stands);
  a count BELOW our ladder, or a count-1 stub, is a real doubt (0.9).
- **A ladder that runs past the linked volume's COUNT is the cheapest mis-link test** ("Jughead v2 (1987)" linked
  to a 45-issue volume with a ladder 150-211 = Archie's Pal Jughead Comics, CV 20115 / GCD 13247).
- Where the arithmetic refuses the run, say so and override: a 201pp digest is not a 4-issue mini; a 551pp
  vertical rip is the 8-chapter Infinite Comic, not the 4-issue print mini.
- cvref's `normName` DROPS "of" (and the leading article) but keeps from/with/in: an exact `lookup.py` probe containing
  "of" ("Sea of Thieves", "Books of Magic", "Year of Valiant") returns 0 CV hits on volumes plainly there — drop the "of".
- RESIDUE, defined (lead ruling after Secret Six S15276): a duplicate rip, an annual, a one-shot, a single misfiled
  book, or ONE trade of a run that lives on another shelf. Two or more whole trades of a DIFFERENT run with its own
  record are a co-equal run — `R` + split-needed even at 6:2 — and the shelf's overlap flag is NOT stale.
- `I` lines are never optional: a batch written without them lands no item links — the lead sent C-019..C-021 back
  for an I-line repair pass.
- The packet's "GCD dump: NNNNN … [] count" numbers are SERIES ids, never issue ids, even at count 1 — an `I` line's
  gcd= wants the issue row (query gcd_issue); a Sonnet reader reused dump ids raw twice in C-022.
- `gcd=s<series>` is valid ONLY on `I` lines; on an `S` line it fails the checker's numeric gate.
- Packet phrase "vol X … 1 issue Y": X is the VOLUME id, Y the ISSUE id — an `I` line wants Y (`lookup.py --issues X`
  lists them); three mix-ups in one batch.
- ITEM PASS (X-010..X-012): confidence is exactly one of 1.0 / 0.95 / 0.9 / 0.7 on `I` and `C` lines too — no
  0.75/0.8/0.85/0.97; the checker rejects anything else (130 failures in one round-trip).
- C-028..C-030: self-check before every S — "does my S line's id equal the id I just called wrong on the same
  shelf?" (three S lines carried their own `F wrong-cv-link` id; `--all` caught them as merge collisions). IDW-Hasbro
  "Chronology" bucket folders (G.I. Joe, TMNT) are read-order trees of fragments of several runs → split-needed.
  Same-bare-title-different-decade clusters (Powers v1-v5, Cyber Force) take one era's stamp across all — years and
  publisher decide. Image trade-only shelves carry the three-record trap (run + GCD "Collected Series" + a CV
  count-1 volume named after a subtitle) throughout. A SHELF batch with 0 `I` lines is incomplete and is sent back.

## Conventions for your batch (context for reading — never a rule applied across a folder)
The conventions ledger (328 entries learned batch by batch: which publisher mints a count-1 trade record, which
folder shape files a book twice, which spelling hides a GCD row) is `LEDGER.md`, every entry TAGGED by publisher /
folder shape / kind / tier / packet signal. **Do not read it whole.** Your batch file opens with a
`## Conventions for this batch` block: the entries whose tags match the shelves in THAT batch, verbatim, with their
`[L-NNN]` ids. Read that block after this brief and before the first packet. If a shelf reminds you of a convention
the block does not carry, `grep` LEDGER.md for the publisher or the folder word — never load the file.

## Report back (≤ 25 lines)
Per batch `{shelves, S by confidence, R, F by flag, I}`; conventions learned (one line each, naming the publisher
or folder shape — the lead adds them to LEDGER.md with tags); systemic findings (wrong-link clusters, a packet block
that misled, a shelf tiered wrong); any instruction-vs-code conflict presented, not resolved. If a Stop hook repeats a finding, answer once and end.
Never write to books.db, never run a `books-*` verb or the host exe, never open a book archive. Use the Write tool
for the decision file (bash mangles backslashes). Run `python docs\books\identity\tools\check_identity.py <batch>`
until it prints 0 failing.
