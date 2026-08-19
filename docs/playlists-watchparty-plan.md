# Playlists & Watch Parties — plan

**Status:** SHIPPED 2026-07-03 — historical design doc. The living map is the `tv` skill.

Both features are the **same primitive**: a *user-owned channel whose lineup is an explicit,
hand-picked ordered list of playables* (movies + episodes + misc). Everything else — the shared
broadcast timeline, the age gate, presence, shared pause/skip, the TV player shell, the guide — is
reused from Channels 2.0 unchanged.

- A **Playlist** is that channel on the normal always-on shared timeline, shown in a private
  "My Playlists" shelf, deletable by its owner.
- A **Watch Party** is the *same* channel with a "wait for everyone to press **Begin**" flag and a
  shareable invite link. It is hidden from every shelf/guide and reached only by its link; its
  timeline does not start until the lobby starts it, exactly like an Arcade room.

This mirrors the user's own framing: "a Watch-party movie session acting exactly like a Channel that
only begins when all viewers have clicked Begin … maybe just a checkbox that forces that new playlist
channel to wait for viewers, with an easy shareable link like the Arcade page."

## Why this shape

The channel engine already gives us, for free, everything both features need:

| Need | Already exists |
|---|---|
| Per-user visibility (playlist private to its owner) | `Channel.OwnerUserId` (used by "For You" reco channels); every viewer query already filters `OwnerUserId == null || == me` |
| Shared synchronized playback | `ChannelScheduleItem` immutable timeline + client joins at `Now.offsetSeconds` |
| "Everyone press Begin" / start gate | `ChannelSkipService` frozen-clock pause + majority-vote presence model |
| Shared pause / skip / restart during the party | `ChannelController` Skip/Restart/PlayPause + `ChannelScheduleService.ShiftForResumeAsync` |
| Shareable tokened invite link + lobby roster + reaper | Arcade room: `ArcadeRoomService`, `ArcadeRoomReaperService`, `/arcade/room/{code}`, copy-invite |
| The whole player (HLS/ABR/subtitles/PiP/wake-lock) | `TvPage.js` + shared hooks |

The **one genuinely new engine capability** is an *explicit, hand-ordered* lineup — every existing
strategy is filter-derived. We add a `"Playlist"` schedule strategy sourced from a new
`PlaylistItem` table, preserving the user's order and looping.

## Data model — 1 EF migration (applied manually to the shared DB; dry-check counts first)

Additive only; all new `Channel` columns nullable so existing `FilterJson`/rows deserialize unchanged.

**Extend `Channel`:**
- `IsUserPlaylist bool` (default 0) — hand-picked user playlist; distinguishes it from the reco
  "For You" channels that also set `OwnerUserId`.
- `WatchpartyToken nvarchar(32) NULL`, unique filtered index — non-null ⇒ private watch party
  (hidden from shelves/guide, reached only by token).
- `WatchpartyStartedUtc datetime2 NULL` — null until the lobby presses Begin; persisted so a
  server restart mid-party doesn't lose the start. Also the schedule epoch is re-anchored here.

**New `PlaylistItem`:**
- `Id (long, PK)`, `ChannelId (FK Channel)`, `PlayableId (FK Playable)`, `Position (int)`.
- The explicit ordered content, shared by both playlists and watch parties. Airs by **PlayableId**
  (never movieId) so movies, episodes, and misc all work through the existing `TitlesForAsync`.

## Backend

### Engine (`ChannelScheduleService`)
- New effective strategy `"Playlist"`. In `BuildEligibleCoreAsync`, when the channel has
  `PlaylistItem` rows, build the eligible set directly from those rows (join `Playable` → same
  file-present + duration + rating/ceiling gating as today), carrying `OrderRank = Position`.
- `GenerateRound("Playlist")` returns items ordered by `OrderRank`; treated as an *ordered* strategy
  (no anti-repeat cooldown, resume-from-tail) so it plays in the chosen order and loops cleanly.
- An empty playlist (all items unplayable) yields no eligible items → schedule stays empty, guide
  shows "nothing scheduled" — no crash.

### Playlist CRUD — on `ChannelController` (user-scoped: `StreamingUser` + owner check, **not** admin)
- `POST /API/Channel/Playlist/Create` `{ name, items:[playableId…], watchparty:bool }`
- `GET  /API/Channel/Playlist/Mine`
- `POST /API/Channel/Playlist/{id}/AddItems` `{ items:[…] }`
- `POST /API/Channel/Playlist/{id}/RemoveItem` / `/Reorder` / `/Rename`
- `POST /API/Channel/Playlist/{id}/Delete`
- Every mutation drops the not-yet-aired schedule tail (the established `DropScheduleTail` pattern in
  Save/Catalog/Reco) so edits take effect within a beat, keeping the currently-airing item alive.
- Ownership enforced on every call (`channel.OwnerUserId == me`); 403 otherwise.

### Watch-party lobby — `WatchpartyService` (singleton, cloned from `ChannelSkipService`) + `WatchpartyController`
- `WatchpartyService` tracks per-token in-memory: participants (userId → lastSeen + name), ready-set,
  started latch; presence TTL like the others. Ephemeral; `WatchpartyStartedUtc` on the row is the
  durable truth for "has it begun."
- `WatchpartyController` (token-scoped, `StreamingUser`):
  - `GET  /API/Watchparty/{token}` → channel meta + roster + ready states + started? + amHost
  - `POST /API/Watchparty/{token}/Heartbeat` (presence), `/Ready` (toggle), `/Begin`
    (host may force-start; otherwise auto-fires once **every present participant is ready**) — sets
    `WatchpartyStartedUtc = now` and re-anchors the channel (`AnchorUtc = now`) so the shared clock
    starts *now*; materializes the schedule on demand. `/Leave`.
  - While live, playback reuses the channel timeline: the lobby hands the player a `channelId`, and
    Now/Skip/Restart/PlayPause run through the existing `ChannelController` machinery (participants
    are authorized by party membership rather than the shelf visibility gate).
- Watch-party channels are excluded from `List` / `GuideGrid` (`WatchpartyToken == null` added to
  those where-clauses) and from the background maintainer's warm set (so their timeline never
  advances before Begin). A small reaper (Arcade-style) deletes the hidden channel + its
  `PlaylistItem`s once the party empties out / times out.

## Frontend

### Add-to-playlist UX (make bulk-select genuinely easy — the core ask)
- A reusable **"＋ Add to playlist"** action:
  - On movie cards (`CardList`/`MovieCard`) and inside the episode **season tree** in `MovieModal`
    (which already groups episodes by season with collapsible headers).
  - A **select mode**: a selected-`Set` overlay with checkboxes over `CardList`, plus
    "Add whole season / add all episodes" bulk actions in `MovieModal` — so "many episodes of a show
    or many movies" is one flow, not one-at-a-time.
- A small `PlaylistPickerModal` (clone of the `ChannelAdminModal` scaffold, user-scoped, no admin
  gate): choose an existing playlist or "New playlist…", name it, and a checkbox
  **"Make this a watch party (wait for everyone to press Begin; get a shareable link)."**

### "My Playlists" shelf
- New grouped rail in `Browse.js` beside `NowOnTvRail` (and/or a pinned group in `ChannelBrowser`),
  listing my playlist channels as poster-collage tiles, each with edit + **delete**. Tapping opens
  `/tv/:channelId` — the existing `TvPage` plays it with zero new player code.

### Watch party
- Creating with the checkbox (or a **"Watch together"** button on a movie modal → a 1-item party)
  surfaces a copy-able invite link to `/watch-together/:token`.
- New `/watch-together/:token` route → a **lobby** component (roster + Ready/Begin + copy-invite,
  heartbeat poll ~2 s) that, once `started`, hands off to the `TvPage` player pointed at the
  party's channel — reusing the viewers roster + shared pause + all stream/subtitle/ABR machinery.
- `MovieAPI.js` gains wrappers for every new endpoint; lobby polling tightened to ~2 s.

## Rollout / verification
1. Generate the EF migration; review the SQL; **dry-check row counts** on the shared DB, then apply
   manually via `dotnet ef database update` (shared prod/dev DB rule).
2. Build & run backend + `npm run build` frontend.
3. End-to-end verify: create a playlist (bulk-add a season + some movies) → it appears in the
   My-Playlists shelf → plays and loops in order. Create a watch party from a movie → open the link
   in a second session → both press Begin → synchronized start → shared pause propagates → leaving/
   deleting reaps it.

## Non-goals (defaults, adjustable later)
- Playlists are **private to their creator** (per-user shelf), matching "their own shelf."
- During a party, control follows the existing channel model (**anyone present** can pause; skip is
  majority-vote). Guests must be logged-in users (link prompts login first), like Arcade.
- Sync stays on the app's proven **polling** model (no new SignalR/WebSocket push) — tightened to
  ~2 s in the lobby; Begin lands within a couple of seconds for everyone.
