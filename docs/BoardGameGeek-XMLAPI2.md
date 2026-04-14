# BoardGameGeek XMLAPI2 Reference

## Introduction

To start, read the introductory guide to using the XML API and the XML API terms of use.

The `XMLAPI2` is a newer version of the XML API and is currently in **BETA**. For the old API, see `BGG_XML_API`.

Each base URI is described and parameters are URL-encoded in the standard way.

For current info, discussions, and source material:

- BoardGameGeek XML API / guild thread: <https://boardgamegeek.com/guild/1229>
- Related wiki: **Data Mining** (based on previous XMLAPI)
- Current bugs/enhancement requests: **XML API Enhancements**

## CSV Downloads

In addition to the XML API, BGG provides a single CSV file with names, IDs, ranks, and average rating for all games.

If you need all game names and ranks, this is the preferred source.

- CSV link: <https://boardgamegeek.com/data_dumps/bg_ranks>
- For licensing purposes, this data is considered part of the XML API.

## Root Path

All XMLAPI2 requests are prefixed with:

- `https://boardgamegeek.com/xmlapi2/`
- `https://rpggeek.com/xmlapi2/`
- `https://videogamegeek.com/xmlapi2/`

These are interchangeable.

> Avoid `www` subdomains (for example, do not use `https://www.boardgamegeek.com/xmlapi2/`) because this may interfere with request authorization.

## Rate Limit

BGG throttles requests. If requests are too frequent, the server may return `500` or `503` (“too busy”).

A **5-second delay** between requests is typically sufficient.

## Commands

Usage below is current but may change. XML API areas not yet changed follow `BGG_XML_API` syntax.

Parameter separator rules:

- First parameter follows `?`
- Additional parameters follow `&`

---

## Thing Items

In BGG, a physical/tangible product is a **thing**.

Supported `THINGTYPE` values:

- `boardgame`
- `boardgameexpansion`
- `boardgameaccessory`
- `videogame`
- `rpgitem`
- `rpgissue` (periodicals)

> Note: Max 20 items from XML API and XML API2.

**Base URI:** `/xmlapi2/thing?parameters`

| Parameter | Description |
|---|---|
| `id=NNN` | ID(s) of thing(s) to retrieve. Comma-delimited IDs allowed. Maximum 20. |
| `type=THINGTYPE` | Filter returned results by specified THINGTYPE(s), regardless of requested ID type. Comma-delimited allowed. |
| `versions=1` | Return version info. |
| `videos=1` | Return videos. |
| `stats=1` | Return ranking and rating stats. |
| `historical=1` | Not currently supported. Intended to return historical data over time. See `page`. |
| `marketplace=1` | Return marketplace data. |
| `comments=1` | Return comments (including ratings when commented). See `page`. |
| `ratingcomments=1` | Return ratings (including comments when rated). See `page`. Cannot be used with `comments`; `comments` takes precedence. Sorted ascending by rating value. |
| `page=NNN` | Default `1`; controls page for historical/comments/ratings data. |
| `pagesize=NNN` | Page size. Minimum `10`, maximum `100`. |
| `from=YYYY-MM-DD` | Not currently supported. |
| `to=YYYY-MM-DD` | Not currently supported. |

---

## Family Items

Abstract/esoteric concepts are represented as a **family**.

Supported `FAMILYTYPE` values:

- `rpg`
- `rpgperiodical`
- `boardgamefamily`

**Base URI:** `/xmlapi2/family?parameters`

| Parameter | Description |
|---|---|
| `id=NNN` | ID(s) of family/families to retrieve. Comma-delimited allowed. |
| `type=FAMILYTYPE` | Filter results by FAMILYTYPE(s), regardless of requested ID type. Comma-delimited allowed. |

---

## Forum Lists

Request forums for a specific type/ID.

**Base URI:** `/xmlapi2/forumlist?parameters`

| Parameter | Description |
|---|---|
| `id=NNN` | ID of database entry whose forum list is requested. |
| `type=[thing,family]` | Entry type. |

---

## Forums

Request threads in a forum.

**Base URI:** `/xmlapi2/forum?parameters`

| Parameter | Description |
|---|---|
| `id=NNN` | Forum ID. |
| `page=NNN` | Thread-list page. Page size is `50`. Sorted by most recent post. |

---

## Threads

Request thread details and contained articles/postings.

**Base URI:** `/xmlapi2/thread?parameters`

| Parameter | Description |
|---|---|
| `id=NNN` | Thread ID. |
| `minarticleid=NNN` | Return articles with ID >= `NNN`. |
| `minarticledate=YYYY-MM-DD` | Return articles on or after date. |
| `minarticledate=YYYY-MM-DD%20HH%3AMM%3ASS` | Return articles on/after date-time (`HH:MM:SS`). |
| `count=NNN` | Max articles to return (`max 1000`). |
| `username=NAME` | Not currently supported. |

---

## Users

Request basic public profile info by username.

**Base URI:** `/xmlapi2/user?parameters`

| Parameter | Description |
|---|---|
| `name=NAME` | Username (one user per request). |
| `buddies=1` | Include buddies (paged; see `page`). |
| `guilds=1` | Include guilds (paged; see `page`). |
| `hot=1` | Include user hot 10 (omitted if empty). |
| `top=1` | Include user top 10 (omitted if empty). |
| `domain=DOMAIN` | Domain for hot/top 10 lists. Default `boardgame`. Valid: `boardgame`, `rpg`, `videogame`. |
| `page=NNN` | Page for buddies/guilds. Default `1`. Page size documented as `100` (current implementation may return `1000`). Controls both buddies and guilds if both are requested. Empty `<buddies>`/`<guilds>` indicates out-of-range page, or none exist on page 1. |

---

## Guilds

Request guild information.

**Base URI:** `/xmlapi2/guild?parameters`

| Parameter | Description |
|---|---|
| `id=NNN` | Guild ID. |
| `members=1` | Include member roster (paged/sorted). |
| `sort=SORTTYPE` | Member sort order. Default `username`. Valid: `username`, `date`. |
| `page=NNN` | Members page. Page size is `25`. |

---

## Plays

Request logged plays by user or item.

**Base URI:** `/xmlapi2/plays?parameters`

| Parameter | Description |
|---|---|
| `username=NAME` | Player username. Returns reverse-chronological plays. Must include either `username` OR `id`+`type`. |
| `id=NNN` | Item ID. Returns reverse-chronological plays. |
| `type=TYPE` | Item type. Valid: `thing`, `family`. |
| `mindate=YYYY-MM-DD` | Plays on/after this date. |
| `maxdate=YYYY-MM-DD` | Plays on/before this date. |
| `subtype=TYPE` | Limits results to subtype. Default `boardgame`. Valid: `boardgame`, `boardgameexpansion`, `boardgameaccessory`, `boardgameintegration`, `boardgamecompilation`, `boardgameimplementation`, `rpg`, `rpgitem`, `videogame`. |
| `page=NNN` | Result page. Page size is `100`. |

---

## Collection

Request a user collection.

Important notes:

- Check HTTP status:
  - `202` = queued by BGG; retry with delay until status is not `202`.
  - `200` = ready.
- `subtype=boardgame` (or default) returns both boardgames and expansions, but expansions may be mislabeled as `subtype=boardgame`.
  - Workaround: call once with `excludesubtype=boardgameexpansion`, then again with `subtype=boardgameexpansion`.

**Base URI:** `/xmlapi2/collection?parameters`

| Parameter | Description |
|---|---|
| `username=NAME` | Username whose collection is requested. |
| `version=1` | Return version info for each item. |
| `subtype=TYPE` | Collection subtype: `boardgame`, `boardgameexpansion`, `boardgameaccessory`, `rpgitem`, `rpgissue`, `videogame`. Default `boardgame`. |
| `excludesubtype=TYPE` | Exclude subtype from results. |
| `id=NNN` | Filter to specific item ID(s); comma-delimited allowed. |
| `brief=1` | Return abbreviated results. |
| `stats=1` | Return expanded rating/ranking info. |
| `own=[0,1]` | Filter owned items. |
| `rated=[0,1]` | Filter rated items. |
| `played=[0,1]` | Filter played items. |
| `comment=[0,1]` | Filter items with comments. |
| `trade=[0,1]` | Filter items marked for trade. |
| `want=[0,1]` | Filter items wanted in trade. |
| `wishlist=[0,1]` | Filter wishlist items. |
| `wishlistpriority=[1-5]` | Filter by wishlist priority. |
| `preordered=[0,1]` | Filter preordered games. |
| `wanttoplay=[0,1]` | Filter items marked want-to-play. |
| `wanttobuy=[0,1]` | Filter items marked want-to-buy. |
| `prevowned=[0,1]` | Filter previously owned items. |
| `hasparts=[0,1]` | Filter items with Has Parts comment. |
| `wantparts=[0,1]` | Filter items with Wants Parts comment. |
| `minrating=[1-10]` | Min personal rating filter. |
| `rating=[1-10]` | Max personal rating filter (named `rating`, not `maxrating`). |
| `minbggrating=[1-10]` | Min BGG rating filter (`0` ignored; can use `-1`, e.g. min `-1` and max `1` for no-rating items). |
| `bggrating=[1-10]` | Max BGG rating filter (named `bggrating`, not `maxbggrating`). |
| `minplays=NNN` | Min recorded plays filter. |
| `maxplays=NNN` | Max recorded plays filter. |
| `showprivate=1` | Show private collection info (works only for own collection while logged in). |
| `collid=NNN` | Restrict to a specific collection ID (also returned in normal query results). |
| `modifiedsince=YY-MM-DD` | Return items whose status changed/was added since date (not deletions). Time allowed: `YY-MM-DD%20HH:MM:SS`. |

---

## Hot Items

Retrieve most active items.

**Base URI:** `/xmlapi2/hot?parameter`

| Parameter | Description |
|---|---|
| `type=TYPE` | Hot-list type. Valid: `boardgame`, `rpg`, `videogame`, `boardgameperson`, `rpgperson`, `boardgamecompany`, `rpgcompany`, `videogamecompany`. |

---

## Geeklist

Not yet updated to XMLAPI2.

---

## Search

Search database items by name.

**Base URI:** `/xmlapi2/search?parameters`

| Parameter | Description |
|---|---|
| `query=SEARCH_QUERY` | Returns all matching item types. Spaces replaced by `+`. |
| `type=TYPE` | Restrict matches by type: `rpgitem`, `videogame`, `boardgame`, `boardgameaccessory`, `boardgameexpansion`, `boardgamedesigner`. Multiple types allowed (comma-delimited). |
| `exact=1` | Exact-match only. |

---

## XML Schema

See **Lesser Known Elements**.
