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
N <sid> note worth keeping
```
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
  TPB record while a run exists → `F wrong-cv-link`. The trade's own container record may go on an `I` line
  (`I <itemId> cv=<tpb volume's issue id> gcd=<tpb issue id>`); it is optional. Say which case applies in the clause.
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

## Report back (≤ 25 lines)
Per batch `{shelves, S by confidence, R, F by flag, I}`; conventions learned (one line each — they go into this
ledger); systemic findings (wrong-link clusters, a packet block that misled, a shelf tiered wrong); any
instruction-vs-code conflict presented, not resolved. If a Stop hook repeats a finding, answer once and end.
Never write to books.db, never run a `books-*` verb or the host exe, never open a book archive. Use the Write tool
for the decision file (bash mangles backslashes). Run `python docs\books\identity\tools\check_identity.py <batch>`
until it prints 0 failing.
