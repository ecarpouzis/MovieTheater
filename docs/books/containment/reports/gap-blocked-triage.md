# The two gap follow-ups, triaged (2026-09-25, after wave 192)

Both items in `gap-blocked-by-identity-rows.md` and `gap-misnumbered.txt` were measured against the live db
and the code before anything was proposed. Neither needs the tooling change the first report asked for.

## 1. The 41 judged ranges the import would not write

`CollectedEditionSpan` holds one Curated row per item; the identity pass's `C` lines own it (`identity:`
provenance is "proven", so the containment import answers `Kept`). Measured over every `S` line in the
decision files against the live rows: **41** blocked, in three populations — and the framing "identity stores
a cross-run range in the slot" is only true of the third.

| population | items | what happened | what fixes it |
|---|---|---|---|
| a later revisit batch ERASED an earlier own-run line | 9 | R-043 / R-132 / R-042 / R-173 / R-167 restated only the "also collects" material (an annual, a crossover chapter, a one-shot). A `C` statement REPLACES the item's run rows and the span takes the batch's FIRST line, so the own-run line X-011 / X-058 / X-059 had landed on 09-22 was overwritten (SeriesInferenceDecision shows both, in that order). Captain Marvel Vol. 08 went from #37-41 to the annual's #1-1. | `X-063` restates the whole statement, own run first |
| an identity line the book's own page contradicts | 11 | Human Target #1-5 (a model:b050 read) vs the copyright page's 1-6; Batman Inc #0-13 ("per the shelf") vs 1-12; Supergirl #13-22 (GCD roll-up) vs the contents page's 11-22; GL Vol. 05 #29-34 vs the copyright page's 27-34; Harley Vol. 02/03 and JLD Vol. 01 narrowed to avoid a hole the engine already reads from the quotation; Thunderbolts Book 01 #1-33 vs the back cover's #0-33 | `X-063` |
| the identity line is RIGHT and the `S` line is the stale one | 21 | Hellboy (3), Baltimore (8), Witchfinder (6): ripper-renumbered LINE shelves where R-047 / R-114 / X-014 deliberately stored each trade's range in its mini's own numbering and said so ("the judged #6-10 was the ripper's numbering — the RANGE CONTRADICTED"); Summer Wars / Vagabond (chapters vs volumes); Detective Vol. 05 (GCD's "material from #1027" is more specific than the complement inference); Thunderbolts Book 03 (the identity splits the 1997 run from the 2006 run's #100-109) | nothing to write; **0 ladder files sit inside any of these `S` ranges**, so they are coverage-only. Lead's call whether to turn them into `u` lines so the decision files stop disagreeing with the identity rows |

**What is at stake in files** (issue files on the shelf whose number falls inside the `S` range and that nest
nowhere today): GL Vol. 05 11, X-Force Book 02 14, Hulk v4 Vol. 03 11, Supergirl v6 Vol. 04/05/06 5 + 8 + 1,
Thunderbolts Book 01 5, Harley Vol. 03 1 — **56 files**, all on the 20 trades X-063 restates. Every other
blocked trade has zero.

### X-063 (written, checked, dry-run; NOT landed)
- `docs/books/identity/decisions/X-063.txt` + `batches/X-063.ids` — 20 books, 36 `C` lines (own run first,
  every cross-run line restated verbatim from the batch that last landed it), the 20 `I` records restated
  unchanged for the item pass's coverage contract.
- `check_identity.py X-063.txt` → OK, 0 failures.
- `apply_identity.py X-063` (dry run) → 20 `Curated span update`, 63 run rows, 0 conflicts, 0 exposed.
- Land it the ordinary way: `pwsh docs/books/identity/tools/wave_land.ps1 -Wave 193 X-063`. After it, the
  containment import answers `Kept` on the same range for all 20 `S` lines, and `gcdnotes.py --contradictions`
  drops Human Target and Batman Inc.
- Two lines are a reading call, flagged in their evidence: Supergirl Vol. 02 (#11-22 from the contents page vs
  GCD's "#11, #13-22") and GL Vol. 05 (#27-34 from the copyright page vs GCD's "#27, #29-34"). The book's own
  page was preferred, as the containment doctrine ranks it; if #12 / #28 are really absent, a `C` restating
  the hull with the quotation naming the hole is the fix, not a narrowed range.
- Harley Vol. 02 (#50-56, #55 denied) and Vol. 03 (#55-63, #56 denied) are hulls that overlap on paper;
  `overlap_check.py` may list them the way it lists Green Lantern v4 — the quotations make them consistent.

### The cause, and the guard it wants (Eric's call)
`apply_identity.py` documents that a `C` statement is the WHOLE statement ("the run refs REPLACE whatever the
span carried"), but nothing refuses a batch that restates only part of one. Readers write "also collects …"
lines meaning to ADD, and the tool erases. The guard belongs in `check_identity.py`: a `C` line for an item
that already carries `identity:` run rows must restate every run those rows name (or say which it retracts),
or the batch fails. Not written — it changes the identity lane's contract, which is yours.

## 2. Issue files nesting under trades they are not in

`gap-misnumbered.txt` said the issue-number tool "can only write plain numbers" and that annuals / point issues
"need a representation it lacks". Neither is so:

- `ComicDetail.IssueNo` is TEXT and `apply_read_issue_numbers.py` writes whatever the sheet says — `23.1`
  already sits on 18487 / 18450 / 18467 (Manual). `apply_read_format.py` exists and writes `Format`.
- The gap is in `ContainmentJob`: it ignored `Format` / the reading tier entirely, so an annual's "1" was
  coordinate 1 on the run's ladder; and a fractional coordinate (23.1) sits inside 20-25 arithmetically, so
  every point issue nested wherever the whole numbers around it did. Blanking a number is no escape either:
  `num[i] = IssueNumber ?? ReadNumber ?? VolumeNo ?? i + 1` gives an unnumbered file its POSITION as a
  coordinate.

Measured live (before the change): **70 annual / special files nested** (ReadTier 10: 30, 20: 40) — Aquaman
Annuals 1-5 under Book 01, both Iron Man annuals under Vol. 01, four Deathstroke annuals under Assassins, a
2022 Justice League annual under the 2018 Vol. 01 — and **not one** of them is in the book its parent's note
names (2 parents mention an annual: Titans East Special, ASM Annual 1-2; neither is the nested file). **3 point
issues nested**: Superman 23.1 / 23.2 / 23.3 under Vol. 04 Psi War, whose copyright page says 'SUPERMAN 18-24'.

### Code (built, 622/622 Books tests green, uncommitted, live db untouched)
- `ContainmentJob`: `Book.ReadTier` loaded from `ReadingOrderEntry` (Format OR the word "annual" / "special" /
  "one-shot" in the name — the judgement the reading order already makes). An annual / special ISSUE file gets
  a NaN coordinate (never on the ladder, never a child), in both the range pass and the positional pass.
  `PointIssueNamed`: a fractional coordinate on an issue file is measured against a container only when the
  container's own quoted page names that exact number ('THE FLASH 20-25, 23.2' takes #23.2 and not #23.1).
- `SpanEvidence.QuotedIssues` now reads point issues (`23.2`) and no longer reads "23.1-23.4" as the range
  1-23; `SelfProving` counts only whole numbers against the range's width.
- `apply_read_format.py`: an `ItemId` row may set any Format on any single-issue file (the shelf row keeps its
  narrow population) — the lever for a one-shot stored as "#1".
- Tests: `AnAnnualOrASpecialNeverTakesAPlaceOnTheRunsLadder`, `APointIssueNestsOnlyWhenTheBooksOwnPageNamesIt`,
  two `SpanEvidenceTests` for the parser.
- Expected effect on rebuild: ~70 annual / special files and the 3 Superman point issues un-nest; nothing
  else moves. Run the six standing checks after.

### Data (sheets written, dry-run clean, NOT applied)
- `tools/issue-numbers-read-gap.csv` — 10 rows: Flash 23.1 / 23.2 / 23.3 (74337-74339), Superman v3 23.1-23.4
  second rips (72578-72581), GL 23.2 / 23.3 (65374-65375), Red Star 7.5 (37054). Dry run: 10 to write, 0 unknown.
- `tools/format-read-gap.csv` — 11 rows: WicDiv Funnies, Power Girl Uncovered, What If Newer FF, Echoes of Fear
  → OneShot (6); Money Shot ColorUp!, Barbaric Cover Gallery, Hulk / GotG 1.MU, the Hanes giveaway, Superman
  Director's Cut → Special (5); Tomb of Dracula Complete Collection Vol. 03 (a 0-page trade rip) → Tpb (1),
  which also wants `IsCollection = 1` via `apply_read_iscollection.py`. Dry run: 11 to write.
- Not decided here (F flags stand): Quasar 102379, Tangled 20420 ("only its number is wrong" — which?),
  Irredeemable #1 Artist Edition (a reprint of #1; nesting under Vol. 01 is arguably right).
- Order: apply both sheets → `books-reading-order` (ReadTier comes from there) → `books-containment` rebuild.
  Flash Vol. 04's `S` line (S100574.txt) already quotes 'THE FLASH 20-25, 23.2', so after the sheet #23.2 nests
  and #23.1 / #23.3 sit flat — exactly what its three F lines ask for.
- Misfiled runs → split lane, all in `revisit.txt` now: NTT 1984 #38 (56096), Sentinel 2006 #1-5, DC
  R.E.B.E.L.S. (16319 / 16332), Alpha Flight v2 #18-20 (88909-88911), Cyclops 2011 #1 (91710).

## What is Eric's
1. Land X-063 (wave 193) — or say which of the two reading calls to reverse first.
2. Say yes / no to the `check_identity` completeness guard.
3. Say go on the containment code (commit + the rebuild) and on applying the two sheets.
4. Optional: `u` lines for the 21 stale `S` lines on the line shelves.

## Closed 2026-09-26

- **X-063** landed (wave 193) after reading both flagged calls from the books: Supergirl Vol. 02 has no #12 (hull #11-22,
  hole quoted), Green Lantern Vol. 05 has #28 (GCD indexes the flipbook as s236848). X-064 relinked the 50 issue files;
  all 50 nest. The `check_identity` completeness guard is in (LEDGER L-417, TOOLS_TODO 46).
- **Containment code + sheets** landed and rebuilt (c47f3c4d, 48414226, d9b1f8bd).
- **The 21 stale S lines** (population 3 above) are RESTATED in each run's own numbering from the identity C lines —
  Eric: restate, never `u` — each tagged `[restated 09-26 …]`; the finder now returns 0 and the import answers `Kept` on
  the same range.
- **Harley Quinn #63 / #65 stay flat — by design.** Shelf S94834 holds eleven trades and only two issue files (1pp
  variant covers); with fewer than `ContainmentJob.RunFloor` (3) issues the trades ARE the shelf's base ladder, so no
  container exists to nest them under. Changing that floor would move every trade-heavy shelf to save two cover images.
