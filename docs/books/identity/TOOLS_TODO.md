# Tooling still owed (for the next tools-worker round; one worker at a time per Eric, so readers go first)

1. `apply_identity.py`: `I` lines may carry `gcd=s<series id>` (only the GCD series of the book is known) —
   store as `ItemProviderLink(Provider=3, ProviderKey=NULL, SecondaryKey=<series>, Status=5, Method='identity-read')`
   and `cv=-` / `gcd=-` mean no id (already tolerated by the checker since 2026-09-09 late).
2. `identity_coverage.py --second-read`: list the S.2 population (0.9 / 0.7 / R / any F / trade-only S / ≥2-key S at
   0.95) as sids + reasons, ready for `next_batch.py --revisit-file`.
3. `web_worklist.py`: merge `F needs-web` (verbatim question), `compare_decisions.py --worklist` output, and 0.7
   shelves into `web-worklist.md`, one entry per shelf with candidate ids, the settling fact, and the page URLs.
4. `next_batch.py`: raise tier B ceiling to 150 (packets are 5–8 lines).
5. `identity_coverage.py`: add an ITEM-level decided partition — issue files on an accepted run whose number is in
   the cached CV/GCD list · on an accepted run, number not cached · books with an `I` line · books on an accepted
   shelf with no `I` line (the gap Eric asked about) · on refused shelves · undecided — summing to 118,440.

# Added after wave 1 (2026-09-09 landing)
6. `check_identity.py --all`: an `S cv=<id>` whose id equals the STORED `CvVolumeId` of a different file-holding
   shelf (not only another S line) MERGES the two at resolve — require `merge-with` on it, exactly like the
   shared-S rule. Wave 1 had 8 such merges; all were intended, all undeclared to the checker.
7. `apply_identity.py --apply` (and the dry run): after the SeriesKeyLink writes, print the MERGE EXPOSURE list —
   for every merge the resolve will make, the collected editions that will move onto a shelf that has a
   containment decision file, or that carry an unjudged provider span. Wave 1 halted at audit_containment on
   two of these (Kick-Ass, Super Friends).
8. `merge_refusals.py`: write the containment refusals (`u <itemId> arrived by the wave-N identity merge; no range
   read`) for every moved collected edition into `docs/books/containment/decisions/S<survivor>.txt` (append an
   ADDENDUM block; create the file when the survivor has none, covering ALL its editions with refusals), dry-run
   by default, so `wave_land.ps1` can run it between apply and the chain. The lead did wave 1's two by hand.
9. `wave_land.ps1`: insert `merge_refusals.py --apply` + `check_decisions.py` + `books-curated-spans-import --apply`
   between `apply_identity` and `books-resolve`… no — the merge only exists AFTER resolve. Order: apply → resolve →
   merge_refusals → check_decisions → curated-spans-import → chain3 → checks.

# Added after R-004 / B-009 (2026-09-10)
10. `check_identity.py --all`: the stored-CvVolumeId collision rule must NOT fire when the partner shelf is itself
    decided (in any file, by precedence) to a DIFFERENT volume or refused — a decision that re-links the partner
    removes the collision at landing. Readers withheld 11+ correct ids because of this (N lines say
    "withheld cv=<id> … stored link of S<partner>").
11. `withheld_pairs.py`: list every (shelf, withheld cv id, partner) from the N lines + every `F <partner>
    wrong-cv-link` written from OUTSIDE the partner's own batch, ready for `next_batch.py --revisit` — the lead
    emits R-005 from it and a reader decides both sides together (the withheld shelf gets its id back; the partner
    gets its right volume or an R).

# Added after B-021 (2026-09-10)
12. Packet: for every CV volume shown with CountOfIssues == 1 (a trade's / OGN's own record), print its single
    ISSUE id from cvref `cv_iss` (the checker already verifies I-line cv ids there) as `issue <id>` beside the
    volume — readers wrote ~2,000 `I` lines with `cv=-` because the packet exposes only volume ids. Also
    `lookup.py --issues <volId>` to list a volume's issue ids/numbers.

# Added 2026-09-10 after Eric pointed at the full on-disk dumps
13. `fill_cv_from_rip.py` (dry-run default, chunked by volume id with a printed cursor, idempotent): for every
    CV volume an identity decision accepted (S lines' cv=, plus every `needs-fetch cv=<id>`), upsert
    `CvVolume` (Id, Name, StartYear, PublisherName, CountOfIssues, Deck, Description, ImageUrl, SiteDetailUrl,
    FetchedAt) from the rip's `cv_volume.raw_api_response`, and `CvIssue` rows (Id, VolumeId, Name,
    IssueNumber, CoverDate, StoreDate, Deck, Description, ImageUrl, SiteDetailUrl) from cvref `cv_iss` joined to
    the rip's `cv_issue.raw_api_response` — NO API. Never overwrite a CvVolume/CvIssue row the site fetched
    later than the rip (compare FetchedAt / date_last_updated). Replaces PLAN Phase C.1's API fetch; after it,
    `books-reading-order` upgrades container dates to rung (a) and the packets can print issue ids (item 12).
    Also `legs.CvVolumeRaw` (characters/teams/concepts/locations) can be filled from the same records for
    Phase C's fold.

# Added 2026-09-10 after the wave-2 FK failure (fixed: MergeMinorities now carries ContainmentFlag)
14. `books-series-prune --apply` (SeriesMismatchService.PruneAsync) deletes empty Series after clearing only
    SeriesAlias/SeriesTag/MuSeriesLink — 31 ContainmentFlag rows already name a series with no items, so the verb
    would crash on the FK today. Decide: re-seat each flag onto its ITEM's current series (the flag's SeriesId is a
    denormalization; ContainedDuplicateJob reads the item's series) — probably a one-off `flag_reseat.py` + the
    prune verb doing the same before deleting. Present to Eric with the 31 listed.
15. A comic Series row with NULL ParsedKey would be deleted by the finish phase without appearing in MergeMap —
    zero today; add an audit_identity invariant so it stays zero.

# Added 2026-09-16 — Eric: "close the gap there" (the ITEM pass, after wave 5 and the span-run-refs tooling)
16. **Item pass tooling** — the coverage partition shows 7,501 collected editions on ACCEPTED shelves with no `I`
    line (tier A / early tier B, before "I lines are never optional"), plus every chain-of-minis / trade-line /
    omnibus book that needs a `C` line (SPAN_RUN_IDS.md). One pass closes both:
    - `next_batch.py --items` emits `X-NNN` batches of BOOKS grouped by shelf: each packet block = the shelf's
      landed/decided identity (S line, cv/gcd, name, years) + its books lacking an `I` line or (for a book whose
      Curated span has no run rows on a shelf whose S is a collected line) a `C` line, with the per-book
      candidates the identity packet already prints (CV issue ids via the line's `--issues`, GCD issue rows,
      ISBN, LOCG bridge, judged range). Cap ~150 books per batch; `.ids` lists ITEM ids.
    - Decision files for `X-` batches carry ONLY `I` / `C` / `N` lines (no S/R: the shelf identity stands);
      `check_identity` accepts that kind (coverage = every item id in `.ids` has an `I` or an explicit
      `N <item> no-record | why`), `apply_identity` lands them like any other I/C lines, and wave landing
      includes `X-` batches. `identity_coverage.py` gains the two counts (books without I; line-shelf books
      without C) so the pass has a partition to drive to zero.
    - Sonnet reads them (item-level work suits it); Opus only for shelves the reader flags.
17. **Per-run ranges on the run table** (Opus, R-020..R-022 rewrite): a `C` line carries ONE range for ONE item, and
    `CollectedEditionSpanRun`'s PK is (ItemId, Source, Provider) — so a trade collecting TWO minis (Hellboy Vol. 06/12,
    Hell on Earth Vol. 02/04/05/07, Baltimore Vol. 03-05, Lobster Johnson Vol. 03/05/06, Abe Sapien Vol. 02, every
    omnibus / Library Edition — ~30 books in those three files, named only on N lines) cannot be stated, and legs that
    number the same issues differently (Return of the Master = CV 51622 #1-5 = GCD 71228 #103-107) get one range.
    Fix: widen the PK to (ItemId, Source, Provider, ProviderKey) and put IssueStart/IssueEnd ON the run row (the
    span's own range stays the shelf-numbered one); grammar = several `C` lines per item, one per (leg, run), each
    with the range in THAT run's numbering; apply writes one run row per C; ContainmentJob compares two spans on any
    shared (Provider, key) using the run rows' ranges. Build it with 16 (the item pass), then the item pass writes
    the second C lines for the ~30 books and every omnibus.

18. **Packet: print what a trade collects from GCD's own reprint data** (R-025): `gcd_reprint` + `gcd_story` joined story →
    reprint → origin issue → series gives each collected edition's contents outright; `gcd_series.notes` states ranges in
    prose. Neither is in the packet or lookup.py today — add a `collects:` line per book (and `lookup.py --collects <issue>`)
    so readers stop deriving C ranges from page arithmetic.

19. **Tier D packet: probe the FILE's full title, not the parsed key** (D-001..D-003): with no stored leg the candidate
    probe falls back on the shelf's parsed key, i.e. the folder's words ("Dark Horse Maverick 2000" on 20+ shelves, "The
    Originals" on every ComiXology shelf) — the record was found by re-probing the file's own title on a third of shelves.
    identity_packet should probe each FILE's title (minus the ripper suffix) and the ComicInfo Title/Series before the key.
    Addendum (D-009): on ~80 shelves the parsed key is a TRUNCATION or a ripper tag, so the candidate block is actively
    misleading (every Star Trek / Heavy Metal packet was handed 'Hanna-Barbera Yogi Bear'); also probe gcd_issue.barcode on
    the 11-digit UPC core and print the hit.
    Addendum (D-012): identity_packet FOLDS runs of >= 6 same-skeleton filenames into a range WITHOUT item ids, so a
    shelf like 3x3 Eyes v01-v39 cannot get per-volume I lines from the packet — the X item pass covers them (they show
    as "NO I line"), or the packet should list item ids in the fold.
# Added 2026-09-22 after wave 12 (Eric: defer the 0.9s; make workers cheaper to start)
20. **Slim the brief.** READER_BRIEF.md is 110 KB; 96 KB is the conventions ledger (335 entries) that every worker
    reads and carries through ~100 tool calls. Move the ledger to `LEDGER.md` with every entry TAGGED (publisher /
    imprint / folder pattern / tier / topic); promote the few entries that apply everywhere into Rulings; the brief
    keeps the contract only (~15 KB target). The packet builder (identity_packet.py / next_batch.py) attaches, per
    batch, ONLY the ledger entries whose tags match that batch's shelves (a ## Conventions for this batch block at
    the top of the batch file). No ledger entry may be lost; selftest proves the round-trip (every entry reachable).
21. **Packet: GCD's own "Collects …" notes.** R-028/X-062 settled most C ranges from gcd_issue.notes
    ("Collects X #a-b") and it overturned four judged ranges (Bloodshot 2019 Books 1-4, Life Is Strange: Coming
    Home, American Vampire Book One, the X-Men chronology). Print the notes line on every GCD issue row a packet shows
    for a trade (with gcd_reprint, TODO 18, if not already there), and flag in the packet any judged range that the
    notes contradict (RANGE CONTRADICTED by GCD notes: judged #a-b, GCD says #c-d).
22. **lookup.py --batch <file>**: answer many name / volume probes in one call (one line per query), so a reader
    does not spend a tool round-trip per spelling.
23. **0.9 triage (`triage_09.py`, read-only, chunked by shelf id with a cursor):** the 6,327 shelves at 0.9 are NOT
    re-read blind. List only those with a SIGNAL, one row per (shelf, signal, detail): (a) a CV volume or GCD series
    claimed by another shelf's S line or by an I/C line elsewhere; (b) a majority of the shelf's per-file CV links
    disagree with its S volume; (c) a C line / judged range / GCD notes range that does not fit the S run's count or
    numbering; (d) an open ContainmentFlag; (e) 21+ files; (f) touched by a landed merge or split since it was
    decided. Print totals per signal and the de-duplicated shelf count. Feed format = what next_batch.py can emit as
    a tier-S2 batch. Signal-free 0.9 shelves stay at 0.9 and are DONE.
# Added 2026-09-22 after R-029
24. **lookup.py --gcd-issues <series>** (and `--gcd-series "<name>"`): GCD issue rows with page counts, ISBNs and
    notes. The R-029 reader had to hand-write read-only SQLite on the GCD dump for exactly this.
25. **gcdnotes.py: detect stamped rows.** The `GCD says` row comes from the item's stored GCD issue link, which on
    DC/Marvel trade lines is often another book's row (WW by Pérez Vol. 01-03, Uncanny by Austen 1-6 showed the 2013
    Bendis trades). Compare the row's series name / issue title / page count to the file; print `⚠ stored GCD row is
    another book` instead of a contradiction, and re-count the 66 contradictions without the stamped ones.
26. (Decide later) a `C` grammar for disjoint ranges of one run (`#435-436,442-443`) so the N workaround in the
    brief can retire; needs checker + apply + curated-spans-import support.
# Added 2026-09-22 after wave 13 — the split lane (S.2's biggest open-by-construction set)
27. **Split lane (`P-NNN` batches).** 508 shelves carry `F split-needed` (proposed runs in the detail) and are
    refused until split; a blind re-read cannot fix them. Build: (a) `next_batch.py --splits` emits P- batches,
    one packet per flagged shelf = the winning F split-needed detail + the R line + `propose_split.py`'s grouping
    (title prefix x folder x numbers) + every item id with folder/filename/pages; big shelves (> 300 lines) alone.
    (b) The reader writes `decisions/P-NNN.jsonl`: one `{"itemId", "key", "run"}` per item it MOVES (items that
    stay omit), where `key` is the new ParsedSeriesKey and `run` names CV/GCD ids for the new run when known.
    (c) `check_splits.py`: every item belongs to its shelf, keys are new or an existing shelf's key named in the
    F line, no key collides with an unrelated live shelf's ParsedKey, every group >= 1 item, no item twice.
    (d) The landing (lead / Eric): backup_live -> `books-series-split --apply` per file -> `books-resolve --series`
    -> wave_fix -Resolve checks -> the NEW shelves go into the next identity batches (their S lines can be seeded
    from the `run` ids). Walk-back = the verb's CSV. Selftest covers the checker.
# Added 2026-09-22 after the P-001 pilot (30 two-file shelves; 119k tokens, 13 tool calls, ~2.5 min)
28. **Before the big split shelves (Judge Dredd S10002, Uncanny X-Men S6791, X-Men S66349, the 101-300 file band):**
    (a) packet: a `nearby:` line listing live shelves whose key/title shares the run's title words or whose S line
    holds a cv/gcd id named in the F line or the groups (the Elric S6101 → S6105 join was missed for lack of it);
    (b) check_splits: WARN when an item line's `run` cv/gcd id is already the S identity of another live shelf that
    the F line does not name — a probable missed join; allow a lead-approved join via a `{"shelf": N, "join": [sid,
    …]}` field instead of editing the F line; (c) a group/range move syntax — `{"group": "G3", "key": …}` or
    `{"range": "#1-66", "match": "<filename words>", "key": …}` — expanded by check_splits --project into item
    lines, so a 900-file shelf is not hand-typed item by item; (d) brief: the key shape for one-shots and specials
    (`<Title> (<Year>)`, with the special's own name, never `#0`).
# Added 2026-09-23 after P-002 (80 shelves of 1-6 files)
29. Split packets: `nearby:` must also probe ids in the decided R clause and the shelf's N lines (P-002 missed
    S102444 Cosplayers and S9439 Infinity, both named only there); print a trade's COLLECTED-run ids beside its own
    record (gcd_reprint); a shelf line `pending_join: [sid]` for "stays only because it cannot move without a join",
    carried into the next R packet as a merge-with prompt.
30. lookup.py drops "of" from probes ("Legion of Monsters", "Heart of Darkness" → 0 CV hits) — fix the stopword list.. check_identity --all's undeclared-merge rule must also compare an S line's cv against the STORED Series.CvVolumeId of REFUSED shelves (and their aliases' Matched v1 SeriesKeyLinks): wave 17's S15478 (cv=22340) merged undeclared into refused S47160, whose v1 keys still carry 22340. The merge was right (the trade joined its run), but it must be declared.

# Added 2026-09-23 after TODO 30's finding
32. **S.2 signal: probe-blind provider-missing.** Until TODO 30, ~34k of 154k CV volumes (names with of/the/and/a) could not be found by an exact probe, so earlier `F provider-missing` / `cv=-` lines may have missed a volume that exists. Re-probe every winning `cv=-` / provider-missing shelf with the fixed index (read-only, chunked) and feed the hits to the S.2 worklist (and triage_09 as signal (g)).
    **DONE 2026-09-23:** `tools/reprobe_cvmissing.py` (read-only, chunked, resumable; the packet's own probe set against the fixed vs the old keying, reporting only volumes the old index could not see). Full run: 3,341 shelves probed, 1,429 with a new hit, 855 with a NEAR one (640 by the shelf's own spelling) -> `reprobe.tsv` / `reprobe-near.tsv` (signal g) -> `next_batch --s2-file`.
# Added 2026-09-23 after R-032
33. **Keep split pairs together.** After a split, a KEPT half usually still stores the CV volume of the run that moved
    off it, so the new shelf's "run wins" S collides (R-032 had to withhold 10 cv ids, L-151). (a) `check_splits
    --landed` sheet rows carry a `pair=<P-shelf>` group; `next_batch --revisit-file` must never cut a batch
    inside a group (halve by groups); (b) the kept-half packet prints `stored cv <id> = the moved run's` when its
    stored CvVolumeId equals a moved item's `run` cv, prompting `F wrong-cv-link`.
34. **lookup.py --shelf <sid>** (keys, files, stored cv/gcd, S line in force) and **--who-stores cv=<id>|gcd=<id>**
    (every live shelf storing it, incl. alias SeriesKeyLinks). R-032's reader spent ~12 calls building these by hand.
35. Rule 31 must also cover EMPTY shelves: wave 19's S96256 (R-032 cv=144027) merged undeclared into the empty S64503, which stored 144027 with no files (the reader had flagged it on an N line). Treat a stored CvVolumeId on ANY live Series row, file-holding or not, as a merge partner that needs a declared merge-with or a pre-landing clear.
36. (with 34) split packets' `nearby:` / `named:` lines print each shelf's ParsedKey(s) verbatim — a `join` key must match exactly, and P-003 and P-004 readers each wrote a scratch sqlite helper for it; `lookup.py --id cv=<vol>|cvi=<issue>|gcd=<series>|gcdi=<issue>` prints the name, publisher, year and count.
37. **Stale flags land in ONE wave, not two.** Today a reader who finds an open conflated/overlap flag stale (or resolved by L-186) must write R + `F conflated-series`, the lead verifies + dismisses, and a LATER revisit lands the S (R-031 → R-034 → R-036 chains). Instead: the reader writes the S plus `F <sid> stale-flag=<flagId> | evidence`; check_identity accepts an S on a flagged shelf only with that line; wave_land.ps1 (before apply) prints every stale-flag claim and STOPS unless the lead has listed the flag id in `identity/stale-approved.txt` (verified against the shelf's files); the landing then dismisses exactly the approved flags (Note = the evidence + 'lead-verified') before apply. Selftest: unapproved claim stops the wave; approved claim lands S + dismissal together.
38. check_identity: WARN (or fail) on `F <sid> merge-with=<target>` when the winning S lines of BOTH shelves carry `cv=-` — the resolver merges only on a shared CV, so the line can never take effect (R-043's 14555→103376). Point the reader at a split-lane join instead.
39. **Two split-lane leaks (found 2026-09-23 after wave 31).** (a) `check_splits --landed` lists new shelves and kept halves but NOT `split: false` shelves, so a shelf kept whole under L-186 never reaches an identity batch and stays a refused split-needed forever (P-003..P-009 left nine: S15497, S17660, S20348, S7823, S8441, S5939, S6410, S11422, S7004) — list them as `kept-whole` rows. (b) `next_batch --splits` (and the lead's picker) exclude every shelf ever emitted in a P- batch, so a shelf RE-flagged split-needed by a later R- revisit (S22811, S8477) is skipped forever — re-admit a shelf whose winning decision file is newer than its last P- emission.
40. **Selftest split fixtures stand on the LIVE split population, which is now drained** (2026-09-23, after wave 49: only S19456 + S14950 left). Three checks fail for want of data, not code: 'the split population holds a shelf the fixtures can stand on', 'the split packet (SNone) carries … EVERY item id', and 39b 're-admits the shelves a later revisit re-flagged ([19456])' (19456 is excluded on purpose — it needs an issue-number fix, not a split). Same class, seen at wave 106 (09-23): "S1928 conflated: ours block" (the fixture shelf changed under it) and "(f) the population mode does a bounded chunk" (expects processed 5; the open conflated/overlap population is down to 1). Build the split fixtures on a scratch copy of the db (a synthetic split-needed shelf) instead of the live population. Also: R-061 found the S.2 yield is mostly GCD-side (17 provider-missing withdrawn by hand `--contains` / `--gcd-series` probes); the GCD index was never blind, so a GCD re-probe would need FRAGMENT / article-dropping probes (L-334), not a keying fix.
41. **Series ids are REUSED, and a merge can leave no SeriesMerge row** (wave 59, 2026-09-23). S102390 (the New Guard 2015 run shelf, created by a split in September) carried an id an EARLIER shelf had used — that one merged into S20873 on 2026-09-08 — so `lead/merge_count_check.py` (latest SeriesMerge row by OldSeriesId) printed 'S102390 -> merged into S20873' although the declared merge S8162 <-> S102390 had happened correctly (S8162 holds all 8 files) and wrote NO SeriesMerge row of its own. Fix: merge_count_check should only read SeriesMerge rows newer than the pre-wave backup, and fall back to 'where did its files go' (Item.SeriesId in the live db for the backup's item ids); every audit that joins SeriesMerge by OldSeriesId (audit_identity's merged-away exemptions) has the same id-reuse blind spot.
42. **check_identity forces a never-landing merge-with on two cv=- shelves that share a GCD id** (R-073, R-079). The gcd-collision rule accepts only a merge-with, which TODO 38 then WARNs can never take effect; the real mechanism is `F <sid> split-needed | join S<partner>`. Accept a shared gcd between two cv=- shelves when one carries a split-needed naming the other, and stop asking for the merge-with.
43. **check_splits --all (landed files) reports P-028 item 92349 as not on its join target** — P-028 joined S6482 -> S94599, then R-087 merged S94599 INTO the emptied S6482 (the empty shelf survived), so the item correctly sits on S6482 under S94599's key. The landed-join exemption should follow SeriesMerge in BOTH directions (target merged into the source). The landing gate (--all --unlanded) is unaffected.
44. **P-043 / R-116 reader findings (2026-09-24).** (a) `check_splits` REFUSES a `"from"` field on item lines although
WORKER_PROMPT_SPLITS.md tells multi-shelf batches to add one — fix one or the other. (b) `check_identity` FAILS two shelves
sharing a GCD id unless a merge-with is declared, yet merges only land through a shared CV — the reader is forced to write an
inert merge-with (Grandville 7836->7834, Don Rosa 21191->68154); same root as TODO 42.
**(b) RESTATED 2026-09-24 (lead, L-377):** a shared GCD with no shared ComicVine id REQUIRES a split-lane `join` or a
`pending_join` naming the partner, NEVER a forced merge-with — check_identity must accept that and stop demanding the merge-with.
The split lane can empty a one-file shelf onto a live join target (check_splits, since 9f2bfea5), so L-373's "lead lane" was never needed.
**DONE 2026-09-24 (42 + 44b):** check_identity accepts a shared gcd when every shelf of the group joins, or is joined by, another member through `F <sid> split-needed | … join S<member>`.
45. **`lookup.py --batch` passes only the FIRST WORD of a plain-name line** (R-123 reader, 2026-09-24): "Ghostbusters Get
Real" was probed as "ghostbusters". `--issues` / `--gcd-issues` lines work. Fix the name-line parse (quote-aware or whole line).
