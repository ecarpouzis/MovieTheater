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
  from `docs\books\identity\tools` (a few seconds to load).

## Conventions ledger (context for reading — never a rule applied across a folder)
- `<Publisher>\<Title> (<year>)\<Title> NNN (<year>) (digital) (<ripper>).cbz` is the house shape; the name carries
  title, start year and ladder.
- AC Comics: folder `<Title> (001-0NN)(YYYY-YYYY)`; GCD's `YearBegan-YearEnded` reproduces those years; revivals a
  decade later are the same GCD series (`1989-2000`); ratio > 1 is duplicate rips.
- Aftershock / Action Lab / Ablaze: 4–5 issue minis; sequels in `v2 - <Subtitle>` subfolders → two runs;
  Ablaze's Cimmerian line numbers its subfolders in READING order (`05 …`, `06 …`), not issue order.
- Archie: bucket folders (`_Archie One-shots`, `_Betty & Veronica`) name no run; look at the files.
- Avatar: `\covers` variant packs (3–4pp) double the file count and collide every number without making two runs.
- 2000AD: progs, Fleetway reprints and IDW runs can all number from #1 on one shelf.
- Crossovers published as separate one-shots (Fear on Four Worlds) are separate GCD records per title.
- Boom! trade shelves: both legs carry TWO same-name records — the run and a collected series whose count equals
  the BOOKS we hold; the stored link is the trade record about half the time (`F wrong-cv-link`, S line = the run).
  Judged ranges decide (#1-4 + #5-8 + #9-12 = 12 = the run). Exception: a line whose trades are joined by an OGN in
  the same numbered sequence (Goldie Vance vol 5, Backstagers Encore) IS the collected series.
- Cinebook: one ~50pp album per file; both providers hold Dargaud / Splitter / Epsilon / Panini rows of the same
  album — the PUBLISHER column is the only test. GCD's span = the file years.
- DC Black Label 2024-25: 1pp variant covers in `\Variant Covers` double the file count; not a second run.
- DC event folders: the leading number is a READING-ORDER position, and the same book is filed again under its own
  title folder — n issues × 2 files is one run.
- Comixology Originals (Best Jackett): absent from GCD; GCD holds only Dark Horse's later print repackaging at a
  different count — `gcd=-` or the reprint with the clause saying so.
- GCD spells `&` where CV writes `and` (Flashpoint: Deathstroke, Frankenstein) — a dump lookup that misses may hit
  with the other spelling.
- `round2-folder` / `round2-series` links: wrong in 23 of 23 checked; four would have MERGED two shelves in one
  batch (Rose and Thorn ← Eternity; Cybernetic Summer ← Dog Days of Summer). Where the dump does not name the
  right per-title series, write `gcd=-` + `F provider-missing` rather than take the hull.
- DC `#DC Events` read-order folders hold a SECOND rip of issues that also live on the title's own shelf — the
  source of nearly every DC ratio 2.00; residue, not a second run. Event umbrellas (GCD "Convergence", 9) get
  handed to two-issue tie-in shelves — the hull shape.
- Glorith rips' ComicInfo `Web` ids are NOT GCD issue ids in our dump (29 checked: missing or unrelated). Never
  claim 1.0 from one. A ComicInfo `Volume` holding a 6-digit number IS a ComicVine volume id (Lex and the City =
  162184) — a genuine 1.0 assertion.
- Digital-first DC titles carry two CV volumes (chapters vs print issues); a trade's page count picks which.
- Stored links that are the PARENT ongoing (Harley Quinn Road Trip → the ongoing), a different publisher
  (Justice → Panini; Inferno → Games Workshop), or an empty stub (Arkham Horror) → `F wrong-cv-link`.
- Digital chapters vs print issues: CV indexes both as two volumes (Injustice 2: 72 chapters vs 36 issues); GCD
  usually only the print one. Where our files are chapters, `gcd=-` + `F provider-missing` beats a count that lies.
- Reprint LIBRARY / omnibus lines over a run split across publishers (Fear Agent Final Edition, Kabuki Library,
  Madman Library) have no single run record → the collected line at 0.9 with an `N` naming the underlying run.
- The Disney folder is publisher-agnostic (Joe Books, Dark Horse, IDW, Dynamite, Fantagraphics, Yen Press on one
  shelf) — the provider's publisher column is the only test. DSTLRY filenames' "(N covers)" is a cover count.
- One-shot "Edition" books (Absolute Wonder Woman Noir / Outlaw Edition) can carry each other's stored link —
  check the file's own title against the record's.
- 23 stored CV links in three batches pointed at the TPB record (count = the books we hold) instead of the run
  — heavy on trade-only Dark Horse / Dynamite shelves. Expect it; `F wrong-cv-link`, `S` = the run.
- Europe Comics (digital English imprint of Dargaud / Dupuis / Le Lombard): ComicVine has a volume per shelf; GCD has
  NO Europe Comics rows. **Eric's ruling: the original-language GCD row is the right comic in the wrong language —
  link it** (at 0.9, naming the language edition) rather than `gcd=-`; when the dump holds two language editions,
  take the original publisher's.
- Dynamite and IDW mint a same-title count-1 record a year after every mini — the trade; on trade-only shelves
  the stored link is that record a third to a half of the time. IDW LIBRARY / ARCHIVE lines follow the reprint-
  library rule. Red Sonja's `_Trades, minis and one-shots` is a bucket folder, not a run.
- Image trade-only shelves carry THREE records for one comic: the RUN (CV volume + a GCD dump row), a GCD
  "Collected Series" row whose count equals the BOOKS we hold, and a CV count-1 volume named after the trade's
  subtitle. The per-file GCD leg and the stored CV link both tend to be the COLLECTED record — and their
  agreement is NOT two legs, because both merely matched our holdings. The dump line names the run: read it.
  Genuinely volume-published lines (Kill Six Billion Demons, Popgun, Norroway, Blood Stain, Swing) have no run
  — the collected series IS the identity at 0.95. A later volume collecting a SEQUEL series → 0.9, residue named,
  and the book still gets its `I` line on the collected series that covers it.
- Foreign-language stored links are a cluster (Berserk → Glénat's French volume while our 41 books are Dark
  Horse's; The Marvels → Panini España; Avengers → Panini Verlag): the English pair is in the rip/dump every time.
- Marvel `\Variant Covers` 1pp packs (always of #1) are the whole of every ratio 1.2–2.0 on modern Marvel shelves.
- On trade-only Marvel / Image / Dark Horse shelves the "CV linked" block is the block that misleads (it is the
  collected record); `lookup.py` by the run's name finds the run when the packet names only the collected record.
- One folder split into two shelves by a subtitle in one filename (Analog, Witch Doctor) → `F merge-with=<sid>`.
- A trade of a run that lives on ANOTHER shelf (a Batman Vol. 2 trade whose run is the Batman 2016 shelf):
  `S` at the run's ids + `F merge-with=<that shelf's sid>` ONLY when that shelf is unambiguous (one title, one
  numbering) and the trade's range falls inside its ladder. When the run is split across legacy-numbered shelves
  (Amazing Spider-Man), keep a multi-volume collected LINE both legs carry at 0.9 + `N` naming the run, and a
  lone count-1 TPB record at 0.7 + `N`. Never link a trade shelf to an ongoing it would merge five shelves into.
- A Marika-Empire rip's ComicInfo `Web` id IS a ComicVine ISSUE id (21/21 resolve to the stored volume) — the
  1.0 shape. The ripper decides: Glorith's Web ids are not GCD issue ids. Check one against the rip before
  claiming 1.0 on a new ripper.
- Krakoa-era Marvel folders are cut into several shelves by the CREATOR name in trade filenames (New Mutants v4
  → Hickman / Brisson / Vita Ayala; "X-Men by Gerry Duggan"): trades of ONE run in ONE folder → the run is the
  identity, siblings take `F merge-with`; `lookup.py` by the run's plain name supplies ids the packet lacks. A
  stored link carried across by the creator name alone (X-Men by Duggan → "Marauders by Duggan") is `wrong-cv-link`.
- `_X-Men Complete Chronology` / `_Marvel Major Event Chronology` folders re-file the same books a second and
  third time — every ratio 2.00 there, never a second run. Crossover blocks get handed one member's series by
  `round2-folder` (four Age of Apocalypse shelves → "X-Man", a 76-issue hull).
- Marvel Digital Comics Unlimited exclusives (8–12pp `(MDCE)` webrips, Infinite Comics 56–104pp): ComicVine
  indexes them, GCD does not → `gcd=-` + `F provider-missing`; the page count is the identifier.
- A shelf that is ONLY 1pp variant covers (Skottie Young) still identifies its comic → run ids + `F partial-rip`.
- LOCG bridges named a DIFFERENT comic in six cases so far ("'68", Firebreak, Moon Knight Saga, Penny Dora,
  Quarry's War, Invasion) — a support count is no name check; never cite a bridge as a leg.
- A provider count ABOVE our holdings on a series whose end year is `?` is staleness, not residue (0.95 stands);
  a count BELOW our ladder, or a count-1 stub, is a real doubt (0.9).
- A run that changed publisher mid-stream is TWO GCD series while ComicVine keeps one volume (DV8: Image
  #1-25 + DC for the rest) — take the majority GCD leg and name the split.
- NBM / Papercutz album lines and Scout one-shots carry TWIN adjacent GCD rows for one book (120209/120210) —
  cap at 0.9 and name both. Titan's Doctor Who "Year N" volumes number continuously across a Doctor (a ladder
  starting at #6 is complete). A publisher's read-order crossover folder (Titan `Lost Dimension`, Valiant
  `_Chronological Valiant`) = DC's `#DC Events` shape.
- The GCD per-file leg can be the collected record even on a shelf holding NO trade (Robotech Remix).
- **A ladder that runs past the linked volume's COUNT is the cheapest mis-link test** ("Jughead v2 (1987)" linked
  to a 45-issue volume with a ladder 150-211 = Archie's Pal Jughead Comics, CV 20115 / GCD 13247).
- "The dump names no other series" often means the packet probed the wrong SPELLING: re-probe `lookup.py` with the
  CV record's own title (found a GCD series on 27 of 450 tier-B shelves).
- Archie files annual holiday one-shots as a NEW series every year in both providers — a shelf holding four years
  is four comics with no spanning record (`R` + `split-needed`); the year in the FILENAME is the selector.
- Rebellion's Judge Dredd TPB line: one record per BOOK in both providers — a one-file shelf with CV count 1 and a
  same-year GCD count-1 row is normal indexing, not the "matches our holdings" trap.
- Small-press American floppies (Action Lab, Aftershock minis, Behemoth) are thin in GCD → `provider-missing` on
  the GCD side is common and honest.
- BOOM! trade shelves = the Image three-record trap: run + same-title count-1 record a year later in BOTH
  providers = the trade; the stored CV link IS that trade on 60 of 450 shelves. Jim Henson's Storyteller: `"The
  Storyteller: X"` count 4 = the RUN, `"Jim Henson's The Storyteller: X"` count 1 = the trade, all eight titles.
- **ComiXology Originals is a provider SPLIT, not a missing leg**: ComicVine indexes the digital serial under the
  creator's imprint (count 4–6); GCD holds only Dark Horse's print repackaging (count 1–3, years later). Pair them
  at 0.9 with the clause saying so.
- BOOM's licensed-kids folders (Ben 10, Ice Age, Garfield, BOOM! Box Mix Tape) file a new record per book/year in
  both providers — the Archie holiday-annual shape; the year in the filename selects.
- When the packet's lookups miss, re-probe `lookup.py` WITHOUT a licensor prefix ("Jim Henson's"), with the exact
  punctuation of the record ("Good Apollo. I'm a Burning Star IV"), as one word ("Ruinworld"), or by the
  abbreviation the publisher uses ("HSE").
- GCD spellings that hide a hit: Bongo's annual is plainly "Treehouse of Horror" (5435); Miller's 1983 series is
  "Rōnin" (2721); Comico's Primer is "Primer" (14288). DC's Blackest Night revival issues sit in the ORIGINAL
  series (Power of SHAZAM! 5246 runs 1995-2010).
- The per-file CV matcher picks the LEGACY volume for every New 52 shelf (Batman 1940, Wonder Woman 1942, Nightwing
  1996, Catwoman 1993) because it matched by issue number — the stored link on a 2011 shelf that names a 1940
  volume is `wrong-cv-link`, the 2011 volume is in the rip.
- Every DC Ink / Zoom OGN pairs one CV count-1 volume with one GCD count-1 series 6-20 pages larger than our rip.
- Fan-made DCP "Archive Edition" compiles and story-only extracts → `R` (not a published edition), never linked.
- ComicVine splits ONE line across two spellings of its own ("Batman Arkham: X" vs "Batman: Arkham: X"). GCD
  prefixes titles the packet probes without ("Batman: Catwoman Defiant", "The Blue Beetle" for the 2006 series).
- The Archie-holiday split runs BOTH ways: GCD mints six one-shot series for DC's Earth-Prime week while CV keeps
  one 6-issue volume — whichever provider spans the shelf carries the S line; the other takes `provider-missing`.
- DC Zoom / Super Hero Girls OGNs are serialised in CV as 12–15 "chapter" volumes GCD never indexes: the BOOK record
  is the identity, the chapter volume is `wrong-cv-link`. GCD indexes no DC digital-first chapter series and no
  Justice League of America (2006) trades — honest `provider-missing`.
- A `(digital-mobile)` vertical reformat has 2–3× a floppy's page count and may match nothing → `R` + `mobile-rip`.
- A truncated parsed key reaching a different comic ("Riddle", "Batman - Death", "Once Upon") is `wrong-cv-link`.
- A digital re-release can cut each collected volume into chapter files restarting at 1 (Dark Horse's 2014-15
  Kabuki: seven `vNN` folders = seven different published comics from Caliber / Image / Marvel; v1 stamped one
  Image volume on all 44 files). Read the chapter files as the runs they came from.
- GCD keeps ONE album series where ComicVine mints a volume per book (Grandville 42860, Polar 88420 — the
  Cinebook shape); sibling shelves split by ComicVine take `F merge-with` naming the GCD line.
- Dark Horse art books, licensed tie-ins and digital-only shorts are systematically absent from GCD — the
  ComicVine ISSUE id is then the book's only item-level record. GCD hides hits behind "in:", dropped articles,
  licensor prefixes ("Edgar Rice Burroughs' …") and bracketed FCBD contents.
- Delcourt / Soleil English digital albums: CV mints one English volume per line, GCD holds only the FRENCH
  original — probe the French title (Prométhée, Les Maîtres Inquisiteurs, Orcs & Gobelins), pair at 0.9 naming
  the language. The Delcourt folder is cut into shelves by a subtitle in one filename (Ekho v03 / v07) and each
  such shelf carried a stored link to a different comic of that name → `wrong-cv-link` + `merge-with`.
- GCD indexes PRINTINGS: the S line takes the first edition both legs agree on; the `I` line takes the printing
  we hold (page counts match the edition's row). GCD issue ids for books come from `gcd_issue` — prefer the
  issue id to the `s<series>` form; use `s<series>` only for twin/variant rows.
- Ladder coincidence: two runs with the same issue COUNT (X 1994 vs X 2013, both 25) fool the per-file matcher;
  the year in the filenames decides.
- Drawn & Quarterly / one-book publishers: CV count-1 volume + GCD `[en-ca]` row, 0.95 with an `I` line; an ISBN
  barcode on the file is a 1.0.
- Dynamite: the same-title count-1 record a year after every mini exists in BOTH providers; the stored link is
  that trade on ~30% of Dynamite shelves and the run is one line below in "CV candidates". GCD indexes no
  Dynamite art book and no Dynamite holiday one-shot.
- Europe Comics may have an ENGLISH PRINT sister edition in GCD (SelfMadeHero, Cinebook, NBM, Fanfare,
  Magnetic Press, Ablaze) — the same text, not a translation — which is the better second leg than the
  original-language row.
- Archive / reprint lines (EC Archives, Dark Horse archives) — RULING: when both legs also hold the ORIGINAL
  series, the S line is the original (the run wins) and the `I` line is the archive printing we hold; only
  when the original is in neither leg does the archive line carry the S at 0.9 with an `N` naming what it
  reprints. Do not let the stored link decide which shape you write.
- A truncated key inside a deep character folder reaches the wrong relative ("Red Sonja - Red Sitha" → the
  Red Sonja ongoing; "Elle(s)" → Soleil's "Elle") → `wrong-cv-link`. Subtitle repetition inside one ladder
  (Crusade 005-008 = 001-004's subtitles) is four albums ripped twice, not eight.
- First Second (and similar) carry TWO adjacent GCD rows per book that are HARDCOVER / PAPERBACK printings with
  different ISBNs — our rip's PAGE COUNT picks the row (0.95); only when both rows are blank cap at 0.9.
- GCD hides behind the SUBTITLE: "The Hunting Accident", "Giraffes on Horseback Salad", "Undesirables" return
  nothing under their full titles — re-probe the SHORT title before writing `provider-missing`.
- A ripper's filename can carry a publisher's title shape ("H. G. Wells - <title>" = Insight/Lion Forge 2018),
  not the older adaptation the stored link names.
- An EAN-13 read off the page that equals a GCD issue's ISBN is a 1.0 on the `I` line even when the S line takes
  another territory's record.
- An archive of a run that the run's own records do NOT cover (Peanuts Dell Archive collects Four Color issues
  the Dell "Peanuts" run excludes) takes the archive line's records, with an `N` naming the run.
- ComicVine mints a VOLUME PER BOOK on Epic Collection / Masterworks lines while GCD keeps one series for the
  line — that splits one folder into many shelves → `F merge-with` between the siblings, naming the GCD line.
- `_Marvel Facsimile Editions`: all 24 single-book shelves carry the same wrong `round2-folder` GCD series
  (185205 Moon Knight, Panini France). Foreign-language legs (Panini España/Brasil/France/Deutschland, a Greek
  edition) are rejected on the publisher column alone.
- IDW is the Dynamite/BOOM shape at scale: EVERY mini has a same-title count-1 trade a year later in BOTH
  providers, and the stored link is that trade on ~1 shelf in 5 (`wrong-cv-link` is the commonest IDW flag). The
  run is the line below it in "CV candidates".
- Humanoids' English programme: one book = one CV count-1 volume + one GCD count-1 series (0.95). An ALBUM LINE is
  a CV volume of N whose issue names are the album subtitles while GCD keeps only the one-volume collection —
  that caps the shelf at 0.9.
- On a reprint-library line (IDW Collection: G.I. Joe / TMNT / Transformers) `gcd_issue` page counts are the
  strongest per-book check — our rips matched volume-by-volume within ±10pp.
- Heavy Metal magazine is CV 19498 / GCD 110631 "Heavy Metal Magazine"; the suffix-stripped probe misses it and
  returns only a French Gallimard row — re-probe by the full title.
- A Glorith-HD rip's ComicInfo `Web` id CAN be a ComicVine ISSUE id of the stored volume (599834, DC 100-Page
  Spectacular) — Glorith's Web ids are never GCD ids, but check them against the CV rip.
- A file's EAN-13 that equals a GCD issue's UPC (0761941202327 ↔ 76194120232700111) is a 1.0 on the `I` line.
- IDW/Hasbro shelves: the per-file CV matcher reaches foreign licences by bare words (Macross Delta on "Delta 13",
  TM-Semic on "G.I. Joe", Kingstone on "Babylon", Cross Cult on "Angry Birds") — reject on publisher.
- `gcd_issue` carries the trade's page count AND ISBN: it matched our rips within ±10pp on 100+ books and told
  two same-title count-1 rows apart (an 8pp ashcan is not a twin of the trade).
- The count-1 trade-as-stored-link rate is ~1 shelf in 4 on IDW AND Image (101 of 438) — a linker artefact,
  publisher-independent; expect it everywhere a mini was collected a year later.
- The Cinebook shape (one GCD series, a CV volume per book) recurs inside IDW/Image (Godzilla Rivals, Obscure
  Cities, MLP Annual, Cyberforce Origins, Hinges, Image+, Bloodstrike, X-Files specials): siblings take
  `merge-with`; where CV has no series-level volume the S line is `cv=-`.
- GCD hides behind a LONGER title too ("… and the Tale of Azkon's Heart", "Holiday Party (One-Shot)"), not only a
  shorter one — probe both directions before `provider-missing`.
- A facsimile of a SINGLE issue is its own book; a reprint edition of a whole series is not — the original wins.
- Legendary Comics and Mad Cave mint the same-title count-1 collected record beside every mini in BOTH providers
  (the Image/IDW/BOOM shape); the stored link is that record on half their shelves, and its `ratio 0/N` LOOKS like
  a missing rip rather than a wrong record.
- Lion Forge / Magnetic Press is a FORMAT split, not a missing leg: ComicVine serialises each European album into
  N digital chapters while GCD holds the one collected book (Meka 4↔1, Naja 10↔1). Pair them at 0.9; when our file
  is the whole album the stored link is the chapter volume.
- Dark Horse / DC Jinxworld re-issues of Bendis's 1990s Caliber/Image books (Jinx, Torso, Goldfish, Powers) are
  PRINTINGS: the S line takes the first edition both legs hold, the `I` line the Jinxworld printing.
- GCD labels every modern MAD book "EC" where ComicVine and ComicInfo say DC — the same publisher's imprint, not a
  rejection.
- `(of NN)` in a filename can contradict both providers (Transformers #1 40th Anniversary: count 1 in both legs,
  file says "01 (of 04)") — that residue caps at 0.9.
- A folder that is a ripper's READING ORDER over several separately published arcs (The Ride, Revolution) has no
  spanning record — the ladder is arc numbers, not issue numbers.
- GCD's exact filing recovers legs the probe misses: singular/plural ("Greeting", "Return"), two words ("Super
  Hero"), a spelled ordinal ("Fortieth"), a dropped subtitle, or the colon form ("Jinx: Torso") — try each before
  `provider-missing`.
- MANGA shelves: the per-file CV matcher picks a FOREIGN-LANGUAGE volume on about half (Carlsen/Egmont/Cross
  Cult, Glénat/Ki-oon/Kazé, Panini España/Norma/Ivrea, JBC, Altraverse) and the STORED link is the JAPANESE
  original on most of the rest — the English pair is in "CV local rip" + "GCD dump" every time.
- GCD hides English manga behind a LONGER title far more than a shorter one ("Oishinbo a la Carte", "Neon
  Genesis Evangelion 3-in-1 Edition", "Fairy Tail S: Tales from Fairy Tail") and occasionally a typo ("Fairy Tale:
  Fairy Girls"); the mirror exists too (no ComicVine Viz volume for JoJo Parts 1-3 while GCD has all six → `cv=-`).
- A spin-off family carries the PARENT run per-file (all eight Fairy Tail side shelves stamped 46777) — reject.
- A publisher change mid-line (Vertical → Kodansha USA) splits the GCD leg while CV keeps one volume (Ajin, CITY,
  Miss Nagatoro): one S line, the GCD row that holds the wider span, `N` naming the other.
- Page count tells GCD twins apart (a 32pp sampler vs the 256pp anthology); a count-0 GCD row (236534 "Cells at
  Work! Lady") is an empty stub, not a leg.
- Where GCD has no English row for a manga, readers have linked the same work's Japanese original (else the
  European edition) at 0.9 with the language named — the Europe Comics ruling extended to manga, CONFIRMED by
  Eric 2026-09-10: the right comic in another language beats no leg.
- A LeDuch (Markosia) rip's ComicInfo `Web` id IS a ComicVine ISSUE id — 11 of 11 resolved to the stored
  volume's own issue (Androsaurs' resolved to #2, the v02 file we hold): the 1.0 shape.
- Star Wars manga needs BOTH spellings: ComicVine files Dark Horse's 1999 adaptations as "Manga Star Wars:
  <Film>", GCD as "Star Wars: … — Manga"; neither probe finds the other.
- Scanlation shelves are readable and the filename's TITLE and YEAR decide them (Usagi Drop ≠ the English "Bunny
  Drop"; 24-30pp Slayers floppies = Central Park Media, not Tokyopop's graphic novels).
- The count-1-trade-as-stored-link artefact runs at Marvel too (14 Star Wars shelves in one batch); a collected
  series whose count equals exactly our two books is the matches-our-holdings trap.
- Kodansha Comics USA digital-first and Markosia / Digital Manga Publishing are honest GCD holes — re-probe once
  (short, long, licensor prefix), then `provider-missing`.
- A truncated parsed key reaches a wholly different comic ("Original Sin" → the Marvel event, "The Battle" → a
  1972 Chick tract, "Wonder" → a 1942 British weekly) — the filename's full title decides, never the key.
- RULING (lead, 2026-09-10, from S16653 Star Wars Legends Epic Collections): the ARCHIVE ruling (take the run's
  records) applies only when the collected line has NO record of its own. When either provider carries the
  line (Epic Collection, Omnibus, Masterworks…) the LINE wins at 0.9 with an `N` naming the run — never link a
  trade shelf to an ongoing it would merge into.
- Marvel Infinity Comics are a TOTAL GCD hole (~115 shelves): ComicVine indexes every serial as its own volume,
  GCD indexes none, and every GCD hit is the PRINT comic of a similar name, years off. `gcd=-` +
  `provider-missing` at 0.9 is the honest shape; probing harder buys nothing.
- Star Wars "Marvel Edition" digital trades (Kileko/Zone/Shan-Empire): both providers mint a same-title count-1
  record the year the trade shipped beside the 4-6 issue run; the stored link is the trade on 36 of 150 shelves.
- Episodes IV/V film adaptations have no series record of their own (they sit INSIDE Star Wars 1977); the 2015
  collected record carries them with an `N`. Episodes I/II/III/VI take the Dark Horse / 1983 Marvel original.
- ComicInfo `Volume` holding a 4-digit ComicVine volume id (not a year) is a 1.0 assertion like the 6-digit
  case (Ka-Zar v2 1974: `Volume 2692` + `Count 20` = CV 2692) and disproves the per-file matcher's pick.
- Marika-Empire and Glorith-HD `Web` ids are ComicVine ISSUE ids of the stored volume (7 of 7 here).
- Creator-named collected lines both legs carry with a multi-volume count (FF by Ryan North 6, Deadpool by Ziglar
  3) take the line at 0.9 + `N` naming the run — the run record is usually absent from rip and dump.
- Old Marvel folders cut into `vN (year)` shelves: the per-file matcher picks the wrong DECADE by issue number
  (Amazing Adventures 1961 on the 1970 run, Ka-Zar 1997 on 1974, Kull 1971 on 1982/1983). The folder's `vN (year)`
  suffix and ComicInfo `Volume` beat it every time.
- Epic Collection / Modern Era Epic LINES: probe the line name WITHOUT the volume subtitle — six "empty" packets
  held a GCD line row that way (Black Widow Epic 158119, Carnage Epic 183078, Daredevil Modern Era 209809).
- A GCD collected series numbers its issues by VOLUME and titles them with the volume SUBTITLE — that, not the
  page count, places a trade when a shelf holds several volumes of one line.
- GCD filing quirks that recovered legs: drop "Comics" from a title, "and" for "&", "Digest" appended, reversed
  or reordered name lists ("Deadpool / Amazing Spider-Man / Hulk: Identity Wars").
- Marvel UK: GCD keeps ONE series across a weekly's title changes ("Super Spider-Man" 2407, 153 issues) while CV
  mints a volume per title; the quarterly "ThunderCats Collected Comics" is ONE CV volume and FIVE GCD series
  (one per season) — the Archie holiday split with ComicVine on the spanning side.
- `_2099 Marvel` shelves carry a truncated key ("Doom", "Ghost Rider") that reaches the 20th-century namesake;
  the folder's "<Title> 2099" is the identity and both providers hold it.
- Marvel "Saga" primers (7-11pp) and "Update '89" (filed by both providers as plain "The Official Handbook of the
  Marvel Universe" 1989) are GCD holes like the Infinite Comics.
- `round2-folder` / `round2-series` was wrong on 10 of 13 shelves carrying it in B-053..B-055, including one link
  stamped on four sibling one-shot shelves in one folder — verify each by title + count + years, never by method.
- A `<Title> vN (<year>)` folder holding only a collected edition is the RUN's folder — the run takes the S
  line, the trade an `I` line; a `_Trades` / `_Minis` / `_One-shots` bucket is the opposite (the book's own
  count-1 record, minted by BOTH providers, wins).
- A stored link that is the same title a DIFFERENT DECADE later is a new cluster (Fist of Khonshu 2024 on the
  1985 Moon Knight run; a 1971 Sub-Mariner Annual on the 1998 annual) — years decide, never the title.
- Scholastic / Abrams all-ages Marvel books are in ComicVine under the real publisher and in GCD only
  sometimes — the publisher column, not the title, is the test.
- A ComicInfo `Volume` holding a 5-digit ComicVine id (Hulk Comic 37309) is the same 1.0 assertion as the 4- and
  6-digit cases.
- The checker refuses an S line on a shelf with an OPEN conflated-series flag: write `R` + `F split-needed`
  with the run's own ids in the R clause AND an `N` (`cv=… gcd=…`), so clearing the flag makes it one edit.
- GCD hides behind the definite article too ("Spider-Man vs. The Black Cat" 40812) and behind ComicVine's
  spelling of a one-shot the parsed key never reached (four Thunderbolts one-shots) — probe the CV name in GCD.
- `lookup.py --year` separates same-title relaunches (Superior Spider-Man 2018 vs 2013); "Lethal Protector II"
  is its own 5-issue run in both legs, not the 2022 Lethal Protector.
- Where the arithmetic refuses the run, say so and override: a 201pp digest is not a 4-issue mini; a 551pp
  vertical rip is the 8-chapter Infinite Comic, not the 4-issue print mini.
- Epic Collection shape is total at Marvel: CV mints a volume per BOOK, GCD keeps one line row; the CV per-book
  issues are labelled "Volume N" and match filenames volume-for-volume — the cheapest per-book verification.

- GCD splits a compound word ComicVine joins: "X-Men: Clan Destine" (5570) is invisible to a "ClanDestine" probe,
  and 32535 "ClanDestine vs. The X-Men" is the collected edition, not the mini.
- GCD hides a graphic novel behind a reversed / creator-first title: the bare word "Scorpio" found 14536 "Wolverine,
  Nick Fury: The Scorpio Connection" and 16225 "Scorpio Rising [Wolverine & Nick Fury]", both missed by the dump.
- An Epic / Complete Collection LINE is found by probing the line name with NO volume subtitle: "Generation X Epic
  Collection" → GCD 174151, shared by four sibling shelves.
- Marika-Empire and Glorith-HD ComicInfo `Web` ids are ComicVine ISSUE ids of the stored volume (8 of 8 in B-061..063:
  45305/109026 → X-Men Unlimited #20/#34; 124309, 70469-70471 → Hidden Years #1-4) — each a clean 1.0.
- `_Trades, Minis and One-shots` / `_X-Men TPBs` bucket folders: the book's own count-1 record wins ONLY when both
  providers mint it; where only one does, the numbered run behind the trade wins (Wolverine/Punisher, Victims, Origin II).
- GCD mints TWIN count-1 rows for Marvel digital trades far more often than expected (14 pairs in three batches:
  192062/192065, 233265/233608, 234147/234148, 218420/218632 …); a twin caps the line at 0.9 and blocks a GCD issue-level `I` id.
- A folder-wide CV stamp exists too: Space Goat's Evil Dead 2 programme (CV 20289 on 37 files) and Millarworld's Dark
  Horse folder (CV 115757 on the sequel) stamp ONE wrong volume across a folder — the `round2-folder` shape on the CV side.
- Bubble Comics (Exlibrium, Major Grom, Igor Grom, Meteora), Space Between, Scout/Roar, Strip For Me, Studio D., Imagine
  Bin, SAF Comics, Magnetic Press and Lost His Keys Man are total GCD holes — ComicVine-only is the honest answer there.
- Marvel Infinite Comics / Digital Comics Unlimited / Legacy Primer Pages / Marvel Universe cartoon serials are a complete
  GCD hole (14 shelves took `gcd=-`); GCD's only hits are the PRINT repackaging at a different count.
- A shelf read in an EARLIER batch may already link the volume you found (a second rip of the same mini in another
  folder): `check_identity --all` catches it as an undeclared merge — write `F <sid> merge-with=<partner>` when the
  partner is the same comic (Iron Fist: Wolverine S9566/S9567; X-Men/Runaways FCBD S22298/S11337).

- Oni Press and Scout Comics are the IDW/BOOM count-1-trade shape at full strength: 31 shelves in B-064..066 carried
  the stored link to the same-title count-1 collected record instead of the run (Alabaster Shadows, Blood Feud, Cemetery
  Kids, Cult of the Lamb, Princess Ugg, Terrible Lizard, Black Cotton, Canopus, Smoketown, Stabbity Bunny, White Ash …).
- Papercutz / NBM mints TWIN GCD rows for nearly every book (155074/155075, 142309/142310, 119438/119439, 94997/94998,
  187878/187879, 178089/178090) — that twin is the commonest single cause of a 0.9 on those shelves.
- A ripper's ComicInfo `Web` id is a ComicVine ISSUE id for Novus-Year Four (Days Like This 354967, The Tomb 392636) and
  LeDuch (Fear City: Thumper 1078598) — clean 1.0s.
- Panel Syndicate and MonkeyBrain are the same total GCD hole: GCD indexes only the later Image/IDW print collections,
  never the digital-first serial (17 shelves).
- The "GCD hides behind the licensor prefix" shape recurs at Avatar: `Streets of Glory` returns only the trade,
  `Garth Ennis' Streets of Glory` returns the six-issue run (26642).
- GCD files New England Comics' Tick specials under the POSSESSIVE ComicVine drops ("The Tick's Big Romantic Adventure"
  15953, "The Tick's Big Back to School Special" 15958) — re-probe with `Tick's`.
- Silver Sprocket, Storm King, Scout and Oni mint one record per BOOK, so a shelf holding several titles has no spanning
  record at all (Storm Kids, Tales of Science Fiction, Tea Dragon, Metalshark Bro, the two Whiteout volumes, Queen &
  Country: Declassified, Tank Girl's bucket shelf).
- `round2-folder` was wrong on every shelf carrying it in B-064..066 (Tank Girl Colour Classics ×2 right by accident;
  Rotten & Zombies vs Cheerleaders got "Hack/Slash Meets Zombies vs Cheerleaders", a different crossover) — still ⚠ never a leg.
- The withheld rule beats the checker's merge-with remedy text: a stored-link collision with a DIFFERENT comic is
  `cv=-` + `N withheld cv=<id> … S<partner>`, never a merge (S101634 vs S22834); merge-with only for the same comic.

- Titan Doctor Who "Year Two" runs exist in GCD (92899 Eleventh, 95387 Twelfth) and NOT in the ComicVine rip — the
  mirror of the usual hole; the trade sits on the collected line (GCD 95180 / 111484) by volume number + subtitle.
- Valiant's GCD rows count DOUBLE because GCD indexes every issue's "Pre-Order Edition" as its own row (Doctor Tomorrow
  10 for 5, Fallen World 10 for 5, X-O Manowar 16 for 8) — exactly twice ComicVine's count is that artefact; caps at 0.9.
- Top Shelf is an IDW imprint: GCD files every post-2016 Top Shelf book under publisher "IDW" while ComicVine says
  "Top Shelf" — the publisher columns disagree by imprint, not by comic (40 clean 0.95 pairs in B-068).
- TKO Studios ships a 6-issue floppy run AND a same-title count-1 collected record simultaneously in both providers;
  the stored link is the collected record on 9 of 15 TKO shelves.
- Acclaim's Eternal Warriors is the Archie-holiday split with ComicVine on the spanning side: CV 26146 count 6 vs six
  separate GCD one-shot series (15146, 21569-21572, 21589).
- A GCD row with ZERO issues is an empty stub, not a leg (Skull Cat 197154, Rivers of London: Stray Cat Blues 213823,
  Archer & Armstrong: Revival 164703) → `provider-missing`.
- Titan's licensed European albums (Azimut, Cutting Edge, Tyler Cross, Yragaël) are the Delcourt shape: CV mints the
  English volume, GCD holds only the original-language album line — pair at 0.9 with the language named.
- An exact-name probe can miss a sequel the rip DOES hold: `--contains "Icarus Society"` found CV 143976 after the
  exact "Prodigy: The Icarus Society" probe returned nothing — try `--contains` on the subtitle before `provider-missing`.
- A withheld shelf with NO GCD leg to keep becomes `R` + `N withheld cv=<id> … S<partner>` (S4944, S99691) — the
  revisit pair re-links the partner and then the S line can be written.

- cvref's `normName` DROPS "of" (and the leading article) but keeps from/with/in: an exact `lookup.py` probe containing
  "of" ("Sea of Thieves", "Books of Magic", "Year of Valiant") returns 0 CV hits on volumes plainly there — drop the "of".
- A SPACE inside a title hides the English record in BOTH providers: "Gen 13: Armageddon" (CV 19859/GCD 27648) and
  "Superman/Gen 13" (CV 26595/GCD 16731) are invisible to a "Gen13" probe — that is why such packets offer only foreign editions.
- GCD hides behind a bracketed alternate title: "War Stories: J for Jenny [War Story: J for Jenny]" (10937), "Dance Like
  Everybody's Watching! [A Zits Treasury]" (132081), "You're Out of Your Mind, Charlie Brown! [Horizontal]" (155990).
- Year-by-year archives of a newspaper/web strip (Doonesbury, Dennis the Menace, Rip Kirby, The Little King) have no record
  in either provider → R + provider-missing; where a provider holds the strip's own collected line (Cyanide & Happiness,
  Oglaf) pair at 0.9 as a format split.
- GCD files all three modern Bloom County books as ONE 3-issue series (110459): each shelf takes `gcd=-` and the row id on
  its `I` line, or the three shelves merge.
- Valiant's GCD pre-order doubling confirmed again: GCD 153708 "The Visitor" count 12 = six issues each indexed twice.
- Zenescope runs the count-1-trade-as-stored-link shape at full strength (24 wrong-cv-links in B-071 alone), and its
  collected records are almost never in GCD (`I … gcd=- 0.9` is the honest per-trade shape).
- A "withheld pair" the lead framed can turn out to be the SAME comic filed twice (Sea of Thieves v2 / Origins: one Titan
  2021 mini, CV 134597 "Volume named per the indicia") — the reader's evidence wins; write merge-with, no wrong-cv-link.

- `_Marvel Major Event Chronology (digital)` is one folder per tie-in holding that tie-in's collected edition: the
  count-1-trade-as-stored-link artefact runs at 86 of 202 shelves there, the highest rate in any single folder.
- Modern Marvel tie-in collections are 116pp almost without exception (a 3-issue mini) and 124pp for a 4-issue one;
  `gcd_issue` page+ISBN places every such trade within 3-20pp of our rip.
- Marvel's giveaway newspapers and preview magazines are a total GCD hole (Fallen Son Daily Bugle, Civil War II Daily
  Bugle, New York Bulletin, Empyre Magazine, Blood Hunt Diaries, Art of War of the Realms) → `provider-missing`.
- Marvel ships minis under a SHORTER title than the trade: AVX: VS / AVX: Consequences, "1872", "Years of Future Past",
  "The Union", "Atlantis Attacks", "War of the Realms: Punisher" (no "The") — probe the abbreviation or the bare
  title before writing provider-missing.
- `Marvel\__Skottie Young Covers` holds a 1pp variant shelf for nearly every 2015 Secret Wars mini, each already linked
  to that mini's volume — the commonest stored-link collision on Secret Wars shelves (merge-with when it is the mini).
- A legacy-renumbered run is its own volume in both legs (Captain Marvel #125-129 = CV 105506 / GCD 117972; GotG
  #146-150 = GCD 118041); the per-file matcher reaches the 1960s/1990s namesake by issue number every time.
- In the chronology folders the RUN rule stands over the bucket rule: `S` = the numbered run, the tie-in's collected
  record goes on the `I` line (B-072/073, ~90 shelves); the book's own record is the S only where no numbered run exists.

- A COVER YEAR read as an issue number is a distinct wrong coordinate: 2000 AD's Christmas bumper progs ("PROG 2013",
  "2000AD 2014") and "Festive Thrillpower 2017" collide with real progs 2013-2017; "2000AD Annual 1984/1986" likewise.
- A bracketed publisher series number becomes a phantom ladder entry: Thun'da's "[A1-056]/[A1 073]/[A1 083]" produced
  ladder 56/73/83 and a ratio 5.00 — read the bracket, not the ladder.
- GCD splits a long British weekly by PUBLISHER ERA while ComicVine keeps one volume (2000 AD: GCD 11289 IPC / 11295
  Fleetway / 11294 Egmont / 11293 Rebellion vs CV 19752) — the publisher-change rule: pair CV with the majority-era GCD
  row, name the others in an `N`. (An earlier tier-C note said GCD keeps one row; it does not for 2000 AD.)
- AC Comics mints a NEW count-1 series in BOTH providers every time a title returns a decade later (Fem Fantastique,
  Femzine, Paragon Illustrated, Nightveil, Bill Black's Fun Comics) while ComicVine keeps ONE spanning volume.
- AC's "Retro Comics" line numbers ACROSS titles (#0 Cat-Man, #2 Miss Victory, #4 Jungle Girls, #5 All-Hero): CV has
  the 5-issue line volume (21438) AND per-title count-1 volumes, GCD only per-title one-shots — the line volume spans a
  bucket shelf; per-title rows go on `N` lines.
- A ripper's CONTINUOUS numbering can hide several published comics: Gung-Ho 001-014 = Gung-Ho (7) + Sexy Beast (4) +
  Anger (4); the trailing number after each filename subtitle is the real run's number, and a ladder running PAST the
  linked count is the tell → `R` + split-needed, never `S` the first run.
- Two different comics can share a title on one shelf via two publisher folders (Stillwater: Action Lab 2016 mini vs
  Image/Skybound 2020 ongoing) — ComicInfo Publisher, not the title, splits them.
- The numbered "01 …/02 …/03 …" (or vN) subfolder is the small-press SEQUEL marker at Action Lab, Aftershock, Ahoy,
  Ablaze and American Gothic alike (Danger Doll Squad, Rough Riders, Voracious, We Live, Killbox, E-Ratic, The Wrong
  Earth): each subfolder is a co-equal mini with its own record → `R` + split-needed unless one dominates by proportion.
- `lookup.py --contains` on the LINE name recovers a whole collected programme the packet shows nothing of: "Essential
  Judge Dredd" exact = 0 hits, `--contains` = all seven volume-per-book records.
- Fan-made STORY EXTRACTS cut out of an anthology weekly (Judge Dredd strips at 5-25pp, plain + "(Colour Reprint)"
  twins) are not a comic the ledger links: the shelf is `R` + `not-a-run` for that part, and the real published lines
  on it (IDW's ongoing, the Essential line) are named per run.
- REMINDER (lead ruling, applied again on Princeless S14096): a collected LINE with its own record (GCD 106434, nine
  trades) wins over the first mini's run; linking the first four-issue mini to a nine-volume trade shelf is wrong-cv-link.

- A plain "<Title>" 2015 count-4/5 CV/GCD record IS the Secret Wars Warzones mini; the "<Title>: Warzones!" /
  "Battleworld" count-1 records are the 2016+ collected repackagings — the mini's record wins, the trade goes on `I`.
- The matches-our-holdings collected-edition trap dominates Boom! / Berger Books trade-only shelves: a stored or
  per-file count-N pick where N equals OUR trade count is almost always the trade record; the run's true (larger)
  count is usually one candidate away.
- Cinebook English-album shelves: GCD's per-file matcher regularly picks a French/Dutch/German/Spanish co-edition over
  the English row — probe the English title in GCD before pairing at 0.9 with a language named.
- ComiXology Originals beyond Best Jackett: GCD holds only a later Dark Horse print reprint at a different count; the
  digital-first CV record is the identity, the print reprint is `N`.

- On a wave-2 `NN <Title> vN (<year>)` split-out shelf (ids S94xxx+) an `overlap-in-series` flag is stale by
  construction — the colliding Vol. 01s now sit on sibling shelves (13 of 14 in R-016); the lead dismisses the flag.
- Nine such shelves carry a same-title provider row whose count equals exactly the BOOKS we hold, one line below the
  run (GCD 98247/3, 173432/2, 135977/3, 56708/2, 118040/2, 140636/3, 174204/4 …) — the collected record, never the S.
- GCD spells a relaunch's volume ordinal into the NAME ("I Hate Fairyland Volume 2", 192611), so `--year 2022` on the
  plain title returns 0 hits — probe with the ordinal or `--contains`.
- A legacy-renumbered arc collected under its own title ("Punisher: War Machine") belongs to the parent volume's
  count: CV 90118 / GCD 100917 count 28 = #1-17 + #218-228 — the arithmetic that split S66039.
- Trade SUBTITLES tell two same-title relaunches apart when the per-file matcher cannot (Batman Beyond 2015 = Brave
  New Worlds / City of Yesterday / Wired for Death; 2016 = Escaping the Grave / Rise of the Demon / The Long Payback).
- LEAD RULING (S15513 She-Hulk): a creator-named collected LINE wins the shelf only when it is COEXTENSIVE with the
  shelf; a line that also collects a sequel series (Sensational She-Hulk volumes on the same line) is `I` lines on the
  line, and the run is the S.

- The per-file matcher's "wrong RELAUNCH" trap: it stamps the LATER same-title volume onto files dated to the earlier
  era by issue-number coincidence (Wonder Woman 2016, Batman 2016, JLA v2/v3, Firestorm, Hawk and Dove v5, Atari Force
  v2, Adventure Comics 2010) — the tell is files whose years or numbers predate or exceed the stamped volume's own.
- ComicVine keeps ONE spanning volume across era-retitled arcs (L.E.G.I.O.N. / R.E.B.E.L.S., Deathstroke "The Hunted",
  Green Lantern 1960, Detective, Action, All-Star) while GCD names each era its own row — majority GCD era + `N` the
  rest; no split for that alone.
- DC's "#DC Events" chronology folders resolve once the cross-referenced-run pattern is seen: a crossover reprint
  bucket or a tie-in folder filed adjacent to the main run takes the main run's identity with the tie-ins named.

- An open `overlap-in-series` / `conflated-series` flag on a shelf whose evidence shows ONE run (a wave-2 split-out
  whose siblings now hold the other ladders, or several trades LABELLED "Vol. 01" for arcs of one continuously
  numbered run) is a STALE flag: do NOT write R to satisfy the checker — write the S + `N <sid> stale-flag | …`, accept
  that one checker failure, and list the sid under "stale flags" in your report; the lead dismisses the flag.
- The per-file matcher's "wrong relaunch / legacy volume" trap generalises to DC: Batman (1940) stamped across every
  modern Batman variant-cover shelf, Batgirl (2000) across every later Batgirl relaunch, Warlord (a D.C. Thomson weekly
  of the same bare name) across the 1976 DC Warlord.
- GCD's `round2-folder` DC failure mode: every unmatched one-shot in a "_Miniseries and One-shots" bucket gets stamped
  with one wrong Dutch "Batman" ongoing (GCD 18155) — always rejected for the dump's exact match.
- Two shelves claiming the same stored CV volume across batches (duplicate rips of one run or one-shot filed under
  different folders, "Rebirth Deluxe Edition" volumes of one line, a run split by a year folder) is routine on a
  large DC vertical — `--all` catches it; `merge-with` in your own file after checking the partner's name and files.

- `lookup.py --issues <collected-line volume>` names each TRADE's own ComicVine ISSUE id: 71 of 73 `I` lines in
  R-018 carry an item-level CV id instead of `cv=-`.
- A DC trade LINE restarts its own "Vol. 01" every creative era while the comic keeps legacy numbering: Detective
  Comics has four such lines (CV 98510/GCD 111829, 121066/150022, 145333/180852, 150502/203114) over ONE unbroken
  #934-1093 ladder — that alone minted a "five Vol. 01s" flag; Batgirl 2011 has two lines over one 53-issue run.
- GCD's "&" spelling hides a run from every "and" probe (Batman & the Outsiders 2019: CV 118841 / GCD 144938, 17
  issues) — probe both spellings.
- GCD 60830 "Detective Comics" spans the New 52 AND Rebirth; CV splits them (42594 New 52, 91098 Rebirth) — a New 52
  sibling shelf must take CV 42594 and be declared before landing, or the shared GCD row misleads.
- Regular Show (S14554): Salem's v01/v04/v05/v06 are BOOM's Original Graphic Novel line (count-1 CV volumes 76877,
  102477, 112595, 116041; GCD 163681), Empire's Vol. 01-09 + Salem's v10 are the ongoing's ten collected volumes
  (CV 73115 / GCD 93394) — one run plus standalone books, NOT two rips; the lead's earlier "two rips" note was wrong.

- DC's digital-first "Injustice: Gods Among Us — Year N" chapters: CV's digital-chapter count matches our ladder
  exactly while GCD indexes only the print-issue count (about a half to a third) — the Bombshells digital/print split;
  pair them, GCD's lower count is not residue.
- The wrong-relaunch trap is near-universal on any DC title with 3+ relaunches (Green Arrow, Green Lantern, Harley
  Quinn, Justice League, Legion, Lobo, Martian Manhunter, Doom Patrol): the matcher stamps the oldest legacy volume or
  the largest/most-recent one across every sibling shelf; `lookup.py` by name + year recovers the true id.
- An `overlap-in-series` flag on a franchise with many `vN` sibling shelves (Deathstroke, Harley Quinn, Green Lantern,
  Justice League, JLD, Legion, Lobo) describes the CROSS-SHELF Vol. 01 collision, not conflation on the one shelf —
  confirmed 14 of 14 in C-013..C-015; the stale-flag note must name the sibling shelf.
- A continuation of legacy numbering (#201+ picking up a prior volume's own numbering, Green Lantern Corps S94605→
  S94610) is the SAME run as the prior volume — merge-with, not a separate id.

- RESIDUE, defined (lead ruling after Secret Six S15276): a duplicate rip, an annual, a one-shot, a single misfiled
  book, or ONE trade of a run that lives on another shelf. Two or more whole trades of a DIFFERENT run with its own
  record are a co-equal run — `R` + split-needed even at 6:2 — and the shelf's overlap flag is NOT stale.
- The wrong-relaunch / legacy-volume stamp ran at 70-95% of files on Midnighter, Mister Miracle, Robin, Nightwing,
  Supergirl, Plastic Man and Suicide Squad shelves in C-016 — assume it on any DC relaunch shelf and verify by year.
- A launch one-shot can sit beside a distinct "Deluxe Edition" collected-line record (Nightwing / Suicide Squad
  Rebirth) — a third record beside the run and its trades, not the trades-vs-run shape.
- Regional reprint editions (Canadian, Philippine, Australian) recur as small harmless residue on Golden Age DC shelves.
- A generic-title bucket shelf ("Robin" S14733) holding scattered issues of an already-dedicated volume takes
  `merge-with` that volume's shelf even when its ComicInfo tags match on their own.
- ComicVine keeps Action Comics as ONE volume (18005) across the 1938 run and the 2018 legacy-numbered continuation
  while GCD splits the eras (97 / 59922) — whether the two era shelves merge is a revisit question (R-019).

- A shelf holding ONE TRADE PER MINI of a chain (Blue Book "1961" / "1947", Brain Boy / The Men from G.E.S.T.A.L.T.,
  Briggs Land / Lone Wolves, Vox Machina Origins I / II, Baltimore's minis + its #1-40 trade line) is SEVERAL runs
  numbering from #1 — its conflated flag is TRUE of the shelf, never stale; `R` + split unless a provider holds a
  collected LINE record coextensive with the shelf (then S the line, minis on `N`). Sonnet called 7 of these stale in
  C-021; the lead sent them to R-020. **ERIC'S RULING 2026-09-16: ONE leg's coextensive line record is enough** —
  the S carries that line at 0.9 (`cv=-` when ComicVine has no line volume), each book's own ComicVine count-1
  volume / issue id, ISBN and LOCG bridge go on its `I` line, the minis underneath are named on `N` lines, and the
  flag is declared stale so the lead can dismiss it. Refusing such a shelf recorded nothing; the correct data is the
  line PLUS the per-book records. A shelf holding ISSUE files of several minis (Baltimore, Lobster Johnson) or
  several publishers' runs (Angel) is still `R` + split — no single record describes it.
- The wrong-relaunch / legacy-volume stamp runs in BOTH directions on DC legacy titles: the 1938/1959/1942 volume on
  modern relaunch files AND a modern or FOREIGN volume on legacy files (a 1976 French Éditions Lug "Titans" reprint,
  CV 44350, is the per-file majority on every US Titans shelf) — reject on publisher and language.
- GCD keeps ONE spanning row across a numbering restart while CV splits by volume for era-retitled legacy continuations
  (Superman #650-714 after Infinite Crisis; New Gods 1971 vs 1977) — the reverse of the weekly shape; pair the majority.
- Second-rip fragments of shelves already read in earlier batches (Flash Secret Files, Spectre 1992, Teen Titans
  2003/2016, Wonder Woman 1942/2016, Zatanna 1993, Donna Troy) surface as `--all` collisions — `merge-with` after
  grepping the partner's name and count.
- Dark Horse conflated-series flags split about evenly between real conflation (Alabaster, Aliens, American Gods, Barb
  Wire, Billy the Kid, Canto, Captain Midnight — R + split) and a stale flag on a 2-file shelf holding only ONE of the
  conflated minis — but check the two files are the SAME mini before calling it stale (Blue Book was not).
- `I` lines are never optional: a batch written without them lands no item links — the lead sent C-019..C-021 back
  for an I-line repair pass.

- The packet's "GCD dump: NNNNN … [] count" numbers are SERIES ids, never issue ids, even at count 1 — an `I` line's
  gcd= wants the issue row (query gcd_issue); a Sonnet reader reused dump ids raw twice in C-022.
- `gcd=s<series>` is valid ONLY on `I` lines; on an `S` line it fails the checker's numeric gate.
- Dynamite's same-title families (Red Sonja, Green Hornet, Turok, Bettie Page, Miss Fury, Black Terror, Sweetie Candy
  Vigilante) carry the wrong-relaunch stamp at near 100%: per-file CV puts the OLDEST same-title volume on every
  `vN (year)` relaunch shelf — the folder label is the tell, `lookup.py --year` recovers the id.
- Franchise folders that are genuinely several runs → `R` + split-needed, not residue: James Bond (three ladders),
  Charmed (reboot + two Zenescope seasons), Green Hornet (three eras), Savage Tales (three publishers on a bare
  title), Terminator (1999 Dark Horse mini + 2024 Dynamite), Vampirella (ongoing + "Year One"), Kirby: Genesis
  (main + Dragonsbane), Tomb Raider (two same-numbered relaunches).
- Same-run continuations split by a folder or a parsed-key token surface as cross-batch collisions → `merge-with`
  after reading the partner (Disney Masters S21200→S5517, Red Sonja v5 S99463→S14482, Sheena S15521→S15518,
  BSG Classic trade S2336 ↔ run S94559, Elvira #1 S6117 ↔ #2-5 S6116).
- Tier-C Dark Horse: a reader-derived CV id can be a FOREIGN edition (Magic Press "B.P.R.D." 136599), a hardcover
  Book-ladder record (Hell on Earth Book 1-5 = 107353) or the ISSUE-form ongoing behind the trades (Plants vs.
  Zombies 82701 = the 2015 12-issue run) — read the id's own issue list against the files before pairing it with a
  GCD collected-line row; a mismatched pair is not a 0.9 S. (Lead, C-022 verification → R-021.)
- Separately titled minis filed under a franchise shelf (Army of Darkness: "Ash Gets Hitched", "Ash in Space") are
  their own 4-issue runs, not arcs of the same-era series whose trade sits beside them (Ash S1357 → R-022).
- The Goon's collected GCD series 26047 numbers Vol. 01-15 across S65962 (C-022) and S65963 (C-023) — a retrofit
  candidate for S65962's `I` lines.

- ComicVine does NOT index Dark Horse TRADE LINES: a plain "Hellboy" / "B.P.R.D." volume query returns nothing; CV
  mints a count-1 volume per BOOK while GCD keeps one collected series per title (R-021: six of seven shelves).
  RULED by Eric 2026-09-16 (see the chain-of-minis ruling above): the GCD line alone is the identity at 0.9, CV's
  per-book volumes go on the `I` lines. R-020..R-022's one-leg refusals were flipped to S by the R-023 pass.
- The count-1-trade / first-mini stored-link artefact runs at full strength on Dark Horse and Dynamite chains
  (Ether 95727, Briggs Land 93181, Brain Boy 67220, Itty Bitty Hellboy 66787, Pathfinder 51262, Ash 68723): the FIRST
  mini's volume stamped across the whole chain.
- A ripper's continuous 001-0NN numbering over a mini chain is readable from the filename's own "(of NN)"
  (Baltimore, Lobster Johnson) — but the same shape can be one run (Abe Sapien 001-036 = CV 59507 / GCD 73562).
- GCD 102900 Plants vs. Zombies starts its volume numbering at #4; B.P.R.D. Hell on Earth is continuously numbered
  #103-147 from 2012 (CV 61307 / GCD 71228), not a pure chain.
- Action Comics: CV 18005 stops at #904 (2011); the Rebirth continuation #957+ is CV 91078 (ComicInfo `Volume`
  asserts it) — the "v1 (2016)" and "v1 (2018)" folder shelves are one run (R-019 merge-with).

- ARCHIVE shape, stated once (after C-025): a shelf that is PURELY the collected trades of a Golden-Age original
  (Dark Horse "EC Comics & Others Archives", Crime Does Not Pay, Frontline Combat, Magnus, War Against Crime, Savage
  Sword of Conan) takes the archive LINE's own CV/GCD record — the S16653 line-wins ruling; the older "S = the original
  when both legs hold it" line applies only when NO line record exists. The later, dated ruling wins.
- Packet phrase "vol X … 1 issue Y": X is the VOLUME id, Y the ISSUE id — an `I` line wants Y (`lookup.py --issues X`
  lists them); three mix-ups in one batch.
- `round2-folder` stamps a stale NEIGHBOUR shelf's GCD series across every singleton in a bucket folder ("Our Story
  Thus Far" 56736 across "Graphic Novels", "Infinite Wheatpaste" 212845 across Avery Hill) — never a leg.
- Juvenile franchise reboots (Geronimo / Thea Stilton: original run, Papercutz HC reissue, Scholastic Graphix reboot)
  are several co-existing lines on one shelf — R + split, not stale.
- Gold Key / Dell / Western funny-animal and Gold Key Star Trek: CV keeps ONE volume across the Dell→Western handover
  while GCD splits by era (the Daffy Duck / Flintstones / Bugs Bunny shape); a later DC/Marvel volume stamped by
  issue-number coincidence is the wrong-relaunch trap again.

- Under the one-leg ruling every Dark Horse / Dynamite chain-of-minis shelf in R-020..R-022 became `S cv=- gcd=<line>
  0.9` + `F wrong-cv-link` (the stored id was the FIRST mini's run in every case) + minis on N + a `C` per trade —
  except the issue-file chains (Baltimore, Lobster Johnson), the several-publisher shelf (Angel), a shelf mixing one
  season's issues with another's books (Angel & Faith), and Ash (three count-1 records, no line on either leg).
- A trade collecting TWO minis, and an omnibus, cannot yet be stated by a `C` line (one range, one run, one per item):
  name the runs on an `N` for now — TOOLS_TODO 17 adds per-run ranges and several `C` per item.

- A SHARED STORED ComicVine id is not proof of one run: `merge-with` on it only after the id fits BOTH shelves' own
  keys and GCD links. C-008 merged the real Flash 1959 shelf into the "The Flash v2 (2007)" folder shelf because both
  stored CV 1995 — but that shelf's own key and GCD link (26125, 2007-2009) said Flash vol. 2; the stamp was the
  legacy-number trap. The merged shelf became two runs (R-023, R + split). When a shelf's GCD leg contradicts its
  stored CV id, the CV id is the suspect, and the merge is `F wrong-cv-link` on the mis-stamped shelf instead.

- ITEM PASS (X-001..X-003): on RUN-identity shelves the per-file GCD link reuses the run's own series matched to ONE
  issue by number for vol. 01 only, while later volumes resolve to a real collected-edition series — check whether
  vol. 01's link differs in kind from vol. 02+ before trusting it (Kevin Keller, Mega Man, Klaus, Dark Crystal …).
- A `C` that restates the shelf's own run needs its ids repeated in the clause to clear 40 characters; the clause is
  the evidence, not a pointer. A shelf's own collected LINE and the run it collects are two records (Invisible
  Kingdom: line GCD 151274 vs run CV 117748 / GCD 142448; Goldie Vance: line CV 94628 vs run CV 89593) — the `C`
  names the RUN.
- Sibling shelves of one franchise can share one real GCD collected-edition line (Fence S6636/S6639/S6640 → 123731);
  the sibling's landed S is evidence for the weak per-file matches on the others.

- ITEM PASS (X-004..X-006): per-file ComicVine on a BOOM trade shelf often matches the run's issue by ORDINAL
  position (trade #3 → issue #3) — not the trade's record; write cv=- and take the GCD collected-edition row.
- The packet's `linked …` field can say no match (st2/st3) while the shelf's own `gcd issues:` ladder or `N` note
  holds the title/ISBN match one line above — read the pool, not the verdict (Lumberjanes Max Ed, Wynd Book 03).
- Deluxe / Yearbook / Omnibus editions of the shelf's own ongoing with NO judged range get an `N` naming the
  relationship, never an invented `C` range (the range is containment's to read).
- Per-file GCD "collected edition" companions can alternate with a FOREIGN edition per file (Red Lanterns: 77665 vs
  Panini Deutschland 68739) — check each book's series against the shelf's `N` before trusting the id.
- A subtitle can name a SEQUEL series filed on the first run's shelf (Wild's End "Beyond the Sea" = CV 151678 /
  GCD 201441, not the 2014 run) — the `C` names the sequel run.
- Volume-only ids (a per-book CV volume with no issue row: Smallville Season Eleven's eight volumes) are an `N
  no-record`, not an `I` — an `I` wants the issue.

- ITEM PASS (X-007..X-009): foreign-language GCD twins are the commonest per-book trap on DC trade shelves —
  Spanish (84-…), German (3-86201 / 3-95798), French (2-365-77… / 979-10-268…), Dutch rows beside the English one;
  the ISBN prefix is the fast tell (Aquaman, Green Arrow, Catwoman, Checkmate, Deathstroke Inc. 185111 vs 187856).
- The packet's shelf-level `identity:` list of the shelf's own per-book GCD rows (pages + ISBN) outranks a book's
  own mismatched `linked` pick — read the shelf list first (Detective Comics 2012, Curse of Brimstone, Wonder Twins).
- Modern DC digital lines carry TWIN "Collected Edition" GCD rows for one trade (Detective 77783/77579, Stormwatch
  65339 stub / 76642, Dark Knights of Steel 189483/203963, I Am Batman 188323/201985) — one `I`, capped at 0.9.
- A degenerate packet block can stamp ONE GCD issue id onto every book of a shelf (Green Arrow S97350, six eras of
  collected lines) — resolve only by each series' full issue list matched by title, never by the stamp.

- ITEM PASS (X-010..X-012): confidence is exactly one of 1.0 / 0.95 / 0.9 / 0.7 on `I` and `C` lines too — no
  0.75/0.8/0.85/0.97; the checker rejects anything else (130 failures in one round-trip).
- On DC run-wins trade shelves the per-file CV pick is the run's issue by ordinal OR a franchise-wide legacy stamp
  reused across eras (cv 2839 on ~15 Green Lantern trades; 92750/134718 on every Harley Quinn era) — default cv=-
  and take GCD's book-specific collected-edition row.
- Sibling-era shelves of one franchise cross-contaminate each other's per-file GCD picks by "Vol N" ordinal
  (Harley Quinn S94832↔S94833, Nightwing S65356↔S94872, Legion S98212↔S65063; Supergirl's five shelves so badly
  that S94709's thirteen books are all no-record) — read the shelf's own list, not the neighbour's.
- A single-book "collected volume" shelf's S line often carries the book's own CV issue id — use it over a
  mismatched per-file pick.

- ITEM PASS (X-013..X-015): the per-file CV matcher stamps a SPANNING legacy volume on every modern Wonder Woman
  trade and the French Éditions Lug 1976 reprint (CV 44350) on every US Titans trade — reject on era / language.
- Title-only (st3) GCD matches reuse ONE gcd id across every volume of a line (Golden City ×10, Windmaker,
  Tarzan Kubert Years) — re-derive each book from the shelf's own numbered pool.
- Delcourt / Soleil English-digital shelves pair the English CV volume with the French GCD original at 0.9 (the
  Europe Comics shape) — except when GCD's same-name row is an unrelated book (Spin Angels → Marvel's GCD 53252).
- Creator-line trades on a mega-shelf ("Wonder Woman by Pérez / Byrne / Rucka") collide on a reused GCD issue id
  across lines — match by the creator line's own volume number, never the reused id.

- A GCD "collected edition" series sits beside the genuine RUN under a different id (Dept H 100362 vs 111468, Harrow
  County 89353 vs 94662, Sword Daughter 125748 vs 134585) — the `C` names the run; `lookup.py` by title finds it.
- Long lines GCD splits by publisher era (Usagi Yojimbo: Fantagraphics 3475 / Mirage 4888 / Dark Horse 5623 / 2019
  relaunch 147897; Little Lulu's Dell era 539) — bucket a book's judged range by era before naming its `C` run.
- An omnibus bundling several minis gets one `C` per mini at that mini's full range; an anthology TPB ("… and
  Others") is not a numbered run and gets an `N`, not a forced `C`.

- ITEM PASS (X-016..X-018): an archive line's `C` run is the reprinted Golden-Age ORIGINAL, and it is usually in
  the catalogs even when the shelf's `N` said "no record" (Creepy cv 2194 / gcd 1640, Crime Does Not Pay 943 / 296,
  Eerie 2300 / 1755, Frontline Combat 1443 / 834, Magnus 2157 / 1600, War Against Crime 11807 / 12814) — probe.
- round2-folder can stamp ONE wrong id across several UNRELATED shelves (gcd 212845 on three Avery Hill singletons,
  56736 on Dead Beats and Wait, What?) — never a leg, anywhere.
- A ripper's `vNN` can be offset by one from the album's own subtitle (Carthago Adventures) — the title decides.
- The Adventures of Tintin: every per-file gcd pick (60515/108543) was wrong; the shelf's own gcd pool matched by
  ENGLISH title was right for all 23 — the pool over the pick, again.

- ITEM PASS (X-019..X-021, manga): the per-file GCD pick reuses volume 1's title-only id across EVERY book of a
  manga shelf whose header already lists N numbered rows (A Bride's Story 12/12, Ajin 17/17, Assassination Classroom
  21/21) — match the header pool by ordinal, ignore the pick. Per-file CV lands on a foreign twin of the same
  title even more often (Milky Way, Egmont Ehapa, Glénat, Panini España, Carlsen, Altraverse, the Shogakukan
  original) — the shelf's identity is the ENGLISH edition; use the header's cv-issues pool.
- A count-1/2/3 trade record the shelf's `N` called absent is often in the catalogs under a spelling the packet
  never probed ("The Adventures of …", "Complete Collection", "Deluxe") — probe before writing no-record.
- GCD splits one manga line into a second publisher-branded series for its LAST volumes (Ajin v17, APOSIMZ v07-09:
  Vertical → Kodansha USA); `SELECT id, number, page_count, isbn, title FROM gcd_issue WHERE series_id=?` on the
  dump recovers the rows the header pool lacks.

- ITEM PASS (X-022..X-024, manga): the reused per-file stamp is not always volume 1's id — it can jump to a new
  wrong series partway through a shelf (Berserk: three wrong series across v02-v41; Bleach cycles four before
  settling) — always re-derive from the header pool by `#N`, never from a plausible-looking pick.
- Two parallel rip sets of one manga (Berserk .cbz + .cbr) take IDENTICAL per-volume ids from the same ordinal pool.
- Blade of the Immortal (S23118) is the canonical "line + run" shape: GCD's 31 collected-edition rows are the `I`
  identities (cv=- — ComicVine mints only the floppy volume), and the floppy run CV 9069 is what every `C` names,
  ranges #0-206 ascending across the 31 books. A single-leg shelf's books inherit the shelf's 0.9, never more.

- ITEM PASS (X-025..X-027, manga): when GCD splits a line in two (an earlier series for v1-N, a later one
  continuing: Blood on the Tracks 156521 / 171046, CITY 123267 / 172756, Nagatoro 152847 / 171408) the packet's
  header pool shows only the shelf's STORED series — find the other with `lookup.py --contains` and pull its issues
  with `gcd_issue WHERE series_id=?`.
- A chronology folder's ORDER prefix ("04 Dragon Ball - Full Color Freeza Arc v01") gets read by the per-file num
  matcher as the ISSUE number — v01 in folder "04" linked to issue #4; the true mapping is the shelf's own vNN by
  ordinal. Any "NN Title vNN" bucket naming is this trap.
- A `gcd=-` / dump-only shelf's books inherit the shelf's 0.9 (Cells at Work! Lady, Dragon Head); an exact edition
  can exist on GCD alone and be absent from ComicVine entirely (Cat Shit One Omnibus GCD 186621 → 0.9, cv=-).
- A manga whose only catalog runs are indexed by tankōbon VOLUME cannot take a chapter-count `C` range (Dragon Ball
  Full Color: judged #1-17 are chapters) — `N no-record`, never an invented mapping.

- ITEM PASS (X-028..X-030): a manga tankōbon line that GCD tags [Collected Editions] (Food Wars! GCD 95211) has no
  chapter-indexed run in either catalog — the `C OWED` marker is satisfied by `N no chapter-run`, the Dragon Ball
  Full Color ruling; the books ARE the run. Our single volumes against GCD's "two-in-one" record have no safe 1:1
  mapping → `gcd=s<series>` (Erased 118174), never an invented pairing.

- ITEM PASS (X-031..X-033): JoJo's Bizarre Adventure part-shelves are double-stamped — per-file CV is the whole-saga
  hardcover omnibus volume (165667, one issue per PART) and per-file GCD the umbrella whole-saga series (27115) by
  raw issue number; both wrong for all 37 books. Parts 1-3 have no CV softcover record (cv=-); Parts 4-5 do.
- A shelf-level "no record" `N` can be wrong for an English product too (Innocent Omnibus: CV 157999 / GCD 206125
  under "Innocent Omnibus") — probe the edition's own name before inheriting the shelf's gap.
- A series can be wrong on BOTH legs at once (Hunter x Hunter, Inuyashiki, Kakegurui Twin): the header pool by
  ordinal is the only source; an omnibus with no dedicated record on either leg is `N no-record`, never a mapping.

- ITEM PASS (X-034..X-036): the reused stamp can jump FOUR times over one shelf (My Hero Academia: 105138 → 204933
  → 236032 → 228276 over 38 books, CV pinned on foreign 166768 throughout); a shelf can carry NO gcd pick at all
  (Muhyo & Roji's ×18). The header ladder by ordinal is the only source on manga — no exceptions found in 400 books.

- ITEM PASS (X-037..X-039): one GCD issue row can cover TWO of our volumes when GCD's count undershoots (PTSD Radio,
  Ojojojo) — both books share the id at 0.9; a genuine 2-in-1 omnibus whose header rows read #1-2 / #3-4 maps 1:1
  at 0.95 (Orb). A 2-vol English omnibus against a 4-vol Japanese original has no safe mapping → `gcd=s<series>`.
- A spin-off filed in its own subfolder on the parent's shelf gets the PARENT's v01 ids stamped by the per-file
  matcher (Rent-a-(Really Shy!)-Girlfriend on Rent-A-Girlfriend) — `N no-record` (or the shelf note), never the
  parent's record; it is a misfiling for the lead.

- ITEM PASS (X-040..X-042): a run you can NAME but whose GCD rows carry no page counts gives no safe per-volume
  range (SPRIGGAN Deluxe → GCD 188803) — `N`, not a guessed `C`; a per-file link can point at a foreign edition of
  the WRONG book entirely (Summer Wars: the Complete Edition has its own one-shot record CV 155900 / GCD 142966).
- A stored English digital volume's cached rip may cover only its last issues (Suzuka #13-18 of 18) — the earlier
  books take gcd-only identities from the GCD series' full list, cv=-.

- ITEM PASS (X-043..X-045, ~30 manga shelves): not one case where the per-file pick was right and the header pool
  wrong. A foreign GCD leg that undershoots by one (Twin Star Exorcists, 26 vs 27) → the last book takes gcd=-;
  a Vertical → Kodansha USA imprint split duplicate-stamps the later imprint's id onto the earlier book
  (Weathering with You) — reject the duplicate, keep it on its own volume.

- ITEM PASS (X-046..X-048): a shelf can hold TWO published editions of one work with no shared pool (Monster: the
  18-volume line beside the 2014 "Two-in-One" Perfect Edition) — each edition needs its own pool or an honest
  no-record, never a mapping across editions. When a shelf's file count exceeds its pool, the excess usually
  belongs to a CONTINUATION volume the shelf's own `N` names (Ranma 1/2 v22-38 → CV 26409 / GCD 17651) — the pool
  for it must be pulled (`lookup.py --issues`, `gcd_issue WHERE series_id=?`) before writing no-record. GCD may mint
  a "collected edition" row for only the FIRST of a same-title pair (Zombie Makeout Club) — the second is gcd=-.

- ITEM PASS (X-049..X-051, Marvel trades): a per-file pick flagged round2-folder / title-only / page-rematch is
  "check it", not "reject it" — several were right on independent verification (Han Solo & Chewbacca cv 146853,
  Darkhold gcd 1851609, Machine Man). An `identity-read` per-file id can still be a duplicate across two books
  (Doctor Strange Epic Vol. 01 and 13 both cv 626281) → 0.7. Fifteen "no record" shelf notes were resolvable under
  another spelling ("Complete Collection", "Omnibus (2022)", the creator-named line) — re-probe before conceding.
- A magazine whose files carry only a ripper-assigned continuous "Vol. NNN" (Weekly Shonen Jump: CV numbers by
  cover date, no page counts, no GCD leg) is unmappable → `N no-record` per book. An Epic Collection whose judged
  ranges are a CUMULATIVE count across several volumes (Doctor Strange: Strange Tales + two later volumes) has no
  single run id → `N` explaining the cumulative numbering.
- ComicVine's issue-number field follows LEGACY print numbering (Amazing Spider-Man 2015 v4 #792-801 under CV
  85076 / GCD 92892) — a `C` range in the 790s is still that volume.

- ITEM PASS (X-052..X-055, the tail): a book's trade record is often findable on its FULL subtitle when the packet's
  per-file probe returned nothing (~40 recovered: X-Men Legacy: Legion, House of M omnibuses …); Deluxe Editions on
  Valiant / Marvel shelves usually have their own count-1 record the per-file leg mis-picked. ISBN 978-2 / 978-2-375
  is French even on English-titled shelves. A Papercutz line can be a SELECTIVE translation of a longer foreign line
  (Ralph Azham v01-04 = tomes 3/5/8/10) — match by chapter title, never by ordinal.

- R-024 (the item readers' shelf findings): GCD hides long runs behind a dropped article or suffix — "Flash" 3358
  (1987-2006, 232 issues) and "James Bond" 207986 (2024) are invisible to the shelves' own keys; two packets' "no
  GCD row" verdicts were wrong — probe without the article / the "007". A per-file CV id reused across a shelf's
  books can be a wholly different comic's issue list (Portman "Demon!" 1978 on Shiga's four Demon volumes) — a
  revisit re-verifies item-pass ids, never merely adds them. ComicVine's issue NAMES are strong `C` evidence
  (Flash 2010 "Case One, Part N"; POTA's four arcs). One ripper folder can be split across two shelves by filename
  wording (ten Usagi Saga deluxe files, 5 + 5). GCD indexes a deluxe line's 2nd-edition printing in the twin row.

- C-028..C-030: self-check before every S — "does my S line's id equal the id I just called wrong on the same
  shelf?" (three S lines carried their own `F wrong-cv-link` id; `--all` caught them as merge collisions). IDW-Hasbro
  "Chronology" bucket folders (G.I. Joe, TMNT) are read-order trees of fragments of several runs → split-needed.
  Same-bare-title-different-decade clusters (Powers v1-v5, Cyber Force) take one era's stamp across all — years and
  publisher decide. Image trade-only shelves carry the three-record trap (run + GCD "Collected Series" + a CV
  count-1 volume named after a subtitle) throughout. A SHELF batch with 0 `I` lines is incomplete and is sent back.

- Locke & Key is six separately numbered 6-issue seasons with a distinct count-6 record each (not arcs of one
  ongoing); Criminal's Image 2015-16 volumes are reprint PRINTINGS of the Icon originals (GCD 86891 title-matches,
  the first editions are CV 39022 / 39023 / 39025). A CV VOLUME id in `cv=` on an `I` line and a raw GCD SERIES id
  in `gcd=` (instead of `gcd=s<series>`) are the two commonest checker-caught slips — the `I` wants the issue row.

- C-031..C-033 (Image / IDW trade shelves): the matches-our-holdings collected-edition trap dominates trade-only
  Image shelves — the run wins; `lookup.py --contains` recovers the English "Outcast by Kirkman & Azaceta" (CV 75114 /
  GCD 81445) when the packet's probes return only foreign editions; genuine different-comics conflations still
  exist (Skyward: Action Lab 2013 vs Image 2018; Zero: Matsumoto vs Kot) — the years and publisher decide.

- C-034..C-036 (Marvel current ongoings, big manga runs): a 2022-2025 Marvel ongoing shelf is one clean per-file
  CV+GCD pair plus 1pp MikeNY76-Empire variant packs (ratio 2-11) — never a second run; the bare-title legacy
  volume (Avengers 2128, Captain America 2400, Fantastic Four 2045, Hulk 7053, Deadpool 6000, Daredevil 2190) is
  stamped across them — GCD's title-exact row is the reliable second leg, and when the run's own 2020s volume is
  not yet in CvVolume write cv=- (Phase C.1 fetches it), never the legacy stamp. GCD mints a fresh one-shot per year
  for annual Marvel specials (Crypt of Shadows, Timeless). A licensor / creator prefix recovers manga legs the
  bare-title probe missed (20th Century Boys); scanlation-only manga link the Japanese original at 0.7-0.9.
- On a 60-volume manga run the `I` lines map the shelf's own file order onto the CV/GCD issue-list order (ordinal);
  that is the accepted precision at this scale — the item pass verified it held on 400+ books.

- C-037..C-039 (Krakoa / From the Ashes X-Men, Marvel legacy umbrellas): on a 2019-2025 relaunch shelf the real
  id is often on a SIBLING shelf's "CV local rip" line rather than the shelf's own per-file block — sibling shelves
  of one vertical share the relaunch's id. Marvel Infinity Comics are a total GCD hole (`gcd=-` + provider-missing
  at 0.9). "Marvel Tales" (GCD 1747, retitled twice), "Marvel Super Special" (dropped "Comics" mid-run) and "Marvel
  Graphic Novel" (GCD 2658, 75 unrelated OGNs — not a run, no `C`) are single lines split across shelves by their
  own title changes → merge-with. The 2019-21 "Marvel Tales" one-shots are all round2-stamped onto "Marvel Tales:
  Avengers" 144637 — the dump's exact-title row every time. Long-running legacy ongoings (Marvel Team-Up, Marvel
  Comics Presents, Darkhawk, Exiles) surface as `--all` stored-id collisions with an earlier batch — same-comic
  fragments from several on-disk folders → merge-with, not withheld.

- `lookup.py --issues <volume>` returns the trade's own "Vol. N: <subtitle>" name — title + number matching our label
  is the fastest confirmation of a found CV id. 2024-2025 trades are the single largest genuine no-record cause
  (too new for the static rip / dump); a "Classic" / Epic / Masterworks line with a CV volume and no GCD counterpart
  (or vice versa) takes `gcd=-` / `cv=-` honestly.

- C-040..C-042 (Marvel hero teams): the wrong-relaunch trap at franchise scale — CV 2128 / 2400 / 11492 / 2401 (the
  1960s-2004 Avengers / Captain America / Black Widow / Captain Marvel) stamped on 20+ relaunch shelves' trades; a
  bare-title `lookup.py --year <folder year>` recovered the true volume every time. Epic Collection lines: CV mints
  a volume per book, GCD keeps one series for the line → the singleton shelves share the line's gcd and merge. On an
  `S` line `gcd=` is a SERIES id; on an `I` line an ISSUE id — citing an Epic volume's issue on the S is the commonest
  typo. GCD can split one continuously numbered run at a mid-run retitle with no era change (Cloak & Dagger 1988-91:
  3656 / 14574) — majority per-file match, name both. A file's ComicInfo Web id decides a same-title collision
  (Avengers Infinity 2000 mini vs Hickman's 2013 "Infinity" HC).

- C-043..C-045 (Marvel D-I): a DEGENERATE stamp can cover ~470 files across several unrelated shelves (CV 150441
  "Iron Man Epic Collection: The Crossing" on S9575 and S97887-97891) — any id shared by hundreds of files of
  different titles is the stamp, never a leg. Epic Collection singleton shelves need explicit `F merge-with` chains
  between the siblings that share the line's GCD id (the checker enforces the merge). A ComicInfo Web id equal to a
  CV issue id is a 1.0 on any ripper.

- A collected line's own issue rows carry the trade's SUBTITLE as the issue title, so once one book of a line is
  identified, `--issues <volume>` or `gcd_issue WHERE series_id=?` resolves the whole Vol. 01-N run in one query —
  "own container id not probed" is never a valid no-record.

- R-025 (Opus): the GCD dump's `gcd_reprint` + `gcd_story` tables answer "what does this trade collect" outright —
  join story → reprint → origin issue → series and the `C` range falls out (Elektra #7-22, Casanova Luxuria #1-7,
  all six Criminal ranges); `gcd_series.notes` states ranges in prose. A GCD line row with `year_ended` NULL and a
  count BELOW our holdings is NOT coextensive when the provider files the missing books under their own row
  (Casanova 148774 = 3 vols + Acedia 236165) — two lines, not staleness. Before `F wrong-cv-link` on a revisit read
  Series.CvVolumeId: the shelf may already carry the right id and re-flagging would clear it. A stored id can belong
  to a series holding NO comic files (outside the checker's population) — note it, never merge.

- C-046..C-048 (Marvel cosmic / Masterworks / Facsimiles): the `_Marvel Facsimile Editions` folder carries a
  round2-folder stamp (GCD 185205, a Panini France Moon Knight) on all 31 facsimile singletons — never a leg. Marvel
  Masterworks lines: CV mints no line volume (per-file picks are legacy-issue artefacts on the original series);
  GCD keeps one collected-edition series per line (sometimes a hardcover and a paperback round) — the Epic
  Collection shape catalog-wide. A UK weekly reprint misfiled under the US original's folder is 100%-stamped with
  the US run even when ComicInfo asserts the UK edition (Marvel Super-Heroes 1979) — ComicInfo Publisher decides.
  `lookup.py` by the exact CV record name recovers GCD rows the packet's dump block missed.

- C-049..C-051 (Marvel N-T): Amazing Spider-Man's legacy run spans FIVE shelves sharing CV 2127 while GCD splits by
  era (1570 / 11288 / 92892) — merge-with chains satisfy the shared-id gate; Thor likewise (CV 2294). A creator-name
  folder ("Dan Slott Spider-Man") can bundle four real runs — its conflated flag is TRUE (R + split). A Spider-Man
  "OGNs, Minis & One-shots" folder shares one round2-folder stamp (GCD 65039 "Hooky") wrong on every singleton.
  Kids' tie-ins (Spidey and His Amazing Friends Golden Books) and prose novels are total CV/GCD holes — honest
  provider-missing. An Epic Collection singleton whose STORED CV link names a different volume than the file on the
  shelf (S881) is a data mismatch → R, not a stale flag.

- C-052..C-054 (X-Men family): the per-file CV matcher stamps a FOREIGN publisher's same-title volume across whole
  franchises (Panini Comics "Wolverine" 69993 on nearly every Wolverine / X-Force / Cable / X-23 vN shelf; Panini
  España "X-Men" 56940 on unrelated X-Men v3 trades) — `lookup.py --year <folder year>` recovers the English id
  every time. The "_X-Book Epic Collections" folder: CV per book (issue id in the packet), one GCD line row per
  franchise, round2-folder stamps it AND it is right ("the dump names no other series") — 26 singletons chained by
  merge-with. The classic X-Men / Uncanny X-Men Annual line (#3-21, 1979-94) has no unified record on either leg →
  R + split-needed, duplicate fragments merge-with it.

- `cvref.normName` drops "the / a / an / of / and" everywhere, not just leading articles — a word-preserving
  normalizer misses ~90% of exact matches; replicate cvref's tokenizer when matching titles by script. A bare
  trailing-number fallback (trade "01" → run issue #1) is safe only for mislabeled single-issue files, never for
  "Vol." / "Book" / dash-subtitled titles (it matched an 824pp omnibus to issue #1). One CV issue id reused across
  two differently titled books on a shelf is a sign of two eras, not one book. A digital "Collection Book NN"
  bundle is a different catalog shape from a "Vol. NN" trade and is often uncatalogued — honest no-record.

- C-055..C-057 (Marvel V-X): "Flashback -1" (1997) and "AU" (2013 Age of Ultron) tie-ins are the host ongoing's OWN
  numbering (GCD has literal "-1" rows), never a separate volume. Modern "Vol. NN - Subtitle" trades often have
  their own count-1 CV volume with no GCD row — honest gcd=-. `F wrong-cv-link` is its own line, never text inside
  the S clause (the checker rejects it).

- C-058..C-060 (Marvel covers, Millarworld, indie / Oni): Millarworld chain-of-minis shelves (American Jesus,
  Chrononauts, Jupiter's Circle) hold co-equal seasons with NO coextensive line — R + split, not the one-leg
  exception, when the only "line" candidate merely matches our own holdings count. Loose OGNs in a flat publisher
  root folder (Oni Press) share one wrong round2-folder stamp ("Strangetown" 53172) — a folder-neighbour artefact;
  ComicInfo Web ids give clean 1.0s there. Publisher-change continuities (Deadworld Caliber → Arrow, Lenore SLG →
  Titan) keep one CV volume while GCD splits by era — majority per-file era on the S. A `\__Skottie Young Covers`
  singleton carries a round2 stamp to "Infinity" 75977 — never a leg.

- R-026 (withheld pairs): a shelf that is only a 1pp variant-cover rip IS the mini it covers — merge-with when the
  mini's shelf exists, else the run's ids + partial-rip. A "_Minis & One Shots" / "_Deluxe Editions" bucket filed
  beside a numbered run usually carries that run's volume as its stored link — the bucket is never the run (R +
  split + wrong-cv-link). A withheld id whose partner's link was already cleared has no collision left — write it
  plainly. A GCD publisher-era split that SUMS to ComicVine's count (Deadworld 9 + 17 = 26) confirms the spanning
  CV volume; the majority era by FILE count takes the S. Two shelves split by a possessive ("The Tick" / "The
  Tick's") are one comic. A legacy-renumbered tail (#300-304) is not a second `C` range on the same run — an `N`.

- C-061..C-063 (Valiant, Vertigo, strips): newspaper-strip yearly webrip compilations (Garfield, Nancy, Prince
  Valiant, Mark Trail …) are systematically provider-missing — CV/GCD index the comic-book series, not a strip
  archive. An ongoing anthology split across per-story shelves by design (TKO Shorts, the World of Warcraft
  webcomic) is chained by merge-with, not two comics. Fables is one 161-issue run (CV 9723 / GCD 10549) across seven
  sibling shelves → all merge into one. A coincidental CV volume-id / issue-id collision can slip past the
  checker's existence test — an `I` line's `cv=` must come from `--issues`, never from the volume list. And the
  hard S-gate on an open flag is NEVER a reason to write R: S + stale-flag when the shelf is one run or a
  coextensive line (a reader refused ten such shelves and was sent back).

- C-064..C-065 (Marvel event chronologies, the tier C tail): GCD indexes Zenescope's year-restarting "Grimm Tales
  of Terror" as "Volume N" series per year (82049 / 93359 / 111761 / 123675). On "_Marvel Major Event Chronology"
  tie-in shelves the per-file GCD matcher reuses one franchise-wide round2 stamp (Savage Avengers 144638 on six King
  In Black singles) — the dump's exact title + year row every time. 2024 "Blood Hunt" one-shots are 1pp
  variant-cover packs at ratios up to 8 and still identify cleanly by the exact-title pair.

- TIER D (D-001..D-003, Opus): the dominant shape is the ONE-LEG singleton a single catalog indexes — `provider-
  missing` is the normal verdict (116 of 360). A `#DC Events` / read-order folder shelf is usually ONE ISSUE of a
  long run — the filename's leading number IS the issue number; always a merge, never a comic. Fan compiles
  announce themselves ("by FraBig", "DCP Archive Edition", "DCP Essentials", "(Pencils only)") → R + not-a-run; a
  "Maximum Colour Edition" is a printing nobody indexes and takes the published row's I. The Archie-holiday split
  runs both ways (GCD one spanning row vs CV one per year for CBLDF Liberty Annual; the reverse for 52: World War
  III) — take the provider whose row is the SHAPE of the shelf. A barcode is the cheapest 1.0 (5 of 6 EAN-13s
  matched `gcd_issue.isbn`). Licensed non-comics (art books, readers, Lego giveaways, card compendia) and 2022-25
  ComiXology Originals are holes in BOTH catalogs. The packet's "CV candidates" block is near-useless on tier D (the
  probe falls back on the parsed key = the folder's words) — re-probe the file's own full title every time.

- TIER D (D-004..D-006): one CV volume per LINE vs one GCD row per BOOK is what cuts a tier-D folder into shelves
  (Maakies, Black Moon Chronicles, Wunderwaffen, Ekho, Moebius Library) — `--issues <line volume>` resolves the whole
  folder by subtitle; the reverse (GCD keeps the line, CV per book: Don Rosa Library, Complete Crepax, History
  Comics) → cv=- on the S, per-book CV issues on the I. 2022-25 Fantagraphics / D&Q / First Second / Dark Horse
  singletons are past the rip's horizon → GCD-only cv=- + provider-missing (~150 of 360). A GCD row with count 0 and a
  "do not add issues" note naming its constituents IS the finding (R + split with those ids); a GCD row with zero
  indexed issues but the right title still carries the S, the I takes `gcd=s<series>`. GCD splits a run at a mid-run
  trademark retitle (Tommy Gun: Wizards 149904 / Machine Gun: Wizards 153050 vs CV's one 120928). LEAD RULING: when
  the ONLY catalog record of a comic is a third-territory licence (a German Splitter edition of a French album whose
  original is in neither catalog) link it at 0.7 + provider-missing — the review queue, not an applied identity —
  so the web lane gets a concrete question instead of a refusal.

- TIER D (D-007..D-009): the `Graphic Novels` bucket's publisher sub-folders are TRADE houses — GCD indexes them,
  the CV rip largely does not (cv=- gcd=<row> + provider-missing at 0.9), and GCD mints TWIN rows for
  HarperCollins / Andrews McMeel / Abrams / Scholastic books more often than not (0.9). Heavy Metal's 1977-79
  album line is in both catalogs under the ALBUM's own title — the `Heavy_Metal-19NN-PR-` prefix hides it; probe
  the bare title. A `\Covers` folder of 1pp index sheets ("84Monthly.cbz") is R + not-a-run (unlike variant-cover
  packs, which identify their mini). Files tagged "(compilation)", "(panels)", "Portfolio", "(short)", "from
  <anthology> #NN" are reader extracts → not-a-run. Search `gcd_issue.barcode` on the 11-digit UPC core, not only
  the EAN-13 / ISBN — it found four barcodes the full-code query missed. `cvref.normName` drops "the" everywhere:
  probe "Star Trek: Next Generation".

- TIER D (D-010..D-012): Heavy Metal's seasonal companion is CV 35025 "Heavy Metal Special" / GCD 77284, indexed by
  the special's SUBTITLE (40 of 40 matched); on a per-issue magazine shelf the cover MONTH pins the file to one
  issue on each leg. `SELECT * FROM gcd_series WHERE publisher_id=?` and a LIKE on `cv_vol.name` beat every
  normalised probe when the key is a truncation. Three spellings hide a record from `norm_name`: an accent
  (Murciélago), a slashed O (Gødland), an inner space ("How Toons"). A cluster's leg can appear on exactly ONE
  member's candidate block — check the siblings' packets. An exact probe returning only foreign editions is not a
  finding — `--contains` first (Lorna: six English albums; D-009 refused two of them → R-027). Manga on tier D splits
  three ways: an English line both legs hold (0.95), a scanlation of an unlicensed work (Japanese original at 0.9),
  a work in NO catalog (R; the tell is an MU id and nothing else). A barcode can correct which SERIES a book belongs
  to (Obscure Cities → Alaxis Press, not the IDW line). MAD's 2024-25 digital-mobile shorts are a total hole.

- TIER D (D-013..D-015) — LEAD RULING confirming the reader's tie-break for a single trade alone in a `<Title> vN
  (<year>)` run folder: (a) both legs hold a multi-volume line coextensive with the shelf → the LINE at 0.9, the run
  on N; (b) otherwise the RUN, with `F merge-with` into the run's landed shelf (a lone trade is an edition of its
  run, never a second comic); (c) otherwise the book's own count-1 record. One published work shipped as separate
  printed objects is indexed that way by GCD (Building Stories: 14 lettered rows vs CV's one volume — 0.9 by
  construction). An album title can be an ISSUE row no series probe reaches (Asterix Miscellany #29 / #31 / #33 of
  the Hodder line) — search `gcd_issue.title` / `cv_iss.name`. Spellings that hide records: superscripts (Alien³),
  periods (N.H.K.), an apostrophe year ("Cable '99" = "Cable 1999"), a doubled letter, an "∞" issue number, a literal
  translation of a Japanese title. ComicVine carries Japanese originals GCD lacks — probe the Japanese title before
  conceding a scanlation shelf. Marvel renumbers sub-series into one volume (Ultimate Comics Avengers 1/2/3 = #1-18).

## Report back (≤ 25 lines)
Per batch `{shelves, S by confidence, R, F by flag, I}`; conventions learned (one line each — they go into this
ledger); systemic findings (wrong-link clusters, a packet block that misled, a shelf tiered wrong); any
instruction-vs-code conflict presented, not resolved. If a Stop hook repeats a finding, answer once and end.
Never write to books.db, never run a `books-*` verb or the host exe, never open a book archive. Use the Write tool
for the decision file (bash mangles backslashes). Run `python docs\books\identity\tools\check_identity.py <batch>`
until it prints 0 failing.
