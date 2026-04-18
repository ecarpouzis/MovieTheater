# BoardGameGeek XMLAPI2 Reference

> **Version date:** 2025-07-02

## Table of Contents

- [Introduction](#introduction)
- [Registration Requirements](#registration-requirements)
- [Licenses](#licenses)
- [Using the XML API for a Business](#using-the-xml-api-for-a-business)
- [Using Other Parts of the API](#using-other-parts-of-the-api)
- [Usage Limits](#usage-limits)
- [Application Tokens](#application-tokens)
- [Changes to the XML API and its Policies](#changes-to-the-xml-api-and-its-policies)
- [Technical Support](#technical-support)
- [API Reference](#api-reference)

---

## Introduction

BoardGameGeek provides some of its data in XML form via an API, which users may use according to the [XML API terms of use](https://boardgamegeek.com/wiki/page/XML_API_Terms_of_Use). For details about what data is available via the XML API, see `BGG_XML_API` and `BGG_XML_API2`.

**Registration and authorization is required for use of the XML API.** To register your application, go to <https://boardgamegeek.com/applications>, and click the button to create an application. Please be patient regarding a response—it may be a week or more before BGG gets back to you.

The `XMLAPI2` is a newer version of the XML API and is currently in **BETA**. For the old API, see `BGG_XML_API`.

Each base URI is described and parameters are URL-encoded in the standard way.

For current info, discussions, and source material:

- BoardGameGeek XML API / guild thread: <https://boardgamegeek.com/guild/1229>
- Related wiki: **Data Mining** (based on previous XMLAPI)
- Current bugs/enhancement requests: **XML API Enhancements**

---

## Registration Requirements

You can register either a commercial or non-commercial application. Registration is required for nearly all use of the XML API.

**Exceptions:**
- If all you are doing with the XML API is downloading your own collection while logged in, you do not need to register.
- You can also download other users' collections without registering while logged in, but without a registered application, this will be heavily rate limited.
- If you routinely need to download multiple users' collections, you should register.
- You do not need to register to download the CSV dump of all games while logged in.

Licenses may not be approved in all cases; in particular, any application which, in BGG's judgment, competes with any part of BGG's business, or which harms them in any way, may be denied. In particular, any application which helps manage ticketing for conventions is likely to be declined. Approved applications which, in BGG's judgment, harm BGG may have their licenses withdrawn.

---

## Licenses

### New Commercial Licenses

If your organization is for-profit or if it is used to raise money in any way, or if your application will be showing advertising or offering users any benefit in exchange for payment, it is considered commercial. All commercial applications will require a commercial license.

Current policies for commercial licenses (subject to change at any time):

| Monetization Type | License Policy |
|---|---|
| User payments | Usually free until 100 paying users |
| Advertisements (no user payments) | Usually free until 1000 users |
| Sales (e.g., online game stores) | Usually requires paid commercial license immediately; local game stores without significant online business may qualify for free license |
| Voluntary donations only (no additional features for donors) | Requires commercial license, but usually free |
| Non-public facing commercial applications | Evaluated case-by-case |

To obtain a commercial license, register your application and choose "Commercial" in the appropriate option. Costs for commercial licenses, when applicable, are determined on a case-by-case basis.

### Existing Commercial Licenses

If you have an existing commercial license, you will still need to register and add the Authorization header. When you register, please include details about your license, including the cost (if any), whom you discussed this with, when, and whether it was via email, geekmail, or some other method.

### Non-Commercial Licenses

If your application is purely non-commercial, you may be eligible for a non-commercial license. A non-commercial license is generally provided at no cost, but may have different usage limits than a commercial license. Authorization requirements are the same for non-commercial as commercial applications.

---

## Using the XML API for a Business

The XML API and these policies are subject to change at any time. If you are building a business application that depends on this data, proceed at your own risk.

---

## Using Other Parts of the API

In addition to the public-facing XML API, BGG has several other private APIs used by their website. Unless otherwise noted or authorized, no license is granted for use of those endpoints.

This agreement (and Authorization requirements) also applies to downloading user collections in XML or CSV format, with the exception of downloading your own collection directly from the site while logged in.

An approved application is also required for the CSV download of all games. If you have an approved application, you can download that CSV directly from the page while logged in, or use the Application Token.

---

## Usage Limits

Exact usage limits are still being determined. General guidance:

- **Server-side requests preferred:** When possible, all requests should be made by your servers, with the results cached. Having requests come directly from clients (browser or app) may result in too much traffic, which could be grounds for having your license suspended.
- **Minimize requests:** Keep your number of requests to a minimum.
- **License-dependent limits:** Usage limits may be affected by your license type.
- **Monitor usage:** You can monitor your current usage at <https://boardgamegeek.com/applications> by clicking "Usage" under your application name.

---

## Application Tokens

Along with registration, BGG has introduced Authorization tokens to enforce registration.

Once you have an approved application, you can create Tokens by going to <https://boardgamegeek.com/applications> and clicking "Tokens" by your application.

### Using a Token

To use a token, send your HTTPS request to BGG with an `Authorization` header:

```
Authorization: Bearer e3f8c3ff-9926-4efc-863c-3b92acda4d32
```

(Replace with your actual token.)

For more details on Authorization headers, see: <https://developer.mozilla.org/en-US/docs/Web/HTTP/Authentication>

For now, Bearer tokens are used but not required to be refreshed; this may change.

**If you are unable to add the Authorization header to your requests, you will not be able to use the XML API.**

### Troubleshooting Tokens

If your token does not seem to be working:
- Ensure you are making requests to the correct domain (`boardgamegeek.com`, **WITHOUT** a leading `www`)
- Ensure the format for your authorization header is correct: `Bearer` followed by a space (no colon!) and the bearer token

### Third Party Tools for Using the API

If you are working on a 3rd party library to allow other applications to use the XML API, you should provide a configuration to allow users of your library to set their application tokens. Each application using your library should have its own token.

**Note:** This is for software libraries, in contrast to services. Third party services that allow other applications (not end users) to access BGG data are strictly prohibited.

If you have an application intended for end users (not programmers), you should obtain your own token, and the application should have access to it; you should not be asking non-programmer end-users to obtain their own token.

### Applications Which Make Client-Side Requests

If your application must make client-side XML API requests from a browser, and you do not want to move your requests server-side, the client must have access to the token. This could create a security issue—if your token is captured and used for unauthorized purposes, your access could be revoked, or you may at least have to generate a new token. **Make your API calls server-side where possible.**

### Public Facing Applications

As mentioned in the XML API terms of use, public facing apps must include the "Powered by BGG" logo, which should link back to BoardGameGeek. The logo should be sized so that the text remains easily legible. Logo files can be found on the BGG site.

---

## Changes to the XML API and its Policies

The XML API and its policies are subject to change. Planned changes will typically be announced in the [Geek Tools News forum](https://boardgamegeek.com/forum/46/boardgamegeek/geek-tools-news).

---

## Technical Support

No technical support is available for the XML API. If you have issues, search the forums and, if necessary, ask questions in the [Geek Tools Guild](https://boardgamegeek.com/guild/1229).

---

## API Reference

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
