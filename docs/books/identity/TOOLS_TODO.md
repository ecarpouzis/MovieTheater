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
