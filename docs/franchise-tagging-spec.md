# Franchise tagging spec

The rubric for `TitleTag` rows with `Category = Franchise`. These tags are model-generated
in-session and loaded by `load-ai-metadata` (see `data/ai-metadata/README.md`); this doc is the
**version-controlled prompt** for the franchise facet so generation stays consistent across
sessions. It exists because the modal now renders a **franchise rail** — an ordered strip of a
title's franchise, current film highlighted, so you can see what comes next/before. That rail is
only as good as the tags: a tag that conflates separate continuities produces a wrong "next".

## What a franchise tag is

A shared-continuity grouping a viewer would binge or follow in order: a film series, a shared
universe, a long-running anime, an author's adaptations. **Not** a genre, studio, or vibe (those
are other categories). A standalone film with no series gets **no** franchise tag.

## The two rules that matter for the rail

### 1. Most-specific shared continuity wins

Tag the **narrowest continuity the title actually belongs to**, because the rail orders members
by release date — mixing continuities makes the wrong film look "next".

Worked example — the `godzilla` problem: the bare `godzilla` tag spanned three unrelated
continuities (1954+ Toho, the 2018 anime trilogy, the American MonsterVerse). For
*Kong: Skull Island* (2017) that makes the next-by-date film the *anime* (Jan 2018) instead of
*Godzilla: King of the Monsters* (2019). The fix is a continuity-specific tag (`monsterverse`).

### 2. Dual-tag: keep the umbrella, add the continuity

When a continuity-specific split is needed, give the title **both** tags:

- the **umbrella** (`godzilla`, `dc`, `spider-man`) — for broad browse/discovery, and
- the **continuity** (`monsterverse`, `dceu`, `spider-verse`) — for an accurate rail.

The rail **anchors on the most specific** franchise (fewest members), so the dual-tag title
sequences within its continuity while still showing up under the umbrella. Weight the
continuity tag ≥ the umbrella so it reads as primary.

**Only split when the continuity has enough library members (~3+) to form a real rail.** Don't
fragment a 4-film series into singletons (e.g. don't split Batman into Burton/Schumacher/Nolan
unless each continuity is well represented on disk). Under-splitting (one umbrella) beats
over-splitting (many singletons): a singleton continuity is just noise.

## Naming

- Lowercase; spaces between words; keep established hyphens (`spider-man`, `x-men`, `scooby-doo`).
- No leading article (`lord of the rings`, not `the lord of the rings`).
- One canonical slug per franchise — reuse the existing one, don't coin a variant. The loader
  normalizes some known drifts (`marvel cinematic universe` → `mcu`,
  `fast and the furious` → `fast and furious`) and the canonical slug seed lives in
  `AiMetadataVocab.cs` (`TagCategory.Franchise`). A franchise not in that seed logs as "novel"
  on load — that's the signal to add it there.

## Weight (0–100)

Centrality of the title to the franchise: a numbered mainline entry is high (90–95); a spin-off,
crossover cameo, or loosely-attached entry is lower (60–80). When a title carries both an umbrella
and a continuity tag, the continuity tag should be the higher of the two.

## Franchises-only regeneration

To fix tags without regenerating whole insights (which would churn the good narrative/slider
data), emit a **franchises-only** batch — entries carrying only `subjectKind`, `subjectId`, and
`Franchise` tags — and load with:

```
dotnet run --project src/MovieTheater -- load-ai-metadata \
  --file data/ai-metadata/insights-franchises-<name>.json --franchises-only
# add --apply to write
```

`--franchises-only` replaces just the `Franchise` tags on each subject's **newest** insight and
leaves every other facet untouched. It is idempotent (a subject whose franchise tags already
match is skipped), dry-run by default, and honors `--limit`. Work **one franchise cluster at a
time** (a cluster = all members of one current franchise value) so each run is bounded and the
remaining-count drives you to completion.
