import { useEffect, useMemo, useRef, useState, useCallback } from "react";
import { useHistory, useLocation } from "react-router-dom";
import { Button, Empty, Modal, Select, Spin, Typography, message } from "antd";
import { MovieAPI } from "../../MovieAPI";
import GameCard from "./GameCard";
import GameModal from "./GameModal";
import HeavyGameModal from "./HeavyGameModal";
import LiveRooms from "./LiveRooms";
import RecentlyPlayed from "./RecentlyPlayed";
import SavesManager from "./SavesManager";
import SavesVaultManager from "./SavesVaultManager";
import RetroAchievementsModal from "./RetroAchievementsModal";
import ArcadePager from "./ArcadePager";
import ConsoleCarousel from "./ConsoleCarousel";
import { rememberLobbySearch } from "./arcadeLobbyState";
import { hasSaveStates, QUICK_SLOT } from "./arcadeSystems";
import useArcadeFilters from "./useArcadeFilters";
import { parseSystems, toggleSystem, SYSTEM_PARAM } from "./arcadeSystemFilter";
import useInfiniteScroll from "../../hooks/useInfiniteScroll";
import useGridWindow from "../../hooks/useGridWindow";
import "./ArcadePage.css";

const { Text } = Typography;
const PAGE_SIZE = 60;
const SENTINEL_STYLE = { height: 1 };

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
// Per-room video codec (worker patch 0036). AV1 is the default (better quality per bit), but a
// device without HARDWARE AV1 decode — most tablets — still negotiates it (Chrome offers software
// dav1d) and then can't decode 60fps in real time; the keyframeless AV1 stream gives it nothing to
// resync to, so video falls minutes behind the (separately-decoded) audio. H.264 hardware-decodes
// everywhere. Room-wide: one encoder per room, so pick H.264 when ANY player will be on a tablet.
const CODEC_OPTIONS = [
  { short: "Codec: AV1", label: "Codec: AV1 · best picture (PCs & recent devices)", value: "av1" },
  { short: "Codec: H.264", label: "Codec: H.264 · smoothest on tablets & older devices", value: "h264" },
];
function loadQuality() {
  try {
    const q = JSON.parse(localStorage.getItem(QUALITY_KEY));
    if (q && typeof q.videoBitrateKbps === "number") {
      // Legacy audioFec-shaped values (pre network-profile) map to LAN — the old default behavior.
      const network = NETWORK_PROFILES[q.network] ? q.network : "lan";
      const codec = q.codec === "h264" ? "h264" : "av1";
      return { videoBitrateKbps: q.videoBitrateKbps, network, codec };
    }
  } catch { /* ignore */ }
  // Auto + LAN. NOTE: a stored value is NOT migrated — someone who deliberately picked "Balanced ·
  // 5 Mbps" on a thin uplink should not be silently moved to Auto (whose ceiling reaches 14 Mbps on
  // GameCube; ABR would walk it back, but the choice is theirs). They opt in by choosing Auto once.
  return { videoBitrateKbps: 0, network: "lan", codec: "av1" };
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

  const [games, setGames] = useState(null); // null = first load
  const [total, setTotal] = useState(0);
  // The catalog is ONE list that the grid seeks into; `startIndex` is the absolute catalog index of
  // games[0]. A pager jump re-anchors it (jump to "M" → startIndex = M's offset) and infinite scroll
  // appends from there, so there's no page bookkeeping to keep coherent with the appended window.
  const [startIndex, setStartIndex] = useState(0);
  const [loading, setLoading] = useState(false); // first page / a pager jump (replaces the grid)
  const [loadError, setLoadError] = useState(false); // the page request failed — not an empty catalog
  const [loadingMore, setLoadingMore] = useState(false); // appending the next page
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
  const [modalGame, setModalGame] = useState(null); // the game whose full-page modal is open
  const [heavyGame, setHeavyGame] = useState(null); // heavy-lane card → the Play-via-Moonlight modal
  const [quality, setQuality] = useState(loadQuality); // creator's per-room stream quality (persisted)
  const unconfiguredRef = useRef(false);
  const sectionRef = useRef(null);

  // Read by the identity-stable loaders below, so the scroll listener never re-subscribes.
  const epochRef = useRef(0);
  const abortRef = useRef(null);
  const loadingMoreRef = useRef(false);
  const gamesRef = useRef([]);
  const startRef = useRef(0);
  const totalRef = useRef(0);
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
      sort: p.get("sort") || "",
      search: p.get("q") || "",
      ra: p.get("ra") || "",
    };
  }, [location.search]);
  const filterKey = JSON.stringify(filters);
  filtersRef.current = filters;
  gamesRef.current = games || [];
  startRef.current = startIndex;
  totalRef.current = total;

  // Stash the filtered lobby URL so the room's exit buttons can come back to it (arcadeLobbyState).
  useEffect(() => { rememberLobbySearch(location.search); }, [location.search]);

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

  /**
   * Load `pageSize` cards starting at absolute catalog index `skip`.
   * `replace` = this is a new anchor (filters changed, or a pager jump): it cancels whatever is in
   * flight, bumps the epoch so a late reply from the old query can't append its rows onto the new
   * list, and re-seats the grid at `skip`. Otherwise it's an append.
   */
  const loadPage = useCallback((skip, replace) => {
    if (replace) {
      epochRef.current += 1;
      abortRef.current?.abort();
      loadingMoreRef.current = false;
      setLoadingMore(false);
      setLoading(true);
    } else {
      if (loadingMoreRef.current) return;
      loadingMoreRef.current = true;
      setLoadingMore(true);
    }
    const epoch = epochRef.current;
    const controller = new AbortController();
    abortRef.current = controller;

    MovieAPI.getArcadeGames({ ...filtersRef.current, skip, pageSize: PAGE_SIZE }, controller.signal)
      .then((r) => {
        if (r.status === 501) { unconfiguredRef.current = true; return null; }
        return r.ok ? r.json() : null;
      })
      .then((data) => {
        if (epochRef.current !== epoch) return; // a newer query owns the grid now
        // A page that didn't arrive is NOT an empty catalog. Falling through to an empty list here made
        // a failed request render as "No games match those filters" — the lobby blaming the user's
        // filters for its own broken fetch, with no way to tell the difference or to try again.
        if (!data) { setLoadError(true); setGames((g) => g || []); return; }
        setLoadError(false);
        setTotal(data.totalCount);
        if (replace) {
          setStartIndex(data.skip ?? skip);
          setGames(data.games);
        } else if (data.games.length) {
          setGames((prev) => (prev ? prev.concat(data.games) : data.games));
        }
      })
      .catch((err) => { if (err?.name !== "AbortError") { setLoadError(true); setGames((g) => g || []); } })
      .finally(() => {
        if (epochRef.current !== epoch) return;
        loadingMoreRef.current = false;
        setLoadingMore(false);
        setLoading(false);
      });
  }, []);

  // Reset + fetch from the top whenever the filters change. Keyed on the serialized filters, not on
  // the `filters` object (fresh every render) — loadPage reads them through a ref.
  useEffect(() => {
    setGames(null);
    setStartIndex(0);
    loadPage(0, true);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [filterKey]);

  // A–Z bucket offsets for the pager. Only under the alphabetical sort — under any other sort the
  // letter buckets aren't contiguous, so the strip shows page numbers and needs nothing from here.
  useEffect(() => {
    if (filters.sort) { setLetters(null); return undefined; }
    let alive = true;
    MovieAPI.getArcadeGameLetters(filters)
      .then((r) => (r.ok ? r.json() : null))
      .then((d) => { if (alive) setLetters(d?.letters || []); })
      .catch(() => {});
    return () => { alive = false; };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [filterKey]);

  // Live-rooms strip, polled every 12 s.
  useEffect(() => {
    let alive = true;
    const load = () => MovieAPI.getArcadeRooms().then((r) => (r.ok ? r.json() : [])).then((rs) => { if (alive) setRooms(rs); }).catch(() => {});
    load();
    const id = setInterval(load, 12000);
    return () => { alive = false; clearInterval(id); };
  }, []);

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

  // Infinite scroll. The old version listened on `window` and measured `document.body.offsetHeight`,
  // which is wrong on desktop — the page scrolls inside .app-content, so scrollY never moved and the
  // grid only ever grew via the Load more button. The shared hook resolves the real scroll root.
  const hasMore = games != null && startIndex + games.length < total;
  const loadMore = useCallback(() => {
    const next = startRef.current + gamesRef.current.length;
    if (loadingMoreRef.current || next >= totalRef.current) return;
    loadPage(next, false);
  }, [loadPage]);
  const { sentinelRef, recheck } = useInfiniteScroll({ enabled: games != null, hasMore, onLoadMore: loadMore });
  useEffect(() => { recheck(); }, [games, recheck]);

  // Only the rows near the viewport stay mounted; the rest of the loaded list's height is held by
  // spacers. An arcade card's height varies (box-art aspect, whether it has a version/cheats row), so
  // the hook measures rows rather than assuming a fixed one.
  const { hostRef, gridRef, start, end, padTop, padBottom, visibleStart } = useGridWindow(games?.length || 0, {
    resetKey: `${filterKey}:${startIndex}`,
  });
  // `.filter(Boolean)` guards the render, not the data: the grid maps to `key={game.key}`, so ONE
  // empty slot anywhere in the loaded list throws and takes the entire arcade page down with it —
  // a blank screen instead of a missing tile. Cheap insurance on a list this page can't render without.
  const visibleGames = useMemo(() => (games || []).slice(start, end).filter(Boolean), [games, start, end]);

  // Seek the grid to an absolute catalog offset (a letter bucket or a page boundary).
  const jumpTo = useCallback((offset) => {
    loadPage(Math.max(0, offset), true);
    sectionRef.current?.scrollIntoView({ block: "start" });
  }, [loadPage]);

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
        const hasContinue = states.some((s) => s.slotId === 0);
        const quick = states.find((s) => s.slotId === QUICK_SLOT);
        const snaps = states.filter((s) => s.slotId >= 1 && s.slotId !== QUICK_SLOT)
          .sort((a, b) => a.slotId - b.slotId);
        // Nothing restorable → no choice to present.
        if (!hasContinue && !quick && snaps.length === 0) {
          return doCreateRoom(versionId, { newGame: true, cheats, hwContext, renderProfile, controllerScheme });
        }
        const start = (opts) => doCreateRoom(versionId, { ...opts, cheats, hwContext, renderProfile, controllerScheme });
        const when = (s) => (s?.updatedUtc ? new Date(s.updatedUtc).toLocaleString() : "");
        const modal = Modal.confirm({
          title: "How do you want to start?",
          icon: null,
          width: 520,
          okText: "🏁 Clean Start",
          cancelText: "Cancel",
          onOk: () => start({ newGame: true }),
          onCancel: () => setCreating(0),
          content: (
            <div className="arcade-start-choice">
              <div style={{ marginBottom: 10 }}>
                <b>🏁 Clean Start</b> — boot fresh, no save-state. Your memory card / battery stays in,
                so you can still load from the game's own menu. <b>Only a clean start can set a legit
                score, time, or achievement.</b>
              </div>
              <div style={{ borderTop: "1px solid rgba(128,128,128,.25)", paddingTop: 10 }}>
                <Text type="secondary" style={{ fontSize: 12 }}>
                  Or pick up a save-state — these count as save-scumming, so the run won't be legit:
                </Text>
                <div style={{ marginTop: 6, maxHeight: 200, overflowY: "auto" }}>
                  {hasContinue && (
                    <div style={{ padding: "3px 0" }}>
                      <a onClick={() => { modal.destroy(); start({}); }}>▶ Continue Auto-Save</a>
                      <Text type="secondary" style={{ fontSize: 11, marginLeft: 8 }}>
                        where you left off — saved automatically each time you leave
                        {when(states.find((s) => s.slotId === 0)) ? ` · ${when(states.find((s) => s.slotId === 0))}` : ""}
                      </Text>
                    </div>
                  )}
                  {quick && (
                    <div style={{ padding: "3px 0" }}>
                      <a onClick={() => { modal.destroy(); start({ seedSlot: QUICK_SLOT }); }}>▶ Quickload</a>
                      <Text type="secondary" style={{ fontSize: 11, marginLeft: 8 }}>
                        your quicksave{when(quick) ? ` · ${when(quick)}` : ""}
                      </Text>
                    </div>
                  )}
                  {snaps.map((s) => (
                    <div key={s.slotId} style={{ padding: "3px 0" }}>
                      <a onClick={() => { modal.destroy(); start({ seedSlot: s.slotId }); }}>
                        ▶ {s.label || `Snapshot ${s.slotId}`}
                      </a>
                      <Text type="secondary" style={{ fontSize: 11, marginLeft: 8 }}>{when(s)}</Text>
                    </div>
                  ))}
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
    // worker stay profile-agnostic.
    const q = loadQuality();
    const net = NETWORK_PROFILES[q.network] || NETWORK_PROFILES.lan;
    return MovieAPI.createArcadeRoom(versionId, { ...opts, videoBitrateKbps: q.videoBitrateKbps, ...net, videoCodec: q.codec })
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
    if (game.lane === "heavy") { setHeavyGame(game); return; }
    setModalVersionId(versionId);
    setModalGame(game);
  };

  if (unconfiguredRef.current) {
    return <div style={{ padding: 48 }}><Empty description="The arcade isn't set up on this server yet." /></div>;
  }

  // "all" is the Mods & Hacks DEFAULT, and picking it explicitly leaves ?variant=all in the URL — so
  // treating any variant value as a filter made an unfiltered lobby report "No games match those
  // filters", which reads as though something had been filtered away when nothing had.
  const anyFilter = filters.system || filters.hideRegions || filters.maxPlayers
    || (filters.variant && filters.variant !== "all") || filters.genre || filters.search || filters.ra;

  return (
    <div className="arcade-page">
      <div className="arcade-page__inner">
        <header className="arcade-header">
          <div className="arcade-header__lede">
            <h1 className="arcade-title">Arcade</h1>
            <p className="arcade-subtitle">Pick a game to open a room, then send friends the link to play together.</p>
          </div>

          {/* Compact toolbar: the two things you open (saves, trophies) + a ⚙ that reveals the room-quality
              controls. On mobile the pills collapse behind ⚙ so the games sit near the top; on desktop
              they're always shown and ⚙ is hidden. Quality applies only to rooms YOU start (one encoder
              per room = the creator's pick is what everyone gets). */}
          <div className="arcade-toolbar">
            <Button className="arcade-tool-btn" onClick={() => setSavesVaultOpen(true)}>💾 Saves</Button>
            <Button className="arcade-tool-btn" onClick={() => setRaOpen(true)}>🏆 Trophies</Button>
            <Button
              className={"arcade-tool-btn arcade-options-toggle" + (optionsOpen ? " is-open" : "")}
              aria-expanded={optionsOpen}
              onClick={() => setOptionsOpen((o) => !o)}
            >
              ⚙ Quality
            </Button>
            <div className={"arcade-conn" + (optionsOpen ? " arcade-conn--open" : "")}>
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
                  onChange={(v) => setQ({ network: v })}
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
                  onChange={(v) => setQ({ codec: v })}
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
        </header>

        <ConsoleCarousel
          systems={facets?.systems}
          selected={selectedSystems}
          onToggle={onToggleSystem}
          onClear={() => setSystems([])}
        />

        <RecentlyPlayed rows={recentGames} onOpen={openGame} />

        <LiveRooms rooms={rooms} onJoin={joinRoom} />

        <section className="arcade-section" ref={sectionRef}>
          <div className="arcade-section__head arcade-section__head--games">
            <h2 className="arcade-section__title">Games</h2>
            <span className="arcade-section__count">
              {total.toLocaleString()} {anyFilter ? (total === 1 ? "match" : "matches") : (total === 1 ? "title" : "titles")}
            </span>
          </div>

          {games === null ? (
            <div className="arcade-loading"><Spin size="large" /></div>
          ) : games.length === 0 ? (
            /* Never claim "nothing matched" while a request is still out, or when one failed. A wide
               filter change — above all clearing the LAST console, which puts the whole catalog back in
               scope — is the slowest query the lobby can ask for, and an empty grid that explains itself
               as a filter result is indistinguishable from a real one. */
            loading ? <div className="arcade-loading"><Spin size="large" /></div>
              : loadError ? (
                <Empty description="Couldn't load the games list.">
                  <Button onClick={() => loadPage(startIndex, true)}>Try again</Button>
                </Empty>
              ) : <Empty description={anyFilter ? "No games match those filters." : "No games here yet."} />
          ) : (
            <>
              <div ref={hostRef}>
                {padTop > 0 && <div className="grid-spacer" style={{ height: padTop }} aria-hidden="true" />}
                <div className="arcade-grid" ref={gridRef}>
                  {visibleGames.map((game) => (
                    <GameCard key={game.key} game={game} onOpen={openGame} />
                  ))}
                </div>
                {padBottom > 0 && <div className="grid-spacer" style={{ height: padBottom }} aria-hidden="true" />}
              </div>
              <div ref={sentinelRef} aria-hidden="true" style={SENTINEL_STYLE} />
              <div className="arcade-more">
                {loadingMore ? <Spin /> : hasMore ? (
                  <Button onClick={loadMore}>Load more</Button>
                ) : (
                  <Text type="secondary">— that's all {total.toLocaleString()} —</Text>
                )}
              </div>
              {/* Letters when sorted A–Z, page numbers under any other sort. Both seek into the same
                  continuous list; the active button follows the grid as you scroll. */}
              <ArcadePager
                mode={filters.sort ? "pages" : "letters"}
                letters={letters}
                total={total}
                pageSize={PAGE_SIZE}
                currentIndex={startIndex + visibleStart}
                onJump={jumpTo}
                disabled={loading}
              />
            </>
          )}
        </section>
      </div>

      {modalGame && (
        <GameModal
          game={modalGame}
          creating={creating}
          canEditMovies={userData?.canEditMovies}
          renderers={renderers[modalGame.system] || []}
          initialVersionId={modalVersionId}
          onClose={() => setModalGame(null)}
          // Both actions leave the browse tile: close the game modal first so the follow-on surface
          // (the Continue/New-game confirm, or the saves manager) isn't stranded behind it at a lower
          // z-index. This restores the exact pre-modal flow those surfaces were built for.
          onStart={(versionId, title, cheats, hwContext, controllerScheme, renderProfile, competitive) => {
            // Grab the system BEFORE clearing the modal — the start-choice prompt needs it to know
            // whether this core even has save-states to offer (psp/scummvm don't).
            const sys = modalGame?.system;
            setModalGame(null);
            createRoom(versionId, title, cheats, hwContext, controllerScheme, renderProfile, competitive, sys);
          }}
          onManageSaves={(gameId, title) => { setModalGame(null); setManageSaves({ gameId, title }); }}
        />
      )}
      {manageSaves && (
        <SavesManager game={manageSaves} onClose={() => setManageSaves(null)} onResume={doCreateRoom} />
      )}
      {savesVaultOpen && (
        <SavesVaultManager onClose={() => setSavesVaultOpen(false)} onResume={doCreateRoom} />
      )}
      <RetroAchievementsModal open={raOpen} onClose={() => setRaOpen(false)} />
      {heavyGame && (
        <HeavyGameModal
          game={heavyGame}
          onClose={() => setHeavyGame(null)}
          onPlayInBrowser={(versionId) => { setHeavyGame(null); doCreateRoom(versionId, {}); }}
        />
      )}
    </div>
  );
}
