# Plan: Proper Insertion/Update Normalization (execute AFTER the scrape completes)

## Goals & constraints
- **Primary source = the data the APIs/textboxes give us, parsed into the new tables.**
  IMDB Playwright scrape is the **fallback/enricher** (adds nm ids, characters, full cast, summaries).
- Old textbox inputs (Genre, Actors, Director, Writer, Runtime, Plot, Rating) keep working; on
  save we **parse** them into the normalized model.
- **Do not stop the running scrape. Do not drop the scraped data.** All steps are additive or
  data-preserving, applied only after the bulk scrape finishes (with a DB backup first).

## The core problem
API/OMDB data and manual text give people as **names only — no IMDB `nm` id**. `Person` is
currently keyed by `ImdbNameId` (string). To store text/API people we need `Person` to support
nm-less rows. The clean fix is to re-key `Person` to a synthetic PK with a nullable `nm`.

## Step 1 — `Person` / `MovieCredit` redesign (data-preserving, post-scrape)
Final shape:
```
Person      { int Id PK; string? ImdbNameId (unique when not null); string DisplayName; string NameKey }
MovieCredit { int Id PK; int MovieID FK; int PersonId FK; CreditRole Role; int Ordering; string? Character;
              unique (MovieID, PersonId, Role) }
```
`NameKey` = normalized DisplayName (lowercase, articles/punctuation stripped) used to dedup nm-less
people and to **upgrade** a text-entered person to a real `nm` identity when the scrape later
supplies it.

Applied as one ordered, transactional SQL script (after backup) — **in-place ALTERs, no table drops**:
1. Add `Person.Id INT IDENTITY` (unique), add `Person.NameKey`; make `ImdbNameId` nullable + filtered-unique.
   Backfill `NameKey` from `DisplayName`.
2. Add `MovieCredit.PersonId INT NULL`; backfill `PersonId` by joining `PersonImdbNameId → Person.ImdbNameId`.
3. Make `PersonId` NOT NULL; add FK + unique `(MovieID, PersonId, Role)`; drop old FK/index/column
   `PersonImdbNameId`. Swap `Person` PK from `ImdbNameId` to `Id`; keep `ImdbNameId` as nullable-unique.

Every existing scraped row is preserved (nm stays in `ImdbNameId`, credits re-pointed to `PersonId`).

## Step 2 — Shared building blocks (keep it DRY, not a mess)
- **`RuntimeParser`**: string → minutes. Handles `"1 h 30 min"`, `"136 min"`, `"2h 16m"`, `"PT2H16M"`, bare ints.
- **`PersonResolver`**: resolve-or-create a `Person`.
  - nm known → find by `ImdbNameId`; else find nm-less by `NameKey` and **upgrade** it (set nm); else create.
  - nm unknown → find by `NameKey`; else create nm-less.
- **`MovieNormalizer`**: parse a movie's legacy text fields → normalized model (uses PersonResolver + Genre upsert).
  - `Genre` CSV → `Genre`/`MovieGenre`; `Runtime` → `RuntimeMinutes`; `Plot` → `PlotFull`; `Rating` → `MpaaRating`;
    `Actors`/`Director`/`Writer` CSV → `MovieCredit` (Ordering by position, Character null).
- **`ImdbDataApplier`** (existing, scrape path): refactored to use `PersonResolver` + `PersonId`. Remains
  authoritative for verified movies (nm cast, characters, summaries, synopsis).

## Step 3 — Insert / Update endpoints
- **`InsertMovie`**: save legacy movie (unchanged) → `MovieNormalizer.Parse(movie)` (text/API → normalized).
  Leave `ImdbVerifiedDate = null` so the bulk scrape can later enrich. (Optional "Enrich from IMDB now"
  action calls `ImdbScrapeService` synchronously for a known imdbID.)
- **`UpdateMovie`**: save legacy (unchanged) → re-parse **only the fields that actually changed** vs the
  prior legacy values. Precedence:
  - Movie scrape-verified & field unchanged → keep the richer scraped normalized data.
  - User edited a field → that field's normalized data is re-parsed from the edited text (user wins for
    that field; people become name-only for that role until re-scraped).
- Both reuse the shared blocks above; **no change to the textbox UI** (it still posts the same legacy fields).

## Step 4 — Frontend (minimal)
- Insert/Batch/Edit forms unchanged (still legacy string fields → backend parses).
- `MovieModal` already prefers normalized data for display.
- Optional: add an "Enrich from IMDB" button (edit/insert) to trigger on-demand scrape enrichment.

## Step 5 — Rollout order (after scrape done)
1. Confirm scrape finished; **back up `MovieSite`**.
2. Apply the Step-1 restructure SQL (transactional, data-preserving).
3. EF: regenerate the model snapshot/migration to match (so future migrations are consistent).
4. Deploy the new app build (parsing on insert/update, updated applier, read paths).
5. (Optional) one-time `MovieNormalizer` pass over scrape-flagged/missed movies so they still get
   genre/runtime/plot parsed from their existing OMDB text.

## Notes / risks
- Name-collision dedup for nm-less people is best-effort (two different same-named people merge); the
  scrape's nm data is authoritative and corrects this on enrichment.
- Deployed Docker image must include Playwright browsers for the on-demand "enrich" path (the bulk scrape
  runs locally). If we keep enrichment as bulk-only, the container needs no browser.
