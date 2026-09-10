# Defects found while reading the numberless population

Written 2026-09-08 by the reader who worked `--slice=0/3` (all 235 run-shelves plus ~590 singleton
shelves), then `--slice=1/3` and the tail of `--slice=2/3`. Nothing here was written to the database.
Every item below was read from its filename; where the title alone could not settle a question,
`Item.PageCount` was used rather than opening pages.

The readings themselves are in `issue-numbers-read-slice0.csv` (380 rows) and
`issue-numbers-read-slice0b.csv` (64 rows). This file is what those readings *revealed*.

---

## 1. Shelves that hold more than one run — fix before any containment de-dup

A run is the unit that owns a number space (PLAN §14.1). Where two runs share a `Series`, the same
issue number exists twice on one shelf, and a range recorded against a trade on that shelf can nest
the wrong file. These three are the ones I hit.

### 1.1 `S10154 Justice League of America` — THREE runs in one shelf

| run | evidence on the shelf |
|---|---|
| JLA v1 (1960) | `Justice__League_of__America_v1_021` … `_v1_185`, plus already-numbered `v1_195`–`v1_244` |
| JLA v2 (2006) | `Justice_League_of_America_013__2007_` … `_34__2009_`, plus `Justice League of America v2 01`–`v2 07` |
| JLA v3 (2013) | already-numbered `006 Justice League of America 006 - September 2013`, `011 … 007 - October 2013` |

**#21, #29 and #30 now each exist twice on this shelf** (v1 #21 = item `16719`; v2 #21 = items `17841`
and `17821`). I recorded all 57 readings because each is a correct reading of its own filename, but a
trade shelved here that claims `#21-25` will nest across runs until the shelf is folded/split.

**Also on this shelf:** item `17706` has stored `IssueNo = 7` while its filename reads
`[07] Justice League of America 12 (2007) (3 covers) (Minutemen-ThePyre & LegionBoy)` — the parser took
the `[07]` reading-order prefix instead of the issue number **12**. Two other rows on the shelf show the
same prefix-vs-number shape and happen to have come out right, so this is a parse bug worth a sweep, not
a one-off.

### 1.2 `S21522 What If...?` — v1 and v2 in one shelf

`What If v1 027`, `v1 031`, `v1 033` sit beside `What If v2 001` … `v2 106`. **`v1 033` (item `86802`)
and `v2 033` (item `86761`) are both #33.** I recorded both.

I deliberately left two files here blank: `What_If_-_Civil_War_01` (`97592`) and
`What_if_Annihilation_01` (`97593`). They are separate one-shot titles mis-shelved here, and their "01"
would have collided with `What If v2 001`.

### 1.3 `S64823` is named "Green Lantern (1960)" but holds Green Lantern **v4**

Every file on it is `Green Lantern v4 NN` (`21`, `22`, `23`, `28`, `39`, `40`, `41` — the Sinestro Corps
and Agent Orange arcs, 2007–2009). "Green Lantern (1960)" is v2. The shelf identity is simply wrong;
its seven files belong to the 2005 run.

### 1.4 Smaller versions of the same fault

* `S7927 Green Lantern Corps` holds three files that are not GLC at all —
  `Green Lantern - Sinestro Corps Special 01` (×2, items `16196` and `17641`) and
  `green_lantern_sinestro_corps_secret_files_01` (`17658`). Only `Green_Lantern_corps_024` (`17663`) is
  a GLC issue, and it is the only one I numbered.
* `S34339 The Amazing Spider-Man` holds the ASM **newspaper strip** year-webrips (2013–2023) together
  with `Amazing Spider-Man Annual 031a/031b`. Two unrelated works, neither of them the ASM comic run.
* `S1881 Batman` holds `Batman Beyond` Random House prose tie-ins, `Batman - Secret Files`, and
  `Batman_Annual_025`/`_26`. Nothing on it is a Batman issue.
* `S7844 Graphic Ink: ... Frank Quitely` also holds the Darwyn Cooke and Ivan Reis volumes.
* `S16901 Strange Fruit` holds the BOOM! mini-series TPB *and* the unrelated
  `Strange Fruit, Volume I/II - Uncelebrated Narratives from Black History`.
* `S6001 Eden` holds four different works all titled *Eden*.
* `S95825 Blue Monday v03 (2003)` holds three different Blue Monday story arcs.

---

## 2. `S23759 Onepunch-man` — 12 webcomic chapters carrying `IsCollection = 1`

`Onepunch-Man ch093 (v3)`, `ch094 (v4)`, `ch095 (v4)`, `ch097 (v2)`, `ch098 (v2)`, `ch099 (v3)`,
`ch100 (v3)`, `ch102 (v4)`, `ch103 (v3)`, `ch104 (v2)` and their neighbours (items `40452`–`40463`) are
20-page Batoto webcomic chapters. The `(vN)` suffix is a **scan revision** marker — v2/v3/v4 of the same
rip — and the parser read it as a volume marker, so each of these chapters is now a container waiting for
a range. This is exactly the §14.6 hazard `unflag_floppies.py` exists for; they fall under its
"under 60 pages" test but their filenames carry no plain issue number, so the rule did not reach them.

The two files on this shelf that *do* carry numbers carry junk ones, from the same decimal-chapter parse:

| item | filename | stored `IssueNo` |
|---|---|---|
| `40402` | `Onepunch-Man ch044.5 (Webcomic) (Batoto)` | `5` |
| `40451` | `Onepunch-Man ch092.1 (Webcomic) (Batoto)` | `1` |

The fractional part of the chapter id became the issue number. Both should be cleared.

I left all 94 numberless chapters on this shelf blank: a chapter is a third coordinate, not an issue.

---

## 3. `S0 (no shelf)` — the JoJolion chapter/volume collision

The un-shelved bucket holds both coordinates of the same work at once:

* chapters — `JoJolion_010` … `JoJolion_018`, `JoJolion_107` … `JoJolion_110`
* volumes — `JoJolion_v01` … `JoJolion_v26`

This is the JoJo collision PLAN §4.2 names, sitting in one bucket where nothing distinguishes them. I
left all 42 files blank. The same bucket also holds five files that are not comics at all:
`CrossGen website.rar`, `Hurricane website.rar`, `MVCreations website.rar`, and two Milo Manara
`Planches Originales` art dumps (items `141162`, `141163`, `141164`, `141107`, `141108`).

---

## 4. Junk shelves — a series name the parser invented

| shelf | what is actually on it |
|---|---|
| `S1101 Annuals` | three files literally named `Annuals.cbz` (items `28711`, `28778`, `28876`) — no title, no series, no number |
| `S17868 "The"` | five unrelated books fused by the article: `The Book About What's Inside The Box`, `The Book of Hope`, `The Book of Human Insects`, `The Book Of Mr. Natural`, `The Book Tour` |
| `S14922 SSSS Ch1` | `SSSS_Ch1_vol0` … `SSSS_Ch1_vol4` — a chapter marker promoted into the shelf name, with volume numbers under it |
| `S36372 Zombie World` | six **board-game** PDFs (`Core.pdf`, `Expansion - Farm.pdf`, `Expansion - Mall.pdf`, `Rules.pdf`, `Playmats.pdf`, `KS Bonus Material.pdf`) shelved with two genuine Dark Horse *Zombie World* comics |

---

## 5. Collected editions sitting in the numberless-**issue** population

These carry `IsCollection = 0` while plainly being trades — the inverse of the `unflag_floppies.py`
question, and the more dangerous direction, since a container that is not flagged can never nest what it
holds.

| items | what they are | pages |
|---|---|---|
| `106405`–`106413` (`S10457`) | `Knights Of The Dinner Table - Bundle Of Trouble 1` … `9` — KODT's TPB line; each collects ~6 issues | 98–100 |
| `107629`, `107630`, `107631` (`S8026`) | `Grimm_Fairy_Tales_-_Tales_From_Wonderland_Vol_1/2/3_TPB` — the filename says TPB outright | — |
| `17836`, `17837`, `17838` (`S2050`, `S35015`) | `DCP_Archive_Edition_-_Batman_RIP._Heart_of_Hush` (132p), `The_Great_Leap` (134p), `Scattered_Pieces` (47p) — hand-compiled multi-issue archives | 47–134 |

---

## 6. Coordinate rulings made while reading (so they are not re-litigated)

**Volume-numbered, left blank** — the number is a tankōbon/book volume:
`3x3 Eyes` (v01–v40), `I Am a Hero` (v01–v22), `20th Century Boys` (v01–v22), `Battle Royale` (v01–v15),
`Riki-Oh` (v01–v12), `Aqua`, `Hanashippanashi`, `LittleForest`, `G.I. Joe Classics - Special Missions
v01–v04`, `The Losers Book One/Two`, `Wolverine - Three Months To Die Book One/Two`,
`Strange Fruit Volume I/II`.

**Chapter-numbered, left blank** — a third coordinate:
`Onepunch-man` (ch001–ch101), `A Gentle Man` (c01–c03), `Hideout` (`V1_Ch8`, `Ch9`),
`Flesh-Colored Horror` (Part 1–6), `Grand Blue Dreaming 083_extra` / `089_extra`,
`Big Hero 6 - Baymax Chapter 00`.

**Year-labelled, left blank** — the number is a date, the mirror of the `2000 AD` prog trap:
`Dennis the Menace` (29), `FoxTrot` (32), `The Family Circus` (29), `Heathcliff` (20), `Garfield` (15),
`Zippy the Pinhead` (11), `Wallace the Brave` (11), `The Little King` (9), `Rugrats` (8), `Nancy` (7),
`Bad Machinery` (7), `Olive & Popeye`, `Mark Trail`, `Popeye's Cartoon Club`, `Legalization Nation`,
`Thought Bubble Anthology`, `BOOM! Box Mix Tape`, `Archie Christmas/Halloween Spectacular`,
`The Tick - Free Comic Book Day`, `Vampirella Valentine's Day Special`,
`2000 AD Sci-Fi Special` (8 annuals, 2014–2022).

**Volume×issue magazine coordinate, left blank** — `Heavy Metal` (`33x04`, `V19 01`, `21X03`): no single
issue number exists for these.

**Album numbers recorded AS issue numbers**, following the `Ariol 08 → 8` ruling in the brief:
`Ariol`, `Chloe`, `Tao, the Little Samurai`, `Lou!` (Graphic Universe), `Little Nothings`,
`The Keepers of the Maser`, `What Happens Next`, `Black Mary`, `Emilie's Inheritance`, `Lucky Luke`
(Cinebook), `Segments` and `Demon Princes` (47pp Heavy Metal compilations — page count says floppy).

**Two album series I first read as issues and then RETRACTED**, once `PageCount` was checked:

| shelf | page counts | verdict |
|---|---|---|
| `S4234 Corto Maltese` (11 albums) | 82–430 | collected volumes — volume coordinate, blank |
| `S14750 Robo Hunter` (6 books) | 129–160 | collected volumes — volume coordinate, blank |
| `S3770 Charley's War` (9 parts) | 40–56 | serialized reprint parts, not issues — blank |

**Refused rather than guessed**, each for a stated reason:

* `2000 AD 0000 unreleased pilot`, `Brath 000 Prequel`, `Supergirl_00 (2005)`,
  `Crisis_on_Infinite_Earths__0`, `Ghost in the Shell 0 - Covers` — a stored `0` reads as absent
  downstream, and a missing number costs nothing.
* `Spawn 250b (Spawn Resurrection) (2015)` (24pp) — the "250" may be a scanner's ordering of a
  separately-numbered one-shot.
* `Ariol 06 - Where's Petula` (`109186`) and `Ariol 07 - Top Dog` (`109187`) — the shelf shows **two**
  files claiming 06, and Papercutz's own numbering puts these two at 07 and 11. The filenames are wrong,
  so the eight unambiguous Ariol books were recorded and these two were not.
* Every `Annual NN` (`Batman_Annual_025`, `Robin_Annual_07`, `Wonder_Woman_Annual_01`,
  `Amazing Spider-Man Annual 031a/b`, `Crossed - Annual`) — annuals are a separate number space from
  the run they sit beside.
* Reading-order prefixes were never taken as issue numbers: `01 Annihilation - Prologue`,
  `000 Age Of X Historical Logs`, `383. Chaos Effect Alpha`, `484. The Visitor vs. The Valiant Universe`,
  `010 Marvel Holiday Special 1994`, `02 Knight Terrors - Dark Knightmares`,
  `006 Generation X - Underground`.

---

## 7. The `IsCollection` re-check (2,274 rows cleared by rule)

The rule's output is identifiable after the fact: `ComicDetail.Format = 1 AND FormatRaw = 'TPB' AND
IsCollection = 0 AND PageCount < 60` returns **2,271 rows**, which is that population to within three.

I read the **top 330 by page count** — the whole 36–59pp band, which is where a real collection could
hide. **Not one of them is a collected edition.** The band is uniformly magazine-format and
double-sized single issues:

* magazine-format runs — `CRAZY Magazine v1 001`–`v1 090` (the bulk of the band, 44–54pp),
  `The Rampaging Hulk v1 002`–`007` (50–59pp), `Love & Rockets v1/v2` (36–54pp),
  `Gore Shriek v01/v02` (36–59pp), `Heavy_Metal SE - 1989 Desperadoes v1` (58pp);
* double-sized floppies — `X-Men Unlimited v1 013`/`019`/`031`/`033`/`034`/`036`/`037` (36–56pp),
  `What If V2 025`/`041`/`050`/`064`/`100` (40–45pp), `Colossus v1 001` (52pp),
  `Dark Shadows v1 001`–`035` (36–55pp), `Star Trek`/`Star Trek TNG` (36–52pp),
  `Plastic Man v2 005`–`018` (36pp — the case the script was written for);
* ordinary floppies — `2099 - World of Tomorrow`, `Alpha Flight v2`, `X-Force v1`, `Gold Digger v2`,
  `Evil Ernie v02`, `Old City Blues v2`, `Kabuki`, `G.I. Joe ARAH v2`, `DC vs Vampires`, and so on.

**Verdict: zero wrongly cleared in the read range.** Two rows are worth naming as the closest calls,
both of which I judge correctly cleared:

* `46803` — `Daredevil 325 (+TPB pages) (1993)`, 42pp. One issue with bonus TPB pages appended.
* `60356` — `Detective Comics 676 (1994) (digital-TPB)`, 39pp. One issue ripped out of a TPB.

Those two also explain where the whole population's `FormatRaw = 'TPB'` came from: it is the ComicInfo
of the **trade the page-rip was cut from**, not a claim about the file. That is a stronger reason to
trust the clears than the rule itself was.

The risk the rule genuinely does carry is the *opposite* direction — §5 above, real trades that were
never flagged at all. Those cost containment, and no rule was ever run to find them.
