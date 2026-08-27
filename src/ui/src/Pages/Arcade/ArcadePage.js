import { useEffect, useMemo, useRef, useState, useCallback } from "react";
import { useHistory, useLocation } from "react-router-dom";
import { Button, Empty, Modal, Select, Typography, message } from "antd";
import { MovieAPI } from "../../MovieAPI";
import ArcadeHostBanner from "./ArcadeHostBanner";
import GameCard, { PendingGameCard } from "./GameCard";
import GameModal from "./GameModal";
import HeavyGameModal from "./HeavyGameModal";
import LiveRooms from "./LiveRooms";
import RecentlyPlayed from "./RecentlyPlayed";
import SavesManager from "./SavesManager";
import SavesVaultManager from "./SavesVaultManager";
import RetroAchievementsModal from "./RetroAchievementsModal";
import CatalogPager from "../../Components/CatalogPager";
import LoadFailure from "../../Components/LoadFailure";
import ConsoleCarousel from "./ConsoleCarousel";
import { rememberLobbySearch } from "./arcadeLobbyState";
import { hasSaveStates, QUICK_SLOT } from "./arcadeSystems";
import useArcadeFilters from "./useArcadeFilters";
import { parseSystems, toggleSystem, SYSTEM_PARAM } from "./arcadeSystemFilter";
import useGridWindow from "../../hooks/useGridWindow";
import usePagedCatalog from "../../hooks/usePagedCatalog";
import CatalogHost, { AVAILABLE_VIEWS } from "../../catalog/CatalogHost";
import { createArcadeSource, serverSort } from "../../catalog/sources/arcadeSource";
import { readCatalogDefaults, resolveViewState } from "../../catalog/state/useCatalogView";
import "./ArcadePage.css";
import usePolling from "../../hooks/usePolling";

const { Text } = Typography;
const PAGE_SIZE = 60;

// Per-room stream quality the creator picks (arcade per-room bitrate/FEC). Persisted so a friend group
// keeps its setting across sessions; applied to every room YOU start (one encoder per room = creator's
// choice). Lower bitrate = smaller video bursts = smoother audio + less upstream for remote players.
const QUALITY_KEY = "arcade.streamQuality";
const BITRATE_PRESETS = [
  // 0 = Auto: the WORKER now DERIVES the ceiling from the frame it actually encodes — encoded pixels ×
  // fps × a bits-per-pixel target (abr.go autoCeilingKbps), clamped to 5–25 Mbps. It is no longer a
  // per-system constant: that table could not see the core's real viewport, its scale, a core changing
  // resolution mid-game, or a render profile moving the frame. Measured live 2026-07-30: genesis 19328,
  // snes 15508, n64 13271, gc 14583, psp 12584. Auto is usually the RIGHT answer now — the manual
  // presets below are for capping your own upstream, not for beating Auto.
  { label: "Auto · match the frame", value: 0 },
  // 25 Mbps matches abrAutoMaxKbps, so a manual pick can reach what Auto can. Raised from 16 (2026-07-30)
  // because a 960×672 Genesis frame derives 19328 — the old top preset sat BELOW Auto for 2D systems.
  // ⚠ ArcadeController clamps this server-side; that clamp had to move to 25000 with it.
  { label: "LAN · 25 Mbps", value: 25000 },
  // Kept as the old top preset for anyone who had chosen it deliberately.
  { label: "Very high · 16 Mbps", value: 16000 },
  // 10 Mbps, best for hi-res 3D cores (GameCube 1280×1056, PS2 upscaled) on a fat pipe. At
  // 4 remote players that's ~40 Mbps upstream, so it's really a post-FiOS / mostly-LAN setting; on
  // cable uplinks prefer 5 or lower. Overkill (but harmless) for retro/2D. Server clamps 500–25000.
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
// What each profile actually sends (the worker never sees "profiles", only these params).
// audioFec: 1 = on, 2 = off. paceMs: patch-0028 in-frame smoothing window (0 = off; 5G gets a
// wider window because big keyframes at low cellular bitrates benefit from more spread).
const NETWORK_PROFILES = {
  lan: { audioFec: 1, paceMs: 0 },
  remote: { audioFec: 1, paceMs: 5 },
  "5g": { audioFec: 1, paceMs: 8 },
};
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
// Resolve "auto" to a concrete codec for THIS device. powerEfficient is the hardware-decode signal —
// smooth-but-software (dav1d on a big desktop) still reports smooth:true, and software AV1 is exactly
// the tablet failure mode Auto exists to dodge, so the bar is powerEfficient. 1920x1080@60 is the
// worst frame any lane sends today (capture); retro encodes larger canvases but at the same or lower
// pixel rate. Any probe failure (old browser, Firefox without webrtc-type support) falls back to av1
// — the status-quo default, so Auto can never be WORSE than before it existed.
async function resolveAutoCodec() {
  try {
    const info = await navigator.mediaCapabilities.decodingInfo({
      type: "webrtc",
      video: { contentType: 'video/AV1; codecs="av01.0.08M.08"', width: 1920, height: 1080, bitrate: 12_000_000, framerate: 60 },
    });
    return info.supported && info.powerEfficient ? "av1" : "h264";
  } catch { return "av1"; }
}
function loadQuality() {
  try {
    const q = JSON.parse(localStorage.getItem(QUALITY_KEY));
    if (q && typeof q.videoBitrateKbps === "number") {
      // Legacy audioFec-shaped values (pre network-profile) map to LAN — the old default behavior.
      const network = NETWORK_PROFILES[q.network] ? q.network : "lan";
      // Deliberate codec picks are NOT migrated to Auto — a chosen h264 often protects a JOINING
      // tablet, which a creator-device probe cannot see. Deliberate = the codecChosen flag (set only
      // by the Codec dropdown's own onChange), OR any stored "h264": av1 was the seeded default, so
      // an un-flagged "av1" means "never picked" and gets Auto — which resolves back to av1 on every
      // hardware-AV1 device and only changes behavior on the devices av1 was failing on.
      const codec = (q.codecChosen === true && (q.codec === "h264" || q.codec === "av1")) || q.codec === "h264"
        ? q.codec : "auto";
      // networkChosen: set ONLY by the Network dropdown's own onChange, never by seeding — it is
      // what lets an explicit "LAN · pace 0" beat the capture lane's server-side pace default.
      // Legacy values (no flag) stay "not chosen" so those users keep the lane defaults.
      // Both *Chosen flags must round-trip here: setQ persists {...prev, ...patch}, so a flag this
      // function drops would be erased from storage by the next unrelated quality change.
      return {
        videoBitrateKbps: q.videoBitrateKbps, network, codec,
        networkChosen: q.networkChosen === true, codecChosen: q.codecChosen === true,
      };
    }
  } catch { /* ignore */ }
  // Auto + LAN + Auto-codec. NOTE: a stored value is NOT migrated — someone who deliberately picked
  // "Balanced · 5 Mbps" on a thin uplink should not be silently moved to Auto (whose ceiling reaches
  // 14 Mbps on GameCube; ABR would walk it back, but the choice is theirs). They opt in by choosing
  // Auto once.
  return { videoBitrateKbps: 0, network: "lan", codec: "auto", networkChosen: false, codecChosen: false };
}
function saveQuality(q) { try { localStorage.setItem(QUALITY_KEY, JSON.stringify(q)); } catch { /* ignore */ } }

/**
 * The /arcade lobby (docs/arcade-plan.md §7, redesigned per design_handoff_arcade_browse/README.md).
 *
 * Over ~13k cards this is SERVER-SIDE filtered + paged: the filter controls live in the navbar rail
 * (ArcadeNavContent) as URL params, and this page fetches the matching page and appends more on
 * demand. A "Live rooms" strip shows what friends are playing right now.
 */
export default function ArcadePage({ userData }) {
  const history = useHistory();
  const location = useLocation();

  // ── The catalog as SPARSE SLOTS (ported from The Long Box's InfiniteScroller) ──────────────────
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
  // Ported here as a page map: `pages[n]` holds catalog indices [n*PAGE_SIZE, (n+1)*PAGE_SIZE).
  // Anything not yet fetched renders as a skeleton tile of card size. There is no `startIndex`.
  // pages/total/loading/firstLoaded/loadError all live in usePagedCatalog now (the pump was
  // extracted to hooks/usePagedCatalog.js once Browse needed the same machinery).
  const [letters, setLetters] = useState(null); // A–Z bucket offsets, for the pager (A–Z sort only)
  const [rooms, setRooms] = useState([]);
  const [renderers, setRenderers] = useState({}); // system → [{id,label,isDefault}] for the launch menu
  const [recentGames, setRecentGames] = useState([]); // "Recently played" strip (save-derived history)
  const [modalVersionId, setModalVersionId] = useState(null); // pre-selected version (a recent tile's save)
  const [savesVaultOpen, setSavesVaultOpen] = useState(false); // the cross-game "My saves" drawer
  const [raOpen, setRaOpen] = useState(false); // the RetroAchievements hub (account + trophy room)
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
  const unconfiguredRef = useRef(false);

  // Read by the identity-stable fetchPage below, so the pump never re-subscribes.
  const filtersRef = useRef(null);

  const setQ = (patch) => setQuality((prev) => { const next = { ...prev, ...patch }; saveQuality(next); return next; });

  // The active filters, from the URL (set by the navbar panel).
  const filters = useMemo(() => {
    const p = new URLSearchParams(location.search);
    return {
      system: p.get("system") || "",
      hideRegions: p.get("hideRegions") || "",
      maxPlayers: p.get("players") || "",
      variant: p.get("variant") || "",
      genre: p.get("genre") || "",
      // The catalog switcher names the default order "alpha"; the server knows it as "".
      sort: serverSort(p.get("sort")),
      search: p.get("q") || "",
      ra: p.get("ra") || "",
    };
  }, [location.search]);
  const filterKey = JSON.stringify(filters);
  filtersRef.current = filters;

  // ── The catalog views (Wall / List / Extended / Shelves / Newspaper / Directory) over the SAME filters. ──
  // The lobby grid below stays exactly as it is (the host's `grid` override); the other views page the
  // source themselves, so the lobby's pump and letter strip only run while the grid is on screen. The
  // modal opener is defined further down — the source reaches it through a ref.
  const openGameRef = useRef(null);
  const catalogSource = useMemo(
    () => createArcadeSource({
      filters,
      filterKey,
      onOpen: (card) => openGameRef.current?.(card),
      onFilter: (param, value) => {
        const params = new URLSearchParams(location.search);
        params.set(param, value);
        params.delete("game");
        history.push({ pathname: "/arcade", search: `?${params.toString()}` });
      },
    }),
    // history is a stable reference in react-router v5; location.search is what the filters came from.
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [filters, filterKey]
  );
  const catalogView = useMemo(
    () => resolveViewState(location.search, readCatalogDefaults("arcade"), catalogSource, AVAILABLE_VIEWS).view,
    [location.search, catalogSource]
  );
  const gridActive = catalogView === "grid";

  // ── The catalog pump (hooks/usePagedCatalog.js — extracted from this page) ─────────────────────
  // fetchPage reads the filters through the ref so its identity never changes; 501 = the arcade
  // isn't configured (a dedicated empty state, not a load error).
  const {
    pages, total, loading, firstLoaded, loadError, itemAt: gameAt, contentKey, notifyWindow, retry,
  } = usePagedCatalog({
    resetKey: filterKey,
    pageSize: PAGE_SIZE,
    enabled: gridActive,
    fetchPage: (skip, pageSize, signal) =>
      MovieAPI.getArcadeGames({ ...filtersRef.current, skip, pageSize }, signal)
        .then((r) => {
          if (r.status === 501) { unconfiguredRef.current = true; return null; }
          return r.ok ? r.json() : null;
        })
        .then((data) => (data ? { items: data.games, totalCount: data.totalCount } : null)),
  });

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

  // The console carousel writes the same ?system= param the rail's dropdown does — the URL is the one
  // source of truth, so the two surfaces can never disagree about what's picked. Facets come from the
  // shared hook, so the rail and the shelf make ONE request between them rather than one each.
  const facets = useArcadeFilters(filters);
  const selectedSystems = parseSystems(location.search);

  const setSystems = useCallback((next) => {
    const params = new URLSearchParams(location.search);
    const value = next.join(",");
    if (value) params.set(SYSTEM_PARAM, value); else params.delete(SYSTEM_PARAM);
    history.push({ pathname: "/arcade", search: params.toString() ? `?${params.toString()}` : "" });
  }, [history, location.search]);

  const onToggleSystem = useCallback(
    (system) => setSystems(toggleSystem(parseSystems(location.search), system)),
    [setSystems, location.search],
  );

  // A–Z bucket offsets for the pager. Only under the alphabetical sort — under any other sort the
  // letter buckets aren't contiguous, so the strip shows page numbers and needs nothing from here.
  useEffect(() => {
    if (filters.sort || !gridActive) { setLetters(null); return undefined; }
    let alive = true;
    MovieAPI.getArcadeGameLetters(filters)
      .then((r) => (r.ok ? r.json() : null))
      .then((d) => { if (alive) setLetters(d?.letters || []); })
      .catch(() => {});
    return () => { alive = false; };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [filterKey]);

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

  // "Recently played" strip — a fresh fetch on mount is enough: it only changes once a session ends
  // and harvests a save, and returning here from a room is a route change that remounts this page.
  useEffect(() => {
    let alive = true;
    MovieAPI.getArcadeRecentlyPlayed(12).then((rows) => { if (alive) setRecentGames(Array.isArray(rows) ? rows : []); });
    return () => { alive = false; };
  }, []);

  // ── The window, over ALL of it ─────────────────────────────────────────────────────────────────
  // `total`, not "how many we have fetched". Every catalog slot exists from the first render, so the
  // scrollbar is honest immediately and the window can move in either direction into territory that
  // has no data yet — which is the whole of the fix. Only the rows near the viewport stay mounted;
  // the rest of the list's height is held by two spacers. An arcade card's height varies (box art
  // aspect, whether it has a version/cheats row), so the hook measures rows rather than assuming one.
  //
  // resetKey no longer carries a start index, because there is no longer one to carry: a jump does
  // not change the list, so it must not throw away the heights measured for it.
  const { hostRef, gridRef, start, end, padTop, padBottom, visibleStart, scrollToIndex } = useGridWindow(total, {
    resetKey: filterKey,
    // ⚠ Load-bearing on a paged list. A page arriving replaces placeholders with real cards at the
    // SAME slots, so neither the window nor the count changes — and without this the hook would never
    // re-measure them, leaving the prefix describing skeleton heights for rows that are now cards.
    // Every row below would then sit slightly wrong, and the further you scrolled the worse it got.
    contentKey,
  });

  // The mounted slice: one entry per slot, real card or placeholder. Deliberately NOT filtered — a
  // hole is a slot whose page has not arrived, and dropping it would shorten the row and make every
  // card below it move as the data lands.
  const visibleSlots = useMemo(() => {
    const out = [];
    for (let i = start; i < Math.min(end, total); i += 1) out.push({ index: i, game: gameAt(i) });
    return out;
  }, [start, end, total, gameAt]);

  // Fetch what the window is looking at, plus one page either side — the same "want the window's
  // bands + one ahead" rule, made symmetric because here the user can be travelling upward.
  useEffect(() => {
    notifyWindow(start, end);
  }, [start, end, total, pages, notifyWindow]);

  // Seek the grid to an absolute catalog offset (a letter bucket or a page boundary). No fetch, no
  // re-seat: the slot is already there, and scrollToIndex's second phase waits for its page.
  const jumpTo = useCallback((offset) => {
    scrollToIndex(Math.max(0, Math.min(offset, Math.max(0, total - 1))));
  }, [scrollToIndex, total]);

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
    // Merge the creator's current stream quality (read fresh from storage so a mid-modal change wins).
    // The network profile is unbundled HERE into the wire params (audioFec + paceMs) — the server and
    // worker stay profile-agnostic. paceMs is sent ONLY for a deliberate dropdown pick: omitting it
    // (server null) keeps the lane defaults (capture 8, GL 0), while an explicit LAN 0 must actually
    // reach the server to beat the capture default.
    const q = loadQuality();
    const net = NETWORK_PROFILES[q.network] || NETWORK_PROFILES.lan;
    const netParams = q.networkChosen ? net : { audioFec: net.audioFec };
    // "auto" resolves to a concrete codec HERE (the room's encoder needs one) — the server and worker
    // never see "auto". The probe is ~instant (cached capability lookup), so it rides the chain.
    return Promise.resolve(q.codec === "auto" ? resolveAutoCodec() : q.codec)
      .then((codec) => MovieAPI.createArcadeRoom(versionId, { ...opts, videoBitrateKbps: q.videoBitrateKbps, ...netParams, videoCodec: codec }))
      .then(async (r) => {
        if (r.status === 503) { message.warning("The arcade is full — every machine is in use. Try again shortly."); return null; }
        if (!r.ok) { message.error("Couldn't start that game."); return null; }
        return r.json();
      })
      .then((descriptor) => {
        if (descriptor) history.push({ pathname: `/arcade/room/${descriptor.roomCode}`, state: { descriptor } });
      })
      .catch(() => message.error("Couldn't start that game."))
      .finally(() => setCreating(0));
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

  if (unconfiguredRef.current) {
    return <div style={{ padding: 48 }}><Empty description="The arcade isn't set up on this server yet." /></div>;
  }

  // "all" is the Mods & Hacks DEFAULT, and picking it explicitly leaves ?variant=all in the URL — so
  // treating any variant value as a filter made an unfiltered lobby report "No games match those
  // filters", which reads as though something had been filtered away when nothing had.
  const anyFilter = filters.system || filters.hideRegions || filters.maxPlayers
    || (filters.variant && filters.variant !== "all") || filters.genre || filters.search || filters.ra;

  // The section's bar tools (R9 S1): the two things you open + the Quality toggle, before the pills.
  const arcadeTools = (
    <>
      <button type="button" className="bx-tool-btn" onClick={() => setSavesVaultOpen(true)}>💾 Saves</button>
      <button type="button" className="bx-tool-btn" onClick={() => setRaOpen(true)}>🏆 Trophies</button>
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
        {/* The header and its toolbar left the page in R9 S1: the SectionBar names the section, and
            Saves · Trophies · Quality ride the bar's tools slot (see `arcadeTools` on the CatalogHost
            below). The Quality controls open here, under the bar, only while the toggle is on.
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

        <RecentlyPlayed rows={recentGames} onOpen={openGame} />

        <LiveRooms rooms={rooms} onJoin={joinRoom} />

        <section className="arcade-section">
          <div className="arcade-section__head arcade-section__head--games">
            <h2 className="arcade-section__title">Games</h2>
            <span className="arcade-section__count">
              {total.toLocaleString()} {anyFilter ? (total === 1 ? "match" : "matches") : (total === 1 ? "title" : "titles")}
            </span>
          </div>

          <CatalogHost section="arcade" source={catalogSource} tools={arcadeTools} overrides={{ grid: !firstLoaded ? (
            /* Skeleton cards in the real grid layout — the site-wide first-paint convention (movies,
               boardgames), instead of a lone spinner. */
            <div className="arcade-grid" aria-hidden="true">
              {Array.from({ length: 8 }).map((_, i) => <PendingGameCard key={i} />)}
            </div>
          ) : total === 0 ? (
            /* Never claim "nothing matched" while a request is still out, or when one failed. A wide
               filter change — above all clearing the LAST console, which puts the whole catalog back in
               scope — is the slowest query the lobby can ask for, and an empty grid that explains itself
               as a filter result is indistinguishable from a real one. */
            loading ? (
              <div className="arcade-grid" aria-hidden="true">
                {Array.from({ length: 8 }).map((_, i) => <PendingGameCard key={i} />)}
              </div>
            )
              : loadError ? (
                <LoadFailure message="Couldn't load the games list." onRetry={retry} />
              ) : <Empty description={anyFilter ? "No games match those filters." : "No games here yet."} />
          ) : (
            <>
              {/* No "Load more", no "Earlier titles", no sentinel. Every catalog slot exists from the
                  first render and the window fetches whichever pages it is looking at, so scrolling —
                  in EITHER direction — is the only control there needs to be. */}
              <div ref={hostRef}>
                {padTop > 0 && <div className="grid-spacer" style={{ height: padTop }} aria-hidden="true" />}
                <div className="arcade-grid" ref={gridRef}>
                  {visibleSlots.map(({ index, game }) => (game ? (
                    <GameCard key={game.key} game={game} onOpen={openGame} />
                  ) : (
                    /* A slot whose page is still on the wire. Same footprint as a card so the row it
                       is in does not reflow when the real one replaces it. */
                    <PendingGameCard key={`slot-${index}`} />
                  )))}
                </div>
                {padBottom > 0 && <div className="grid-spacer" style={{ height: padBottom }} aria-hidden="true" />}
              </div>
              <div className="arcade-more">
                <Text type="secondary">— {total.toLocaleString()} titles —</Text>
              </div>
              {/* Letters when sorted A–Z, page numbers under any other sort. Both seek into the same
                  continuous list; the active button follows the grid as you scroll. */}
              <CatalogPager
                mode={filters.sort ? "pages" : "letters"}
                letters={letters}
                total={total}
                pageSize={PAGE_SIZE}
                currentIndex={visibleStart}
                onJump={jumpTo}
              />
            </>
          ) }} />
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
      {savesVaultOpen && (
        <SavesVaultManager onClose={() => setSavesVaultOpen(false)} onResume={doCreateRoom} />
      )}
      <RetroAchievementsModal open={raOpen} onClose={() => setRaOpen(false)} />
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
