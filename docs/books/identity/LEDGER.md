# Conventions ledger (tagged) — context for reading, never a rule applied across a folder

Moved out of READER_BRIEF.md on 2026-09-22 (TOOLS_TODO 20). **Readers do not read this file whole**: every batch
file opens with a `## Conventions for this batch` block holding the entries whose tags match its shelves
(`tools/ledger.py` — the match rule is in its docstring). The lead adds new conventions HERE, each under a tag
line, and the next emitted batch carries them.

Entry format: a tag line `[L-NNN] <tag> <tag> …`, then the entry text verbatim, then a blank line.
Tag namespaces — SCOPE: `pub:` publisher / imprint · `folder:` folder shape · `kind:` kind of comic · `tier:` ·
`pass:` (item / revisit / s2). TOPIC: `sig:` a packet signal. An entry matches on its MOST SPECIFIC namespace
only (pub, then folder, then kind, then tier/pass, then sig); an entry naming both a publisher and a folder shape
needs both. So a DC relaunch lesson reaches DC batches, a bucket-folder lesson naming no publisher reaches every
batch with a bucket folder, and a topic-only lesson reaches every batch showing that signal. `ruling` = copied
into the brief's Rulings (applies everywhere), not attached to batches. Use only slugs `ledger.py --stats` lists —
a tag no shelf can produce makes the entry unreachable, and selftest fails on it.


[L-001] ruling sig:probe
- `<Publisher>\<Title> (<year>)\<Title> NNN (<year>) (digital) (<ripper>).cbz` is the house shape; the name carries
  title, start year and ladder.

[L-002] pub:ac
- AC Comics: folder `<Title> (001-0NN)(YYYY-YYYY)`; GCD's `YearBegan-YearEnded` reproduces those years; revivals a
  decade later are the same GCD series (`1989-2000`); ratio > 1 is duplicate rips.

[L-003] folder:events folder:sequel-subfolder pub:ablaze pub:action-lab pub:aftershock
- Aftershock / Action Lab / Ablaze: 4–5 issue minis; sequels in `v2 - <Subtitle>` subfolders → two runs;
  Ablaze's Cimmerian line numbers its subfolders in READING order (`05 …`, `06 …`), not issue order.

[L-004] folder:bucket pub:archie
- Archie: bucket folders (`_Archie One-shots`, `_Betty & Veronica`) name no run; look at the files.

[L-005] folder:variant-covers pub:avatar
- Avatar: `\covers` variant packs (3–4pp) double the file count and collide every number without making two runs.

[L-006] pub:2000ad pub:idw
- 2000AD: progs, Fleetway reprints and IDW runs can all number from #1 on one shelf.

[L-007] folder:bucket
- Crossovers published as separate one-shots (Fear on Four Worlds) are separate GCD records per title.

[L-008] kind:ogn kind:trade-only pub:boom sig:trade-link
- Boom! trade shelves: both legs carry TWO same-name records — the run and a collected series whose count equals
  the BOOKS we hold; the stored link is the trade record about half the time (`F wrong-cv-link`, S line = the run).
  Judged ranges decide (#1-4 + #5-8 + #9-12 = 12 = the run). Exception: a line whose trades are joined by an OGN in
  the same numbered sequence (Goldie Vance vol 5, Backstagers Encore) IS the collected series.

[L-009] kind:foreign pub:cinebook pub:europe-comics
- Cinebook: one ~50pp album per file; both providers hold Dargaud / Splitter / Epsilon / Panini rows of the same
  album — the PUBLISHER column is the only test. GCD's span = the file years.

[L-010] folder:variant-covers kind:new pub:dc
- DC Black Label 2024-25: 1pp variant covers in `\Variant Covers` double the file count; not a second run.

[L-011] folder:events pub:dc
- DC event folders: the leading number is a READING-ORDER position, and the same book is filed again under its own
  title folder — n issues × 2 files is one run.

[L-012] pub:comixology pub:dark-horse
- Comixology Originals (Best Jackett): absent from GCD; GCD holds only Dark Horse's later print repackaging at a
  different count — `gcd=-` or the reprint with the clause saying so.

[L-013] sig:probe
- GCD spells `&` where CV writes `and` (Flashpoint: Deathstroke, Frankenstein) — a dump lookup that misses may hit
  with the other spelling.

[L-014] sig:probe sig:round2
- `round2-folder` / `round2-series` links: wrong in 23 of 23 checked; four would have MERGED two shelves in one
  batch (Rose and Thorn ← Eternity; Cybernetic Summer ← Dog Days of Summer). Where the dump does not name the
  right per-title series, write `gcd=-` + `F provider-missing` rather than take the hull.

[L-015] folder:events pub:dc
- DC `#DC Events` read-order folders hold a SECOND rip of issues that also live on the title's own shelf — the
  source of nearly every DC ratio 2.00; residue, not a second run. Event umbrellas (GCD "Convergence", 9) get
  handed to two-issue tie-in shelves — the hull shape.

[L-016] sig:comicinfo
- Glorith rips' ComicInfo `Web` ids are NOT GCD issue ids in our dump (29 checked: missing or unrelated). Never
  claim 1.0 from one. A ComicInfo `Volume` holding a 6-digit number IS a ComicVine volume id (Lex and the City =
  162184) — a genuine 1.0 assertion.

[L-017] kind:digital pub:dc
- Digital-first DC titles carry two CV volumes (chapters vs print issues); a trade's page count picks which.

[L-018] kind:foreign pub:dc
- Stored links that are the PARENT ongoing (Harley Quinn Road Trip → the ongoing), a different publisher
  (Justice → Panini; Inferno → Games Workshop), or an empty stub (Arkham Horror) → `F wrong-cv-link`.

[L-019] kind:digital pub:dc sig:probe
- Digital chapters vs print issues: CV indexes both as two volumes (Injustice 2: 72 chapters vs 36 issues); GCD
  usually only the print one. Where our files are chapters, `gcd=-` + `F provider-missing` beats a count that lies.

[L-020] kind:archive kind:collected-line pub:dark-horse
- Reprint LIBRARY / omnibus lines over a run split across publishers (Fear Agent Final Edition, Kabuki Library,
  Madman Library) have no single run record → the collected line at 0.9 with an `N` naming the underlying run.

[L-021] pub:dark-horse pub:disney pub:dstlry pub:dynamite pub:fantagraphics pub:idw
- The Disney folder is publisher-agnostic (Joe Books, Dark Horse, IDW, Dynamite, Fantagraphics, Yen Press on one
  shelf) — the provider's publisher column is the only test. DSTLRY filenames' "(N covers)" is a cover count.

[L-022] pub:dc
- One-shot "Edition" books (Absolute Wonder Woman Noir / Outlaw Edition) can carry each other's stored link —
  check the file's own title against the record's.

[L-023] kind:trade-only pub:dark-horse pub:dynamite sig:trade-link
- 23 stored CV links in three batches pointed at the TPB record (count = the books we hold) instead of the run
  — heavy on trade-only Dark Horse / Dynamite shelves. Expect it; `F wrong-cv-link`, `S` = the run.

[L-024] kind:foreign pub:europe-comics
- Europe Comics (digital English imprint of Dargaud / Dupuis / Le Lombard): ComicVine has a volume per shelf; GCD has
  NO Europe Comics rows. **Eric's ruling: the original-language GCD row is the right comic in the wrong language —
  link it** (at 0.9, naming the language edition) rather than `gcd=-`; when the dump holds two language editions,
  take the original publisher's.

[L-025] folder:bucket kind:trade-only pub:dynamite pub:idw sig:trade-link
- Dynamite and IDW mint a same-title count-1 record a year after every mini — the trade; on trade-only shelves
  the stored link is that record a third to a half of the time. IDW LIBRARY / ARCHIVE lines follow the reprint-
  library rule. Red Sonja's `_Trades, minis and one-shots` is a bucket folder, not a run.

[L-026] kind:collected-line kind:trade-only pass:item pub:image sig:trade-link
- Image trade-only shelves carry THREE records for one comic: the RUN (CV volume + a GCD dump row), a GCD
  "Collected Series" row whose count equals the BOOKS we hold, and a CV count-1 volume named after the trade's
  subtitle. The per-file GCD leg and the stored CV link both tend to be the COLLECTED record — and their
  agreement is NOT two legs, because both merely matched our holdings. The dump line names the run: read it.
  Genuinely volume-published lines (Kill Six Billion Demons, Popgun, Norroway, Blood Stain, Swing) have no run
  — the collected series IS the identity at 0.95. A later volume collecting a SEQUEL series → 0.9, residue named,
  and the book still gets its `I` line on the collected series that covers it.

[L-027] kind:foreign pub:marvel
- Foreign-language stored links are a cluster (Berserk → Glénat's French volume while our 41 books are Dark
  Horse's; The Marvels → Panini España; Avengers → Panini Verlag): the English pair is in the rip/dump every time.

[L-028] folder:variant-covers pub:marvel
- Marvel `\Variant Covers` 1pp packs (always of #1) are the whole of every ratio 1.2–2.0 on modern Marvel shelves.

[L-029] kind:trade-only pub:dark-horse pub:image pub:marvel sig:probe sig:trade-link
- On trade-only Marvel / Image / Dark Horse shelves the "CV linked" block is the block that misleads (it is the
  collected record); `lookup.py` by the run's name finds the run when the packet names only the collected record.

[L-030] sig:collision
- One folder split into two shelves by a subtitle in one filename (Analog, Witch Doctor) → `F merge-with=<sid>`.

[L-031] kind:collected-line kind:trade-only pub:dc pub:marvel sig:collision sig:legacy-stamp sig:trade-link
- A trade of a run that lives on ANOTHER shelf (a Batman Vol. 2 trade whose run is the Batman 2016 shelf):
  `S` at the run's ids + `F merge-with=<that shelf's sid>` ONLY when that shelf is unambiguous (one title, one
  numbering) and the trade's range falls inside its ladder. When the run is split across legacy-numbered shelves
  (Amazing Spider-Man), keep a multi-volume collected LINE both legs carry at 0.9 + `N` naming the run, and a
  lone count-1 TPB record at 0.7 + `N`. Never link a trade shelf to an ongoing it would merge five shelves into.

[L-032] sig:comicinfo
- A Marika-Empire rip's ComicInfo `Web` id IS a ComicVine ISSUE id (21/21 resolve to the stored volume) — the
  1.0 shape. The ripper decides: Glorith's Web ids are not GCD issue ids. Check one against the rip before
  claiming 1.0 on a new ripper.

[L-033] pub:marvel sig:collision sig:probe
- Krakoa-era Marvel folders are cut into several shelves by the CREATOR name in trade filenames (New Mutants v4
  → Hickman / Brisson / Vita Ayala; "X-Men by Gerry Duggan"): trades of ONE run in ONE folder → the run is the
  identity, siblings take `F merge-with`; `lookup.py` by the run's plain name supplies ids the packet lacks. A
  stored link carried across by the creator name alone (X-Men by Duggan → "Marauders by Duggan") is `wrong-cv-link`.

[L-034] folder:events pub:marvel sig:round2
- `_X-Men Complete Chronology` / `_Marvel Major Event Chronology` folders re-file the same books a second and
  third time — every ratio 2.00 there, never a second run. Crossover blocks get handed one member's series by
  `round2-folder` (four Age of Apocalypse shelves → "X-Man", a 76-issue hull).

[L-035] kind:digital pub:marvel sig:probe
- Marvel Digital Comics Unlimited exclusives (8–12pp `(MDCE)` webrips, Infinite Comics 56–104pp): ComicVine
  indexes them, GCD does not → `gcd=-` + `F provider-missing`; the page count is the identifier.

[L-036] folder:skottie folder:variant-covers pub:marvel
- A shelf that is ONLY 1pp variant covers (Skottie Young) still identifies its comic → run ids + `F partial-rip`.

[L-037] ruling
- LOCG bridges named a DIFFERENT comic in six cases so far ("'68", Firebreak, Moon Knight Saga, Penny Dora,
  Quarry's War, Invasion) — a support count is no name check; never cite a bridge as a leg.

[L-038] ruling sig:stale-flag sig:trade-link
- A provider count ABOVE our holdings on a series whose end year is `?` is staleness, not residue (0.95 stands);
  a count BELOW our ladder, or a count-1 stub, is a real doubt (0.9).

[L-039] pub:dc pub:image
- A run that changed publisher mid-stream is TWO GCD series while ComicVine keeps one volume (DV8: Image
  #1-25 + DC for the rest) — take the majority GCD leg and name the split.

[L-040] folder:bucket folder:events pub:dc pub:papercutz-nbm pub:scout pub:titan pub:valiant sig:twin
- NBM / Papercutz album lines and Scout one-shots carry TWIN adjacent GCD rows for one book (120209/120210) —
  cap at 0.9 and name both. Titan's Doctor Who "Year N" volumes number continuously across a Doctor (a ladder
  starting at #6 is complete). A publisher's read-order crossover folder (Titan `Lost Dimension`, Valiant
  `_Chronological Valiant`) = DC's `#DC Events` shape.

[L-041] sig:trade-link
- The GCD per-file leg can be the collected record even on a shelf holding NO trade (Robotech Remix).

[L-042] pub:archie ruling
- **A ladder that runs past the linked volume's COUNT is the cheapest mis-link test** ("Jughead v2 (1987)" linked
  to a 45-issue volume with a ladder 150-211 = Archie's Pal Jughead Comics, CV 20115 / GCD 13247).

[L-043] sig:probe
- "The dump names no other series" often means the packet probed the wrong SPELLING: re-probe `lookup.py` with the
  CV record's own title (found a GCD series on 27 of 450 tier-B shelves).

[L-044] folder:bucket pub:archie sig:split
- Archie files annual holiday one-shots as a NEW series every year in both providers — a shelf holding four years
  is four comics with no spanning record (`R` + `split-needed`); the year in the FILENAME is the selector.

[L-045] kind:trade-only pub:2000ad sig:trade-link
- Rebellion's Judge Dredd TPB line: one record per BOOK in both providers — a one-file shelf with CV count 1 and a
  same-year GCD count-1 row is normal indexing, not the "matches our holdings" trap.

[L-046] pub:action-lab pub:aftershock pub:small-press sig:probe
- Small-press American floppies (Action Lab, Aftershock minis, Behemoth) are thin in GCD → `provider-missing` on
  the GCD side is common and honest.

[L-047] kind:trade-only pub:boom pub:image sig:trade-link
- BOOM! trade shelves = the Image three-record trap: run + same-title count-1 record a year later in BOTH
  providers = the trade; the stored CV link IS that trade on 60 of 450 shelves. Jim Henson's Storyteller: `"The
  Storyteller: X"` count 4 = the RUN, `"Jim Henson's The Storyteller: X"` count 1 = the trade, all eight titles.

[L-048] kind:digital pub:comixology pub:dark-horse
- **ComiXology Originals is a provider SPLIT, not a missing leg**: ComicVine indexes the digital serial under the
  creator's imprint (count 4–6); GCD holds only Dark Horse's print repackaging (count 1–3, years later). Pair them
  at 0.9 with the clause saying so.

[L-049] kind:kids kind:strip pub:archie pub:boom
- BOOM's licensed-kids folders (Ben 10, Ice Age, Garfield, BOOM! Box Mix Tape) file a new record per book/year in
  both providers — the Archie holiday-annual shape; the year in the filename selects.

[L-050] sig:probe
- When the packet's lookups miss, re-probe `lookup.py` WITHOUT a licensor prefix ("Jim Henson's"), with the exact
  punctuation of the record ("Good Apollo. I'm a Burning Star IV"), as one word ("Ruinworld"), or by the
  abbreviation the publisher uses ("HSE").

[L-051] pub:bongo pub:dc pub:small-press sig:probe
- GCD spellings that hide a hit: Bongo's annual is plainly "Treehouse of Horror" (5435); Miller's 1983 series is
  "Rōnin" (2721); Comico's Primer is "Primer" (14288). DC's Blackest Night revival issues sit in the ORIGINAL
  series (Power of SHAZAM! 5246 runs 1995-2010).

[L-052] pub:dc sig:stamp sig:twin
- The per-file CV matcher picks the LEGACY volume for every New 52 shelf (Batman 1940, Wonder Woman 1942, Nightwing
  1996, Catwoman 1993) because it matched by issue number — the stored link on a 2011 shelf that names a 1940
  volume is `wrong-cv-link`, the 2011 volume is in the rip.

[L-053] kind:ogn pub:dc sig:trade-link
- Every DC Ink / Zoom OGN pairs one CV count-1 volume with one GCD count-1 series 6-20 pages larger than our rip.

[L-054] kind:archive kind:fan
- Fan-made DCP "Archive Edition" compiles and story-only extracts → `R` (not a published edition), never linked.

[L-055] pub:dc pub:small-press sig:probe
- ComicVine splits ONE line across two spellings of its own ("Batman Arkham: X" vs "Batman: Arkham: X"). GCD
  prefixes titles the packet probes without ("Batman: Catwoman Defiant", "The Blue Beetle" for the 2006 series).

[L-056] pub:archie pub:dc sig:probe
- The Archie-holiday split runs BOTH ways: GCD mints six one-shot series for DC's Earth-Prime week while CV keeps
  one 6-issue volume — whichever provider spans the shelf carries the S line; the other takes `provider-missing`.

[L-057] kind:digital kind:kids kind:ogn pub:dc sig:probe
- DC Zoom / Super Hero Girls OGNs are serialised in CV as 12–15 "chapter" volumes GCD never indexes: the BOOK record
  is the identity, the chapter volume is `wrong-cv-link`. GCD indexes no DC digital-first chapter series and no
  Justice League of America (2006) trades — honest `provider-missing`.

[L-058] kind:digital
- A `(digital-mobile)` vertical reformat has 2–3× a floppy's page count and may match nothing → `R` + `mobile-rip`.

[L-059] pub:dc
- A truncated parsed key reaching a different comic ("Riddle", "Batman - Death", "Once Upon") is `wrong-cv-link`.

[L-060] folder:vn-year pub:caliber pub:dark-horse pub:image pub:marvel sig:stamp
- A digital re-release can cut each collected volume into chapter files restarting at 1 (Dark Horse's 2014-15
  Kabuki: seven `vNN` folders = seven different published comics from Caliber / Image / Marvel; v1 stamped one
  Image volume on all 44 files). Read the chapter files as the runs they came from.

[L-061] pub:cinebook sig:collision
- GCD keeps ONE album series where ComicVine mints a volume per book (Grandville 42860, Polar 88420 — the
  Cinebook shape); sibling shelves split by ComicVine take `F merge-with` naming the GCD line.

[L-062] pub:dark-horse sig:probe
- Dark Horse art books, licensed tie-ins and digital-only shorts are systematically absent from GCD — the
  ComicVine ISSUE id is then the book's only item-level record. GCD hides hits behind "in:", dropped articles,
  licensor prefixes ("Edgar Rice Burroughs' …") and bracketed FCBD contents.

[L-063] kind:foreign pub:delcourt sig:collision sig:probe
- Delcourt / Soleil English digital albums: CV mints one English volume per line, GCD holds only the FRENCH
  original — probe the French title (Prométhée, Les Maîtres Inquisiteurs, Orcs & Gobelins), pair at 0.9 naming
  the language. The Delcourt folder is cut into shelves by a subtitle in one filename (Ekho v03 / v07) and each
  such shelf carried a stored link to a different comic of that name → `wrong-cv-link` + `merge-with`.

[L-064] pass:item sig:twin
- GCD indexes PRINTINGS: the S line takes the first edition both legs agree on; the `I` line takes the printing
  we hold (page counts match the edition's row). GCD issue ids for books come from `gcd_issue` — prefer the
  issue id to the `s<series>` form; use `s<series>` only for twin/variant rows.

[L-065] sig:stamp
- Ladder coincidence: two runs with the same issue COUNT (X 1994 vs X 2013, both 25) fool the per-file matcher;
  the year in the filenames decides.

[L-066] pass:item pub:drawn-quarterly sig:barcode sig:trade-link
- Drawn & Quarterly / one-book publishers: CV count-1 volume + GCD `[en-ca]` row, 0.95 with an `I` line; an ISBN
  barcode on the file is a 1.0.

[L-067] pub:dynamite sig:trade-link
- Dynamite: the same-title count-1 record a year after every mini exists in BOTH providers; the stored link is
  that trade on ~30% of Dynamite shelves and the run is one line below in "CV candidates". GCD indexes no
  Dynamite art book and no Dynamite holiday one-shot.

[L-068] kind:foreign pub:ablaze pub:cinebook pub:europe-comics pub:lion-forge-magnetic pub:papercutz-nbm
- Europe Comics may have an ENGLISH PRINT sister edition in GCD (SelfMadeHero, Cinebook, NBM, Fanfare,
  Magnetic Press, Ablaze) — the same text, not a translation — which is the better second leg than the
  original-language row.

[L-069] kind:archive pass:item pub:dark-horse pub:ec-archives
- Archive / reprint lines (EC Archives, Dark Horse archives) — RULING: when both legs also hold the ORIGINAL
  series, the S line is the original (the run wins) and the `I` line is the archive printing we hold; only
  when the original is in neither leg does the archive line carry the S at 0.9 with an `N` naming what it
  reprints. Do not let the stored link decide which shape you write.

[L-070] pub:delcourt pub:dynamite
- A truncated key inside a deep character folder reaches the wrong relative ("Red Sonja - Red Sitha" → the
  Red Sonja ongoing; "Elle(s)" → Soleil's "Elle") → `wrong-cv-link`. Subtitle repetition inside one ladder
  (Crusade 005-008 = 001-004's subtitles) is four albums ripped twice, not eight.

[L-071] pub:first-second sig:barcode
- First Second (and similar) carry TWO adjacent GCD rows per book that are HARDCOVER / PAPERBACK printings with
  different ISBNs — our rip's PAGE COUNT picks the row (0.95); only when both rows are blank cap at 0.9.

[L-072] sig:probe
- GCD hides behind the SUBTITLE: "The Hunting Accident", "Giraffes on Horseback Salad", "Undesirables" return
  nothing under their full titles — re-probe the SHORT title before writing `provider-missing`.

[L-073] pub:lion-forge-magnetic
- A ripper's filename can carry a publisher's title shape ("H. G. Wells - <title>" = Insight/Lion Forge 2018),
  not the older adaptation the stored link names.

[L-074] pass:item sig:barcode
- An EAN-13 read off the page that equals a GCD issue's ISBN is a 1.0 on the `I` line even when the S line takes
  another territory's record.

[L-075] kind:archive kind:strip pub:gold-key-dell
- An archive of a run that the run's own records do NOT cover (Peanuts Dell Archive collects Four Color issues
  the Dell "Peanuts" run excludes) takes the archive line's records, with an `N` naming the run.

[L-076] kind:collected-line pub:marvel sig:collision
- ComicVine mints a VOLUME PER BOOK on Epic Collection / Masterworks lines while GCD keeps one series for the
  line — that splits one folder into many shelves → `F merge-with` between the siblings, naming the GCD line.

[L-077] folder:facsimile kind:archive kind:foreign pub:marvel sig:round2
- `_Marvel Facsimile Editions`: all 24 single-book shelves carry the same wrong `round2-folder` GCD series
  (185205 Moon Knight, Panini France). Foreign-language legs (Panini España/Brasil/France/Deutschland, a Greek
  edition) are rejected on the publisher column alone.

[L-078] pub:boom pub:dynamite pub:idw sig:trade-link
- IDW is the Dynamite/BOOM shape at scale: EVERY mini has a same-title count-1 trade a year later in BOTH
  providers, and the stored link is that trade on ~1 shelf in 5 (`wrong-cv-link` is the commonest IDW flag). The
  run is the line below it in "CV candidates".

[L-079] pub:humanoids sig:trade-link
- Humanoids' English programme: one book = one CV count-1 volume + one GCD count-1 series (0.95). An ALBUM LINE is
  a CV volume of N whose issue names are the album subtitles while GCD keeps only the one-volume collection —
  that caps the shelf at 0.9.

[L-080] kind:archive pub:idw
- On a reprint-library line (IDW Collection: G.I. Joe / TMNT / Transformers) `gcd_issue` page counts are the
  strongest per-book check — our rips matched volume-by-volume within ±10pp.

[L-081] kind:foreign kind:magazine pub:heavy-metal sig:probe
- Heavy Metal magazine is CV 19498 / GCD 110631 "Heavy Metal Magazine"; the suffix-stripped probe misses it and
  returns only a French Gallimard row — re-probe by the full title.

[L-082] pub:dc sig:comicinfo
- A Glorith-HD rip's ComicInfo `Web` id CAN be a ComicVine ISSUE id of the stored volume (599834, DC 100-Page
  Spectacular) — Glorith's Web ids are never GCD ids, but check them against the CV rip.

[L-083] pass:item sig:barcode
- A file's EAN-13 that equals a GCD issue's UPC (0761941202327 ↔ 76194120232700111) is a 1.0 on the `I` line.

[L-084] kind:foreign pub:idw sig:stamp
- IDW/Hasbro shelves: the per-file CV matcher reaches foreign licences by bare words (Macross Delta on "Delta 13",
  TM-Semic on "G.I. Joe", Kingstone on "Babylon", Cross Cult on "Angry Birds") — reject on publisher.

[L-085] sig:barcode sig:trade-link sig:twin
- `gcd_issue` carries the trade's page count AND ISBN: it matched our rips within ±10pp on 100+ books and told
  two same-title count-1 rows apart (an 8pp ashcan is not a twin of the trade).

[L-086] pub:idw pub:image sig:trade-link
- The count-1 trade-as-stored-link rate is ~1 shelf in 4 on IDW AND Image (101 of 438) — a linker artefact,
  publisher-independent; expect it everywhere a mini was collected a year later.

[L-087] pub:cinebook pub:idw pub:image sig:collision
- The Cinebook shape (one GCD series, a CV volume per book) recurs inside IDW/Image (Godzilla Rivals, Obscure
  Cities, MLP Annual, Cyberforce Origins, Hinges, Image+, Bloodstrike, X-Files specials): siblings take
  `merge-with`; where CV has no series-level volume the S line is `cv=-`.

[L-088] sig:probe
- GCD hides behind a LONGER title too ("… and the Tale of Azkon's Heart", "Holiday Party (One-Shot)"), not only a
  shorter one — probe both directions before `provider-missing`.

[L-089] sig:probe
- A facsimile of a SINGLE issue is its own book; a reprint edition of a whole series is not — the original wins.

[L-090] pub:boom pub:idw pub:image pub:legendary pub:mad-cave sig:trade-link
- Legendary Comics and Mad Cave mint the same-title count-1 collected record beside every mini in BOTH providers
  (the Image/IDW/BOOM shape); the stored link is that record on half their shelves, and its `ratio 0/N` LOOKS like
  a missing rip rather than a wrong record.

[L-091] kind:digital pub:lion-forge-magnetic
- Lion Forge / Magnetic Press is a FORMAT split, not a missing leg: ComicVine serialises each European album into
  N digital chapters while GCD holds the one collected book (Meka 4↔1, Naja 10↔1). Pair them at 0.9; when our file
  is the whole album the stored link is the chapter volume.

[L-092] pass:item pub:caliber pub:dark-horse pub:dc pub:image pub:jinxworld
- Dark Horse / DC Jinxworld re-issues of Bendis's 1990s Caliber/Image books (Jinx, Torso, Goldfish, Powers) are
  PRINTINGS: the S line takes the first edition both legs hold, the `I` line the Jinxworld printing.

[L-093] pub:dc pub:ec-archives pub:mad sig:comicinfo
- GCD labels every modern MAD book "EC" where ComicVine and ComicInfo say DC — the same publisher's imprint, not a
  rejection.

[L-094] pub:idw
- `(of NN)` in a filename can contradict both providers (Transformers #1 40th Anniversary: count 1 in both legs,
  file says "01 (of 04)") — that residue caps at 0.9.

[L-095] sig:probe
- A folder that is a ripper's READING ORDER over several separately published arcs (The Ride, Revolution) has no
  spanning record — the ladder is arc numbers, not issue numbers.

[L-096] sig:probe
- GCD's exact filing recovers legs the probe misses: singular/plural ("Greeting", "Return"), two words ("Super
  Hero"), a spelled ordinal ("Fortieth"), a dropped subtitle, or the colon form ("Jinx: Torso") — try each before
  `provider-missing`.

[L-097] kind:foreign sig:stamp
- MANGA shelves: the per-file CV matcher picks a FOREIGN-LANGUAGE volume on about half (Carlsen/Egmont/Cross
  Cult, Glénat/Ki-oon/Kazé, Panini España/Norma/Ivrea, JBC, Altraverse) and the STORED link is the JAPANESE
  original on most of the rest — the English pair is in "CV local rip" + "GCD dump" every time.

[L-098] kind:manga sig:probe
- GCD hides English manga behind a LONGER title far more than a shorter one ("Oishinbo a la Carte", "Neon
  Genesis Evangelion 3-in-1 Edition", "Fairy Tail S: Tales from Fairy Tail") and occasionally a typo ("Fairy Tale:
  Fairy Girls"); the mirror exists too (no ComicVine Viz volume for JoJo Parts 1-3 while GCD has all six → `cv=-`).

[L-099] sig:stamp
- A spin-off family carries the PARENT run per-file (all eight Fairy Tail side shelves stamped 46777) — reject.

[L-100] kind:manga
- A publisher change mid-line (Vertical → Kodansha USA) splits the GCD leg while CV keeps one volume (Ajin, CITY,
  Miss Nagatoro): one S line, the GCD row that holds the wider span, `N` naming the other.

[L-101] sig:twin
- Page count tells GCD twins apart (a 32pp sampler vs the 256pp anthology); a count-0 GCD row (236534 "Cells at
  Work! Lady") is an empty stub, not a leg.

[L-102] kind:foreign kind:manga pub:europe-comics
- Where GCD has no English row for a manga, readers have linked the same work's Japanese original (else the
  European edition) at 0.9 with the language named — the Europe Comics ruling extended to manga, CONFIRMED by
  Eric 2026-09-10: the right comic in another language beats no leg.

[L-103] pub:markosia sig:comicinfo
- A LeDuch (Markosia) rip's ComicInfo `Web` id IS a ComicVine ISSUE id — 11 of 11 resolved to the stored
  volume's own issue (Androsaurs' resolved to #2, the v02 file we hold): the 1.0 shape.

[L-104] kind:manga pub:dark-horse pub:marvel pub:star-wars sig:probe
- Star Wars manga needs BOTH spellings: ComicVine files Dark Horse's 1999 adaptations as "Manga Star Wars:
  <Film>", GCD as "Star Wars: … — Manga"; neither probe finds the other.

[L-105] kind:manga kind:ogn pub:dark-horse
- Scanlation shelves are readable and the filename's TITLE and YEAR decide them (Usagi Drop ≠ the English "Bunny
  Drop"; 24-30pp Slayers floppies = Central Park Media, not Tokyopop's graphic novels).

[L-106] pub:marvel pub:star-wars sig:trade-link
- The count-1-trade-as-stored-link artefact runs at Marvel too (14 Star Wars shelves in one batch); a collected
  series whose count equals exactly our two books is the matches-our-holdings trap.

[L-107] kind:digital kind:manga pub:markosia sig:probe
- Kodansha Comics USA digital-first and Markosia / Digital Manga Publishing are honest GCD holes — re-probe once
  (short, long, licensor prefix), then `provider-missing`.

[L-108] kind:weekly pub:marvel
- A truncated parsed key reaches a wholly different comic ("Original Sin" → the Marvel event, "The Battle" → a
  1972 Chick tract, "Wonder" → a 1942 British weekly) — the filename's full title decides, never the key.

[L-109] kind:collected-line pub:marvel pub:star-wars
- RULING (lead, 2026-09-10, from S16653 Star Wars Legends Epic Collections): the ARCHIVE ruling (take the run's
  records) applies only when the collected line has NO record of its own. When either provider carries the
  line (Epic Collection, Omnibus, Masterworks…) the LINE wins at 0.9 with an `N` naming the run — never link a
  trade shelf to an ongoing it would merge into.

[L-110] kind:digital pub:marvel sig:probe
- Marvel Infinity Comics are a TOTAL GCD hole (~115 shelves): ComicVine indexes every serial as its own volume,
  GCD indexes none, and every GCD hit is the PRINT comic of a similar name, years off. `gcd=-` +
  `provider-missing` at 0.9 is the honest shape; probing harder buys nothing.

[L-111] pub:marvel pub:star-wars sig:trade-link
- Star Wars "Marvel Edition" digital trades (Kileko/Zone/Shan-Empire): both providers mint a same-title count-1
  record the year the trade shipped beside the 4-6 issue run; the stored link is the trade on 36 of 150 shelves.

[L-112] pub:dark-horse pub:marvel pub:star-wars sig:trade-link
- Episodes IV/V film adaptations have no series record of their own (they sit INSIDE Star Wars 1977); the 2015
  collected record carries them with an `N`. Episodes I/II/III/VI take the Dark Horse / 1983 Marvel original.

[L-113] sig:comicinfo sig:stamp
- ComicInfo `Volume` holding a 4-digit ComicVine volume id (not a year) is a 1.0 assertion like the 6-digit
  case (Ka-Zar v2 1974: `Volume 2692` + `Count 20` = CV 2692) and disproves the per-file matcher's pick.

[L-114] sig:comicinfo
- Marika-Empire and Glorith-HD `Web` ids are ComicVine ISSUE ids of the stored volume (7 of 7 here).

[L-115] kind:collected-line pub:marvel
- Creator-named collected lines both legs carry with a multi-volume count (FF by Ryan North 6, Deadpool by Ziglar
  3) take the line at 0.9 + `N` naming the run — the run record is usually absent from rip and dump.

[L-116] folder:vn-year pub:marvel sig:comicinfo sig:legacy-stamp sig:stamp
- Old Marvel folders cut into `vN (year)` shelves: the per-file matcher picks the wrong DECADE by issue number
  (Amazing Adventures 1961 on the 1970 run, Ka-Zar 1997 on 1974, Kull 1971 on 1982/1983). The folder's `vN (year)`
  suffix and ComicInfo `Volume` beat it every time.

[L-117] kind:collected-line pub:marvel sig:probe
- Epic Collection / Modern Era Epic LINES: probe the line name WITHOUT the volume subtitle — six "empty" packets
  held a GCD line row that way (Black Widow Epic 158119, Carnage Epic 183078, Daredevil Modern Era 209809).

[L-118] sig:probe
- A GCD collected series numbers its issues by VOLUME and titles them with the volume SUBTITLE — that, not the
  page count, places a trade when a shelf holds several volumes of one line.

[L-119] pub:marvel
- GCD filing quirks that recovered legs: drop "Comics" from a title, "and" for "&", "Digest" appended, reversed
  or reordered name lists ("Deadpool / Amazing Spider-Man / Hulk: Identity Wars").

[L-120] kind:weekly pub:archie pub:marvel
- Marvel UK: GCD keeps ONE series across a weekly's title changes ("Super Spider-Man" 2407, 153 issues) while CV
  mints a volume per title; the quarterly "ThunderCats Collected Comics" is ONE CV volume and FIVE GCD series
  (one per season) — the Archie holiday split with ComicVine on the spanning side.

[L-121] folder:bucket pub:marvel
- `_2099 Marvel` shelves carry a truncated key ("Doom", "Ghost Rider") that reaches the 20th-century namesake;
  the folder's "<Title> 2099" is the identity and both providers hold it.

[L-122] kind:digital pub:marvel sig:probe
- Marvel "Saga" primers (7-11pp) and "Update '89" (filed by both providers as plain "The Official Handbook of the
  Marvel Universe" 1989) are GCD holes like the Infinite Comics.

[L-123] sig:round2 sig:stamp
- `round2-folder` / `round2-series` was wrong on 10 of 13 shelves carrying it in B-053..B-055, including one link
  stamped on four sibling one-shot shelves in one folder — verify each by title + count + years, never by method.

[L-124] folder:bucket folder:vn-year pass:item sig:trade-link
- A `<Title> vN (<year>)` folder holding only a collected edition is the RUN's folder — the run takes the S
  line, the trade an `I` line; a `_Trades` / `_Minis` / `_One-shots` bucket is the opposite (the book's own
  count-1 record, minted by BOTH providers, wins).

[L-125] sig:probe
- A stored link that is the same title a DIFFERENT DECADE later is a new cluster (Fist of Khonshu 2024 on the
  1985 Moon Knight run; a 1971 Sub-Mariner Annual on the 1998 annual) — years decide, never the title.

[L-126] kind:kids pub:marvel pub:trade-house
- Scholastic / Abrams all-ages Marvel books are in ComicVine under the real publisher and in GCD only
  sometimes — the publisher column, not the title, is the test.

[L-127] pub:marvel sig:comicinfo
- A ComicInfo `Volume` holding a 5-digit ComicVine id (Hulk Comic 37309) is the same 1.0 assertion as the 4- and
  6-digit cases.

[L-128] sig:split sig:stale-flag
- The checker refuses an S line on a shelf with an OPEN conflated-series flag: write `R` + `F split-needed`
  with the run's own ids in the R clause AND an `N` (`cv=… gcd=…`), so clearing the flag makes it one edit.

[L-129] folder:bucket pub:marvel sig:probe
- GCD hides behind the definite article too ("Spider-Man vs. The Black Cat" 40812) and behind ComicVine's
  spelling of a one-shot the parsed key never reached (four Thunderbolts one-shots) — probe the CV name in GCD.

[L-130] pub:marvel sig:legacy-stamp sig:probe
- `lookup.py --year` separates same-title relaunches (Superior Spider-Man 2018 vs 2013); "Lethal Protector II"
  is its own 5-issue run in both legs, not the 2022 Lethal Protector.

[L-131] ruling
- Where the arithmetic refuses the run, say so and override: a 201pp digest is not a 4-issue mini; a 551pp
  vertical rip is the 8-chapter Infinite Comic, not the 4-issue print mini.

[L-132] kind:collected-line pub:marvel
- Epic Collection shape is total at Marvel: CV mints a volume per BOOK, GCD keeps one line row; the CV per-book
  issues are labelled "Volume N" and match filenames volume-for-volume — the cheapest per-book verification.

[L-133] pub:marvel sig:probe
- GCD splits a compound word ComicVine joins: "X-Men: Clan Destine" (5570) is invisible to a "ClanDestine" probe,
  and 32535 "ClanDestine vs. The X-Men" is the collected edition, not the mini.

[L-134] kind:ogn pub:marvel sig:probe
- GCD hides a graphic novel behind a reversed / creator-first title: the bare word "Scorpio" found 14536 "Wolverine,
  Nick Fury: The Scorpio Connection" and 16225 "Scorpio Rising [Wolverine & Nick Fury]", both missed by the dump.

[L-135] kind:collected-line
- An Epic / Complete Collection LINE is found by probing the line name with NO volume subtitle: "Generation X Epic
  Collection" → GCD 174151, shared by four sibling shelves.

[L-136] pub:marvel sig:comicinfo
- Marika-Empire and Glorith-HD ComicInfo `Web` ids are ComicVine ISSUE ids of the stored volume (8 of 8 in B-061..063:
  45305/109026 → X-Men Unlimited #20/#34; 124309, 70469-70471 → Hidden Years #1-4) — each a clean 1.0.

[L-137] folder:bucket kind:trade-only pub:marvel sig:trade-link
- `_Trades, Minis and One-shots` / `_X-Men TPBs` bucket folders: the book's own count-1 record wins ONLY when both
  providers mint it; where only one does, the numbered run behind the trade wins (Wolverine/Punisher, Victims, Origin II).

[L-138] pub:marvel sig:trade-link sig:twin
- GCD mints TWIN count-1 rows for Marvel digital trades far more often than expected (14 pairs in three batches:
  192062/192065, 233265/233608, 234147/234148, 218420/218632 …); a twin caps the line at 0.9 and blocks a GCD issue-level `I` id.

[L-139] pub:dark-horse pub:millarworld pub:small-press sig:round2 sig:stamp
- A folder-wide CV stamp exists too: Space Goat's Evil Dead 2 programme (CV 20289 on 37 files) and Millarworld's Dark
  Horse folder (CV 115757 on the sequel) stamp ONE wrong volume across a folder — the `round2-folder` shape on the CV side.

[L-140] kind:strip pub:lion-forge-magnetic pub:scout pub:small-press sig:probe
- Bubble Comics (Exlibrium, Major Grom, Igor Grom, Meteora), Space Between, Scout/Roar, Strip For Me, Studio D., Imagine
  Bin, SAF Comics, Magnetic Press and Lost His Keys Man are total GCD holes — ComicVine-only is the honest answer there.

[L-141] kind:digital pub:marvel sig:legacy-stamp sig:probe
- Marvel Infinite Comics / Digital Comics Unlimited / Legacy Primer Pages / Marvel Universe cartoon serials are a complete
  GCD hole (14 shelves took `gcd=-`); GCD's only hits are the PRINT repackaging at a different count.

[L-142] pub:marvel sig:collision
- A shelf read in an EARLIER batch may already link the volume you found (a second rip of the same mini in another
  folder): `check_identity --all` catches it as an undeclared merge — write `F <sid> merge-with=<partner>` when the
  partner is the same comic (Iron Fist: Wolverine S9566/S9567; X-Men/Runaways FCBD S22298/S11337).

[L-143] kind:kids pub:boom pub:idw pub:oni pub:scout sig:trade-link
- Oni Press and Scout Comics are the IDW/BOOM count-1-trade shape at full strength: 31 shelves in B-064..066 carried
  the stored link to the same-title count-1 collected record instead of the run (Alabaster Shadows, Blood Feud, Cemetery
  Kids, Cult of the Lamb, Princess Ugg, Terrible Lizard, Black Cotton, Canopus, Smoketown, Stabbity Bunny, White Ash …).

[L-144] pub:papercutz-nbm sig:twin
- Papercutz / NBM mints TWIN GCD rows for nearly every book (155074/155075, 142309/142310, 119438/119439, 94997/94998,
  187878/187879, 178089/178090) — that twin is the commonest single cause of a 0.9 on those shelves.

[L-145] pub:markosia sig:comicinfo
- A ripper's ComicInfo `Web` id is a ComicVine ISSUE id for Novus-Year Four (Days Like This 354967, The Tomb 392636) and
  LeDuch (Fear City: Thumper 1078598) — clean 1.0s.

[L-146] kind:digital pub:digital-indie pub:idw pub:image sig:probe
- Panel Syndicate and MonkeyBrain are the same total GCD hole: GCD indexes only the later Image/IDW print collections,
  never the digital-first serial (17 shelves).

[L-147] pub:avatar sig:probe
- The "GCD hides behind the licensor prefix" shape recurs at Avatar: `Streets of Glory` returns only the trade,
  `Garth Ennis' Streets of Glory` returns the six-issue run (26642).

[L-148] pub:small-press sig:probe
- GCD files New England Comics' Tick specials under the POSSESSIVE ComicVine drops ("The Tick's Big Romantic Adventure"
  15953, "The Tick's Big Back to School Special" 15958) — re-probe with `Tick's`.

[L-149] folder:bucket kind:kids pub:oni pub:scout pub:small-press
- Silver Sprocket, Storm King, Scout and Oni mint one record per BOOK, so a shelf holding several titles has no spanning
  record at all (Storm Kids, Tales of Science Fiction, Tea Dragon, Metalshark Bro, the two Whiteout volumes, Queen &
  Country: Declassified, Tank Girl's bucket shelf).

[L-150] sig:round2
- `round2-folder` was wrong on every shelf carrying it in B-064..066 (Tank Girl Colour Classics ×2 right by accident;
  Rotten & Zombies vs Cheerleaders got "Hack/Slash Meets Zombies vs Cheerleaders", a different crossover) — still ⚠ never a leg.

[L-151] sig:collision sig:withheld
- The withheld rule beats the checker's merge-with remedy text: a stored-link collision with a DIFFERENT comic is
  `cv=-` + `N withheld cv=<id> … S<partner>`, never a merge (S101634 vs S22834); merge-with only for the same comic.

[L-152] kind:collected-line pub:titan
- Titan Doctor Who "Year Two" runs exist in GCD (92899 Eleventh, 95387 Twelfth) and NOT in the ComicVine rip — the
  mirror of the usual hole; the trade sits on the collected line (GCD 95180 / 111484) by volume number + subtitle.

[L-153] pub:valiant
- Valiant's GCD rows count DOUBLE because GCD indexes every issue's "Pre-Order Edition" as its own row (Doctor Tomorrow
  10 for 5, Fallen World 10 for 5, X-O Manowar 16 for 8) — exactly twice ComicVine's count is that artefact; caps at 0.9.

[L-154] pub:idw pub:top-shelf
- Top Shelf is an IDW imprint: GCD files every post-2016 Top Shelf book under publisher "IDW" while ComicVine says
  "Top Shelf" — the publisher columns disagree by imprint, not by comic (40 clean 0.95 pairs in B-068).

[L-155] pub:tko sig:trade-link
- TKO Studios ships a 6-issue floppy run AND a same-title count-1 collected record simultaneously in both providers;
  the stored link is the collected record on 9 of 15 TKO shelves.

[L-156] pub:archie pub:valiant
- Acclaim's Eternal Warriors is the Archie-holiday split with ComicVine on the spanning side: CV 26146 count 6 vs six
  separate GCD one-shot series (15146, 21569-21572, 21589).

[L-157] sig:probe
- A GCD row with ZERO issues is an empty stub, not a leg (Skull Cat 197154, Rivers of London: Stray Cat Blues 213823,
  Archer & Armstrong: Revival 164703) → `provider-missing`.

[L-158] kind:foreign pub:delcourt pub:titan
- Titan's licensed European albums (Azimut, Cutting Edge, Tyler Cross, Yragaël) are the Delcourt shape: CV mints the
  English volume, GCD holds only the original-language album line — pair at 0.9 with the language named.

[L-159] sig:probe
- An exact-name probe can miss a sequel the rip DOES hold: `--contains "Icarus Society"` found CV 143976 after the
  exact "Prodigy: The Icarus Society" probe returned nothing — try `--contains` on the subtitle before `provider-missing`.

[L-160] pass:revisit sig:withheld
- A withheld shelf with NO GCD leg to keep becomes `R` + `N withheld cv=<id> … S<partner>` (S4944, S99691) — the
  revisit pair re-links the partner and then the S line can be written.

[L-161] ruling sig:probe
- cvref's `normName` DROPS "of" (and the leading article) but keeps from/with/in: an exact `lookup.py` probe containing
  "of" ("Sea of Thieves", "Books of Magic", "Year of Valiant") returns 0 CV hits on volumes plainly there — drop the "of".

[L-162] kind:foreign pub:dc sig:probe
- A SPACE inside a title hides the English record in BOTH providers: "Gen 13: Armageddon" (CV 19859/GCD 27648) and
  "Superman/Gen 13" (CV 26595/GCD 16731) are invisible to a "Gen13" probe — that is why such packets offer only foreign editions.

[L-163] sig:probe
- GCD hides behind a bracketed alternate title: "War Stories: J for Jenny [War Story: J for Jenny]" (10937), "Dance Like
  Everybody's Watching! [A Zits Treasury]" (132081), "You're Out of Your Mind, Charlie Brown! [Horizontal]" (155990).

[L-164] kind:archive kind:collected-line kind:strip sig:probe
- Year-by-year archives of a newspaper/web strip (Doonesbury, Dennis the Menace, Rip Kirby, The Little King) have no record
  in either provider → R + provider-missing; where a provider holds the strip's own collected line (Cyanide & Happiness,
  Oglaf) pair at 0.9 as a format split.

[L-165] kind:strip pass:item
- GCD files all three modern Bloom County books as ONE 3-issue series (110459): each shelf takes `gcd=-` and the row id on
  its `I` line, or the three shelves merge.

[L-166] pub:valiant
- Valiant's GCD pre-order doubling confirmed again: GCD 153708 "The Visitor" count 12 = six issues each indexed twice.

[L-167] pub:zenescope sig:trade-link
- Zenescope runs the count-1-trade-as-stored-link shape at full strength (24 wrong-cv-links in B-071 alone), and its
  collected records are almost never in GCD (`I … gcd=- 0.9` is the honest per-trade shape).

[L-168] pub:titan sig:collision sig:withheld
- A "withheld pair" the lead framed can turn out to be the SAME comic filed twice (Sea of Thieves v2 / Origins: one Titan
  2021 mini, CV 134597 "Volume named per the indicia") — the reader's evidence wins; write merge-with, no wrong-cv-link.

[L-169] folder:events pub:marvel sig:trade-link
- `_Marvel Major Event Chronology (digital)` is one folder per tie-in holding that tie-in's collected edition: the
  count-1-trade-as-stored-link artefact runs at 86 of 202 shelves there, the highest rate in any single folder.

[L-170] pub:marvel sig:barcode
- Modern Marvel tie-in collections are 116pp almost without exception (a 3-issue mini) and 124pp for a 4-issue one;
  `gcd_issue` page+ISBN places every such trade within 3-20pp of our rip.

[L-171] kind:magazine kind:strip pub:marvel sig:probe
- Marvel's giveaway newspapers and preview magazines are a total GCD hole (Fallen Son Daily Bugle, Civil War II Daily
  Bugle, New York Bulletin, Empyre Magazine, Blood Hunt Diaries, Art of War of the Realms) → `provider-missing`.

[L-172] pub:marvel sig:probe
- Marvel ships minis under a SHORTER title than the trade: AVX: VS / AVX: Consequences, "1872", "Years of Future Past",
  "The Union", "Atlantis Attacks", "War of the Realms: Punisher" (no "The") — probe the abbreviation or the bare
  title before writing provider-missing.

[L-173] folder:skottie folder:variant-covers pub:marvel sig:collision
- `Marvel\__Skottie Young Covers` holds a 1pp variant shelf for nearly every 2015 Secret Wars mini, each already linked
  to that mini's volume — the commonest stored-link collision on Secret Wars shelves (merge-with when it is the mini).

[L-174] pub:marvel sig:legacy-stamp sig:stamp
- A legacy-renumbered run is its own volume in both legs (Captain Marvel #125-129 = CV 105506 / GCD 117972; GotG
  #146-150 = GCD 118041); the per-file matcher reaches the 1960s/1990s namesake by issue number every time.

[L-175] folder:bucket folder:events pass:item
- In the chronology folders the RUN rule stands over the bucket rule: `S` = the numbered run, the tie-in's collected
  record goes on the `I` line (B-072/073, ~90 shelves); the book's own record is the S only where no numbered run exists.

[L-176] pub:2000ad
- A COVER YEAR read as an issue number is a distinct wrong coordinate: 2000 AD's Christmas bumper progs ("PROG 2013",
  "2000AD 2014") and "Festive Thrillpower 2017" collide with real progs 2013-2017; "2000AD Annual 1984/1986" likewise.

[L-177] sig:probe
- A bracketed publisher series number becomes a phantom ladder entry: Thun'da's "[A1-056]/[A1 073]/[A1 083]" produced
  ladder 56/73/83 and a ratio 5.00 — read the bracket, not the ladder.

[L-178] kind:weekly pub:2000ad
- GCD splits a long British weekly by PUBLISHER ERA while ComicVine keeps one volume (2000 AD: GCD 11289 IPC / 11295
  Fleetway / 11294 Egmont / 11293 Rebellion vs CV 19752) — the publisher-change rule: pair CV with the majority-era GCD
  row, name the others in an `N`. (An earlier tier-C note said GCD keeps one row; it does not for 2000 AD.)

[L-179] pub:ac sig:trade-link
- AC Comics mints a NEW count-1 series in BOTH providers every time a title returns a decade later (Fem Fantastique,
  Femzine, Paragon Illustrated, Nightveil, Bill Black's Fun Comics) while ComicVine keeps ONE spanning volume.

[L-180] folder:bucket pub:ac sig:trade-link
- AC's "Retro Comics" line numbers ACROSS titles (#0 Cat-Man, #2 Miss Victory, #4 Jungle Girls, #5 All-Hero): CV has
  the 5-issue line volume (21438) AND per-title count-1 volumes, GCD only per-title one-shots — the line volume spans a
  bucket shelf; per-title rows go on `N` lines.

[L-181] sig:split
- A ripper's CONTINUOUS numbering can hide several published comics: Gung-Ho 001-014 = Gung-Ho (7) + Sexy Beast (4) +
  Anger (4); the trailing number after each filename subtitle is the real run's number, and a ladder running PAST the
  linked count is the tell → `R` + split-needed, never `S` the first run.

[L-182] pub:action-lab pub:image sig:comicinfo
- Two different comics can share a title on one shelf via two publisher folders (Stillwater: Action Lab 2016 mini vs
  Image/Skybound 2020 ongoing) — ComicInfo Publisher, not the title, splits them.

[L-183] folder:sequel-subfolder pub:ablaze pub:action-lab pub:aftershock pub:ahoy pub:american-gothic sig:split
- The numbered "01 …/02 …/03 …" (or vN) subfolder is the small-press SEQUEL marker at Action Lab, Aftershock, Ahoy,
  Ablaze and American Gothic alike (Danger Doll Squad, Rough Riders, Voracious, We Live, Killbox, E-Ratic, The Wrong
  Earth): each subfolder is a co-equal mini with its own record → `R` + split-needed unless one dominates by proportion.

[L-184] pub:2000ad sig:probe
- `lookup.py --contains` on the LINE name recovers a whole collected programme the packet shows nothing of: "Essential
  Judge Dredd" exact = 0 hits, `--contains` = all seven volume-per-book records.

[L-185] kind:fan kind:strip kind:weekly pub:2000ad pub:idw sig:twin
- Fan-made STORY EXTRACTS cut out of an anthology weekly (Judge Dredd strips at 5-25pp, plain + "(Colour Reprint)"
  twins) are not a comic the ledger links: the shelf is `R` + `not-a-run` for that part, and the real published lines
  on it (IDW's ongoing, the Essential line) are named per run.

[L-186] kind:collected-line
- REMINDER (lead ruling, applied again on Princeless S14096): a collected LINE with its own record (GCD 106434, nine
  trades) wins over the first mini's run; linking the first four-issue mini to a nine-volume trade shelf is wrong-cv-link.

[L-187] pub:marvel sig:trade-link
- A plain "<Title>" 2015 count-4/5 CV/GCD record IS the Secret Wars Warzones mini; the "<Title>: Warzones!" /
  "Battleworld" count-1 records are the 2016+ collected repackagings — the mini's record wins, the trade goes on `I`.

[L-188] kind:collected-line kind:trade-only pub:berger pub:boom pub:dark-horse sig:trade-link
- The matches-our-holdings collected-edition trap dominates Boom! / Berger Books trade-only shelves: a stored or
  per-file count-N pick where N equals OUR trade count is almost always the trade record; the run's true (larger)
  count is usually one candidate away.

[L-189] kind:foreign pub:cinebook sig:probe sig:stamp
- Cinebook English-album shelves: GCD's per-file matcher regularly picks a French/Dutch/German/Spanish co-edition over
  the English row — probe the English title in GCD before pairing at 0.9 with a language named.

[L-190] kind:digital pub:comixology pub:dark-horse
- ComiXology Originals beyond Best Jackett: GCD holds only a later Dark Horse print reprint at a different count; the
  digital-first CV record is the identity, the print reprint is `N`.

[L-191] folder:sequel-subfolder folder:vn-year pass:revisit sig:stale-flag
- On a wave-2 `NN <Title> vN (<year>)` split-out shelf (ids S94xxx+) an `overlap-in-series` flag is stale by
  construction — the colliding Vol. 01s now sit on sibling shelves (13 of 14 in R-016); the lead dismisses the flag.

[L-192] sig:trade-link
- Nine such shelves carry a same-title provider row whose count equals exactly the BOOKS we hold, one line below the
  run (GCD 98247/3, 173432/2, 135977/3, 56708/2, 118040/2, 140636/3, 174204/4 …) — the collected record, never the S.

[L-193] sig:legacy-stamp sig:probe
- GCD spells a relaunch's volume ordinal into the NAME ("I Hate Fairyland Volume 2", 192611), so `--year 2022` on the
  plain title returns 0 hits — probe with the ordinal or `--contains`.

[L-194] sig:legacy-stamp
- A legacy-renumbered arc collected under its own title ("Punisher: War Machine") belongs to the parent volume's
  count: CV 90118 / GCD 100917 count 28 = #1-17 + #218-228 — the arithmetic that split S66039.

[L-195] pub:dc sig:legacy-stamp sig:stamp
- Trade SUBTITLES tell two same-title relaunches apart when the per-file matcher cannot (Batman Beyond 2015 = Brave
  New Worlds / City of Yesterday / Wired for Death; 2016 = Escaping the Grave / Rise of the Demon / The Long Payback).

[L-196] kind:collected-line pass:item pub:marvel
- LEAD RULING (S15513 She-Hulk): a creator-named collected LINE wins the shelf only when it is COEXTENSIVE with the
  shelf; a line that also collects a sequel series (Sensational She-Hulk volumes on the same line) is `I` lines on the
  line, and the run is the S.

[L-197] pub:dc sig:legacy-stamp sig:stamp
- The per-file matcher's "wrong RELAUNCH" trap: it stamps the LATER same-title volume onto files dated to the earlier
  era by issue-number coincidence (Wonder Woman 2016, Batman 2016, JLA v2/v3, Firestorm, Hawk and Dove v5, Atari Force
  v2, Adventure Comics 2010) — the tell is files whose years or numbers predate or exceed the stamped volume's own.

[L-198] pub:dc
- ComicVine keeps ONE spanning volume across era-retitled arcs (L.E.G.I.O.N. / R.E.B.E.L.S., Deathstroke "The Hunted",
  Green Lantern 1960, Detective, Action, All-Star) while GCD names each era its own row — majority GCD era + `N` the
  rest; no split for that alone.

[L-199] folder:bucket folder:events pub:dc
- DC's "#DC Events" chronology folders resolve once the cross-referenced-run pattern is seen: a crossover reprint
  bucket or a tie-in folder filed adjacent to the main run takes the main run's identity with the tie-ins named.

[L-200] sig:stale-flag
- An open `overlap-in-series` / `conflated-series` flag on a shelf whose evidence shows ONE run (a wave-2 split-out
  whose siblings now hold the other ladders, or several trades LABELLED "Vol. 01" for arcs of one continuously
  numbered run) is a STALE flag: do NOT write R to satisfy the checker — write the S + `N <sid> stale-flag | …`, accept
  that one checker failure, and list the sid under "stale flags" in your report; the lead dismisses the flag.

[L-201] folder:variant-covers kind:weekly pub:dc sig:legacy-stamp sig:stamp
- The per-file matcher's "wrong relaunch / legacy volume" trap generalises to DC: Batman (1940) stamped across every
  modern Batman variant-cover shelf, Batgirl (2000) across every later Batgirl relaunch, Warlord (a D.C. Thomson weekly
  of the same bare name) across the 1976 DC Warlord.

[L-202] folder:bucket kind:foreign pub:dc sig:round2 sig:stamp
- GCD's `round2-folder` DC failure mode: every unmatched one-shot in a "_Miniseries and One-shots" bucket gets stamped
  with one wrong Dutch "Batman" ongoing (GCD 18155) — always rejected for the dump's exact match.

[L-203] kind:collected-line pub:dc sig:collision
- Two shelves claiming the same stored CV volume across batches (duplicate rips of one run or one-shot filed under
  different folders, "Rebirth Deluxe Edition" volumes of one line, a run split by a year folder) is routine on a
  large DC vertical — `--all` catches it; `merge-with` in your own file after checking the partner's name and files.

[L-204] pass:item pass:revisit sig:probe
- `lookup.py --issues <collected-line volume>` names each TRADE's own ComicVine ISSUE id: 71 of 73 `I` lines in
  R-018 carry an item-level CV id instead of `cv=-`.

[L-205] kind:collected-line pub:dc sig:legacy-stamp
- A DC trade LINE restarts its own "Vol. 01" every creative era while the comic keeps legacy numbering: Detective
  Comics has four such lines (CV 98510/GCD 111829, 121066/150022, 145333/180852, 150502/203114) over ONE unbroken
  #934-1093 ladder — that alone minted a "five Vol. 01s" flag; Batgirl 2011 has two lines over one 53-issue run.

[L-206] pub:dc sig:probe
- GCD's "&" spelling hides a run from every "and" probe (Batman & the Outsiders 2019: CV 118841 / GCD 144938, 17
  issues) — probe both spellings.

[L-207] pub:dc
- GCD 60830 "Detective Comics" spans the New 52 AND Rebirth; CV splits them (42594 New 52, 91098 Rebirth) — a New 52
  sibling shelf must take CV 42594 and be declared before landing, or the shared GCD row misleads.

[L-208] pub:boom sig:trade-link
- Regular Show (S14554): Salem's v01/v04/v05/v06 are BOOM's Original Graphic Novel line (count-1 CV volumes 76877,
  102477, 112595, 116041; GCD 163681), Empire's Vol. 01-09 + Salem's v10 are the ongoing's ten collected volumes
  (CV 73115 / GCD 93394) — one run plus standalone books, NOT two rips; the lead's earlier "two rips" note was wrong.

[L-209] kind:digital pub:dc
- DC's digital-first "Injustice: Gods Among Us — Year N" chapters: CV's digital-chapter count matches our ladder
  exactly while GCD indexes only the print-issue count (about a half to a third) — the Bombshells digital/print split;
  pair them, GCD's lower count is not residue.

[L-210] pub:dc sig:legacy-stamp sig:probe sig:stamp
- The wrong-relaunch trap is near-universal on any DC title with 3+ relaunches (Green Arrow, Green Lantern, Harley
  Quinn, Justice League, Legion, Lobo, Martian Manhunter, Doom Patrol): the matcher stamps the oldest legacy volume or
  the largest/most-recent one across every sibling shelf; `lookup.py` by name + year recovers the true id.

[L-211] folder:vn-year pub:dc sig:collision sig:stale-flag
- An `overlap-in-series` flag on a franchise with many `vN` sibling shelves (Deathstroke, Harley Quinn, Green Lantern,
  Justice League, JLD, Legion, Lobo) describes the CROSS-SHELF Vol. 01 collision, not conflation on the one shelf —
  confirmed 14 of 14 in C-013..C-015; the stale-flag note must name the sibling shelf.

[L-212] pub:dc sig:collision sig:legacy-stamp
- A continuation of legacy numbering (#201+ picking up a prior volume's own numbering, Green Lantern Corps S94605→
  S94610) is the SAME run as the prior volume — merge-with, not a separate id.

[L-213] ruling sig:split sig:stale-flag
- RESIDUE, defined (lead ruling after Secret Six S15276): a duplicate rip, an annual, a one-shot, a single misfiled
  book, or ONE trade of a run that lives on another shelf. Two or more whole trades of a DIFFERENT run with its own
  record are a co-equal run — `R` + split-needed even at 6:2 — and the shelf's overlap flag is NOT stale.

[L-214] pub:dc sig:legacy-stamp sig:stamp sig:twin
- The wrong-relaunch / legacy-volume stamp ran at 70-95% of files on Midnighter, Mister Miracle, Robin, Nightwing,
  Supergirl, Plastic Man and Suicide Squad shelves in C-016 — assume it on any DC relaunch shelf and verify by year.

[L-215] kind:collected-line pub:dc sig:twin
- A launch one-shot can sit beside a distinct "Deluxe Edition" collected-line record (Nightwing / Suicide Squad
  Rebirth) — a third record beside the run and its trades, not the trades-vs-run shape.

[L-216] kind:golden-age pub:dc
- Regional reprint editions (Canadian, Philippine, Australian) recur as small harmless residue on Golden Age DC shelves.

[L-217] folder:bucket sig:collision sig:comicinfo
- A generic-title bucket shelf ("Robin" S14733) holding scattered issues of an already-dedicated volume takes
  `merge-with` that volume's shelf even when its ComicInfo tags match on their own.

[L-218] kind:golden-age pass:revisit pub:dc sig:legacy-stamp
- ComicVine keeps Action Comics as ONE volume (18005) across the 1938 run and the 2018 legacy-numbered continuation
  while GCD splits the eras (97 / 59922) — whether the two era shelves merge is a revisit question (R-019).

[L-219] kind:collected-line pass:item pass:revisit pub:dark-horse sig:barcode sig:stale-flag sig:trade-link
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

[L-220] kind:foreign kind:golden-age pub:dc sig:legacy-stamp sig:stamp
- The wrong-relaunch / legacy-volume stamp runs in BOTH directions on DC legacy titles: the 1938/1959/1942 volume on
  modern relaunch files AND a modern or FOREIGN volume on legacy files (a 1976 French Éditions Lug "Titans" reprint,
  CV 44350, is the per-file majority on every US Titans shelf) — reject on publisher and language.

[L-221] kind:weekly pub:dc sig:legacy-stamp
- GCD keeps ONE spanning row across a numbering restart while CV splits by volume for era-retitled legacy continuations
  (Superman #650-714 after Infinite Crisis; New Gods 1971 vs 1977) — the reverse of the weekly shape; pair the majority.

[L-222] pub:dc sig:collision
- Second-rip fragments of shelves already read in earlier batches (Flash Secret Files, Spectre 1992, Teen Titans
  2003/2016, Wonder Woman 1942/2016, Zatanna 1993, Donna Troy) surface as `--all` collisions — `merge-with` after
  grepping the partner's name and count.

[L-223] pub:dark-horse sig:split sig:stale-flag
- Dark Horse conflated-series flags split about evenly between real conflation (Alabaster, Aliens, American Gods, Barb
  Wire, Billy the Kid, Canto, Captain Midnight — R + split) and a stale flag on a 2-file shelf holding only ONE of the
  conflated minis — but check the two files are the SAME mini before calling it stale (Blue Book was not).

[L-224] pass:item ruling
- `I` lines are never optional: a batch written without them lands no item links — the lead sent C-019..C-021 back
  for an I-line repair pass.

[L-225] pass:item ruling
- The packet's "GCD dump: NNNNN … [] count" numbers are SERIES ids, never issue ids, even at count 1 — an `I` line's
  gcd= wants the issue row (query gcd_issue); a Sonnet reader reused dump ids raw twice in C-022.

[L-226] pass:item ruling
- `gcd=s<series>` is valid ONLY on `I` lines; on an `S` line it fails the checker's numeric gate.

[L-227] folder:vn-year pub:dynamite sig:legacy-stamp sig:probe sig:stamp
- Dynamite's same-title families (Red Sonja, Green Hornet, Turok, Bettie Page, Miss Fury, Black Terror, Sweetie Candy
  Vigilante) carry the wrong-relaunch stamp at near 100%: per-file CV puts the OLDEST same-title volume on every
  `vN (year)` relaunch shelf — the folder label is the tell, `lookup.py --year` recovers the id.

[L-228] pub:dark-horse pub:dynamite pub:zenescope sig:legacy-stamp sig:split
- Franchise folders that are genuinely several runs → `R` + split-needed, not residue: James Bond (three ladders),
  Charmed (reboot + two Zenescope seasons), Green Hornet (three eras), Savage Tales (three publishers on a bare
  title), Terminator (1999 Dark Horse mini + 2024 Dynamite), Vampirella (ongoing + "Year One"), Kirby: Genesis
  (main + Dragonsbane), Tomb Raider (two same-numbered relaunches).

[L-229] pub:disney pub:dynamite sig:collision
- Same-run continuations split by a folder or a parsed-key token surface as cross-batch collisions → `merge-with`
  after reading the partner (Disney Masters S21200→S5517, Red Sonja v5 S99463→S14482, Sheena S15521→S15518,
  BSG Classic trade S2336 ↔ run S94559, Elvira #1 S6117 ↔ #2-5 S6116).

[L-230] pass:revisit pub:dark-horse
- Tier-C Dark Horse: a reader-derived CV id can be a FOREIGN edition (Magic Press "B.P.R.D." 136599), a hardcover
  Book-ladder record (Hell on Earth Book 1-5 = 107353) or the ISSUE-form ongoing behind the trades (Plants vs.
  Zombies 82701 = the 2015 12-issue run) — read the id's own issue list against the files before pairing it with a
  GCD collected-line row; a mismatched pair is not a 0.9 S. (Lead, C-022 verification → R-021.)

[L-231] pass:revisit pub:dynamite sig:split
- Separately titled minis filed under a franchise shelf (Army of Darkness: "Ash Gets Hitched", "Ash in Space") are
  their own 4-issue runs, not arcs of the same-era series whose trade sits beside them (Ash S1357 → R-022).

[L-232] pass:item pub:dark-horse
- The Goon's collected GCD series 26047 numbers Vol. 01-15 across S65962 (C-022) and S65963 (C-023) — a retrofit
  candidate for S65962's `I` lines.

[L-233] kind:collected-line pass:item pass:revisit pub:dark-horse sig:trade-link
- ComicVine does NOT index Dark Horse TRADE LINES: a plain "Hellboy" / "B.P.R.D." volume query returns nothing; CV
  mints a count-1 volume per BOOK while GCD keeps one collected series per title (R-021: six of seven shelves).
  RULED by Eric 2026-09-16 (see the chain-of-minis ruling above): the GCD line alone is the identity at 0.9, CV's
  per-book volumes go on the `I` lines. R-020..R-022's one-leg refusals were flipped to S by the R-023 pass.

[L-234] pub:dark-horse pub:dynamite sig:stamp sig:trade-link
- The count-1-trade / first-mini stored-link artefact runs at full strength on Dark Horse and Dynamite chains
  (Ether 95727, Briggs Land 93181, Brain Boy 67220, Itty Bitty Hellboy 66787, Pathfinder 51262, Ash 68723): the FIRST
  mini's volume stamped across the whole chain.

[L-235] pub:dark-horse
- A ripper's continuous 001-0NN numbering over a mini chain is readable from the filename's own "(of NN)"
  (Baltimore, Lobster Johnson) — but the same shape can be one run (Abe Sapien 001-036 = CV 59507 / GCD 73562).

[L-236] pub:dark-horse
- GCD 102900 Plants vs. Zombies starts its volume numbering at #4; B.P.R.D. Hell on Earth is continuously numbered
  #103-147 from 2012 (CV 61307 / GCD 71228), not a pure chain.

[L-237] pass:revisit pub:dc sig:collision sig:comicinfo
- Action Comics: CV 18005 stops at #904 (2011); the Rebirth continuation #957+ is CV 91078 (ComicInfo `Volume`
  asserts it) — the "v1 (2016)" and "v1 (2018)" folder shelves are one run (R-019 merge-with).

[L-238] kind:archive kind:golden-age pub:dark-horse pub:ec-archives
- ARCHIVE shape, stated once (after C-025): a shelf that is PURELY the collected trades of a Golden-Age original
  (Dark Horse "EC Comics & Others Archives", Crime Does Not Pay, Frontline Combat, Magnus, War Against Crime, Savage
  Sword of Conan) takes the archive LINE's own CV/GCD record — the S16653 line-wins ruling; the older "S = the original
  when both legs hold it" line applies only when NO line record exists. The later, dated ruling wins.

[L-239] pass:item ruling sig:probe
- Packet phrase "vol X … 1 issue Y": X is the VOLUME id, Y the ISSUE id — an `I` line wants Y (`lookup.py --issues X`
  lists them); three mix-ups in one batch.

[L-240] folder:bucket folder:graphic-novels pub:small-press sig:round2 sig:stale-flag sig:stamp
- `round2-folder` stamps a stale NEIGHBOUR shelf's GCD series across every singleton in a bucket folder ("Our Story
  Thus Far" 56736 across "Graphic Novels", "Infinite Wheatpaste" 212845 across Avery Hill) — never a leg.

[L-241] kind:kids pub:papercutz-nbm pub:trade-house sig:split sig:stale-flag
- Juvenile franchise reboots (Geronimo / Thea Stilton: original run, Papercutz HC reissue, Scholastic Graphix reboot)
  are several co-existing lines on one shelf — R + split, not stale.

[L-242] pub:dc pub:gold-key-dell pub:marvel sig:legacy-stamp sig:stamp
- Gold Key / Dell / Western funny-animal and Gold Key Star Trek: CV keeps ONE volume across the Dell→Western handover
  while GCD splits by era (the Daffy Duck / Flintstones / Bugs Bunny shape); a later DC/Marvel volume stamped by
  issue-number coincidence is the wrong-relaunch trap again.

[L-243] kind:collected-line pass:item pass:revisit pub:dark-horse pub:dynamite sig:trade-link
- Under the one-leg ruling every Dark Horse / Dynamite chain-of-minis shelf in R-020..R-022 became `S cv=- gcd=<line>
  0.9` + `F wrong-cv-link` (the stored id was the FIRST mini's run in every case) + minis on N + a `C` per trade —
  except the issue-file chains (Baltimore, Lobster Johnson), the several-publisher shelf (Angel), a shelf mixing one
  season's issues with another's books (Angel & Faith), and Ash (three count-1 records, no line on either leg).

[L-244] kind:collected-line pass:item
- A trade collecting TWO minis, and an omnibus, cannot yet be stated by a `C` line (one range, one run, one per item):
  name the runs on an `N` for now — TOOLS_TODO 17 adds per-run ranges and several `C` per item.

[L-245] pass:revisit pub:dc sig:collision sig:legacy-stamp sig:split sig:stamp
- A SHARED STORED ComicVine id is not proof of one run: `merge-with` on it only after the id fits BOTH shelves' own
  keys and GCD links. C-008 merged the real Flash 1959 shelf into the "The Flash v2 (2007)" folder shelf because both
  stored CV 1995 — but that shelf's own key and GCD link (26125, 2007-2009) said Flash vol. 2; the stamp was the
  legacy-number trap. The merged shelf became two runs (R-023, R + split). When a shelf's GCD leg contradicts its
  stored CV id, the CV id is the suspect, and the merge is `F wrong-cv-link` on the mis-stamped shelf instead.

[L-246] kind:collected-line pass:item
- ITEM PASS (X-001..X-003): on RUN-identity shelves the per-file GCD link reuses the run's own series matched to ONE
  issue by number for vol. 01 only, while later volumes resolve to a real collected-edition series — check whether
  vol. 01's link differs in kind from vol. 02+ before trusting it (Kevin Keller, Mega Man, Klaus, Dark Crystal …).

[L-247] kind:collected-line
- A `C` that restates the shelf's own run needs its ids repeated in the clause to clear 40 characters; the clause is
  the evidence, not a pointer. A shelf's own collected LINE and the run it collects are two records (Invisible
  Kingdom: line GCD 151274 vs run CV 117748 / GCD 142448; Goldie Vance: line CV 94628 vs run CV 89593) — the `C`
  names the RUN.

[L-248] kind:collected-line
- Sibling shelves of one franchise can share one real GCD collected-edition line (Fence S6636/S6639/S6640 → 123731);
  the sibling's landed S is evidence for the weak per-file matches on the others.

[L-249] kind:collected-line pass:item pub:boom
- ITEM PASS (X-004..X-006): per-file ComicVine on a BOOM trade shelf often matches the run's issue by ORDINAL
  position (trade #3 → issue #3) — not the trade's record; write cv=- and take the GCD collected-edition row.

[L-250] sig:barcode
- The packet's `linked …` field can say no match (st2/st3) while the shelf's own `gcd issues:` ladder or `N` note
  holds the title/ISBN match one line above — read the pool, not the verdict (Lumberjanes Max Ed, Wynd Book 03).

[L-251] kind:collected-line
- Deluxe / Yearbook / Omnibus editions of the shelf's own ongoing with NO judged range get an `N` naming the
  relationship, never an invented `C` range (the range is containment's to read).

[L-252] kind:foreign
- Per-file GCD "collected edition" companions can alternate with a FOREIGN edition per file (Red Lanterns: 77665 vs
  Panini Deutschland 68739) — check each book's series against the shelf's `N` before trusting the id.

[L-253] sig:probe
- A subtitle can name a SEQUEL series filed on the first run's shelf (Wild's End "Beyond the Sea" = CV 151678 /
  GCD 201441, not the 2014 run) — the `C` names the sequel run.

[L-254] kind:collected-line pass:item pub:dc
- Volume-only ids (a per-book CV volume with no issue row: Smallville Season Eleven's eight volumes) are an `N
  no-record`, not an `I` — an `I` wants the issue.

[L-255] kind:foreign kind:trade-only pass:item pub:dc sig:barcode sig:twin
- ITEM PASS (X-007..X-009): foreign-language GCD twins are the commonest per-book trap on DC trade shelves —
  Spanish (84-…), German (3-86201 / 3-95798), French (2-365-77… / 979-10-268…), Dutch rows beside the English one;
  the ISBN prefix is the fast tell (Aquaman, Green Arrow, Catwoman, Checkmate, Deathstroke Inc. 185111 vs 187856).

[L-256] pub:dc sig:barcode
- The packet's shelf-level `identity:` list of the shelf's own per-book GCD rows (pages + ISBN) outranks a book's
  own mismatched `linked` pick — read the shelf list first (Detective Comics 2012, Curse of Brimstone, Wonder Twins).

[L-257] pub:dc sig:twin
- Modern DC digital lines carry TWIN "Collected Edition" GCD rows for one trade (Detective 77783/77579, Stormwatch
  65339 stub / 76642, Dark Knights of Steel 189483/203963, I Am Batman 188323/201985) — one `I`, capped at 0.9.

[L-258] kind:collected-line sig:stamp
- A degenerate packet block can stamp ONE GCD issue id onto every book of a shelf (Green Arrow S97350, six eras of
  collected lines) — resolve only by each series' full issue list matched by title, never by the stamp.

[L-259] pass:item ruling
- ITEM PASS (X-010..X-012): confidence is exactly one of 1.0 / 0.95 / 0.9 / 0.7 on `I` and `C` lines too — no
  0.75/0.8/0.85/0.97; the checker rejects anything else (130 failures in one round-trip).

[L-260] kind:collected-line kind:trade-only pub:dc sig:legacy-stamp sig:stamp
- On DC run-wins trade shelves the per-file CV pick is the run's issue by ordinal OR a franchise-wide legacy stamp
  reused across eras (cv 2839 on ~15 Green Lantern trades; 92750/134718 on every Harley Quinn era) — default cv=-
  and take GCD's book-specific collected-edition row.

[L-261] pass:item pub:dc sig:twin
- Sibling-era shelves of one franchise cross-contaminate each other's per-file GCD picks by "Vol N" ordinal
  (Harley Quinn S94832↔S94833, Nightwing S65356↔S94872, Legion S98212↔S65063; Supergirl's five shelves so badly
  that S94709's thirteen books are all no-record) — read the shelf's own list, not the neighbour's.

[L-262] sig:stamp
- A single-book "collected volume" shelf's S line often carries the book's own CV issue id — use it over a
  mismatched per-file pick.

[L-263] kind:foreign pass:item pub:dc sig:legacy-stamp sig:stamp
- ITEM PASS (X-013..X-015): the per-file CV matcher stamps a SPANNING legacy volume on every modern Wonder Woman
  trade and the French Éditions Lug 1976 reprint (CV 44350) on every US Titans trade — reject on era / language.

[L-264] sig:probe
- Title-only (st3) GCD matches reuse ONE gcd id across every volume of a line (Golden City ×10, Windmaker,
  Tarzan Kubert Years) — re-derive each book from the shelf's own numbered pool.

[L-265] kind:foreign pub:delcourt pub:europe-comics pub:marvel
- Delcourt / Soleil English-digital shelves pair the English CV volume with the French GCD original at 0.9 (the
  Europe Comics shape) — except when GCD's same-name row is an unrelated book (Spin Angels → Marvel's GCD 53252).

[L-266] pub:dc
- Creator-line trades on a mega-shelf ("Wonder Woman by Pérez / Byrne / Rucka") collide on a reused GCD issue id
  across lines — match by the creator line's own volume number, never the reused id.

[L-267] sig:probe
- A GCD "collected edition" series sits beside the genuine RUN under a different id (Dept H 100362 vs 111468, Harrow
  County 89353 vs 94662, Sword Daughter 125748 vs 134585) — the `C` names the run; `lookup.py` by title finds it.

[L-268] folder:bucket pub:dark-horse pub:fantagraphics pub:gold-key-dell sig:legacy-stamp
- Long lines GCD splits by publisher era (Usagi Yojimbo: Fantagraphics 3475 / Mirage 4888 / Dark Horse 5623 / 2019
  relaunch 147897; Little Lulu's Dell era 539) — bucket a book's judged range by era before naming its `C` run.

[L-269] kind:collected-line kind:trade-only
- An omnibus bundling several minis gets one `C` per mini at that mini's full range; an anthology TPB ("… and
  Others") is not a numbered run and gets an `N`, not a forced `C`.

[L-270] kind:archive kind:golden-age pass:item pub:ec-archives sig:probe
- ITEM PASS (X-016..X-018): an archive line's `C` run is the reprinted Golden-Age ORIGINAL, and it is usually in
  the catalogs even when the shelf's `N` said "no record" (Creepy cv 2194 / gcd 1640, Crime Does Not Pay 943 / 296,
  Eerie 2300 / 1755, Frontline Combat 1443 / 834, Magnus 2157 / 1600, War Against Crime 11807 / 12814) — probe.

[L-271] pub:small-press sig:round2 sig:stamp
- round2-folder can stamp ONE wrong id across several UNRELATED shelves (gcd 212845 on three Avery Hill singletons,
  56736 on Dead Beats and Wait, What?) — never a leg, anywhere.

[L-272] folder:vn-year
- A ripper's `vNN` can be offset by one from the album's own subtitle (Carthago Adventures) — the title decides.

[L-273] sig:probe
- The Adventures of Tintin: every per-file gcd pick (60515/108543) was wrong; the shelf's own gcd pool matched by
  ENGLISH title was right for all 23 — the pool over the pick, again.

[L-274] kind:foreign kind:manga pass:item sig:twin
- ITEM PASS (X-019..X-021, manga): the per-file GCD pick reuses volume 1's title-only id across EVERY book of a
  manga shelf whose header already lists N numbered rows (A Bride's Story 12/12, Ajin 17/17, Assassination Classroom
  21/21) — match the header pool by ordinal, ignore the pick. Per-file CV lands on a foreign twin of the same
  title even more often (Milky Way, Egmont Ehapa, Glénat, Panini España, Carlsen, Altraverse, the Shogakukan
  original) — the shelf's identity is the ENGLISH edition; use the header's cv-issues pool.

[L-275] kind:collected-line pass:item sig:probe sig:trade-link
- A count-1/2/3 trade record the shelf's `N` called absent is often in the catalogs under a spelling the packet
  never probed ("The Adventures of …", "Complete Collection", "Deluxe") — probe before writing no-record.

[L-276] kind:manga
- GCD splits one manga line into a second publisher-branded series for its LAST volumes (Ajin v17, APOSIMZ v07-09:
  Vertical → Kodansha USA); `SELECT id, number, page_count, isbn, title FROM gcd_issue WHERE series_id=?` on the
  dump recovers the rows the header pool lacks.

[L-277] kind:manga pass:item sig:stamp
- ITEM PASS (X-022..X-024, manga): the reused per-file stamp is not always volume 1's id — it can jump to a new
  wrong series partway through a shelf (Berserk: three wrong series across v02-v41; Bleach cycles four before
  settling) — always re-derive from the header pool by `#N`, never from a plausible-looking pick.

[L-278] kind:manga
- Two parallel rip sets of one manga (Berserk .cbz + .cbr) take IDENTICAL per-volume ids from the same ordinal pool.

[L-279] kind:collected-line
- Blade of the Immortal (S23118) is the canonical "line + run" shape: GCD's 31 collected-edition rows are the `I`
  identities (cv=- — ComicVine mints only the floppy volume), and the floppy run CV 9069 is what every `C` names,
  ranges #0-206 ascending across the 31 books. A single-leg shelf's books inherit the shelf's 0.9, never more.

[L-280] kind:manga pass:item sig:probe
- ITEM PASS (X-025..X-027, manga): when GCD splits a line in two (an earlier series for v1-N, a later one
  continuing: Blood on the Tracks 156521 / 171046, CITY 123267 / 172756, Nagatoro 152847 / 171408) the packet's
  header pool shows only the shelf's STORED series — find the other with `lookup.py --contains` and pull its issues
  with `gcd_issue WHERE series_id=?`.

[L-281] folder:bucket folder:events folder:vn-year
- A chronology folder's ORDER prefix ("04 Dragon Ball - Full Color Freeza Arc v01") gets read by the per-file num
  matcher as the ISSUE number — v01 in folder "04" linked to issue #4; the true mapping is the shelf's own vNN by
  ordinal. Any "NN Title vNN" bucket naming is this trap.

[L-282] kind:collected-line
- A `gcd=-` / dump-only shelf's books inherit the shelf's 0.9 (Cells at Work! Lady, Dragon Head); an exact edition
  can exist on GCD alone and be absent from ComicVine entirely (Cat Shit One Omnibus GCD 186621 → 0.9, cv=-).

[L-283] kind:digital kind:manga pass:item
- A manga whose only catalog runs are indexed by tankōbon VOLUME cannot take a chapter-count `C` range (Dragon Ball
  Full Color: judged #1-17 are chapters) — `N no-record`, never an invented mapping.

[L-284] kind:manga pass:item
- ITEM PASS (X-028..X-030): a manga tankōbon line that GCD tags [Collected Editions] (Food Wars! GCD 95211) has no
  chapter-indexed run in either catalog — the `C OWED` marker is satisfied by `N no chapter-run`, the Dragon Ball
  Full Color ruling; the books ARE the run. Our single volumes against GCD's "two-in-one" record have no safe 1:1
  mapping → `gcd=s<series>` (Erased 118174), never an invented pairing.

[L-285] kind:collected-line pass:item sig:stamp
- ITEM PASS (X-031..X-033): JoJo's Bizarre Adventure part-shelves are double-stamped — per-file CV is the whole-saga
  hardcover omnibus volume (165667, one issue per PART) and per-file GCD the umbrella whole-saga series (27115) by
  raw issue number; both wrong for all 37 books. Parts 1-3 have no CV softcover record (cv=-); Parts 4-5 do.

[L-286] kind:collected-line sig:probe
- A shelf-level "no record" `N` can be wrong for an English product too (Innocent Omnibus: CV 157999 / GCD 206125
  under "Innocent Omnibus") — probe the edition's own name before inheriting the shelf's gap.

[L-287] kind:collected-line pass:item
- A series can be wrong on BOTH legs at once (Hunter x Hunter, Inuyashiki, Kakegurui Twin): the header pool by
  ordinal is the only source; an omnibus with no dedicated record on either leg is `N no-record`, never a mapping.

[L-288] kind:foreign kind:manga pass:item sig:stamp
- ITEM PASS (X-034..X-036): the reused stamp can jump FOUR times over one shelf (My Hero Academia: 105138 → 204933
  → 236032 → 228276 over 38 books, CV pinned on foreign 166768 throughout); a shelf can carry NO gcd pick at all
  (Muhyo & Roji's ×18). The header ladder by ordinal is the only source on manga — no exceptions found in 400 books.

[L-289] kind:collected-line kind:manga pass:item
- ITEM PASS (X-037..X-039): one GCD issue row can cover TWO of our volumes when GCD's count undershoots (PTSD Radio,
  Ojojojo) — both books share the id at 0.9; a genuine 2-in-1 omnibus whose header rows read #1-2 / #3-4 maps 1:1
  at 0.95 (Orb). A 2-vol English omnibus against a 4-vol Japanese original has no safe mapping → `gcd=s<series>`.

[L-290] folder:sequel-subfolder pass:item sig:stamp
- A spin-off filed in its own subfolder on the parent's shelf gets the PARENT's v01 ids stamped by the per-file
  matcher (Rent-a-(Really Shy!)-Girlfriend on Rent-A-Girlfriend) — `N no-record` (or the shelf note), never the
  parent's record; it is a misfiling for the lead.

[L-291] kind:collected-line kind:foreign pass:item
- ITEM PASS (X-040..X-042): a run you can NAME but whose GCD rows carry no page counts gives no safe per-volume
  range (SPRIGGAN Deluxe → GCD 188803) — `N`, not a guessed `C`; a per-file link can point at a foreign edition of
  the WRONG book entirely (Summer Wars: the Complete Edition has its own one-shot record CV 155900 / GCD 142966).

[L-292] sig:probe
- A stored English digital volume's cached rip may cover only its last issues (Suzuka #13-18 of 18) — the earlier
  books take gcd-only identities from the GCD series' full list, cv=-.

[L-293] kind:foreign kind:manga pass:item sig:stamp
- ITEM PASS (X-043..X-045, ~30 manga shelves): not one case where the per-file pick was right and the header pool
  wrong. A foreign GCD leg that undershoots by one (Twin Star Exorcists, 26 vs 27) → the last book takes gcd=-;
  a Vertical → Kodansha USA imprint split duplicate-stamps the later imprint's id onto the earlier book
  (Weathering with You) — reject the duplicate, keep it on its own volume.

[L-294] pass:item sig:probe
- ITEM PASS (X-046..X-048): a shelf can hold TWO published editions of one work with no shared pool (Monster: the
  18-volume line beside the 2014 "Two-in-One" Perfect Edition) — each edition needs its own pool or an honest
  no-record, never a mapping across editions. When a shelf's file count exceeds its pool, the excess usually
  belongs to a CONTINUATION volume the shelf's own `N` names (Ranma 1/2 v22-38 → CV 26409 / GCD 17651) — the pool
  for it must be pulled (`lookup.py --issues`, `gcd_issue WHERE series_id=?`) before writing no-record. GCD may mint
  a "collected edition" row for only the FIRST of a same-title pair (Zombie Makeout Club) — the second is gcd=-.

[L-295] kind:collected-line pass:item pub:marvel sig:gcd-notes sig:probe sig:round2 sig:stamp
- ITEM PASS (X-049..X-051, Marvel trades): a per-file pick flagged round2-folder / title-only / page-rematch is
  "check it", not "reject it" — several were right on independent verification (Han Solo & Chewbacca cv 146853,
  Darkhold gcd 1851609, Machine Man). An `identity-read` per-file id can still be a duplicate across two books
  (Doctor Strange Epic Vol. 01 and 13 both cv 626281) → 0.7. Fifteen "no record" shelf notes were resolvable under
  another spelling ("Complete Collection", "Omnibus (2022)", the creator-named line) — re-probe before conceding.

[L-296] kind:collected-line kind:magazine kind:manga pass:item pub:marvel
- A magazine whose files carry only a ripper-assigned continuous "Vol. NNN" (Weekly Shonen Jump: CV numbers by
  cover date, no page counts, no GCD leg) is unmappable → `N no-record` per book. An Epic Collection whose judged
  ranges are a CUMULATIVE count across several volumes (Doctor Strange: Strange Tales + two later volumes) has no
  single run id → `N` explaining the cumulative numbering.

[L-297] pass:item pub:marvel
- ComicVine's issue-number field follows LEGACY print numbering (Amazing Spider-Man 2015 v4 #792-801 under CV
  85076 / GCD 92892) — a `C` range in the 790s is still that volume.

[L-298] kind:collected-line kind:foreign pass:item pub:marvel pub:papercutz-nbm pub:valiant sig:barcode sig:legacy-stamp sig:probe sig:trade-link
- ITEM PASS (X-052..X-055, the tail): a book's trade record is often findable on its FULL subtitle when the packet's
  per-file probe returned nothing (~40 recovered: X-Men Legacy: Legion, House of M omnibuses …); Deluxe Editions on
  Valiant / Marvel shelves usually have their own count-1 record the per-file leg mis-picked. ISBN 978-2 / 978-2-375
  is French even on English-titled shelves. A Papercutz line can be a SELECTIVE translation of a longer foreign line
  (Ralph Azham v01-04 = tomes 3/5/8/10) — match by chapter title, never by ordinal.

[L-299] pass:revisit pub:dark-horse pub:dc sig:probe sig:twin
- R-024 (the item readers' shelf findings): GCD hides long runs behind a dropped article or suffix — "Flash" 3358
  (1987-2006, 232 issues) and "James Bond" 207986 (2024) are invisible to the shelves' own keys; two packets' "no
  GCD row" verdicts were wrong — probe without the article / the "007". A per-file CV id reused across a shelf's
  books can be a wholly different comic's issue list (Portman "Demon!" 1978 on Shiga's four Demon volumes) — a
  revisit re-verifies item-pass ids, never merely adds them. ComicVine's issue NAMES are strong `C` evidence
  (Flash 2010 "Case One, Part N"; POTA's four arcs). One ripper folder can be split across two shelves by filename
  wording (ten Usagi Saga deluxe files, 5 + 5). GCD indexes a deluxe line's 2nd-edition printing in the twin row.

[L-300] folder:bucket folder:events kind:collected-line kind:trade-only pass:item pub:idw pub:image ruling sig:collision sig:split sig:stamp sig:trade-link
- C-028..C-030: self-check before every S — "does my S line's id equal the id I just called wrong on the same
  shelf?" (three S lines carried their own `F wrong-cv-link` id; `--all` caught them as merge collisions). IDW-Hasbro
  "Chronology" bucket folders (G.I. Joe, TMNT) are read-order trees of fragments of several runs → split-needed.
  Same-bare-title-different-decade clusters (Powers v1-v5, Cyber Force) take one era's stamp across all — years and
  publisher decide. Image trade-only shelves carry the three-record trap (run + GCD "Collected Series" + a CV
  count-1 volume named after a subtitle) throughout. A SHELF batch with 0 `I` lines is incomplete and is sent back.

[L-301] pass:item pub:image pub:marvel
- Locke & Key is six separately numbered 6-issue seasons with a distinct count-6 record each (not arcs of one
  ongoing); Criminal's Image 2015-16 volumes are reprint PRINTINGS of the Icon originals (GCD 86891 title-matches,
  the first editions are CV 39022 / 39023 / 39025). A CV VOLUME id in `cv=` on an `I` line and a raw GCD SERIES id
  in `gcd=` (instead of `gcd=s<series>`) are the two commonest checker-caught slips — the `I` wants the issue row.

[L-302] kind:collected-line kind:foreign kind:trade-only pub:action-lab pub:idw pub:image sig:probe sig:trade-link
- C-031..C-033 (Image / IDW trade shelves): the matches-our-holdings collected-edition trap dominates trade-only
  Image shelves — the run wins; `lookup.py --contains` recovers the English "Outcast by Kirkman & Azaceta" (CV 75114 /
  GCD 81445) when the packet's probes return only foreign editions; genuine different-comics conflations still
  exist (Skyward: Action Lab 2013 vs Image 2018; Zero: Matsumoto vs Kot) — the years and publisher decide.

[L-303] folder:variant-covers kind:manga pub:marvel sig:legacy-stamp sig:probe sig:stamp
- C-034..C-036 (Marvel current ongoings, big manga runs): a 2022-2025 Marvel ongoing shelf is one clean per-file
  CV+GCD pair plus 1pp MikeNY76-Empire variant packs (ratio 2-11) — never a second run; the bare-title legacy
  volume (Avengers 2128, Captain America 2400, Fantastic Four 2045, Hulk 7053, Deadpool 6000, Daredevil 2190) is
  stamped across them — GCD's title-exact row is the reliable second leg, and when the run's own 2020s volume is
  not yet in CvVolume write cv=- (Phase C.1 fetches it), never the legacy stamp. GCD mints a fresh one-shot per year
  for annual Marvel specials (Crypt of Shadows, Timeless). A licensor / creator prefix recovers manga legs the
  bare-title probe missed (20th Century Boys); scanlation-only manga link the Japanese original at 0.7-0.9.

[L-304] kind:manga pass:item
- On a 60-volume manga run the `I` lines map the shelf's own file order onto the CV/GCD issue-list order (ordinal);
  that is the accepted precision at this scale — the item pass verified it held on 400+ books.

[L-305] folder:bucket kind:digital kind:ogn pub:marvel sig:collision sig:legacy-stamp sig:probe sig:round2 sig:stamp sig:withheld
- C-037..C-039 (Krakoa / From the Ashes X-Men, Marvel legacy umbrellas): on a 2019-2025 relaunch shelf the real
  id is often on a SIBLING shelf's "CV local rip" line rather than the shelf's own per-file block — sibling shelves
  of one vertical share the relaunch's id. Marvel Infinity Comics are a total GCD hole (`gcd=-` + provider-missing
  at 0.9). "Marvel Tales" (GCD 1747, retitled twice), "Marvel Super Special" (dropped "Comics" mid-run) and "Marvel
  Graphic Novel" (GCD 2658, 75 unrelated OGNs — not a run, no `C`) are single lines split across shelves by their
  own title changes → merge-with. The 2019-21 "Marvel Tales" one-shots are all round2-stamped onto "Marvel Tales:
  Avengers" 144637 — the dump's exact-title row every time. Long-running legacy ongoings (Marvel Team-Up, Marvel
  Comics Presents, Darkhawk, Exiles) surface as `--all` stored-id collisions with an earlier batch — same-comic
  fragments from several on-disk folders → merge-with, not withheld.

[L-306] kind:collected-line kind:new pass:item pub:marvel sig:probe
- `lookup.py --issues <volume>` returns the trade's own "Vol. N: <subtitle>" name — title + number matching our label
  is the fastest confirmation of a found CV id. 2024-2025 trades are the single largest genuine no-record cause
  (too new for the static rip / dump); a "Classic" / Epic / Masterworks line with a CV volume and no GCD counterpart
  (or vice versa) takes `gcd=-` / `cv=-` honestly.

[L-307] kind:collected-line pass:item pub:marvel sig:collision sig:comicinfo sig:legacy-stamp sig:probe sig:stamp
- C-040..C-042 (Marvel hero teams): the wrong-relaunch trap at franchise scale — CV 2128 / 2400 / 11492 / 2401 (the
  1960s-2004 Avengers / Captain America / Black Widow / Captain Marvel) stamped on 20+ relaunch shelves' trades; a
  bare-title `lookup.py --year <folder year>` recovered the true volume every time. Epic Collection lines: CV mints
  a volume per book, GCD keeps one series for the line → the singleton shelves share the line's gcd and merge. On an
  `S` line `gcd=` is a SERIES id; on an `I` line an ISSUE id — citing an Epic volume's issue on the S is the commonest
  typo. GCD can split one continuously numbered run at a mid-run retitle with no era change (Cloak & Dagger 1988-91:
  3656 / 14574) — majority per-file match, name both. A file's ComicInfo Web id decides a same-title collision
  (Avengers Infinity 2000 mini vs Hickman's 2013 "Infinity" HC).

[L-308] kind:collected-line pub:marvel sig:collision sig:comicinfo sig:stamp
- C-043..C-045 (Marvel D-I): a DEGENERATE stamp can cover ~470 files across several unrelated shelves (CV 150441
  "Iron Man Epic Collection: The Crossing" on S9575 and S97887-97891) — any id shared by hundreds of files of
  different titles is the stamp, never a leg. Epic Collection singleton shelves need explicit `F merge-with` chains
  between the siblings that share the line's GCD id (the checker enforces the merge). A ComicInfo Web id equal to a
  CV issue id is a 1.0 on any ripper.

[L-309] kind:collected-line pass:item sig:probe
- A collected line's own issue rows carry the trade's SUBTITLE as the issue title, so once one book of a line is
  identified, `--issues <volume>` or `gcd_issue WHERE series_id=?` resolves the whole Vol. 01-N run in one query —
  "own container id not probed" is never a valid no-record.

[L-310] pass:revisit sig:gcd-notes sig:stale-flag
- R-025 (Opus): the GCD dump's `gcd_reprint` + `gcd_story` tables answer "what does this trade collect" outright —
  join story → reprint → origin issue → series and the `C` range falls out (Elektra #7-22, Casanova Luxuria #1-7,
  all six Criminal ranges); `gcd_series.notes` states ranges in prose. A GCD line row with `year_ended` NULL and a
  count BELOW our holdings is NOT coextensive when the provider files the missing books under their own row
  (Casanova 148774 = 3 vols + Acedia 236165) — two lines, not staleness. Before `F wrong-cv-link` on a revisit read
  Series.CvVolumeId: the shelf may already carry the right id and re-flagging would clear it. A stored id can belong
  to a series holding NO comic files (outside the checker's population) — note it, never merge.

[L-311] folder:facsimile kind:archive kind:collected-line kind:foreign kind:weekly pub:marvel sig:comicinfo sig:legacy-stamp sig:probe sig:round2 sig:stamp
- C-046..C-048 (Marvel cosmic / Masterworks / Facsimiles): the `_Marvel Facsimile Editions` folder carries a
  round2-folder stamp (GCD 185205, a Panini France Moon Knight) on all 31 facsimile singletons — never a leg. Marvel
  Masterworks lines: CV mints no line volume (per-file picks are legacy-issue artefacts on the original series);
  GCD keeps one collected-edition series per line (sometimes a hardcover and a paperback round) — the Epic
  Collection shape catalog-wide. A UK weekly reprint misfiled under the US original's folder is 100%-stamped with
  the US run even when ComicInfo asserts the UK edition (Marvel Super-Heroes 1979) — ComicInfo Publisher decides.
  `lookup.py` by the exact CV record name recovers GCD rows the packet's dump block missed.

[L-312] folder:bucket kind:collected-line kind:kids kind:ogn pub:marvel pub:trade-house sig:collision sig:legacy-stamp sig:probe sig:round2 sig:split sig:stale-flag sig:stamp
- C-049..C-051 (Marvel N-T): Amazing Spider-Man's legacy run spans FIVE shelves sharing CV 2127 while GCD splits by
  era (1570 / 11288 / 92892) — merge-with chains satisfy the shared-id gate; Thor likewise (CV 2294). A creator-name
  folder ("Dan Slott Spider-Man") can bundle four real runs — its conflated flag is TRUE (R + split). A Spider-Man
  "OGNs, Minis & One-shots" folder shares one round2-folder stamp (GCD 65039 "Hooky") wrong on every singleton.
  Kids' tie-ins (Spidey and His Amazing Friends Golden Books) and prose novels are total CV/GCD holes — honest
  provider-missing. An Epic Collection singleton whose STORED CV link names a different volume than the file on the
  shelf (S881) is a data mismatch → R, not a stale flag.

[L-313] kind:collected-line kind:foreign pub:marvel sig:collision sig:probe sig:round2 sig:split sig:stamp
- C-052..C-054 (X-Men family): the per-file CV matcher stamps a FOREIGN publisher's same-title volume across whole
  franchises (Panini Comics "Wolverine" 69993 on nearly every Wolverine / X-Force / Cable / X-23 vN shelf; Panini
  España "X-Men" 56940 on unrelated X-Men v3 trades) — `lookup.py --year <folder year>` recovers the English id
  every time. The "_X-Book Epic Collections" folder: CV per book (issue id in the packet), one GCD line row per
  franchise, round2-folder stamps it AND it is right ("the dump names no other series") — 26 singletons chained by
  merge-with. The classic X-Men / Uncanny X-Men Annual line (#3-21, 1979-94) has no unified record on either leg →
  R + split-needed, duplicate fragments merge-with it.

[L-314] kind:collected-line pass:item
- `cvref.normName` drops "the / a / an / of / and" everywhere, not just leading articles — a word-preserving
  normalizer misses ~90% of exact matches; replicate cvref's tokenizer when matching titles by script. A bare
  trailing-number fallback (trade "01" → run issue #1) is safe only for mislabeled single-issue files, never for
  "Vol." / "Book" / dash-subtitled titles (it matched an 824pp omnibus to issue #1). One CV issue id reused across
  two differently titled books on a shelf is a sign of two eras, not one book. A digital "Collection Book NN"
  bundle is a different catalog shape from a "Vol. NN" trade and is often uncatalogued — honest no-record.

[L-315] pub:marvel sig:trade-link
- C-055..C-057 (Marvel V-X): "Flashback -1" (1997) and "AU" (2013 Age of Ultron) tie-ins are the host ongoing's OWN
  numbering (GCD has literal "-1" rows), never a separate volume. Modern "Vol. NN - Subtitle" trades often have
  their own count-1 CV volume with no GCD row — honest gcd=-. `F wrong-cv-link` is its own line, never text inside
  the S clause (the checker rejects it).

[L-316] folder:skottie folder:variant-covers kind:collected-line kind:ogn pub:caliber pub:dark-horse pub:marvel pub:millarworld pub:oni pub:small-press pub:titan sig:comicinfo sig:round2 sig:split sig:stamp
- C-058..C-060 (Marvel covers, Millarworld, indie / Oni): Millarworld chain-of-minis shelves (American Jesus,
  Chrononauts, Jupiter's Circle) hold co-equal seasons with NO coextensive line — R + split, not the one-leg
  exception, when the only "line" candidate merely matches our own holdings count. Loose OGNs in a flat publisher
  root folder (Oni Press) share one wrong round2-folder stamp ("Strangetown" 53172) — a folder-neighbour artefact;
  ComicInfo Web ids give clean 1.0s there. Publisher-change continuities (Deadworld Caliber → Arrow, Lenore SLG →
  Titan) keep one CV volume while GCD splits by era — majority per-file era on the S. A `\__Skottie Young Covers`
  singleton carries a round2 stamp to "Infinity" 75977 — never a leg.

[L-317] folder:bucket folder:variant-covers kind:collected-line pass:revisit pub:caliber sig:collision sig:legacy-stamp sig:withheld
- R-026 (withheld pairs): a shelf that is only a 1pp variant-cover rip IS the mini it covers — merge-with when the
  mini's shelf exists, else the run's ids + partial-rip. A "_Minis & One Shots" / "_Deluxe Editions" bucket filed
  beside a numbered run usually carries that run's volume as its stored link — the bucket is never the run (R +
  split + wrong-cv-link). A withheld id whose partner's link was already cleared has no collision left — write it
  plainly. A GCD publisher-era split that SUMS to ComicVine's count (Deadworld 9 + 17 = 26) confirms the spanning
  CV volume; the majority era by FILE count takes the S. Two shelves split by a possessive ("The Tick" / "The
  Tick's") are one comic. A legacy-renumbered tail (#300-304) is not a second `C` range on the same run — an `N`.

[L-318] kind:archive kind:digital kind:fan kind:strip pass:item pub:dc pub:tko pub:valiant sig:collision sig:probe sig:stale-flag
- C-061..C-063 (Valiant, Vertigo, strips): newspaper-strip yearly webrip compilations (Garfield, Nancy, Prince
  Valiant, Mark Trail …) are systematically provider-missing — CV/GCD index the comic-book series, not a strip
  archive. An ongoing anthology split across per-story shelves by design (TKO Shorts, the World of Warcraft
  webcomic) is chained by merge-with, not two comics. Fables is one 161-issue run (CV 9723 / GCD 10549) across seven
  sibling shelves → all merge into one. A coincidental CV volume-id / issue-id collision can slip past the
  checker's existence test — an `I` line's `cv=` must come from `--issues`, never from the volume list. And the
  hard S-gate on an open flag is NEVER a reason to write R: S + stale-flag when the shelf is one run or a
  coextensive line (a reader refused ten such shelves and was sent back).

[L-319] folder:bucket folder:events folder:variant-covers pub:marvel pub:zenescope sig:round2 sig:stamp
- C-064..C-065 (Marvel event chronologies, the tier C tail): GCD indexes Zenescope's year-restarting "Grimm Tales
  of Terror" as "Volume N" series per year (82049 / 93359 / 111761 / 123675). On "_Marvel Major Event Chronology"
  tie-in shelves the per-file GCD matcher reuses one franchise-wide round2 stamp (Savage Avengers 144638 on six King
  In Black singles) — the dump's exact title + year row every time. 2024 "Blood Hunt" one-shots are 1pp
  variant-cover packs at ratios up to 8 and still identify cleanly by the exact-title pair.

[L-320] folder:events kind:archive kind:fan kind:new pub:archie pub:comixology pub:dc pub:small-press sig:barcode sig:probe tier:D
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

[L-321] kind:foreign kind:new pub:dark-horse pub:drawn-quarterly pub:fantagraphics pub:first-second sig:probe sig:split tier:D
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

[L-322] folder:bucket folder:graphic-novels folder:variant-covers kind:fan pub:heavy-metal pub:trade-house sig:barcode sig:probe sig:twin tier:D
- TIER D (D-007..D-009): the `Graphic Novels` bucket's publisher sub-folders are TRADE houses — GCD indexes them,
  the CV rip largely does not (cv=- gcd=<row> + provider-missing at 0.9), and GCD mints TWIN rows for
  HarperCollins / Andrews McMeel / Abrams / Scholastic books more often than not (0.9). Heavy Metal's 1977-79
  album line is in both catalogs under the ALBUM's own title — the `Heavy_Metal-19NN-PR-` prefix hides it; probe
  the bare title. A `\Covers` folder of 1pp index sheets ("84Monthly.cbz") is R + not-a-run (unlike variant-cover
  packs, which identify their mini). Files tagged "(compilation)", "(panels)", "Portfolio", "(short)", "from
  <anthology> #NN" are reader extracts → not-a-run. Search `gcd_issue.barcode` on the 11-digit UPC core, not only
  the EAN-13 / ISBN — it found four barcodes the full-code query missed. `cvref.normName` drops "the" everywhere:
  probe "Star Trek: Next Generation".

[L-323] kind:digital kind:foreign kind:magazine kind:manga kind:new pass:revisit pub:heavy-metal pub:idw pub:mad sig:barcode sig:probe tier:D
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

[L-324] folder:vn-year kind:foreign kind:manga kind:trade-only pub:marvel sig:collision sig:probe sig:trade-link tier:D
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

[L-325] kind:archive kind:kids kind:strip pass:item pub:idw pub:papercutz-nbm pub:top-shelf pub:trade-house sig:barcode sig:collision sig:gcd-notes sig:probe tier:D
- TIER D (D-016..D-018): a ripper's per-issue suffix gives every issue of one LINE its own shelf (61 Garfield shelves
  = one Ballantine line CV 39429 / GCD 70018) — survivor takes the S, the rest merge-with. On small-press
  singletons the PUBLISHER LIST beats every name probe (`gcd_series WHERE publisher_id=?` + `cv_vol.publisherName`);
  GCD's first-printing page count pins an unlabelled album rip; `gcd_series.notes` can name the shelf outright —
  read notes before refusing on a title mismatch. Top Shelf post-2016 is filed under IDW in GCD (ISBN 1-60309) and
  absent from ComicVine. Year-by-year web-strip archives are provider-missing however famous (the catalogs hold the
  strip's BOOKS). Papercutz / NBM kids lines span in both legs under NBM — probe the LINE. A barcode UPC core shared
  by every file identifies the SERIES (1.0 on the S, `gcd=s<series>` on the I). A book whose only record is a
  volume-level CV row with no cached issue and no GCD series is covered by `N <item> no-record` naming the volume.

[L-326] folder:bucket kind:archive kind:strip pass:revisit pub:trade-house pub:zenescope sig:barcode sig:probe tier:D
- TIER D (D-019..D-020, R-027): GCD writes "Gen 13" with a space (and "Gen 13: Interactive"); the GCD PUBLISHER column
  on 1970s-80s paperback strip books is the parent house (Charter / Jove / Ace → "Berkley Books", ISBN 0-441; Tempo
  → "Grosset and Dunlap", 0-448) — never reject on it, read the ISBN prefix; GCD can put the COVER YEAR inside a one-
  shot's series NAME ("Cerebus Not the World Tour Book 1995"), defeating exact and --contains probes — list the
  publisher's series. An "origin part 1 / part 2" pair is usually "0" and "00" of the ORIGINAL run. A webcomic's
  volume divisions need not map onto its printed books (pair at 0.9 as a format split, N not forced I). Zenescope
  one-shots carry 6-7 GCD variant rows — the base row (with a page count) is the I id. Year-by-year strip archives
  are provider-missing even where the catalogs hold the strip's BOOKS. A second rip of a landed book copies the
  sibling's ids verbatim; moving both to the run is a second-read edit, never a one-sided one.

[L-327] kind:collected-line kind:trade-only pass:item pub:marvel pub:star-wars sig:barcode sig:probe sig:round2 sig:stamp
- ITEM ROUND 2 (X-056..X-058): on a shelf whose S is a collected LINE, the book's `C` run is usually a DIFFERENT id
  from the S (the line's counts match the TPB count, not the floppy run) — probe for the run (Star Wars 2015 line →
  run CV 79398 / GCD 85861; Amazing Spider-Man 2022 → CV 142577 / GCD 184161; Incredible Hulk 2023 → CV 151522 / GCD
  201474). The packet's "linked cv A/B" is issue A in volume B — when B equals the shelf's own volume it is usually
  the book's genuine record. A degenerate pick reused identically across many books of one shelf is a stamp;
  round2-folder is right about as often as wrong here — right when the S clause already gives an exact ISBN/title.

[L-328] kind:collected-line kind:trade-only pass:item pub:caliber pub:marvel sig:probe sig:stamp
- ITEM ROUND 2 (X-059..X-061, Marvel): a shelf whose S pairs a floppy run (cv=) with a TPB line (gcd=) on ONE line
  needs the `C` to name the run's OWN GCD floppy series — never restate the TPB line's id as the run. GCD's
  `[Omnibus]`-bracketed title hides a record from an exact probe with the plain word (Asterix, 8 books). When the
  packet gives no per-file picks at all (What If…? v2) or only a stamped wrong id (Deadworld), the filename's own
  issue number matched against the shelf's own cv/gcd issue pools (queried off the dump) is a clean mechanical
  resolution. A Masterworks / Epic "judged" number can be the collection's VOLUME ordinal, not an issue span → N,
  not a C. One GCD id stamped across several different omnibus shelves (172199 on three) is a stamp, never a leg.

[L-329] pub:dc pub:marvel kind:collected-line
- DC/Marvel trade lines often store a GCD row that belongs to a FOREIGN or other edition (Urban Comics' French GL
  2011, a German GL 2023 Vol. 1) — check the stored row's series before using its roll-up. GCD carries twin HC/SC
  series for most 2006-2016 DC/Marvel trades (77116/77117, 77936/77937) → cap those I lines at 0.9. CV keeps count-1
  "Title: Subtitle" volumes per trade before ~2016, numbered TPB-line volumes after (R-054).

[L-330] sig:legacy-stamp
- After a split or relaunch, a shelf's year span (and "year gap" signal) can come from per-file CV stamps of the
  OLD volume (1965-68 cover dates on a 1999-run shelf) — read the filename years before hunting a foreign file.
  Per-file stamps survive a Series-row fix; don't read them as evidence against a landed S (R-054, R-058).

[L-331] pub:marvel
- Amazing Spider-Man: CV 2127 keeps #1-441 AND #500-700 as one volume; GCD splits 1570 (#1-441) from 11288 (1999
  #1-58 + #500-700) — a #500+ trade on the 1963 shelf needs a C with gcd=11288. Detective Comics: GCD 60830 spans the
  New 52 #0-52 AND Rebirth #934+ (CV splits 42594 / 91098); a GCD series goes on the S line of the run its
  numbering STARTS with (lead ruling, waves 45/46).

[L-332] kind:collected-line pub:marvel
- Epic Collection one-rule (lead, wave 44): each Epic book is its OWN shelf; its S line carries the book's own
  count-1 CV volume and `gcd=-`; the line's GCD series (74602 ASM, 74592 Iron Man) goes on the book's I line with
  its ISBN; C lines name the runs reprinted. `lookup.py "<Line> Epic Collection" --contains` lists every volume.

[L-333] pub:2000ad
- Judge Dredd: the "2000AD #<prog> Judge Dredd" story extracts are fan cuts, NOT a run (C-002/L-185) — R + F
  not-a-run. Rebellion's Essential Judge Dredd has one CV volume per book and GCD indexes only Vol. 01 (181497) and
  Vol. 07 (223094) → provider-missing for the line. Strip collections reprint prog STORIES, not whole issues — no C
  onto 2000 AD issue ranges. Zenith: GCD 86812 is the 4-book line, CV one volume per phase → the Epic shape.

[L-334] pub:aftershock pub:boom sig:probe
- AfterShock / AMP / Aspen / Boom sequel minis: GCD often DROPS the article or files by ordinal, licensor/creator
  prefix or the first book's subtitle ("Maniac of New York: Volume 2", "Edgar Rice Burroughs' the Moon Maid…",
  "Michael Turner's Soulfire Core") — probe `--contains` on a fragment and `--gcd-series` before provider-missing.

[L-335] pub:archie
- Archie 2019+ "Archie & Friends" one-shots: GCD 188076 is ONE numbered series (Back to School = #3) while CV mints
  a volume per one-shot — the holiday split (L-056): the GCD row goes on the I line.

[L-336] sig:split
- Before proposing a split key, check the items' CURRENT key (`lookup.py --shelf`) and `--who-stores` both the run's
  cv and the key: a key already aliased to the parent (a re-merged wave-2 split-out) "would not move" — respell it
  (P-016 "The Amazing Spider-Man v2 (1999)"); a key already owned by a live run shelf is a JOIN (P-016 Detective v2).

[L-337] pub:dc sig:probe
- DC: GCD and CV both file relaunches under "&" ("Batgirl & the Birds of Prey", "Red Hood & the Outlaws", "Batman &
  the Signal") — probe the "&" AND "and" spellings before provider-missing. GCD's collected-edition series for DC
  relaunch trades are separate same-name rows of 2-5 issues (77516, 115808, 74348, 113651, 33019) whose per-book
  "collects" notes give exact C ranges. A "GCD says" row on a JLA trade can be a German Panini edition (78606) — void
  it on an N line (R-062).

[L-338] pub:cinebook
- Cinebook's "Valerian and Laureline By…" line is GCD 127475 "Valerian and Laureline" [en-gb] 2018-? (R-062).

[L-339] pub:europe-comics kind:foreign
- Europe Comics: "provider-missing in any language" was WRONG on 12 of 22 shelves (R-063). Probe the French title
  with accents dropped (normalisation turns "é" into a space) and the Dutch Dupuis / Silvester or German Salleck
  editions; link the original / other-language row per L-024 / L-068. Dupuis Dutch lines are often filed twice
  (125966/125967 — the pair differs in ISBN, the second carries page counts). Europe Comics can split one French album
  into two parts (Les Filles de Salem 132027 = Daughters of Salem Parts I-II).

[L-340] pub:dark-horse pub:dynamite
- Dark Horse / Dynamite trade shelves: the packet's own "GCD dump" line sometimes already names the run an earlier
  reader called missing (Bettie Page 21042, Complete Emily 103671) — read it before re-stating provider-missing. Dark
  Horse's Savage Sword of Conan volumes each reprint ONE Marvel run (CV 2701 / GCD 2187) → run-wins, C per volume
  from GCD's notes. MOTU mini-comics: CV files the whole line as ONE 49-issue volume (20533) while GCD mints a series
  per mini-comic — the L-056 split with CV on the spanning side: the CV id goes on the I line (R-063).

[L-341] sig:probe
- GCD name shapes that hide a record from an exact probe (R-065): a dropped licensor/author prefix (Insight "H. G.
  Wells:", FSG "Shirley Jackson's"), a dropped leading "The" (DC "Origin of Hordak", IDW "Dark Judges"), a dropped
  subtitle (IDW TMNT "Saturday Morning Adventures"), a BRACKETED subtitle (IDW RID 2015 "[Animated Series]"), a spelled
  volume ordinal ("Comic Book History of Comics Volume 2"), a lengthened title (First Second "Prince of Persia the
  Graphic Novel"). CV spells "'Til" where rippers write "'till".

[L-342] pub:fantagraphics
- Fantagraphics singletons are often ONE numbered row of a GCD line (EC Artists' Library, Freak Brothers Follies) while
  CV mints a volume per book — probe the LINE name before provider-missing. A line spanning several works keeps its
  own identity; a one-volume edition / omnibus of ONE run on its own shelf is a merge into the live run shelf (Bone,
  Mask of Fudo, Tenement — R-065).

[L-343] pub:dark-horse pub:idw pub:marvel sig:probe
- More GCD name shapes (R-067): Dark Horse High Republic Adventures inserts "(Phase III)" and uses a comma form
  ("…Adventures, The Nameless Terror"); IDW / Disney / Marvel Illustrated drop a leading "The" or "A Graphic Novel";
  Marvel's Dark Tower strips punctuation ("Drawing of Three"). GCD indexes NO Marvel Infinity Comics (gcd=- stands).
  An Image-style deluxe / "Complete" edition of ONE run merges into the run shelf (Department of Truth).

[L-344] kind:manga
- Manga (Yen Press / Seven Seas / Kodansha / Viz): the fixed CV index now reaches the count-1 ENGLISH volumes that
  D-012/D-013 called provider-missing; an English CV record replaces a third-territory GCD licence (a German / French
  row) as the identity (R-067).

[L-345] pub:marvel sig:probe
- Marvel on GCD (R-069): "&" for "and" (Infinity Gauntlet, Gods & Gladiators, Rise & Fall); runs filed under an imprint
  or shortened name ("Punisher MAX: …", "Ant-Man and Wasp Prelude", "Spider-Man: Spider's Shadow") — a trade's
  `--collects` roll-up names the run's GCD id fastest; a special's name carries its cover year ("Punisher: X-Mas Special
  2006"). Masterworks lines HAVE a CV line volume with "Volume N" issues (23402 ASM, 35197 FF, 35188 Sub-Mariner, 59106
  ToS, 23450 TtA) → the line is the S (it spans several titles), one I per book; GCD keeps separate series for second
  printings (FF 61146, ASM 52975) — match by year. Digital-first runs (Daughters of the Dragon 2018, Back to Basics,
  Long Live the King, Purple Daughter) have no GCD series: provider-missing is right.

[L-346] sig:probe
- GCD name shapes (R-071): a run filed with its subtitle DROPPED (Oni "Dead of Winter" 116730, the subtitle moved onto
  the trade row); ", Chapter II:" where CV writes " II:"; an event prefix ("Doctor Who Event 2016:"); "Special
  Edition" appended to KODT specials. Titan Doctor Who "Year Two" runs ARE in the fixed CV index (84960 / 87162) — L-152's
  "not in the ComicVine rip" is obsolete; the "Year Two v04" trades are run-wins into the issue shelves. Marvel event
  chronology trade shelves (Death of Captain America, New Guard) are run-wins into the main run shelf.

[L-347] sig:probe
- Name shapes that hid records (R-072): a creators' prefix on BOTH legs ("Paknadel & Trakhanov's Turncoat", Boom);
  ComiXology Originals writing a symbol as a word ("dotSelf" = ".Self") and one-word GCD spellings ("The AllNighter");
  biography books that are English editions of Italian BeccoGiallo originals (link the Italian row, L-024); a 2019+
  Rebellion strip collection as a numbered volume of a GCD book line named for the source weekly ("Wildcat" #2 =
  Loner); "Magazine" / "Year One" suffixes (Archie, Action Lab) — for a one-volume "Year One", pages / issue size
  picks the run. A DC deluxe of one run or one one-shot is run-wins (GCD's notes say which).

[L-348] pub:dc sig:probe
- DC (R-073): the rip holds count-1 CV trade records under "Title: Subtitle" (Justice League Vol. 1-8, Teen Titans 2014,
  Young Justice 2019, Doom Rising, Oz Effect) — probe the SUBTITLE with --contains. One-shots filed by event name ("Flash
  Special" = the 50th anniversary special, "Joker: Last Laugh Secret Files"). CV keeps Daring New Adventures of Supergirl
  #1-13 + Supergirl #14-23 as ONE volume (3113); GCD splits 2626 / 2722 — a C line per leg.

[L-349] pub:dark-horse sig:probe
- Dark Horse FCBD flipbooks are filed on BOTH legs as "A/B - Free Comic Book Day 2006 Special"; Polar is a GCD album line
  (88420) while CV mints a volume per album. D&Q / accented names match ONLY with the accent ("Castrée", not "Castree");
  CV's volume start year can be wrong while its issue date is right (Blabber: volume 2021, issue 2011) (R-073).

[L-350] kind:archive kind:golden-age pub:dark-horse pub:ec-archives
- L-238 is SUPERSEDED for single-run archives (lead, 2026-09-23): the brief's later R-042 ruling (a line that reprints
  ONE numbered run gives way to the run) wins — Dark Horse SSoC -> Marvel SSoC (wave 52), Doctor Solar Archives -> the
  1962 run (P-019). The archive LINE keeps its own identity only when it spans SEVERAL runs / titles (EC Archives, L-109).

[L-351] pub:dynamite sig:probe
- Dynamite: CV parks The Shadow's #0 and #100 specials in a catch-all volume (75720) — a HULL, not a second run. The 2019
  Vampirella FCBD file is #0 of the 2019 run (its EAN = GCD's #0 barcode). Filename misspellings ("Jordon") and one-word
  spellings ("BadGuys") hid real records — probe the corrected / spaced spelling. Quality's American Judge Dredd: CV 3928
  keeps #1-61 as ONE run while GCD splits it at #8 (3341 / 3511) (R-075).

[L-352] kind:foreign sig:probe
- Scanlations of French albums (Glénat / Casterman / Dargaud; "(F)" / "(Scanlation)" files): the English title is a
  translation — probe the FRENCH title with its accents kept ("joueur d'echecs" = 0 hits, "Joueur d'échecs" hits) and
  link the original (L-024). A leading numeral can be part of the title ("2 Sisters"), not a ladder number. Rebellion
  strip collections sit under the line name "War Picture Library" #[n] (the Wildcat shape, L-347). GCD splits the
  1985-87 GL Corps annual by cover title (33216 / 33217 / 2978) where CV keeps one volume (21324); GCD brackets the
  ripper's running Abe Sapien number ([6], [9]) (R-076/R-077).

[L-353] pub:ac pub:idw pub:dynamite
- A retitle that CONTINUES the numbering is one run even when both catalogues split it by title (AC: Fun Comics #1-2 ->
  Bill Black's Fun Comics #3-4, GCD 19985 numbers from #3) — L-212 applied (R-079). IDW "Usagi Yojimbo: Origins" has
  its own CV line volume (135288) spanning four colour-reprint minis. Dynamite "Seduction of the Innocent vNN" is the
  2019 Vampirella run's own trade line (GCD 169778). Action Lab's Raven trade line and Red Rising: Sons of Ares are
  GCD-only volume lines (120710, 122712) while CV mints a count-1 volume per trade.

[L-354] kind:foreign sig:probe
- A book that sits INSIDE a line is fastest found by the dump's `gcd_issue.title` / `gcd_story.title` and cvref's
  `cv_iss.name` (Colección Humanoides #9 "Corazas"; Alef-Thau's "Diamantha!" by its chapter titles). Heavy Metal
  "Related_Publications" albums are English editions of Humanoïdes / Norma / Glénat books — link the original by chapter
  or subtitle match (L-024). Norma is Royo's HOME publisher (the original, not a third territory). Scholastic prose
  chapter-book lines (Geronimo / Thea Stilton spin-offs) are absent under their Italian titles too: provider-missing
  is final. Quarto's Saturday AM GCD rows are still 0-row stubs (R-081).

[L-355] pub:image pub:idw kind:manga sig:probe
- Image: GCD splits one run BY PUBLISHER while CV keeps one volume (Battle Chasers 10169/10170/10171; G.I. Joe 2001 =
  9845 Image + 12003 Devil's Due). EC/DC's MAD magazine is CV 9318 / GCD 10092 (probe "Mad" --year 1952); MAD books
  spell "Super Heroes" as two words. IDW one-shots carry the year in the series name ("Yearbook 2019"); IDW's Library
  of American Comics strip lines exist on BOTH legs ("Rip Kirby: The First Modern Detective") — L-164 is wrong for Rip
  Kirby. Manga: the hyphen / macron romanisation finds Japanese originals on CV ("Kami-sama no Iu Toori", "G-Senjō");
  the accent finds Yen's English volumes ("Éclair Orange"); dump-only Japanese GCD series (Kingdom 175348) have usable
  issue rows. A "⚠ stored GCD row is another book" is void when the filename carries the line number (R-083).

[L-356] pub:marvel kind:manga sig:probe
- Marvel: CV sometimes mints a floppy one-shot as "Marvel Voices: X" (no 's), separate from the "Marvel's Voices: X" TPB
  — probe both. Manga scanlations of UNTRANSLATED work: the Japanese-original row exists when the English title returns
  nothing (CV "Kizuoibito" = Wounded Man, GCD "Nanbā Faibu" = No. 5) — probe the romanised Japanese title. Titan Star
  Wars Insider specials are in the CV rip under their subtitle. A packet's `--id` can show a BLANK CV volume name
  (2909) while `--issues` reveals the real one — check before writing needs-web (R-084).

[L-357] pub:marvel pub:image sig:probe
- Marvel (R-086): CV mints one volume per creator "Book" line (Ms. Marvel Book 1-5 = CV 83966 / GCD 91839); "Trial of
  Venom" = Spider-Man Special Edition #1; Deadpool #900 is Deadpool Team-Up #900 on GCD (40644) though CV puts it in
  Deadpool 2008; Osborn Journal is filed under a bracketed alternate title. Image / Millarworld: CV 65439 "American
  Jesus" and 85113 "Jupiter's Circle" are the trade LINES (issues named "Book One/Two"), not the minis — their conflated
  flags are stale (L-318); Dark Horse's 2024 Millarworld reissues are a NEW GCD line beside the Image originals (Magic
  Order 213614) — match by the file's year. A packet's "years a-b" can come from per-file stamps (read filenames). In a
  `lookup.py --batch` file a multi-word name must be QUOTED or only its first word is probed.

[L-358] pub:oni sig:probe
- NBM: the Dungeon cycles have a CV and GCD record per CYCLE; the bare "Dungeon" 51364/19068 is the 28pp floppy
  serialisation. GCD numbers NBM's Nancy Drew and Dungeon albums by volume ordinal with matching titles. Malibu's 1993
  "Street Fighter" (18369/15154) is filed by rippers as "Street Fighter II (of 3)". Crusade's Tucci Shi books have
  CV-only records (GCD holds only later printings); Europe Comics' SHI is an unrelated comic under one title. Oni: the
  trades of the minis have their own count-1 CV/GCD records (L-143 shape). One distinctive word probed with
  --contains ("Booger") can find what the full title misses (R-088).

[L-359] pub:titan pub:valiant kind:strip sig:probe
- Titan: licensed runs can be filed under a SHORTENED name on both legs ("Templars" = Assassin's Creed: Templars,
  "Tekken: Blood Feud") — probe the title's last word with --contains. Acclaim / Valiant: GCD spells "&" ("Captain
  Johner & the Aliens") and drops the franchise word ("X-O Database"). Strips: Penny Arcade, Wallace the Brave and
  Prince Valiant pair with their own BOOK lines at 0.9, like Rip Kirby (L-355) — the year-file shelf merges into the
  book-line shelf. ASM Omnibus Book 01 (2022) is GCD 58631's "#1 [Fifth Edition]" row. A split key must differ
  from the files' current key AFTER normalisation (L-336) (R-089/R-090).

[L-360] pub:image pub:dc sig:probe
- Wildstorm / Image: "Gen 13" needs the SPACE on both legs; every Gen13 one-shot has a count-1 record on both legs, so
  a "Gen13 Assorted" bucket is a split, not provider-missing; CV 5667 is the whole #0-77 run while GCD splits it 5417
  (Image) / 14809 (DC). DC/Wildstorm World of Warcraft rips named "v2 #NNN - Title (MM-DD-YYYY)" are the 2008 ongoing
  (CV 19669), their date = GCD's on-sale date. Blackthorne 3-D titles live on CV only as issues of "Blackthorne 3-D
  Series" (12050); GCD gives each title its own series (Gumby in 3-D = 38332). NBM Dungeon: Zenith is CV 52034 / GCD
  55263; the 2021 GCD "Dungeon: X" rows are the re-edition line. Crusade's 2020-22 Tucci Shi books are CV-only (GCD rows
  are 2024-25 reprints) (R-091/R-092).

[L-361] pub:archie pub:dark-horse pub:boom
- Archie holiday specials: GCD names each series by COVER year (on sale Dec Y = series Y+1) while CV uses the on-sale
  year; page count separates them (24pp up to Dec 2019, 36pp from Dec 2020). Sabrina (2000) #1-37 and Sabrina the
  Teenage Witch (2003) #38-104 are one continuous numbering, but BOTH catalogues cut at #37/#38 — two runs. Dark Horse /
  Berger's Everything Vol. 2 collects "what would have been #6-10" (trade-only issues). Boom's Fence and Adventure Time
  OGN lines: some trade-line volumes are OGNs — no issue range applies. A 2000 AD strip-collection's prog ranges are N
  lines, never C lines onto the weekly (L-333, reaffirmed R-094) (R-095).

[L-362] pub:archie pub:boom kind:digital pub:dc
- Archie Forsythe-DCP Christmas Spectaculars: the filename year is the COVER year and ComicInfo Notes carry the UPC,
  which equals GCD's row barcode — a 1.0 assertion. Boom: a trade line spanning several runs (Wynd Books, WWE Then. Now.
  Forever., MM/PR Deluxe) is its own identity with C lines onto each run. ComiXology: a per-file CV link to a count-1
  volume ("Digital Collection One", "TPB") is the collection's OWN record (its I line); a link to the 5-8 issue serial
  means the run wins. DC's Green Lantern trades exist as HC (77116) and SC (77117) GCD series — pick ONE edition per
  shelf (R-096/R-097).

[L-363] pub:dc
- DC New 52 trade lines on GCD are HC/TPB TWIN series (BTDK 78638/78639, Batwoman 66358/71298); CV holds per-trade
  count-1 volumes that subtitle probes often miss. The retitle cut shows on GCD only (Fury of Firestorm: The Nuclear
  Men = 61180 #0-12 + 171997 #13-20 = CV 42987's 21); L.E.G.I.O.N. / R.E.B.E.L.S. are split into year series. A "#DC
  Events" shelf holding #41-45 of a New 52 title is a FRAGMENT of the run shelf — merge it in. Deathstroke the
  Terminator 1991 = CV 9645 (#0-60) while GCD splits it 10465 (#1-40) / 15127 The Hunted / 15128 (#48-60); the 1991
  trades (GCD 88153) collect #1-34 (R-098/R-099).

[L-364] pub:dc
- DC (R-101): GCD's notes can disagree with GCD's own REPRINT ROLL-UP (Batgirl 2016 Son of Penguin: notes "#7-12",
  roll-up #7-11) — the roll-up and the next volume's range decide; the packet flags RANGE CONTRADICTED only against
  notes, so wrong judged ranges can hide without the flag. DC Vertigo: a complete short mini in a "vN" subfolder of a
  Golden/Silver Age title's shelf (Witching Hour 1999, Weird War Tales 1997) is a CO-EQUAL run, not residue. "#DC
  Events" New 52 read-order folders carry whole runs onto the pre-2011 shelf of the same title (Batgirl 2000). A retitle
  that continues the numbering stays one run (L-212) even when both catalogues and the folders split it.

[L-365] pub:dc
- DC "#DC Events" read-order rips named "Title NNN - Month Year" or "Title # NN" are NEW 52 issues whose numbers fall
  inside the legacy ladder — a ladder check cannot see them; grep the filenames on any big legacy shelf (Batman 1940,
  Birds of Prey 1999, Green Arrow 2001 all hid other runs). One-run trade lines (Deluxe Editions, the "Batman: Detective
  Comics" line, R.I.P. Vol. 07, the Bronze Age omnibus) take the run's ids and merge into the run shelf. GCD notes can
  typo the series year (Batman B&W Vol. 1 says "1986 series" for the 1996 mini). CV 19721 is "Azrael: Agent of the Bat"
  (#47-100, the 1995 run's retitle); the 2009 relaunch is CV 28873 / GCD 39888 (R-103, P-035).

[L-366] pub:dc sig:split
- A split-out shelf INHERITS its parent's per-file legacy stamp on 100% of its files — every new vN shelf needs F
  wrong-cv-link for that stamp. "#DC Events" New 52 folders hide ANNUALS too ("Batman Annual # 3" = the New 52 annual
  shelf, not the monthly). DC "Bronze Age" trade lines: CV naming is swapped — 107354 (named "Omnibus", 2 issues) is the
  TPB line, 98324 (3 issues) the Omnibus; only the dates tell them apart. C lines written in an ITEM batch (X-) survive
  a later shelf re-read (R-): a revisit must restate or retract them explicitly (R-104, S5272).

[L-367] pub:dc
- DC: collected PARTS of a mini can each add a one-shot (All-Out War Part 1 carries Hunters #1, Part 2 carries Killers
  #1) — check GCD notes for extra runs on any "Part NN" collection. GCD 97270, the 2016 "Doom Patrol Book 01-03" line,
  reprints the 1987 Morrison run (#19-63), not the 2016 run. A "(1998-11) 1000000" One Million file belongs to whichever
  volume was running in 1998. DC Deluxe Editions: GCD keeps the earlier TPB beside the Deluxe (DCeased 2021 236pp vs
  2023) — pick by page count. Label-ambiguous ContainmentFlags do NOT gate an S and cannot take `stale-flag=`; answer
  them on an N line (R-106).

[L-368] pub:dc sig:split
- R vs S + split (lead, wave 95): a CO-EQUAL second run on a shelf means no identity can stand until the split — R +
  split-needed (JL 3000/3001, JLA: Year One). A few MISFILED books that belong to another live run are residue — the
  shelf keeps its S, plus `F <sid> split-needed | items -> join S<run>` (Batman 2011 holding two 2016 trades). A
  separate annual series is its own run and takes its own key. DC "#DC Events" "(New 52) Title NNN (2024)
  (digital-mobile)" re-rips are DUPLICATES, not another run. GCD's New 52 / Rebirth / 2021 trade lines are HC/TPB twins
  (JL 76777/76126, Nightwing 179196/201017, Harley 88183/88152, Injustice 2 117796/123824); X-batch 0.7 I lines often
  cited the Panini / Urban / Televisa row. A collected-series id stored as a shelf's GCD leg makes a judged range look
  impossible (signal c) — point the S at the run. Backup-feature reprint lines get an N, not a C. Digital-first DC series
  take the print GCD series as their second leg at 0.9 (R-105/R-107).

[L-369] pub:dc sig:split
- CLARIFIES L-368 (lead, wave 97): ANNUALS OF A MONTHLY that sit on the monthly's shelf are residue — S at <=0.9 with
  the annuals named, no split (GL 1990 Annuals, Deathstroke / Lobo / Firestorm / JLA Annuals). Only a shelf that IS an
  annual series (the Legion Annual 1985 / 1990 shelves) takes its own key. An L-212 retitle chain stores its FIRST
  record once the chain's opening issues are on the shelf (JL 1987 CV 3796 -> JLI 3798 -> JLA 4198; Azrael 5490). DC
  "#DC Events" Post-Crisis read-order folders hold UNDATED "Title 001.cbr" rips of the 1987 series inside a New 52
  ladder — grep for undated names (extends L-365). X-batch I lines on "<Title> by <Creator> Book NN" lines were often
  attached to same-name rows (Spanish ECC, a 2025 relaunch) — check `--gcd-series "<Title> by <Creator>"` (R-108).

[L-370] pub:dc sig:split
- GCD 59922 "Action Comics" spans New 52 #0-52 AND Rebirth #957+ (the inverse of L-198); a shared gcd between S94962
  (New 52) and S94961 (#957+) is benign — they stay apart on CV. A return to LEGACY numbering after a relaunch (Action
  #957) is NOT an L-212 retitle: it stays its own run unless a catalogue joins it to the old series (lead, wave 99).
  DC Ostrander Suicide Squad trades = GCD 94748 #1-8 (twin #1 rows). The volume-id-as-issue-id mistake happens on the
  CV side too (cv=33054 on an I line) — the a-I signal catches it. A split's warrant is the shelf's WINNING F
  split-needed: an unlanded R- file that restates the shelf must carry the F line too, or check_splits --all refuses
  the P- line (P-039 / R-109, S16896).

[L-371] pub:marvel sig:split
- A split F line's "new key" can already be a LIVE shelf's key (a trade-only shelf of the same run, e.g. Superboy v6
  (2011) = S100132's key) — check `nearby:` / `lookup.py --shelf` and write it as an explicit join. Stored GCD rows on
  Marvel 2022+ trades can be the PANINI FRANCE edition — `--collects` on the English row confirms the right one.
  Digital rips of Marvel trades run ~88-92% of GCD's printed page count — a usable per-book fit when the filename year is
  the digital release date (R-111).

[L-372] pub:dc
- DC (R-112/R-113): every Superman 1939 / 1987 shelf carries a per-file stamp of CV 119775 "Superman Year One" (noise).
  GCD roll-ups can index only PART of a trade (Flash 2023 Vol. 1, WW 2023 Vol. 1 show #5-6 only) — the notes and the
  next volume's start decide. Digital-first trades (WW '77, Legend of WW, Truth & Justice) are numbered in PRINT issues —
  C lines name the print volume. Brightest Day read-order folders: undated "Titans NN" = the 2008 series. The Superman:
  The Man of Steel trade lines reprint the BYRNE era, not the 1991 ongoing. Superman #1-423 -> Adventures of Superman
  #424-649 -> Superman #650-714 is ONE numbering (L-212): the shelves meet by a split-lane join, not a cv merge. GCD
  59922 (Action Comics 2011-?) belongs to the New 52 shelf where its numbering starts (L-331).

[L-373] pub:dark-horse pub:dc sig:split kind:collected-line
- A split-lane join FROM a one-file shelf is impossible by construction (check_splits refuses emptying a shelf): write
  it as split:false + pending_join, then fix the partner's stored cv so the two meet by a cv merge (Black Beetle S18107 ->
  S18106 via CV 54853, R-115); where the catalogue keeps one CV volume per album (Grandville 45516/45517/105757) they
  meet only through the shared GCD row -- a lead-lane merge. Dark Horse Hellboy-universe "Title 0NN - Mini 0N (of 05)"
  ripper ladders can duplicate a live mini shelf (Witchfinder / Gates of Heaven S21752) -- check --who-stores on each
  mini's cv before minting a key. DC "#DC Events" GL Corps read-order folders: "Spectre vN NN" names DC's volume ordinal
  (v4 = 2001), not the ripper's folder vN (P-042 / R-115, waves 105-106).
[L-374] pub:dark-horse pub:dynamite pub:idw sig:stamp kind:trade-only
- One-book Dark Horse / Dynamite shelves: a per-file count-1 volume dated a year after the run is the trade's OWN record
  (I line); the run is the S. IDW Disney trades (Uncle Scrooge, Timeless Tales) and Dark Horse Disney Princess GNs carry a
  count-1 record per book on BOTH CV and GCD — probe by subtitle. GCD counts VARIANT rows in a series' issue count (Shock
  Shop 189080): read the rows before calling a count mismatch. Soleil English digital: CV can hold a French-titled AND an
  English-titled volume of one album series — match issue titles to filenames (Olympus Mons, R-117). Dark Horse "Title
  0NN - Mini 0N (of 05)" ripper ladders: the (of 05) position is the issue number, 0NN is the ripper's running count.
  Split-out shelves with NO stored cv keep the parent's per-file stamp on an N line, not F wrong-cv-link (refines L-366,
  R-116). Grandville / Don Rosa Library: one GCD line, no shared cv — a merge-with there is inert; lead-lane (L-373).
[L-375] pub:fantagraphics pub:oni sig:split
- A split key that NORMALIZES to the items' CURRENT key moves nothing (checker and replay both pass it): respell with
  vN (Usagi Yojimbo: The Crow v1 (2024), P-044) — extends L-336. GCD files Fantagraphics' Segar Popeye Sundays under bare
  "Popeye" (179342) and the French magazine as "Métal Hurlant" with the accent (41897; its "bis" rows = CV "NNbis").
  CV's Western Bugs Bunny splits at #220/221 (11508 / 31736) while GCD 12477 spans both. Rippers number EC's Weird
  Fantasy 1-22 in publication order (files 001-005 = EC's 1950 #13-17). Oni Courtney Crumrin: CV 44811 is the 2012
  monthly, not the 7-volume trade line. A one-book title CV holds twice (Gabrielle Bell 49698 / 64883) merges by
  restating one side onto the other's cv (R-119).
[L-376] pub:idw pub:image pub:dark-horse sig:split kind:trade-only
- Refines L-375: books-series-split compares keys EXACTLY and always writes the re-key; the item lands wherever the
  resolver maps the new key. A join onto a live shelf's EXACT key moves even when it normalizes like the old key; the vN
  respell is needed only when NO exact target exists and the normalized form is aliased to the parent (P-045). IDW / Image
  trade-only shelves often paired the run's CV with the collected line's GCD row — check that both legs name the same thing.
  GCD keeps a limited-HC and a softcover row for deluxe Dark Horse lines (Usagi Saga 87487 HC / 92028 SC) — page counts and
  edition rows decide. GCD hands the Usagi trade numbering to IDW's own row (160732) from Vol. 34. IDW Library of American
  Comics / Winterworld reprints: GCD's roll-up names the ORIGINAL Eclipse / Warp run. CV counts an IDW FCBD issue as #0 of
  the run (Unicron) while GCD files it as its own series — a merge that brings #0 under an unjudged trade halts
  audit_containment: write a `u` line (containment/decisions/S20426.txt) and wave_fix (R-121, wave 112).
[L-377] pub:dark-horse pub:dc sig:split kind:collected-line
- SUPERSEDES L-373's first sentence (lead ruling 2026-09-24): a split-lane join FROM a one-file shelf is NOT impossible.
  Since 9f2bfea5 (09-23 07:56) check_splits lets a shelf EMPTY when every item it gives up lands on a live shelf its
  `join` names (P-010's S14555 emptied that way). So two cv=- shelves that share only a GCD line meet by an ORDINARY P-
  join (`F <sid> split-needed | items -> join S<survivor>`), never a lead-lane hand merge and never a forced inert
  merge-with. After the join, restate the survivor as S68154 / S15358 are: cv empty, the GCD line as the leg, each book's
  CV volume on its I line. The emptied shelf survives as an empty Series row until something prunes it. The Grandville,
  Serenity, Don Rosa, EC Artists' Library, Monsters of Metal, Obscure Cities and S3615 -> S3613 cases are joins (P-046).
[L-378] pub:gold-key-dell pub:marvel pub:idw sig:collision sig:split
- PUBLISHER-CHANGE chains (lead ruling 2026-09-24; Bugs Bunny Dell #28-85 -> Gold Key #86+, Felix Toby -> Harvey, G.I. Joe
  ARAH Marvel #1-155 -> IDW #156+): numbering that continues across a publisher change is ONE run (one shelf, L-212), but
  the shelf STORES THE ERA WITH THE WIDEST SPAN, not the chain's first record. L-369's first-record rule is for retitle
  chains where a catalogue keeps one record across the retitle; on a publisher change both catalogues split by era, so no
  first record covers the chain (precedents: L-039 DV8, L-100 manga publisher moves, L-242 Dell -> Gold Key funny animals).
  The other eras' records go on N lines; each book's own era record goes on its I line. A first record that covers a
  fraction of the shelf fails the ladder test anyway.
[L-379] kind:foreign pub:delcourt pub:marvel
- CORRECTS L-265 (lead ruling 2026-09-24): GCD 53252 is NOT an unrelated book — it is Marvel's 2009 Soleil-imprint English
  floppy edition of Spin Angels albums 1-4. It is still the wrong leg for a seven-album shelf (four issues against seven
  albums fails the ladder test): S24306 stores cv=90068 with the GCD leg empty (or the French Soleil original's GCD row if
  lookup finds one) and names 53252 on an N line as the Marvel edition of albums 1-4.
[L-380] pub:dark-horse kind:trade-only
- Lead rulings 2026-09-24. EDITIONS OF ONE BOOK are item-level, one shelf: Fagin the Jew's 2013 Dark Horse reissue (S6390)
  joins the 2003 first edition GCD indexes (S21684); the reissue's CV 71301 goes on its I line (like the EC Artists'
  Library second rips). WEB SERIALIZATIONS a print record collects: Colonel Weird shorts (S104036, four files / 59 pp, a
  partial rip of Lemire's 2022 Substack serialization) = the 2023 Dark Horse GN — S cv=148987 + merge-with=4038. But
  MySpace Dark Horse Presents (S104039, 36 web issues) does NOT take the six-volume print record (CV 40417 / GCD 38394):
  the ladder fails and no shelf stores that record — it stays its own shelf with no stored record, 40417 / 38394 on an
  N line; print trades, if they ever arrive, get their own shelf on 40417.
[L-381] pub:idw pub:dark-horse pub:gold-key-dell sig:split
- REFINES L-378 (lead, wave 114): the "widest span" is measured on the SHELF'S OWN FILES, not the catalogue's issue count —
  G.I. Joe ARAH S7094 stores IDW's CV 32910 / GCD 51579 because none of our issue files are Marvel-era, although Marvel's
  record is 155 issues against IDW's 146. GCD can keep a reissue as its own "[Second Edition]" series (Fagin the Jew
  98084 = the 2013 Dark Horse edition, beside the 2003 first edition 40684): check before calling two editions GCD-shared;
  the join still stands (editions are item-level, L-380). A split key whose current spelling reaches the parent only
  through a SeriesKeyLink cv (TMNT Color Classics Micro Series -> CV 48762) needs a NEW TITLE per record, not just a year
  (P-046). A GN merged onto its web-serialized shorts (Colonel Weird S104036 -> S4038) nests them under an UNJUDGED span —
  audit_containment halts; write a `u` line (containment/decisions/S4038.txt) and wave_fix (wave 114).
[L-382] pub:image pub:idw kind:trade-only kind:collected-line
- R-123/R-124 (waves 115-116): Image deluxe / compendium / massive lines carry their OWN CV and GCD records (IHF Deluxe
  106678 / 119101, Radiant Black Massive 216939) — probe the line name WITHOUT "Book NN". Benitez Lady Mechanika: GCD
  splits the first mini by publisher (Aspen 54501 #0-3, Benitez 86916 #4-5) while CV keeps one volume 35855. IDW
  Ghostbusters: every one-shot and trade has its own count-1 record on both legs, and v1 stamped CV 43066 on all of them.
  Rat Queens trade line GCD 111005: roll-ups on rows #1-3 only. Spawn Origins Collection reprints only Spawn 1992 —
  run-wins merge onto S15978 (reverses C-032); Prophet's run is GCD 63020, not the trade line 68348. The P-047 joins
  emptied 15 one-file shelves onto their survivors (L-377 in practice).
[L-383] pub:fantagraphics pub:image pub:dc pub:dark-horse kind:manga kind:collected-line
- R-125/R-126 (waves 117-118): CV's own labels SWAP EC Artists' Library #7 and #8 (Sucker Bait "Vol. 8", Zero Hour
  "Vol. 7") — match I lines by subtitle, not ordinal. Kodansha USA / Viz manga shelves stored on the JAPANESE CV line
  (Blue Period 113349, Beast Children 121130): a same-title English-publisher CV volume dated with our files is the
  edition — probe with `--contains` before accepting a stored link. DC 2018 "Powers" (CV 116809 / GCD 134252) and Dark
  Horse 2022 "Powers" (146264 / 189386) are library lines spanning several runs (own identity); a single-run trade filed
  there joins the run. Image Witchblade TPB sub-lines have CV per-line volumes but no GCD rows except Redemption (63620).
[L-384] kind:manga pub:idw sig:split
- R-127/R-128 (waves 119-120): an earlier "no English record" / "ComicVine has no volume" note on a manga shelf is NOT
  reliable (8 of 45 triaged shelves were wrong: Domu 23070, Fist of the North Star 136896, March Comes in Like a Lion
  150064, Dandadan 145549 …) — probe `--contains` and check dates before inheriting it. Viz Death Note GCD 28547 holds
  2007/2009 reprint rows beside the originals — match by subtitle. JoJo Part 6: Viz English (CV 154935 / GCD 206717) is
  9 HCs against 17 originals, so 17-file scanlation shelves stay on the Japanese records. Triage signal c on manga
  (judged ranges in CHAPTERS, catalogues in volumes) is not an identity fault — N line citing L-284. A split's "new"
  key already held by a one-run trade shelf (Transformers (2019) = S20376, IDW trade line GCD 155139) is a JOIN (L-371);
  re-read that shelf as the run afterwards.
[L-385] kind:manga pub:dark-horse sig:stamp
- R-129/R-130 (waves 121-122): GCD 78711 / 74309 "X" (Dark Horse 2013-2015) is Swierczynski's superhero comic, never
  CLAMP's X — the Viz 3-in-1 is GCD 79049 (collects X/1999, GCD 31430). Kodansha USA's 2009-2010 "The Ghost in the
  Shell" is ONE CV volume (46395) spanning GITS 1 and 2: MMI while GCD makes each count-1 (75025 / 75033). Seven Seas
  Ojojojo has a single-volume CV record (116182) AND a 2-in-1 (116028) — page count decides. `(Webcomic) (Batoto)`
  folders are fan scanlations of a webcomic original with no record (Onepunch-Man S23759 = ONE's webcomic, not Murata's
  CV 74851). Del Rey -> Kodansha USA lines (Nodame, Yozakura, Suzuka): CV keeps one volume, GCD holds only the Random
  House era (L-100 / L-381). Pika's French CV volumes (To Your Eternity 166488) masquerade as the line — check language.
[L-386] pub:marvel pub:dark-horse kind:manga
- R-132 (wave 125): Marvel `#MARVEL CURRENT ONGOING TITLES\<Title> vN (year)\Variant Covers` folders collect OTHER runs'
  covers by title alone (2018 ASM, 2022 Daredevil, 2018 Avengers) — the filename date against GCD cover dates decides the
  run; move them by split-lane join. Marvel "<Title> by <Creator>" trade lines (GCD 173399, 200583, 207599, 211328, 201989,
  220651, 203412) were stored as shelves' GCD legs by earlier readers — the run's own GCD series is a different id. GCD
  files Viz VIZBIG as "InuYasha (Vizbig Edition)" (44427) — probe with `--contains`. `Marvel-IDW Star Wars\_Star Wars
  Insider (1987)`: the magazine has no CV or GCD record. S100014 (Star Wars 2020) merged into the EMPTY S65726 (key
  "Star Wars", cv 123860): later joins naming S100014 must target S65726 — and the bare key "Star Wars" normalizes like
  the 1977 shelf's, so check the join lands (L-375/L-376).
[L-387] pub:marvel pub:dark-horse sig:split
- P-052 / R-133 (waves 126-127, lead-approved): "the largest run STAYS" yields to the STORED cv — when the run that holds
  the shelf's stored cv is the smaller one, keep it and move the larger RECORD-LESS run out (Star Wars S16641: the 1977 run
  on CV 2914 stays, 208 Star Wars Insider files leave to "Star Wars Insider (1987)"); a new shelf seeded with the parent's
  cv would collide with its canonical cv. The key normalizer keeps the year: a key differing only by year is new; respell
  only when the exact spelling is a ParsedKey, alias or SeriesKeyLink. A split seed can be a book's own count-1 record —
  re-check against L-124 when the book sits alone in a run's folder (The Other Side 2017 -> DC mini CV 19537 / GCD 21941).
[L-388] pub:marvel sig:split
- R-134 (wave 128, lead ruling): RESIDUE vs co-equal — two or more whole trades of another run are a co-equal run (R +
  split-needed) only when that run has NO shelf of its own; when its run or line already has a shelf, they are misfiled
  books: S + `F split-needed | items -> join S<home>` (L-368; X-Factor 2024 S66336's two Peter David omnibuses -> S22172).
  `#MARVEL CURRENT ONGOING TITLES`: Marika / MikeNY76 ComicInfo Web ids and "cvv<id>" Series strings name the stored CV
  volume (1.0 evidence). From the Ashes "Variant Covers" folders hold the PREVIOUS run's finale covers (X-Men v6 #35 in
  the v7 folder). "_X-Men Complete Chronology V2\NNNN <event>" annuals are event-chapter excerpts = partial rips. A
  ripper's "Omnibus Book 01" in a run's folder can be that run's first omnibus, not the line's Vol. 1 (page count decides).
  CV mints one count-1 volume per Venom by Ewing trade; GCD keeps two lines (183532 Vols 1-4, 208820 Vols 5-8).
[L-389] pub:marvel pub:dark-horse sig:split
- P-053 / R-135 (waves 129-130): a run's issues never JOIN a creator-trade-keyed shelf ("Venom by Al Ewing & Ram V",
  S21029, four trades) — keep them and meet through the SHARED cv at the next revisit (S20988 cv=140084 + merge-with=21029).
  Marvel's 2019-2020 "Star Wars #1 facsimile" is "Star Wars No 1 Facsimile Edition" (CV 123267 / GCD 153348): probe the
  "No N" form. Star Wars Insider and Titan's "Best of" books have no CV / GCD record: R + provider-missing, never the 1977
  comic's stamp. The Dark Horse Star Wars Omnibus umbrella number (Vol. 32 / 35) raises a false "stored GCD row is another
  book" warning against the sub-line's #1 / #2 — page counts and ISBNs decide. An Epic Collection / omnibus line spanning
  several minis keeps its own GCD-line key in a split.
[L-390] pub:marvel sig:split
- Lead ruling (waves 131-132): West Coast Avengers #1-46 -> Avengers West Coast #47-102 is an L-212 RETITLE (same
  publisher, numbering continues) although CV (3521 / 18494) and GCD (3061 / 3830) keep two records — L-378's widest-era
  rule is for PUBLISHER changes only. One run: split-lane join S1629 -> S101396, the survivor storing the chain's FIRST
  record (CV 3521 / GCD 3061, L-369; Firestorm precedent), AWC's records on N lines, each book's era record on its I line.
  Marvel Tales: GCD 1747 keeps ONE record for #3-291 while CV splits it (2293 / 138122 / 137722) — the shelves meet on
  cv=2293 (L-369); they merged into S11602 in wave 132. GCD's "Collects" notes on New Exiles trades run one issue high
  (the run ends at #18). Marvel Graphic Novel: CV #32 / #33 = Thor / Iron Man: Crash but GCD #[32] / #[33] = Groo / Thor
  (Crash is GCD 15018); CV's volume stops at #38 — match by title. Marvel Tales Annual #1-2 is its own series on both legs
  (CV 131835 / GCD 1637) even when named "Marvel Tales 001 / 002". Marika "B Story" files = backup-story extracts (partial
  rips). A lead instruction to write wrong-cv-link on a shelf with NO stored cv was wrong — N line (L-374).
[L-391] pub:marvel sig:split
- Lead rulings (waves 133-134): annuals on a monthly's shelf are residue ONLY while they have no home — when an annual
  series has its own shelf (Avengers Annual S1612, CV 2350 / GCD 1807), they JOIN it (refines L-369). L-369's first-record
  rule needs the chain's OPENING issues on the shelf: Rawhide Kid S14413 holds only the 1960+ era, so it stores the 1960
  records. A TPB line spanning two runs keeps its own identity (Spider-Man 2099 GCD 86779 Vols 1-7 over the 2014 and 2015
  runs; L-109/L-318). Star Brand: CV splits the retitle (3652 #1-10 / 51406 #11-19), GCD 3204 keeps #1-19 — store 3652
  (L-369). Humorama Zip: CV numbers by cover month (#196403); "v14 07" = January 1968. Time Runs Out: GCD 89288 = the
  2015 HC rows, 92245 = the TPB rows; CV 79464 follows the HC dates. Chronology `NNN Avengers` refiles of v1 issues are
  second rips; later-volume files were folded onto the v1 shelf by bare title — split by year.
[L-392] pub:marvel sig:split kind:collected-line
- Waves 135-136: Timely/Atlas Marvel Comics #1 -> Marvel Mystery Comics #2-92 -> Marvel Tales #93-159 is ONE numbering
  (L-390): S11600's Atlas Marvel Tales join S11538. The Epic Collection per-book-shelf shape (Avengers S1589 / GCD 74603,
  AWC S1632 / GCD 130433, MTIO S11633 / GCD 127449) is fixed by cv=- on the line + split-lane joins of the per-book
  shelves, never merge-with chains (each per-book shelf stored a different count-1 cv). OPEN for Eric: an Epic line
  reprinting ONE run — keep the line (L-109/L-350, current practice) or give way to the run (run-vs-line)? A split-lane
  JOIN of an issue onto a shelf of UNJUDGED trades arms them (New Avengers #13.INH -> S94666): armed_unjudged LIVE
  EXPOSURE -> `u` lines in containment/decisions/S94666.txt + wave_fix. Minutemen digital Secret Avengers "(2014)" is the
  digital release year (#13-15 = 2010 run Fear Itself tie-ins). A single-issue facsimile is its own book (L-077).
[L-393] pub:marvel sig:split
- Lead rulings (waves 137-138): L-389 blocks joins onto a CREATOR-trade-keyed shelf only; a shelf keyed by its own trade's
  SUBTITLE that stores the run ("Uncanny Avengers - The Resistance" S20673, CV 153018) takes stray issues/covers of that
  run by join. An annual goes with its run when no annual-series shelf exists (Uncanny Avengers Annual 2016 -> S94729).
  GCD indexes each Skottie Young "baby" cover as a variant row of #1 ("Young Cover") — that row decides which relaunch a
  lone "001 (YYYY) Skottie Young" file belongs to. At a retitle, GCD and CV can split at different issues (GCD keeps WCA
  #47 on 3061, CV starts AWC at #47). Marvel arc minis (Ultimate Avengers 1-3): both legs number one continuous series
  (#1-18) while rips restart at 01 per arc — issue files need I lines or they resolve to the wrong issue.
[L-394] pub:marvel kind:golden-age sig:split
- Waves 139-140: Marika ComicInfo Web ids on Marvel UK rips (Knights of Pendragon, Captain Britain) are ComicVine ISSUE
  ids; GCD's 11 for Knights of Pendragon v2 is incomplete indexing (#1-4 missing), not a shorter run. GCD splits Cloak and
  Dagger at the #14 retitle (3656 / 14574) while CV 4060 keeps #1-19 — one run (L-390). Golden-age chronology tags
  "(cronology #00143)" are read as issue numbers — only I lines fix them. Coates Captain America GCD rows from X-058 were
  Panini Deutschland (series 142421). Winter Soldier: GCD 68861 is the trade line, the run is 63067. Epic Collection
  per-book shelves: when the natural line anchor (plain key) is outside the batch, anchor on a line shelf inside it and
  queue the rest (Conan Chronicles EC -> S4094; S4092 / S4096 owe the same join). Joining stray covers onto a shelf of
  unjudged trades arms them again (S20673 The Resistance) -> `u` line + wave_fix; read that range next.
[L-395] pub:marvel sig:stamp sig:split
- Waves 141-142: Marvel RELAUNCH shelves carry the legacy volume's CV issue ids BY NUMBER (Daredevil 1998 -> 2190,
  Deadpool 2008 / 2012 -> 6000) — compare each file's linked issue number to its filename and correct with I lines.
  Daredevil by Mark Waid Vol. 01 at 272pp is the HC line (CV 57482 / GCD 79153), not the TPB line (45530 / 64482). GCD
  numbers Daredevil 1998's legacy issues "#22 (402)"; a file "115 (495)" is #115. GCD files Daredevil Annual #5 (1989) as
  "#4 [5]". Creator lines reprinting ONE run give way to the run (Miller/Janson -> 1964 run, To Heaven Through Hell ->
  2019 run) — the standing run-vs-line ruling supersedes L-115 / L-196's 0.9 practice. A dismissed conflated flag does not
  stop the RESIDUE ruling: two whole books of another record with no shelf -> R + split-needed (Deadpool Classic
  Companion). An R- file that restates a shelf a pending P- batch splits MUST repeat its F split-needed (L-370 halt,
  S95620 in R-145).
[L-396] pub:marvel sig:split
- Waves 143-144: a lead-proposed new split key can EQUAL the items' current key when a Matched SeriesKeyLink aliases it
  onto the parent ("Dazzler (2010)") — check `keys now:` first; respell with vN ("Dazzler v2 (2010)", extends L-375).
  Daredevil #500-512 sit inside the 1998 CV / GCD volumes (6209 / 6026); per-file CV 2190 on them is a legacy-number
  stamp (L-395). GCD writes the 1989 Daredevil Annual as "#4 [5]" (row 45572); rippers label it "Annual 04b". Collected
  ranges copied from judged ranges often cross a fill-in gap (Miller & Janson Vol. 01 skips #162; the Companion collects
  #219 then #226-233) — state the larger stretch, the rest on an N. ORDERING: when an R- file both MERGES shelf A into B
  (shared cv) and splits books off A, the merge lands first and the split's F lines die with A — the next revisit of B
  must restate them as B's own split-needed (S4784 -> S96254, wave 144).
[L-397] pub:marvel sig:split kind:collected-line
- Waves 145-146: FF 1961 shelf: the Major Event and Complete Chronology folders re-file the 1998 run by event name with
  CV 2045 stamped by number; the 1998 run's #1-70 carry legacy numbers in GCD ("#67 (496)"). DS Epic Collection judged
  ranges are real ranges in named runs (Strange Tales, DS 1968 / 1974, Sorcerer Supreme) — C lines apply; the item
  pass's "cumulative, no C" notes were wrong. FF Epic Vol. 25's judged #347-358 is wrong: GCD gives #403-416. An R that
  splits an omnibus line off a run shelf can arm the omnibus over the run's trades on an unjudged span (Hickman FF
  Omnibus Book 01, provider #570-574): write a containment decision file, and it must then decide EVERY edition on the
  shelf (pass2) — restate existing Curated ranges as S lines, `u` the rest (containment/decisions/S6490.txt).
[L-398] pub:marvel sig:stamp kind:trade-only
- Waves 147-148: Marvel 2008+ runs have a SEPARATE GCD trade-paperback-line series that earlier readers stored as the
  shelf's S gcd (Totally Awesome Hulk 104142 -> run 94628; Rocket Raccoon & Groot 104355 -> run 95266) — the run's own GCD
  series goes on the S. A Panini España CV VOLUME id can equal the right ISSUE id (S6478: 150718 = #1 of CV 25548).
  Ghost Rider (2006) trades are GCD 55198; stored rows were Panini Deutschland (45859). Hulk (2008) trades have twin GCD
  trade series (73891 / 73934) plus 2011-12 HCs — page counts pick 73891. Incredible Hulk (2000) and Ghost Rider (2006)
  shelves carry legacy-number CV stamps (L-395) — per-file I lines. Byrne FF Omnibus merged onto the FF 1961 run (L-395).
[L-399] pub:marvel sig:split folder:events
- Waves 149-150: Marvel chronology folders — "Ghost Rider v2 NNN" / "(1992)" follows CV's VOLUME ordinal (1990 = Vol. 2),
  not our folder vN (1973 = v2); check the file's year against the shelf's run. An FF annual that looks recordless can
  sit under its exact slash title ("Fantastic Four / Fantastic 4 '98", CV 138969 / GCD 7723). GCD splits FF Annual #32-33
  into a separate 2010-2012 series (67080) while CV keeps them on 2129. A Marvel TPB volume continuing a run's trade
  numbering after a relaunch that keeps legacy numbering ("Ghost Rider Vol. 05: Final Vengeance", GCD 191541 #5) joins the
  run's shelf. L-389 blocks only a run's ISSUES from a creator-trade shelf: that creator line's own books join it
  (Doctor Strange by Mark Waid Books 01-02 -> S5642). C lines on a containment-refused book stand (identity-level).
[L-400] pub:marvel sig:split kind:collected-line
- Waves 151-152: a lead-proposed "new" key may be a LIVE key on a different run (Ghost Rider v7 (2011) is S64771's 2019
  run key) — the reader respells ("Ghost Rider (2011)"); L-371 guards joins, this guards mis-joins. Marvel Digital
  Original series (Iron Fist: Phantom Limb 2018) are their own CV volume with no GCD run row; GCD indexes only the trade
  ("collects <Title>: Marvel Digital Original #1-3"). GCD Masterworks notes can misprint a range start (ASM Masterworks
  #7 "#52-67") — check the neighbouring volumes. Masterworks Golden Age / Invincible Iron Man paperback re-issues (GCD
  74965 / 78703) are what digital rips with a later filename year usually are. Tales-of-Suspense-foldered Masterworks
  shelves duplicate the line's own shelf (merge on the shared cv).
