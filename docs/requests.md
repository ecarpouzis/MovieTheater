# The request queue (`/requests`)

*Added 2026-10-06.* The communal wishlist for the **library**: "I'm out and saw a movie / record /
board game / game / book we should have." Any signed-in user files a request, everyone sees the
whole queue and votes on it, and the request is closed when the content lands on the site. It is
distinct from a user's Want-to-Watch list, which is about what to watch out of what we already have.

## Shape

| Piece | Where |
|---|---|
| Entities | `src/MovieTheater.Db/ContentRequest.cs` — `ContentRequest` (one per ask) + `ContentRequestVote` (one "me too" per user per request); the section and status vocabularies live beside them |
| Schema | migration `20261006120000_AddContentRequests` + the idempotent twin `sql/AddContentRequests.sql` (applied to the live DB 2026-10-06) |
| API | `Controllers/RequestsController.cs` → `/API/Requests` (below) |
| Resolver | `Requests/ContentRequestMatcher.cs` — the loose title match + the bounded sweep |
| Sweep | `Requests/ContentRequestSweepService.cs` — half-hourly, chunked, off in Development |
| Page | `src/ui/src/Pages/Requests/RequestsPage.js` (+ `.css`, `requestSections.js`) |
| Rail | `NavBar/RequestsNavContent.js` — counted index rows that are the page's URL views |
| Nudge | `Pages/Requests/RequestsAttentionToast.js` — one toast per session for an admin with proposals waiting |
| Section | a row in `catalog/bar/sections.ts` (one tab), `NavBar.js` SECTIONS + switcher entry, `theme.css` `data-feature="requests"` (slate — amber until 2026-10-08, when it collided with Books/Photos) |

## A request

`Section` is which part of the site it is for — `movies` (movies **and** series), `music`,
`boardgames`, `arcade`, `books`. `Title` as typed; `Year` optional; `Detail` is the section's one
disambiguator (artist / system / author / designer); `Link` an outside URL; `Notes` free text. These
are separate columns on purpose: they are what a future acquisition job keys off.

`Status` is `open` → `fulfilled` (admin, "Added") / `declined` (admin, with a note) / `withdrawn`
(the requester). Reopen is allowed (admin for anything, the requester for their own withdrawal).
Delete is admin-only and hard; a requester withdraws so the history keeps what was asked.

Only the title is required. Year, detail, link and notes are optional — a requester out and about may
not know them, and the form says so.

Duplicates: filing an open request in the same section whose normalized title is **equal** to another
open request's (not the matcher's loose containment — "Dune" must not block "Dune: Part Two"), with the
years within one when both are given and the detail agreeing when both are given, is a **409** carrying
the existing row — the page offers a vote instead. A user may hold at most 50 open requests. Votes are
on other people's OPEN requests only (server-enforced).

## Resolution: loose, admin-confirmed

Nothing closes on its own. The matcher looks for **one** catalog row whose normalized title agrees
with the request, in the request's section:

- normalized = lowercase, ASCII-folded (`Ingest.TitleNorm.Fold`), leading article dropped, `&` → `and`,
  punctuation stripped;
- agree = equal, or whole-token containment where the shorter side is two tokens or a single token of
  ≥ 4 letters (`dune` reaches `dune part two`; `it` never reaches `it follows`);
- a year on both sides must be within one; a loose title with a wrong year is **no** match;
- the candidate pull is an exact-title query plus one `LIKE` on the title column for the request's
  longest token (then its second; an apostrophe inside a token becomes `%` so "assassins" reaches
  "Assassin's"), the LIKE pull ordered shortest title then newest id and capped at 60 rows, scored in
  memory. **Only content we have**: a movie with a non-missing media file, a series with at least one
  such episode, an enabled arcade row; albums/artists and boardgames exist by construction. Movies +
  Series for `movies`; album (artist as tie-break) or artist by name for `music`; `Boardgame.Name`;
  `ArcadeGame.Title`. **Books are not matched** (the library is the Books host's SQLite) — a books
  request is closed by hand.

A hit is written as a **proposal** (`MatchKind/MatchId/MatchTitle/MatchFoundUtc`). The admin confirms
(closes as fulfilled, linked via `FulfilledKind/Id`) or dismisses (`DismissedMatchKey` remembers the
one key so it is not re-proposed; a different row may still be). The matcher runs on filing (so "we
might already have this" shows at once) and in the sweep.

The sweep (`SweepAsync(from, limit)`) walks **open requests with no standing proposal**, in id order,
50 per chunk, resumable; the service runs up to 20 chunks every 30 minutes and keeps its cursor between
ticks (wrapping to 0 at the end), so a long queue is walked end to end rather than its head re-checked. The admin's "Check the
library" button runs the same chunks from the page. The page shows a banner with the count of proposals
waiting; `RequestsAttentionToast` shows one toast per session elsewhere on the site.

## API (`[Authorize]`; admin = config `AdminUsernames` + `amr=pwd`)

| | |
|---|---|
| `GET /API/Requests?status=open\|closed\|all&section=&beforeId=&limit=` | `{ requests, totalCount, nextBeforeId }`, newest first, per-viewer (`votedByMe`) |
| `GET /API/Requests/Summary` | `{ open, needsConfirmation, mine, bySection, canResolve }` |
| `POST /API/Requests` | file; 409 `{ duplicate }` |
| `POST /API/Requests/{id}/Vote` | toggle, on another user's open request → `{ votes, votedByMe }` |
| `POST /API/Requests/{id}/Status` `{ status, note }` | the rules above |
| `POST /API/Requests/{id}/Match/Confirm` · `/Dismiss` | admin |
| `POST /API/Requests/Sweep?from=&limit=` | admin; one chunk → `{ checked, proposed, nextFrom, remaining }` |
| `DELETE /API/Requests/{id}` | admin |

## Automating acquisition later

Everything a job would need is on the row: `Section`, `Title`, `Year`, `Detail`, `Link`, the requester,
the vote count (priority). The natural seam is a consumer that reads `status = open` rows, does its
work, and lets the existing sweep + admin confirm close them — the confirm step stays human.

## Review (2026-10-07, Opus)

Fixed from it: the loose duplicate check; proposals for titles without a playable file; the LIKE cap
dropping the newest rows (exact pass + ordering); the page's unaborted list reads; the sweep restarting
at 0 every tick; antd 6's deprecated notification props; a state setter inside an updater; the a11y of
the custom buttons. **Left as a product decision**: filing and voting need only the passwordless
communal login, which anyone can type — a password-verified session (`amr=pwd`) on those writes would
stop vote-gaming but would also lock out most of the household. Revisit if votes ever drive an
automated acquisition job.

## Tests

`src/MovieTheater.Tests/ContentRequestMatcherTests.cs` (normalization, agreement, lookup, sweep over
SQLite), `RequestsControllerTests.cs` (permissions, 409, votes, status rules, confirm/dismiss, paging),
`src/ui/src/Pages/Requests/RequestsPage.test.jsx` (guest / member / admin surfaces, voting, filing,
URL-driven views).
