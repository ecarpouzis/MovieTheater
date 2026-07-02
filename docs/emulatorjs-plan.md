# EmulatorJS Solo-Play Plan — Client-Side Retro, Zero Ziggy Cost

**Status:** Proposed (2026-07-02). Prerequisites: `docs/arcade-plan.md` Phases 1–2 (the
ArcadeGateway, `ArcadeCapabilityToken`, and the `ArcadeGame` catalog) — this plan is the
"Phase 6" that arcade-plan.md references. Facts below were extracted from EmulatorJS source at
tag **v4.2.3** (latest stable, 2025-07-05) and `main` (4.3.0-pre) on 2026-07-02; anything not
source-confirmed is flagged **[unverified]**.
**Scope:** A "Play solo" button on `/arcade` game pages that runs the game *in the player's own
browser* via EmulatorJS — no CloudRetro worker consumed, zero streaming latency, works on mobile
(free virtual gamepad). Multiplayer stays 100% CloudRetro; this plan never touches rooms.

> **How to read this doc:** §1–§10 are the plan; Appendices A–D are the reference an implementer
> must actually follow — EmulatorJS's persistence keying rules (the part that silently eats
> saves if you get the URL shape wrong), the SPA lifecycle contract (there is **no official
> destroy method**), the exact config globals, and self-hosting/pinning. The design decisions in
> §3–§4 exist *because* of those appendix facts; don't "simplify" a URL shape or skip
> `EJS_gameName` without re-reading Appendix B.

---

## 1. Goals and non-goals

1. **Protect the worker fleet.** One CloudRetro worker = one room; solo players idling in
   Pokémon must not occupy multiplayer capacity. Solo play costs Ziggy nothing but a one-time
   ROM download.
2. **Better solo feel.** Client-side emulation has zero streaming latency and no upload cost.
3. **Same product surface.** Same catalog, same cards, same auth (`StreamingUser`) and age gate;
   "Play solo" and "Create room" are two buttons on the same game.
4. **Bonus: mobile.** EmulatorJS auto-shows a touch virtual gamepad on mobile — solo play on
   phones comes essentially free (multiplayer stays desktop-first).

Non-goals (v1): solo N64 and PS1 via EmulatorJS (they route to a 1-player CloudRetro room
instead — §2); save-state sync to the server (designed-for via hooks, §8, not built); netplay of
any kind (explicitly disabled — Appendix A); threads/SharedArrayBuffer (§3.4); cheats UI
curation; save-file import UI.

## 2. Decision: EmulatorJS v4.2.3, self-hosted, 2D systems only — with per-system routing

**Pin v4.2.3** (latest stable). `main` (4.3.0-pre) rewrote the entire download/cache subsystem
(`data/src/cache.js`) with different keying and config semantics; this plan documents both where
they differ (Appendix B) so the eventual upgrade is a diff against known facts, but we build on
stable. Self-host everything (Appendix D); never load from `cdn.emulatorjs.org` at runtime.

**Engine routing** — a per-system default, overridable per game:

| System | Solo engine | Why |
|---|---|---|
| NES, SNES, Genesis, GB/GBC, GBA | **EmulatorJS** | WASM-flawless, tiny ROMs, full speed single-threaded (source: none of these cores are in `requiresThreads`/`requiresWebGL2`) |
| Arcade (FBNeo) | **EmulatorJS**, flagged | works, but romset zips are passed to the core unextracted and may need parent sets via `EJS_gameParentUrl` — enable per-title after testing, not wholesale |
| N64, PS1 | **1-player CloudRetro room** | browser N64 is demo-quality; PS1 needs BIOS shipped to the client + wasm32 memory limits crash big games (docs-confirmed). A solo room is the same §8 arcade-plan flow with seats unfilled — zero new code |

This lands in the catalog as `ArcadeGame.SoloEngine` (`'ejs' | 'room' | 'none'`), defaulted by
system at ingest, hand-editable.

## 3. Architecture

```
theater.carpouzis.com (unchanged pod + UI container)
 ├── /API/Arcade/Solo/{gameId}   ← mints ROM token, returns solo descriptor (§5)
 ├── /ejs/4.2.3/data/…           ← self-hosted EmulatorJS runtime + cores (UI container static)
 └── /arcade game page ── "Play solo" → same-origin <iframe srcdoc> running EmulatorJS
                                             │ fetch (CORS, no cookies)
                                             ▼
 arcade.carpouzis.com (Caddy → ArcadeGateway on Ziggy)
 └── /r/{token}/{stable-filename}   ← NEW route: token-confined ROM file serve (§6)
        reads D:\Arcade\roms (the same read-only tree the CloudRetro workers mount)
```

### 3.1 ROM data plane — one new gateway route

The ArcadeGateway (arcade-plan.md Appendix C) grows a second route: `GET|HEAD
/r/{token}/{filename}` — validate `ArcadeCapabilityToken`, serve exactly the file the token names
from the ROM root, `403` everything else. StreamGateway anatomy, static-file flavor. Full spec
§6. ROMs therefore still never touch the cluster, and the pod never needs a ROM mount.

### 3.2 The URL shape is load-bearing (do not "simplify" it)

EmulatorJS keys three kinds of browser persistence off the ROM URL/filename (full rules:
Appendix B). The token **must not be the URL basename**: SRAM saves and the ROM cache key off
`url.split("/").pop()` (4.2.3 strips `?`/`#`; **main does not strip the query string**). Hence:

- **Token as a path segment, stable filename last**: `/r/{token}/SuperMarioWorld.zip`. Safe in
  both 4.2.3 and 4.3-pre semantics. Never `/r/{token}` bare, and don't move the token to a query
  param (safe on 4.2.3, breaks on main).
- **Serve ROMs zipped with a stable inner filename** wherever practical: for zipped content,
  `this.fileName` (the SRAM key) comes from the filename *inside* the archive — immune to any
  URL games in every version. Most ROM libraries are already zips; `arcade-ingest` records the
  inner name.
- Belt-and-braces: the gateway sends `Content-Disposition: filename="<stable>"` (honored by
  main's pipeline for both filename and zip detection — main detects zips by *extension*, not
  magic bytes like 4.2.3, so an extensionless URL without this header stops extracting after an
  upgrade).

### 3.3 EmulatorJS assets — served from our origin, pinned

The runtime (`loader.js`, `emulator.min.js/.css`, `src/`, `localization/`, `compression/`
extractor workers) plus **only the core variants we use** are served from the UI container at a
**versioned absolute path** `/ejs/4.2.3/data/` with immutable cache headers. Absolute, because
`EJS_pathtodata` resolves relative paths against the document and our SPA has nested routes.
Pruned size is trivial: cores are ~1 MB each (fceumm 1.05, snes9x 1.09, gambatte 0.97, mgba
1.06, genesis_plus_gx 1.20; fbneo is the outlier at 8.3 MB) + ~2 MB runtime — versus ~290 MB for
the full 40-core release. Sourcing/pinning mechanics: Appendix D.

### 3.4 No threads, no COOP/COEP — a deliberate non-feature

None of our EJS-routed cores require threads or WebGL2 (source: `requiresThreads = ["ppsspp",
"dosbox_pure", "azahar"]`). Enabling `EJS_threads` requires `SharedArrayBuffer`, which requires
the **embedding page** to be cross-origin-isolated (`Cross-Origin-Opener-Policy: same-origin` +
`Cross-Origin-Embedder-Policy: require-corp` on theater.carpouzis.com responses) — which would
break every cross-origin subresource on the whole SPA that lacks CORP/CORS headers and any
OAuth-style popup. Do not set the headers; do not set `EJS_threads`. (4.2.3 without SAB just
warns and runs unthreaded; the `-thread-` core files don't even need to be shipped.)

## 4. The identity contract — what we always set

Every solo launch sets, non-negotiably (rationale in Appendix B):

- **`EJS_gameName` = the catalog Title** (sanitized) — keys save *states* (IndexedDB
  `EmulatorJS-states`, `"<name>.state"`), screenshots, and the settings/controls localStorage
  key. Without it, states key off the filename and settings embed the **full tokened URL** →
  every remap and save state silently orphaned each session.
- **`EJS_gameID` = ArcadeGame.Id** — disambiguates the settings key (source warns unset ids
  "may result in settings persisting across games").
- **Stable URL basename / zip inner filename** (§3.2) — keys SRAM (`/data/saves/<name>.srm` in
  emscripten IDBFS; `EJS_gameName`/`EJS_gameID` have **no effect** on SRAM — filename is the
  only lever) and the ROM cache.

With all three stable, the 4.2.3 ROM cache (IndexedDB `EmulatorJS-roms`) is *useful* — instant
subsequent boots — and bounded by distinct-games-played (it has **no eviction logic** in 4.2.3,
so unstable keys would grow it by one full ROM copy per session, forever). Keep it enabled; it
also means the gateway must answer **HEAD** (4.2.3 revalidates the cache by comparing
`content-length` via a HEAD request before every boot).

## 5. Data model, API, config

```sql
ALTER TABLE ArcadeGame ADD
    SoloEngine     NVARCHAR(8)  NOT NULL DEFAULT 'none',  -- 'ejs' | 'room' | 'none'
    EjsCore        NVARCHAR(40) NULL,   -- exact core, e.g. 'fceumm'; null = system default (§9 table)
    RomInnerName   NVARCHAR(200) NULL;  -- stable filename inside the zip (SRAM key); ingest fills it
```

`GET /API/Arcade/Solo/{gameId}` (StreamingUser + age gate, like everything else) → solo
descriptor:

```jsonc
{
  "romUrl": "https://arcade.carpouzis.com/r/<token>/Super%20Mario%20World.zip",
  "core": "snes9x",                    // resolved: EjsCore ?? system default
  "gameName": "Super Mario World",     // EJS_gameName
  "gameId": 412,                       // EJS_gameID
  "dataPath": "/ejs/4.2.3/data/",      // EJS_pathtodata (versioned)
  "biosUrl": null                      // reserved; EJS systems in v1 need none (GBA = mGBA HLE)
}
```

Config additions to `MovieTheaterConfiguration`: none beyond arcade-plan's (`ArcadeTokenSecret`,
`ArcadeGatewayBaseUrl` are reused). Token TTL: the ROM fetch happens once at boot; reuse
`ArcadeJoinTokenTtlSeconds` (5 min). A replay within TTL just re-downloads a file the user is
already entitled to.

Token payload — reuse `ArcadeCapabilityToken` with the room fields empty and the **ROM relative
path carried base64url-encoded** (paths contain spaces/parens; the payload is `'|'`-joined):
`userId|gameId|b64url(romPath)|"solo"|exp`. The gateway is stateless — the token itself names the
one file it may serve.

## 6. Gateway ROM route spec (delta to arcade-plan Appendix C)

- `app.Map("/r/{token}/{filename}", …)` for **GET and HEAD**. Validate token → resolve
  `ROM_ROOT + payload.RomPath` with a canonical-path check (`Path.GetFullPath` result must
  still start under ROM_ROOT — reject `..`) → require `{filename} ==
  Path.GetFileName(RomPath)` (URL-decode first) so links can't be dressed up as other titles.
- Response headers: `Content-Length` (required — EJS's progress bar and, critically, 4.2.3's
  HEAD-based cache revalidation compare it), `Content-Type: application/octet-stream`,
  `Content-Disposition: attachment; filename="<RomPath filename>"` (§3.2),
  `Access-Control-Allow-Origin: https://theater.carpouzis.com` + `Vary: Origin`, and expose
  `Content-Length` via `Access-Control-Expose-Headers`. EJS fetches without credentials
  (4.2.3 XHR, no `withCredentials`) — the single-origin ACAO with no allow-credentials is
  correct, same posture as StreamGateway.
- **No Range support needed** (EJS does whole-file GETs); no gzip (ROM formats are already
  compressed — and `Content-Encoding` would falsify Content-Length for the progress bar).
- Serve with `Results.File`/`PhysicalFile` streaming from the read-only ROM tree; this route
  must not be able to read outside it (that tree is the entire blast radius of a leaked token).

## 7. Frontend — the iframe embed (and why not a direct div)

EmulatorJS is a **classic script that reads `window.EJS_*` globals once and immediately
instantiates itself** into a selector, wiping the div's children. It has **no official
destroy/exit method** (source-grepped, both versions); its own cleanup runs off `beforeunload`,
which **never fires on SPA route changes**. Direct div-mounting in React 18 therefore leaks: the
emscripten main loop keeps running, the AudioContext keeps playing, and window/document
listeners (resize, fullscreenchange, gamepad poll) are never removed — and StrictMode's double
effect-mount instantiates it twice into the same selector.

**So: render it in a same-origin `<iframe srcdoc>`** (the pattern proven by the
`dimitrikarpov/react-emulatorjs` wrapper):

- Component `Pages/Arcade/SoloPlayer.js` builds an `srcdoc` document that sets the `EJS_*`
  globals from the solo descriptor and injects `<script src="/ejs/4.2.3/data/loader.js">`.
  `srcdoc` iframes are **same-origin**, so IndexedDB saves and localStorage settings persist
  with the site's storage.
- Teardown = best-effort `iframe.contentWindow.EJS_emulator?.callEvent("exit")` (flushes SRAM:
  the exit event runs `saveSaveFiles()` before aborting the wasm runtime) then unmount the
  iframe — the dying browsing context reclaims listeners, audio, wasm, and globals wholesale.
  StrictMode double-mount becomes harmless (two iframes, each self-contained, one discarded).
- Globals set: `EJS_player = "#game"`, `EJS_core`, `EJS_gameUrl`, `EJS_pathtodata`,
  `EJS_gameName`, `EJS_gameID`, `EJS_startOnLoaded = true`, `EJS_volume`, and **nothing
  netplay/ad/debug-related** (Appendix A lists the leave-unset traps: `EJS_DEBUG_XX` bypasses
  the ROM cache and phones home for update checks; netplay needs three flags and stays off).
- Page chrome stays ours (back button, game title, "switch to multiplayer" → create-room);
  `useWakeLock` on the page; antd style-import footgun applies to the page components as usual.
- Router v5 idioms; lazy-load the route so none of this ships to other pages (the EJS runtime
  itself is only ever loaded inside the iframe, so the SPA bundle doesn't grow at all).

Mobile: no extra work — EJS auto-detects touch and shows its virtual gamepad; `EJS_controlScheme`
can pin per-system layouts later if defaults disappoint.

## 8. Save interchange — designed-for, not built

Hooks for a future "saves follow the user" feature (all source-confirmed on `EJS_emulator`):
`gameManager.getSaveFile()` → raw `.srm` bytes; import = `FS.writeFile(getSaveFilePath(), bytes)`
+ `loadSaveFiles()`; `gameManager.getState()`/`loadState(bytes)` for states;
`EJS_onSaveState`/`EJS_onSaveSave` callbacks (registering one **suppresses the default download
UI** — signature `{screenshot, format, state|save}`). v4.3-pre adds a purpose-built
`EJS_onSaveUpdate` (hash-gated periodic save events) — the natural sync trigger when we upgrade;
on 4.2.3 a poll of `getSaveFile()` + hash would do. Parked entirely: v1 saves live in the
browser, per device, and the UI says so ("solo progress stays on this device").

## 9. System → core matrix (v1 solo)

`EJS_core` accepts a system alias or an exact core name; **first entry = the default the alias
resolves to** (from `consts.js`). ⚠ `"genesis"` is **not** an alias — use `segaMD` (or the
explicit core name, which we do via `EjsCore` to remove all ambiguity).

| System | `EJS_core` we set | Alt cores (user-switchable in EJS settings) | Notes |
|---|---|---|---|
| nes | `fceumm` | nestopia | |
| snes | `snes9x` | bsnes | |
| gb / gbc | `gambatte` | | |
| gba | `mgba` | | HLE BIOS built in — no `EJS_biosUrl` |
| genesis | `genesis_plus_gx` | picodrive | alias would be `segaMD`, not "genesis" |
| arcade | `fbneo` | fbalpha2012_* | zip passed unextracted; parent romsets via `EJS_gameParentUrl`; per-title enablement (§2) |
| n64 / ps1 | — | — | `SoloEngine='room'` (§2); revisit after real demand |

## 10. Phases

**Phase A — Assets + gateway route.**
Pin `@emulatorjs/emulatorjs@4.2.3` + per-core packages, build-time copy to `/ejs/4.2.3/data/`
(Appendix D); implement the `/r/{token}/{filename}` route (§6) with GET+HEAD; extend
`arcade-ingest` to fill `RomInnerName`/`SoloEngine`/`EjsCore` defaults.
*Acceptance:* `curl -I` shows Content-Length/ACAO/Content-Disposition; HEAD and GET agree;
path-traversal and cross-title filename attempts 403; a hand-written HTML page against the
tokened URL boots Super Mario World.

**Phase B — Solo descriptor + player page.**
`GET /API/Arcade/Solo/{gameId}`; `SoloPlayer.js` iframe component with teardown; "Play solo"
button on game pages gated by `SoloEngine`.
*Acceptance:* boot → play → save state → leave the page → **audio stops** (the teardown test)
→ return → load state restores; remapped controls survive the round-trip; a *second* session
(fresh token) still sees the same saves/settings — **the keying contract test, run it
explicitly on both a zipped and an unzipped ROM**.

**Phase C — Catalog rollout + mobile pass.**
Enable `SoloEngine='ejs'` across the 2D catalog; per-title arcade (fbneo) verification; touch
layout sanity check on a phone; Firefox pass (WASM-JIT "enhanced security" modes are the known
slowdown class).
*Acceptance:* chunked, resumable smoke-run over `SoloEngine='ejs'` titles boots each to
gameplay; at least one title verified on a phone; IndexedDB growth after replaying the same
title N times is one cached copy, not N.

## 11. Risks

- **R1 — Save-identity regressions on upgrade.** 4.3 changes filename derivation (query no
  longer stripped; Content-Disposition honored; cache keys become content hashes). Our URL
  shape + zips + Content-Disposition were chosen to be version-proof, but the upgrade still
  reruns Phase B's keying-contract test before shipping. Pin hard until then.
- **R2 — Browser storage is the save store.** Clearing site data deletes progress; nothing
  server-side in v1. Mitigation: the §8 hooks exist; UI labels the situation honestly.
- **R3 — fbneo romset compatibility** (parents, versions) is a per-title grind — hence flagged
  per-title enablement, not a bulk switch.
- **R4 — CDN fallback**: if a core file 404s locally, 4.2.3 silently fetches it from
  `cdn.emulatorjs.org`. Ship complete pruned data (Phase A acceptance) and optionally CSP-block
  the domain; never rely on the fallback (it defeats pinning).
- **R5 — GPLv3 hygiene**: keep the per-core `license.txt` files and the in-app license display
  intact; the "source offer" is satisfied by the unmodified upstream (link the repo/release in
  the page footer or About). Don't strip or minify-rebuild the runtime.

## 12. Open questions

1. Zip-normalize the whole solo library at ingest (uniform inner-name control) vs. serve files
   as they sit on disk (zips only where they already exist)? Plan assumes serve-as-is with
   `RomInnerName` recorded when zipped; normalization is a nice-to-have ingest flag.
2. Expose EJS's save-state slots UI as-is, or hide its toolbar (`EJS_Buttons`) down to a
   minimal set for the friends audience? (Plan assumes as-is minus cheats/netplay-adjacent
   buttons.)
3. Do we want screenshots (EJS can produce them per save) surfaced anywhere on the site later?

## 13. Alternatives considered

- **Track `main`/4.3-pre for the better cache + `EJS_onSaveUpdate`** — rejected for v1:
  prerelease, and the cache rewrite is exactly the subsystem our correctness depends on.
  Documented (Appendix B) so the upgrade is mechanical.
- **Direct div mount with manual cleanup** — rejected: no official destroy; hand-unpicking
  window listeners + AudioContext + emscripten loop is fighting the framework; the iframe costs
  nothing and StrictMode-proofs the mount (§7).
- **Serving ROMs from the pod/cluster** — rejected: pod has no ROM storage and doesn't want
  it; the gateway route keeps the single-data-plane-on-Ziggy symmetry.
- **Their CDN with version path** — rejected: availability/privacy coupling and R4; self-host
  is ~15 MB.
- **COOP/COEP + threads for headroom** — rejected §3.4; nothing we run needs it and it taxes
  the entire SPA.

---

# Appendix A — `EJS_*` globals we set / leave unset (v4.2.3 semantics)

**Set:** `EJS_player` (`"#game"` — the div inside the iframe; EJS wipes and owns its children),
`EJS_core` (exact core name, §9), `EJS_gameUrl` (tokened URL, §3.2 shape), `EJS_pathtodata`
(**absolute** `/ejs/4.2.3/data/`), `EJS_gameName` (**mandatory** — §4), `EJS_gameID`
(**mandatory** — §4), `EJS_startOnLoaded = true`, `EJS_volume` (default 0.5 is fine),
optionally `EJS_defaultControls` / `EJS_controlScheme` later.

**Leave unset — each is a trap, not an omission:**
- `EJS_DEBUG_XX` — loads unminified sources, **bypasses ROM/core caches**, and enables an
  update-check fetch to cdn.emulatorjs.org. Never in prod.
- `EJS_threads` — §3.4. (4.2.3 with it set but no SharedArrayBuffer logs a warning and runs
  unthreaded; don't invite the confusion.)
- `EJS_netplayServer` / `EJS_EXPERIMENTAL_NETPLAY` — netplay is dead code unless *both*
  experimental flags and debug are on; leave all three absent.
- `EJS_AdUrl` (+ AdMode/AdTimer/AdSize) — no ad iframe is created unless set.
- `EJS_disableDatabases` / `EJS_CacheLimit` — with our stable keys the ROM cache is an asset;
  leave defaults (cache on, 1 GiB per-ROM gate). Revisit only if keying is ever unstable.
- `EJS_biosUrl` — no v1 system needs it (GBA = mGBA HLE). If PS1-via-EJS ever happens, serve
  BIOS from **our app origin as a stable static asset**, not the tokened gateway — 4.2.3 caches
  BIOS in the same URL-basename-keyed store (same rotating-token trap).
- `EJS_softLoad` — it's a **number of seconds** until an auto-restart (a workaround for cores
  needing a reset-after-boot), not a boolean "soft load" toggle. Only per-title if a core
  demands it.

**Callbacks available** (register via globals before loader injection): `EJS_ready`,
`EJS_onGameStart`, `EJS_onSaveState`, `EJS_onLoadState`, `EJS_onSaveSave`, `EJS_onLoadSave`.
Registering a save/load callback **suppresses the built-in file-download/store behavior** — only
wire them when §8 gets built. (`EJS_onExit`, `EJS_onSaveUpdate`, `EJS_fixedSaveInterval` are
4.3-pre only.)

# Appendix B — Persistence keying (the load-bearing facts)

Three stores, three different rules — verified in `emulator.js`/`GameManager.js` at v4.2.3, with
4.3-pre deltas noted:

1. **Save states** → IndexedDB `EmulatorJS-states`, key `getBaseFileName() + ".state"`.
   Precedence: **`EJS_gameName`** (invalid chars `[#<$+%>!\`&*'|{}/\\?"=@:^\r\n]` stripped) →
   loaded filename. Stable iff `EJS_gameName` set. Screenshots use the same name.
2. **SRAM (battery saves)** → emscripten **IDBFS** auto-persist mount at `/data/saves`;
   RetroArch writes `<content-basename>.srm`. The key is therefore **the ROM's filename**:
   inner-archive filename for zips (stable, best); else URL basename — 4.2.3 strips `?`/`#`
   from the last path segment, **4.3-pre does not strip the query** but honors
   `Content-Disposition: filename=`. `EJS_gameName`/`EJS_gameID` have **zero effect** here —
   the docs' claim that `EJS_gameID` separates saves is wrong per source; filename is the only
   lever. This single fact dictates the §3.2 URL shape.
3. **ROM cache** → 4.2.3: IndexedDB `EmulatorJS-roms`, key = `gameUrl.split("/").pop()`
   (query **included**), **no eviction logic**, per-ROM gate `EJS_CacheLimit` (default 1 GiB),
   and a **HEAD request** comparing `content-length` revalidates before use (gateway must
   answer HEAD). 4.3-pre: rewritten `EmulatorJS-Cache` DB — looked up by URL index but stored
   under a **content hash** with real eviction (4 GB / 5-day defaults, honors Cache-Control).
   BIOS shares the store (keyed by its own URL basename).
4. **Settings/controls/cheats** → localStorage key
   `"ejs-" + (EJS_gameID || 1) + "-" + core + "-" + (EJS_gameName || gameUrl || filename) + "-settings"`
   — unset `EJS_gameName` embeds the **full tokened URL** in the key; unset `EJS_gameID` makes
   distinct games collide. Both mandatory (§4). Global volume/mute live under `"ejs-settings"`.

**The contract in one line:** stable `EJS_gameName` + `EJS_gameID` + stable filename (zip inner
name or URL basename with the token elsewhere in the path) ⇒ every store keys stably in both
4.2.3 and 4.3-pre.

# Appendix C — Lifecycle contract (SPA embed)

- loader.js is a classic script: safe to re-inject per mount; it reads globals once, then
  `window.EJS_emulator = new EmulatorJS(EJS_player, config)` immediately — **globals before
  script, target div must already exist**, and the emulator owns the div's children.
- ROM fetch (4.2.3) is XHR with progress events — percentage requires `Content-Length` (else it
  degrades to "X.XX MB" text); fetch sends **no cookies** (token-in-URL is the auth, by design).
- **No official destroy.** The internal exit event (`callEvent("exit")`) runs: flush SRAM
  (`saveSaveFiles()`), stop the main loop, unmount IDBFS, then `Module.abort()` after 1 s. It's
  wired to `beforeunload` — which SPA navigation never fires. Even after exit, window/document
  listeners (resize, fullscreenchange, gamepad poll) remain registered.
- Hence the same-origin `<iframe srcdoc>` embed (§7): set globals on `iframe.contentWindow`,
  inject the loader inside; teardown = best-effort `contentWindow.EJS_emulator?.callEvent("exit")`
  (the SRAM flush) + remove the iframe — context death reclaims everything else. Same-origin
  `srcdoc` shares IndexedDB/localStorage with the site, so persistence works normally. React 18
  StrictMode double-mounts become two independent, disposable contexts.
- Do not suppress the exit-flush path and rely on IDBFS `autoPersist` alone for SRAM
  **[unverified timing]** — autoPersist syncs on an interval-ish basis via emscripten; the
  explicit flush before unmount is the guarantee.

# Appendix D — Self-hosting & pinning

- **Source of truth:** official npm — `@emulatorjs/emulatorjs@4.2.3` (runtime, ~2 MB, no cores)
  + `@emulatorjs/core-fceumm@4.2.3`, `-snes9x`, `-gambatte`, `-mgba`, `-genesis_plus_gx`,
  `-fbneo` (add `-nestopia`/`-bsnes`/`-picodrive` if the settings-menu alt-core switch should
  work offline). Alternative: the GitHub release archive (~290 MB, all cores) and prune.
- **Vite integration:** npm devDependencies in `src/ui` + a build-time copy (script or
  `vite-plugin-static-copy`) into `public/ejs/4.2.3/data/` — copied, versioned-by-path, not
  imported (EJS is not a module; it must be served as plain static files). Long-lived immutable
  cache headers on `/ejs/*` (the path carries the version).
- **Prune rule:** each core ships 4 variants (`-wasm`, `-thread-wasm`, `-legacy-wasm`,
  `-thread-legacy-wasm` as 7z-packed `.data` files). We need `-wasm` (WebGL2/no-threads) and,
  only if WebGL1 fallback matters, `-legacy-wasm`. **Keep `cores/reports/<core>.json`** — without
  the report file EJS disables core caching entirely and re-downloads the core each boot.
- **Runtime network truth (self-hosted):** exactly two possible external fetches — the
  update-check (only under `EJS_DEBUG_XX` or localhost) and the **CDN core-404 fallback** (R4).
  Ship complete files; optionally add `cdn.emulatorjs.org` to a CSP blocklist for a hard
  guarantee. Nothing else: no fonts, no telemetry, no ads (unset `EJS_AdUrl`).
- **License:** GPLv3. Keep per-core `license.txt` (they're inside the core packages and shown
  in-app) and the runtime's license header; link the upstream repo/release from the site's
  About/footer as the source offer. We ship it unmodified — do not fork-and-minify.
