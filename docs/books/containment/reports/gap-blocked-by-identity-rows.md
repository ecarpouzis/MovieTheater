# Containment decisions the import could not land (2026-09-25, after wave 192)

> Triaged the same day in `gap-blocked-triage.md`: no import change needed — 20 are an identity-lane restatement (X-063), 21 are stale `S` lines on line shelves.

`books-curated-spans-import` keeps ONE `CollectedEditionSpan(Source=Curated)` row per item and never overwrites a row
whose ProviderRef lacks the `model:` prefix ("gold"). The identity pass stores its `C` lines — a trade's range **in
another run's numbering**, with `CollectedEditionSpanRun` refs to that run — in that same slot as `identity:<batch>`.
So when a containment decision file judges the book's range **on its own shelf**, the import answers `Kept` and the
own-shelf range is never written.

Measured: **42** `S` lines across the decision files are blocked this way (9 from this session's gap batches — Human
Target Vol. 01 #1-6, Batman Incorporated Complete #1-12, Supergirl v6 / v7, Green Lantern v5 Vol. 04, X-Men v4 Vol. 04
(a v1 indicia row), …). **0** `u` refusals are blocked. Effect is coverage-only: the cross-run rows nest nothing on
the shelf (their run refs point elsewhere), so these trades simply do not claim their own shelf's issues; nothing is
mis-nested (Batman Inc's two children are its own Vol. 01 / 02 trades).

Fix needs a tooling decision (Eric): let a same-item containment range and a cross-run identity fact coexist (e.g.
key the slot on (ItemId, Source, run) or move identity C facts to their own Source), or let a `model:` own-shelf row
replace an `identity:` row whose run refs name no run on this shelf. Re-run the measurement script in the session log
after any change; `gcdnotes.py --contradictions --all` will then drop Human Target / Batman Inc.
