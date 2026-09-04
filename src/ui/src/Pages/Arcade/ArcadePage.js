import { useEffect, useMemo, useRef, useState, useCallback } from "react";
import { useHistory, useLocation } from "react-router-dom";
import { Button, Empty, Modal, Select, Typography, message } from "antd";
import { MovieAPI } from "../../MovieAPI";
import ArcadeHostBanner from "./ArcadeHostBanner";
import GameCard from "./GameCard";
import GameModal from "./GameModal";
import HeavyGameModal from "./HeavyGameModal";
import LiveRooms from "./LiveRooms";
import SavesManager from "./SavesManager";
import ConsoleCarousel from "./ConsoleCarousel";
import { rememberLobbySearch } from "./arcadeLobbyState";
import { createRoomAndGo, loadQuality, saveQuality } from "./arcadeRoomCreate";
import { hasSaveStates, QUICK_SLOT } from "./arcadeSystems";
import { parseSystems, toggleSystem } from "./arcadeSystemFilter";
import { ARCADE_ENTITY_PARAMS, arcadeNarrows, legacyToArcadeSearch } from "./arcadeFacetSpec";
import useArcadeBrowse from "./useArcadeBrowse";
import CatalogHost from "../../catalog/CatalogHost";
import { hasFacetValue } from "../../catalog/rail/facetSpec";
import useSectionRail from "../../catalog/rail/useSectionRail";
import sectionRailSurfaces from "../../catalog/rail/sectionRailSurfaces";
import { ARCADE_GRID_CELL, createArcadeSource } from "../../catalog/sources/arcadeSource";
import "./ArcadePage.css";
import useIsMobile from "../../hooks/useIsMobile";
import usePolling from "../../hooks/usePolling";

const { Text } = Typography;

/** A group header's lobby param → the rail facet it writes (`arcadeSource.GROUP_FILTER_PARAM` names the params). */
const GROUP_FACET_KEY = { system: "system", genre: "genre", maxPlayers: "players", variant: "variant", ra: "ra" };


// The three quality dropdowns the creator picks from. What they WRITE, and how a room is started
// from it, is `arcadeRoomCreate.js` (loadQuality / saveQuality / createRoomAndGo) — the saves page
// starts rooms too. Applied to every room YOU start (one encoder per room = the creator's choice);
// lower bitrate = smaller video bursts = smoother audio + less upstream for remote players.
const BITRATE_PRESETS = [
  // 0 = Auto: the WORKER now DERIVES the ceiling from the frame it actually encodes — encoded pixels ×
  // fps × a bits-per-pixel target (abr.go autoCeilingKbps), clamped to 5–40 Mbps. It is no longer a
  // per-system constant: that table could not see the core's real viewport, its scale, a core changing
  // resolution mid-game, or a render profile moving the frame. Measured live 2026-07-30: genesis 19328,
  // snes 15508, n64 13271, gc 14583, psp 12584. Auto is usually the RIGHT answer now — the manual
  // presets below cap the stream for the WEAKEST VIEWER's downlink (a tablet on Wi-Fi, a phone on
  // 5G), not for beating Auto. Since the 2026-09-01 fiber cutover (~200 Mbps single-stream / ~630 Mbps
  // aggregate up, measured from Ziggy) our own upstream is no longer what a preset protects: four
  // remote friends at 25 Mbps each is ~100 Mbps up, which the old ~35 Mbps line could never serve and
  // the fiber line does not notice.
  { label: "Auto · match the frame", value: 0 },
  // 40 Mbps matches abrAutoMaxKbps (raised from 25 on 2026-09-02, the fiber re-baseline). The
  // headroom is for the fattest frames we encode — capture-lane 1080p60 (Auto derives ~22.4) and
  // hi-res 3D — on a viewer with a wired or strong Wi-Fi downlink. ⚠ The ceiling is a PERMISSION, not
  // a target: the room still opens at 6 Mbps and climbs +15%/tick (~20 s to 40 Mbps from cold), and a
  // weak peer is walked down from it within a second. ⚠ ArcadeController clamps this server-side and
  // the worker's abrAutoMaxKbps must agree; all three moved together.
  { label: "Fiber · 40 Mbps", value: 40000 },
  // 25 was the top preset 2026-07-30 → 2026-09-02, sized to the old uplink; kept for anyone who chose it.
  { label: "LAN · 25 Mbps", value: 25000 },
  // Kept as an older top preset for anyone who had chosen it deliberately.
  { label: "Very high · 16 Mbps", value: 16000 },
  // 10 Mbps, best for hi-res 3D cores (GameCube 1280×1056, PS2 upscaled) when a viewer's downlink is
  // the limit. Overkill (but harmless) for retro/2D. Server clamps 500–40000.
  { label: "High · 10 Mbps", value: 10000 },
  { label: "Sharp · 8 Mbps", value: 8000 },
  { label: "Balanced · 5 Mbps", value: 5000 },
  { label: "Smooth · 3 Mbps", value: 3000 },
  { label: "Data saver · 1.5 Mbps", value: 1500 },
];
// Network profile (replaces the old Error-correction dropdown): one choice bundling audio FEC and
// in-frame packet pacing. LAN is byte-identical to the old default (FEC on, no pacing — packets
// leave at wire speed for minimum latency). Remote/5G add the patch-0028 smoother: each encoded
// frame's burst is spread over a few ms, which is invisible on a good line but stops the bursts
// from slamming cellular/shallow-buffer queues and panicking the bandwidth estimator (measured on
// real 5G 2026-07-09: estimate collapse to 525 kbps, session pinned at 1500-2500 of a 5000 ceiling).
// `short` is what the collapsed pill shows; the full label, with guidance, stays in the dropdown.
const NETWORK_OPTIONS = [
  { short: "Network: LAN", label: "Network: LAN · lowest latency, home wifi/wired", value: "lan" },
  { short: "Network: Remote", label: "Network: Remote · smoother for friends joining over the internet", value: "remote" },
  { short: "Network: 5G / Cellular", label: "Network: 5G / Cellular · steadiest picture on mobile data", value: "5g" },
];
// Per-room video codec (worker patch 0036). AV1 is the better codec per bit, but a device without
// HARDWARE AV1 decode — most tablets — still negotiates it (Chrome offers software dav1d) and then
// can't decode 60fps in real time; the keyframeless AV1 stream gives it nothing to resync to, so
// video falls minutes behind the (separately-decoded) audio. H.264 hardware-decodes everywhere.
// Room-wide: one encoder per room, so pick H.264 when ANY player will be on a tablet.
//
// "Auto" (ABR plan Phase 4) probes THIS device's hardware AV1 decode via MediaCapabilities at room
// create and picks av1/h264 accordingly — the manual-dropdown-habit fix for rooms landing on h264
// (or worse, tablet-AV1) for no reason. It reads the CREATOR's device only: a creator who knows a
// tablet will JOIN should still pick H.264 explicitly, which is why stored explicit picks are never
// migrated to Auto (same non-migration rule as the bitrate presets below).
const CODEC_OPTIONS = [
  { short: "Codec: Auto", label: "Codec: Auto · best this device can decode", value: "auto" },
  { short: "Codec: AV1", label: "Codec: AV1 · best picture (PCs & recent devices)", value: "av1" },
  { short: "Codec: H.264", label: "Codec: H.264 · smoothest on tablets & older devices", value: "h264" },
];

/**
 * The /arcade lobby (docs/arcade-plan.md §7, redesigned per design_handoff_arcade_browse/README.md).
 *
 * Over ~13k cards this is SERVER-SIDE filtered + paged: the filters live in the URL as the rail's
 * `q/f/x` (R9 S2c — `arcadeFacetSpec.ts` maps them onto `/API/Arcade/Games`' own params; the sider's
 * ArcadeSiderRail (the phone drawer's rail too), the bar's SmartSearch and the console carousel all write the
 * same URL), and this page fetches the matching page and appends more on demand. A "Live rooms"
 * strip shows what friends are playing right now.
 */
export default function ArcadePage({ userData }) {
  const history = useHistory();
  const location = useLocation();
  const isMobile = useIsMobile();

  // ── The catalog as SPARSE BANDS (R9 S3: the package's InfiniteBands, shared with every section) ─
  // The lobby used to hold a dense array of the games it had fetched, anchored at an absolute
  // `startIndex`. That is what made a letter jump one-directional: the array BEGAN at the letter, so
  // there was nothing above it and scrolling up revealed nothing (reported 2026-08-13). Adding pages
  // to the front would have meant prepending to that array, which is the classic teleport — the
  // viewport sits still only if you can compensate for content appearing above it, and the
  // compensation is guesswork until the new rows have been measured.
  //
  // The Long Box does not have that problem because it never prepends. Its whole result set is
  // modelled as fixed slots from the first render — "The whole result set is modelled as
  // `totalBands` fixed slots of `perBand` units … Band data is fetched on demand and cached for
  // instant re-mount (recycling drops DOM, never data)." — so the list is `total` long immediately,
  // the scrollbar is honest immediately, and a slot the user scrolls into is fetched whether it is
  // above them or below them. Upward and downward are the SAME operation, and no array is ever
  // prepended to, so there is nothing to compensate for.
  //
  // The lobby proved that model here first (as a page map + useGridWindow); R9 S3 retired that copy
  // for the package's own `catalog/engine/InfiniteBands`, which every section's Grid now rides.
  // Anything not yet fetched renders as a skeleton tile of card size. There is no `startIndex`.
  const [rooms, setRooms] = useState([]);
  const [renderers, setRenderers] = useState({}); // system → [{id,label,isDefault}] for the launch menu
  const [modalVersionId, setModalVersionId] = useState(null); // pre-selected version (a recent tile's save)
  const [optionsOpen, setOptionsOpen] = useState(false); // mobile: reveal the room-quality pills (desktop always shows them)
  const [creating, setCreating] = useState(0);
  const [manageSaves, setManageSaves] = useState(null); // { gameId, title } for the My Saves modal
  // The open game modal lives in the URL (?game=<versionId> — the browse ?title= pattern): a card
  // click pushes it (so Back closes the full-page modal), ✕ replaces it away, and a shared lobby
  // link opens its game on load. modalCard caches the card OBJECT for that id: a click seeds it
  // synchronously; a cold-load deep link fetches it by id. The card's lane picks the modal
  // (standard vs the heavy Play-via-Moonlight one).
  const [modalCard, setModalCard] = useState(null); // { key: versionId, game }
  const modalCardRef = useRef(null);
  modalCardRef.current = modalCard;
  const [quality, setQuality] = useState(loadQuality); // creator's per-room stream quality (persisted)
  // 501 from /API/Arcade/Games = this server has no arcade configured (a dedicated empty state,
  // not a load error). The source reports the status; the page decides what it means.
  const [unconfigured, setUnconfigured] = useState(false);

  // Read by the identity-stable fetchPage below, so the pump never re-subscribes.
  const filtersRef = useRef(null);

  const setQ = (patch) => setQuality((prev) => { const next = { ...prev, ...patch }; saveQuality(next); return next; });

  // ── The facet rail's state (R9 S2c): the URL is the filter; the sider rail reads the same URL. ──
  // `filters` is that state in the API's vocabulary (system csv, hideRegions, maxPlayers, …) — the
  // pump, the letters strip, the facets request and the catalog source all read it.
  const browse = useArcadeBrowse();
  const { filters, filterKey, facets, spec } = browse;
  filtersRef.current = filters;
  const rail = useSectionRail("arcade", spec, { entityParams: ARCADE_ENTITY_PARAMS });
  const facetState = rail.state;
  const facetActions = rail.actions;

  // A pre-S2c lobby link (?system=&hideRegions=&players=&variant=&genre=&ra= — the old rail's Selects,
  // old bookmarks, a room's exit button from before the deploy) is rewritten ONCE into the facet form
  // it means; the page re-renders on the new URL.
  useEffect(() => {
    const legacy = legacyToArcadeSearch(location.search);
    if (legacy != null) history.replace({ pathname: location.pathname, search: legacy, state: location.state });
  }, [history, location.pathname, location.search, location.state]);

  // ── The catalog (R9 S3: ONE engine under every view) ──────────────────────────────────────────
  // The lobby grid IS the package's GridView now, laying THIS page's GameCard into the shared bands;
  // Wall / List / Extended / Shelves / Newspaper / Directory read the same source. The modal opener
  // is defined further down — the source reaches it through a ref.
  const openGameRef = useRef(null);
  // GameCard is a MODULE-LEVEL component (the BandSlot memo law); this renderer's identity never
  // changes, because everything a card varies on rides in the card item and the tweak values.
  const renderCard = useCallback((item, view) => (
    <GameCard
      game={item.raw}
      cellH={view.cellH}
      metadata={view.metadata}
      hoverClass={view.hoverClass}
      eager={view.eager}
      onOpen={(card) => openGameRef.current?.(card)}
    />
  ), []);
  const catalogSource = useMemo(
    () => createArcadeSource({
      filters,
      filterKey,
      renderCard,
      // Picks the empty line: "No games here yet." when nothing narrows, "…match those filters." when
      // something does — the two are different reports and the second one used to be told either way.
      narrowed: arcadeNarrows(filters),
      // 501 = no arcade on this server. The source reports the status; the page renders the note.
      onStatus: (status) => { if (status === 501) setUnconfigured(true); },
      onOpen: (card) => openGameRef.current?.(card),
      // A group header scopes in place: the facet include, one push, the modal closed. System is the
      // one repeatable facet (the carousel adds consoles); Genre / Players / Variant / RA are
      // single-valued on the API side, so their header REPLACES rather than appends.
      // Region, Developer and Publisher headers reach here at all — the source drops them, because
      // the rail has no include-side state for any of the three (`arcadeSource.GROUP_FILTER_PARAM`).
      onFilter: (param, value) => {
        const key = GROUP_FACET_KEY[param];
        if (!key) return;
        const v = key === "system" ? String(value).toLowerCase() : String(value);
        facetActionsRef.current?.apply((d) => {
          if (key === "system") { if (!hasFacetValue(d.include.system, v)) d.include.system = [...(d.include.system ?? []), v]; }
          else d.include[key] = [v];
        });
      },
    }),
    // The facet actions are read through a ref so the source's identity stays keyed on the filters.
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [filters, filterKey, renderCard]
  );
  const facetActionsRef = useRef(null);
  facetActionsRef.current = facetActions;

  // ?game=<versionId> — the open modal. Anything that doesn't parse is no game at all.
  const openGameId = (() => {
    const raw = new URLSearchParams(location.search).get("game");
    if (!raw || !/^[0-9]+$/.test(raw)) return null;
    const n = Number(raw);
    return Number.isSafeInteger(n) && n > 0 ? n : null;
  })();

  const pushGameParam = (versionId) => {
    const params = new URLSearchParams(location.search);
    params.set("game", String(versionId));
    history.push({ pathname: location.pathname, search: `?${params.toString()}` });
  };
  // ✕ replaces the param away so closing doesn't grow the history (Back would reopen it).
  const closeGame = useCallback(() => {
    setModalCard(null);
    const loc = history.location;
    const params = new URLSearchParams(loc.search);
    if (!params.has("game")) return;
    params.delete("game");
    const search = params.toString();
    history.replace({ pathname: loc.pathname, search: search ? `?${search}` : "" });
    // history is a stable reference in react-router v5.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  // Cold-load (or back/forward) with ?game= but no cached card: fetch the one card by version id.
  // A click never takes this path — openGame seeds the cache before pushing the param.
  useEffect(() => {
    if (!openGameId) {
      // key === null is the defensive versionless-card open (no URL to drive it) — leave it alone.
      if (modalCardRef.current && modalCardRef.current.key != null) setModalCard(null);
      return;
    }
    if (modalCardRef.current?.key === openGameId) return;
    let dead = false;
    MovieAPI.getArcadeGames({ id: openGameId })
      .then((r) => (r.ok ? r.json() : null))
      .then((data) => {
        if (dead) return;
        const g = data?.games?.[0];
        if (g) setModalCard({ key: openGameId, game: g });
        else closeGame(); // a stale/foreign link: drop the param rather than a broken modal
      })
      .catch(() => {});
    return () => { dead = true; };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [openGameId, closeGame]);

  // Stash the filtered lobby URL so the room's exit buttons can come back to it (arcadeLobbyState).
  // Minus ?game=: exiting a room shouldn't land back under the modal of the game just played.
  useEffect(() => {
    const params = new URLSearchParams(location.search);
    params.delete("game");
    const rest = params.toString();
    rememberLobbySearch(rest ? `?${rest}` : "");
  }, [location.search]);

  // The console carousel IS the System facet (Eric, canvas 2026-08-27): it writes the same `f=system:`
  // the rail's chips remove and the bar's SmartSearch adds — the URL is the one source of truth, so no
  // two surfaces can disagree about what's picked. Facets come from the shared hook, so the rail and
  // the shelf make ONE request between them rather than one each.
  const selectedSystems = parseSystems(location.search);

  const setSystems = useCallback((next) => {
    facetActions.apply((d) => {
      if (next.length) d.include.system = [...next];
      else delete d.include.system;
    });
  }, [facetActions]);

  const onToggleSystem = useCallback(
    (system) => setSystems(toggleSystem(parseSystems(location.search), system)),
    [setSystems, location.search],
  );

  // Live-rooms strip, polled every 12 s (visibility-aware: a backgrounded lobby stops asking).
  usePolling(
    () => MovieAPI.getArcadeRooms().then((r) => (r.ok ? r.json() : [])).then(setRooms).catch(() => {}),
    12000
  );

  // Render-profile map (system → core-and-renderer options for the launch menu). Static data; fetch once.
  useEffect(() => {
    let alive = true;
    MovieAPI.getArcadeRenderers().then((m) => { if (alive) setRenderers(m || {}); });
    return () => { alive = false; };
  }, []);

  // `cheats` are the ids the creator ticked on the card (arcade cheats feature). `controllerScheme`
  // is the GameCube-vs-Wiimote+Nunchuk picker (only shown for the two GC-native BrawlEx mods). Both
  // ride every path out of this modal — Continue, New game, and a snapshot resume all launch the
  // same room.
  function createRoom(versionId, title, cheats = [], hwContext = "", controllerScheme = "", renderProfile = "", competitive = false, system = "") {
    if (creating || !versionId) return;
    setCreating(versionId);
    // A competitive room never resumes a save-state (that's the whole point), so skip the Continue/New-game
    // prompt entirely and boot straight in. Cheats are dropped server-side too.
    if (competitive) {
      doCreateRoom(versionId, { competitive: true, hwContext, renderProfile, controllerScheme });
      return;
    }
    // Systems with no emulator save-state (psp noSaveStates, scummvm, heavy/capture lanes) have nothing to
    // continue FROM: their progress is the memory card, which every boot seeds regardless. Offering
    // Continue/Quickload there would be dead UI, so they always boot clean.
    if (!hasSaveStates(system)) {
      doCreateRoom(versionId, { newGame: true, cheats, hwContext, renderProfile, controllerScheme });
      return;
    }
    // Durable saves (arcade-saves-plan): offer the three ways to start. The split is RUN LEGITIMACY,
    // not convenience — see docs/arcade-clean-start-plan.md. Restoring a save-STATE (Continue, a
    // quickload, or a named snapshot) is save-scumming by RA's own rule and taints the run from frame
    // 0; a clean boot does not. Your battery / memory card is NOT a save-state and is kept either way,
    // so "Clean Start" on a game with in-game saves still lets you load from the game's own menu.
    MovieAPI.listArcadeSaves(versionId)
      .then((saves) => {
        const rows = Array.isArray(saves) ? saves : [];
        if (rows.length === 0) return doCreateRoom(versionId, { newGame: true, cheats, hwContext, renderProfile, controllerScheme });
        const states = rows.filter((s) => s.kind === "state");
        const autoSave = states.find((s) => s.slotId === 0) || null;
        const quick = states.find((s) => s.slotId === QUICK_SLOT);
        const snaps = states.filter((s) => s.slotId >= 1 && s.slotId !== QUICK_SLOT)
          .sort((a, b) => a.slotId - b.slotId);
        // Nothing restorable → no choice to present.
        if (!autoSave && !quick && snaps.length === 0) {
          return doCreateRoom(versionId, { newGame: true, cheats, hwContext, renderProfile, controllerScheme });
        }
        const start = (opts) => doCreateRoom(versionId, { ...opts, cheats, hwContext, renderProfile, controllerScheme });
        const when = (s) => (s?.updatedUtc ? new Date(s.updatedUtc).toLocaleString() : "");
        const pick = (opts) => { modal.destroy(); start(opts); };
        // A couple of snapshots read fine as rows; a pile becomes a searchable dropdown so the
        // right one is findable without scanning a long list.
        const useSnapPicker = snaps.length > 3;
        const modal = Modal.confirm({
          title: "How do you want to start?",
          icon: null,
          width: 520,
          onCancel: () => setCreating(0),
          // Continue Auto-Save is what most launches want, so it takes the primary (rightmost)
          // spot; with no auto-save yet, Clean Start is primary instead.
          footer: (
            <div style={{ display: "flex", gap: 8, justifyContent: "flex-end", flexWrap: "wrap", marginTop: 12 }}>
              <Button onClick={() => { modal.destroy(); setCreating(0); }}>Cancel</Button>
              <Button type={autoSave ? "default" : "primary"} onClick={() => pick({ newGame: true })}>🏁 Clean Start</Button>
              {autoSave && (
                <Button type="primary" onClick={() => pick({})}>▶ Continue Auto-Save</Button>
              )}
            </div>
          ),
          content: (
            <div className="arcade-start-choice">
              <div style={{ marginBottom: 10 }}>
                <b>🏁 Clean Start</b> — boot fresh, no save-state. Your memory card / battery stays in,
                so you can still load from the game's own menu.
              </div>
              <div style={{ borderTop: "1px solid rgba(128,128,128,.25)", paddingTop: 10 }}>
                <Text type="secondary" style={{ fontSize: 12 }}>
                  Or pick up a save-state — these count as save-scumming, so the run won't be legit:
                </Text>
                <div style={{ marginTop: 6 }}>
                  {autoSave && (
                    <div className="arcade-start-choice__row">
                      <a onClick={() => pick({})}>▶ Continue Auto-Save</a>
                      <Text type="secondary" className="arcade-start-choice__meta">
                        where you left off — saved automatically each time you leave
                        {when(autoSave) ? ` · ${when(autoSave)}` : ""}
                      </Text>
                    </div>
                  )}
                  {quick && (
                    <div className="arcade-start-choice__row">
                      <a onClick={() => pick({ seedSlot: QUICK_SLOT })}>▶ Quickload</a>
                      <Text type="secondary" className="arcade-start-choice__meta">
                        your quicksave{when(quick) ? ` · ${when(quick)}` : ""}
                      </Text>
                    </div>
                  )}
                  {useSnapPicker ? (
                    <div className="arcade-start-choice__row">
                      <Select
                        showSearch
                        placeholder={`▶ Load a snapshot… (${snaps.length})`}
                        style={{ width: "100%", marginTop: 4 }}
                        optionFilterProp="label"
                        options={snaps.map((s) => ({
                          value: s.slotId,
                          label: `${s.label || `Snapshot ${s.slotId}`}${when(s) ? ` · ${when(s)}` : ""}`,
                        }))}
                        onChange={(slotId) => pick({ seedSlot: slotId })}
                        aria-label="Load a snapshot save"
                      />
                    </div>
                  ) : (
                    snaps.map((s) => (
                      <div key={s.slotId} className="arcade-start-choice__row">
                        <a onClick={() => pick({ seedSlot: s.slotId })}>
                          ▶ {s.label || `Snapshot ${s.slotId}`}
                        </a>
                        <Text type="secondary" className="arcade-start-choice__meta">{when(s)}</Text>
                      </div>
                    ))
                  )}
                </div>
              </div>
              <div style={{ marginTop: 10 }}>
                <a onClick={() => { modal.destroy(); setCreating(0); setManageSaves({ gameId: versionId, title }); }}>
                  ⚙ Manage my saves…
                </a>
              </div>
            </div>
          ),
        });
      })
      .catch(() => doCreateRoom(versionId, { newGame: true, cheats, hwContext, renderProfile, controllerScheme }));
  }

  function doCreateRoom(versionId, opts) {
    // The room-start itself (stored quality, network unbundling, codec resolution, the POST and the
    // push into the room) lives in arcadeRoomCreate — the saves page starts rooms from a save too.
    return createRoomAndGo(versionId, opts, history).finally(() => setCreating(0));
  }

  const joinRoom = (roomCode) => history.push(`/arcade/room/${roomCode}`);

  // A card click opens a modal. Heavy-lane titles have their own Moonlight/capture modal; everything
  // else opens the standard game modal (version/cheats/scheme + Start + My saves all live there now).
  // `versionId` is optional and comes from the "Recently played" strip: saves are per ROM row, so a
  // recent tile opens the modal on the version whose save it is advertising.
  const openGame = (game, versionId = null) => {
    const vid = game.versions?.[0]?.id;
    setModalVersionId(versionId);
    if (!vid) { setModalCard({ key: null, game } ); return; } // defensive: cardless of versions, still open
    setModalCard({ key: vid, game });
    pushGameParam(vid);
  };
  openGameRef.current = openGame;

  if (unconfigured) {
    return <div style={{ padding: 48 }}><Empty description="The arcade isn't set up on this server yet." /></div>;
  }

  // The rail itself is the sider's ArcadeSiderRail — the phone drawer's, too, count and search and
  // all. What is left for the page: the bar's SmartSearch on desktop and the chips over the results.
  const { chips, surfaces } = sectionRailSurfaces(rail, isMobile, {
    placeholder: "A game, system:PS2, genre:RPG…",
  });

  // The section's bar tools (R9 S1): the two things you open + the Quality toggle, before the pills.
  const arcadeTools = (
    <>
      {/* One saves surface, not two: the vault is `/arcade/saves` (also the rail's own row). */}
      <button type="button" className="bx-tool-btn" onClick={() => history.push("/arcade/saves")}>💾 Saves</button>
      <button
        type="button"
        className={"bx-tool-btn" + (optionsOpen ? " on" : "")}
        aria-expanded={optionsOpen}
        onClick={() => setOptionsOpen((o) => !o)}
      >
        ⚡ Quality
      </button>
    </>
  );

  return (
    <div className="arcade-page">
      <div className="arcade-page__inner">
        {surfaces}
        {/* The header and its toolbar left the page in R9 S1: the SectionBar names the section, and
            Saves (a link to its page) + Quality ride the bar's tools slot (see `arcadeTools` on the
            CatalogHost below). Trophies is a bar TAB and a rail row, both onto /arcade/trophies —
            it is a page, not a dialog this lobby hosts. The Quality controls open here, under the
            bar, only while the toggle is on.
            Quality applies only to rooms YOU start (one encoder per room = the creator's pick is what
            everyone gets). */}
        {optionsOpen && (
          <div className="arcade-toolbar arcade-toolbar--quality">
            <div className={"arcade-conn arcade-conn--open"}>
              <div className="arcade-pill">
                <span className="arcade-dot-ok" />
                <Select
                  variant="borderless" value={quality.videoBitrateKbps} options={BITRATE_PRESETS}
                  onChange={(v) => setQ({ videoBitrateKbps: v })}
                  classNames={{ popup: { root: "arcade-pill-dropdown" } }} popupMatchSelectWidth={false}
                  aria-label="Stream bitrate"
                />
              </div>
              <div className="arcade-pill">
                <Select
                  variant="borderless" value={quality.network} optionLabelProp="label"
                  onChange={(v) => setQ({ network: v, networkChosen: true })}
                  classNames={{ popup: { root: "arcade-pill-dropdown" } }} popupMatchSelectWidth={false}
                  aria-label="Network profile"
                >
                  {NETWORK_OPTIONS.map((o) => (
                    <Select.Option key={o.value} value={o.value} label={o.short}>{o.label}</Select.Option>
                  ))}
                </Select>
              </div>
              <div className="arcade-pill">
                <Select
                  variant="borderless" value={quality.codec || "av1"} optionLabelProp="label"
                  onChange={(v) => setQ({ codec: v, codecChosen: true })}
                  classNames={{ popup: { root: "arcade-pill-dropdown" } }} popupMatchSelectWidth={false}
                  aria-label="Video codec"
                >
                  {CODEC_OPTIONS.map((o) => (
                    <Select.Option key={o.value} value={o.value} label={o.short}>{o.label}</Select.Option>
                  ))}
                </Select>
              </div>
            </div>
          </div>
        )}

        {/* Host health, above everything you can click: if a remote desktop is holding the arcade PC
            off its own screen, every room you start from here will be choppy, and that is worth
            knowing BEFORE picking a game rather than after blaming your own wifi. */}
        <ArcadeHostBanner />

        <ConsoleCarousel
          systems={facets?.systems}
          selected={selectedSystems}
          onToggle={onToggleSystem}
          onClear={() => setSystems([])}
        />

        <LiveRooms rooms={rooms} onJoin={joinRoom} />

        {/* No bespoke head: the SectionBar names the section and the rail's head line carries the
            count (R9 S1). The grid is the package's GridView over InfiniteBands, drawing THIS page's
            GameCard; its skeletons, its A-Z strip and its empty/failed states are the package's too. */}
        <section className="arcade-section">
          <CatalogHost section="arcade" source={catalogSource} tools={arcadeTools} beforeResults={chips} />
        </section>
      </div>

      {modalCard && (openGameId != null || modalCard.key == null) && modalCard.game.lane !== "heavy" && (
        <GameModal
          game={modalCard.game}
          creating={creating}
          canEditMovies={userData?.canEditMovies}
          renderers={renderers[modalCard.game.system] || []}
          initialVersionId={modalVersionId}
          onClose={closeGame}
          // Both actions leave the browse tile: close the game modal first so the follow-on surface
          // (the Continue/New-game confirm, or the saves manager) isn't stranded behind it at a lower
          // z-index. This restores the exact pre-modal flow those surfaces were built for.
          onStart={(versionId, title, cheats, hwContext, controllerScheme, renderProfile, competitive) => {
            // Grab the system BEFORE clearing the modal — the start-choice prompt needs it to know
            // whether this core even has save-states to offer (psp/scummvm don't).
            const sys = modalCard?.game?.system;
            closeGame();
            createRoom(versionId, title, cheats, hwContext, controllerScheme, renderProfile, competitive, sys);
          }}
          onManageSaves={(gameId, title) => { closeGame(); setManageSaves({ gameId, title }); }}
        />
      )}
      {manageSaves && (
        <SavesManager game={manageSaves} onClose={() => setManageSaves(null)} onResume={doCreateRoom} />
      )}
      {modalCard && openGameId != null && modalCard.game.lane === "heavy" && (
        <HeavyGameModal
          game={modalCard.game}
          onClose={closeGame}
          onPlayInBrowser={(versionId) => { closeGame(); doCreateRoom(versionId, {}); }}
        />
      )}
    </div>
  );
}
