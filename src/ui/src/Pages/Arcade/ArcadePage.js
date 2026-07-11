import { useEffect, useMemo, useRef, useState, useCallback } from "react";
import { useHistory, useLocation } from "react-router-dom";
import { Button, Empty, Modal, Select, Spin, Typography, message } from "antd";
import { MovieAPI } from "../../MovieAPI";
import GameCard from "./GameCard";
import HeavyGameModal from "./HeavyGameModal";
import LiveRooms from "./LiveRooms";
import SavesManager from "./SavesManager";
import "./ArcadePage.css";

const { Text } = Typography;
const PAGE_SIZE = 60;

// Per-room stream quality the creator picks (arcade per-room bitrate/FEC). Persisted so a friend group
// keeps its setting across sessions; applied to every room YOU start (one encoder per room = creator's
// choice). Lower bitrate = smaller video bursts = smoother audio + less upstream for remote players.
const QUALITY_KEY = "arcade.streamQuality";
const BITRATE_PRESETS = [
  // 0 = Auto: the SERVER picks from the game's system (CloudRetroHost.DefaultVideoBitrateKbps). Encoded
  // resolution varies ~4.6x across systems — a 912×672 arcade board and a 1280×1056 GameCube frame both
  // got a flat 5 Mbps, which starves the big ones (~0.06 bits/pixel/frame). Auto is a CEILING between
  // 5 and 14 Mbps; the worker's ABR (patch 0021) backs off from it within a second when a player's link
  // can't carry it, so picking a number here is now mostly about capping your own upstream.
  { label: "Auto · match the system", value: 0 },
  // Manual override for a mostly-LAN session on a fat pipe. ABR still walks it back for remote players.
  { label: "LAN · 16 Mbps", value: 16000 },
  // 10 Mbps, best for hi-res 3D cores (GameCube 1280×1056, PS2 upscaled) on a fat pipe. At
  // 4 remote players that's ~40 Mbps upstream, so it's really a post-FiOS / mostly-LAN setting; on
  // cable uplinks prefer 5 or lower. Overkill (but harmless) for retro/2D. Worker clamps 500–20000.
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
export default function ArcadePage() {
  const history = useHistory();
  const location = useLocation();

  const [games, setGames] = useState(null); // null = first load
  const [total, setTotal] = useState(0);
  const [page, setPage] = useState(1);
  const [loading, setLoading] = useState(false);
  const [rooms, setRooms] = useState([]);
  const [creating, setCreating] = useState(0);
  const [manageSaves, setManageSaves] = useState(null); // { gameId, title } for the My Saves modal
  const [heavyGame, setHeavyGame] = useState(null); // heavy-lane card → the Play-via-Moonlight modal
  const [quality, setQuality] = useState(loadQuality); // creator's per-room stream quality (persisted)
  const unconfiguredRef = useRef(false);

  const setQ = (patch) => setQuality((prev) => { const next = { ...prev, ...patch }; saveQuality(next); return next; });

  // The active filters, from the URL (set by the navbar panel).
  const filters = useMemo(() => {
    const p = new URLSearchParams(location.search);
    return {
      system: p.get("system") || "",
      region: p.get("region") || "",
      maxPlayers: p.get("players") || "",
      variant: p.get("variant") || "",
      genre: p.get("genre") || "",
      sort: p.get("sort") || "",
      search: p.get("q") || "",
    };
  }, [location.search]);
  const filterKey = JSON.stringify(filters);

  const fetchPage = useCallback((pageNum, replace) => {
    setLoading(true);
    MovieAPI.getArcadeGames({ ...filters, page: pageNum, pageSize: PAGE_SIZE })
      .then((r) => {
        if (r.status === 501) { unconfiguredRef.current = true; return null; }
        return r.ok ? r.json() : null;
      })
      .then((data) => {
        if (!data) { setGames((g) => g || []); return; }
        setTotal(data.totalCount);
        setPage(data.page);
        setGames((prev) => (replace || !prev ? data.games : [...prev, ...data.games]));
      })
      .catch(() => setGames((g) => g || []))
      .finally(() => setLoading(false));
  }, [filters]);

  // Reset + fetch page 1 whenever the filters change.
  useEffect(() => { setGames(null); setPage(1); fetchPage(1, true); /* eslint-disable-next-line */ }, [filterKey]);

  // Live-rooms strip, polled every 12 s.
  useEffect(() => {
    let alive = true;
    const load = () => MovieAPI.getArcadeRooms().then((r) => (r.ok ? r.json() : [])).then((rs) => { if (alive) setRooms(rs); }).catch(() => {});
    load();
    const id = setInterval(load, 12000);
    return () => { alive = false; clearInterval(id); };
  }, []);

  // Infinite scroll: near the bottom, pull the next page (direct scroll-position check — the pattern
  // that survives in this app, per the browse-infinite-scroll notes).
  const hasMore = games != null && games.length < total;
  useEffect(() => {
    if (!hasMore) return undefined;
    const onScroll = () => {
      if (loading) return;
      const nearBottom = window.innerHeight + window.scrollY >= document.body.offsetHeight - 600;
      if (nearBottom) fetchPage(page + 1, false);
    };
    window.addEventListener("scroll", onScroll, { passive: true });
    return () => window.removeEventListener("scroll", onScroll);
  }, [hasMore, loading, page, fetchPage]);

  // `cheats` are the ids the creator ticked on the card (arcade cheats feature). They ride every path out
  // of this modal — Continue, New game, and a snapshot resume all launch the same room.
  function createRoom(versionId, title, cheats = []) {
    if (creating || !versionId) return;
    setCreating(versionId);
    // Durable saves (arcade-saves-plan): if this user has a save/snapshots for the game, offer Continue,
    // any named snapshot, or New game.
    MovieAPI.listArcadeSaves(versionId)
      .then((saves) => {
        const rows = Array.isArray(saves) ? saves : [];
        if (rows.length === 0) return doCreateRoom(versionId, { cheats });
        const snaps = rows.filter((s) => s.slotId >= 1 && s.kind === "state")
          .sort((a, b) => a.slotId - b.slotId);
        const modal = Modal.confirm({
          title: "Resume your saved game?",
          okText: "Continue (latest)",
          cancelText: "New game",
          onOk: () => doCreateRoom(versionId, { cheats }),
          onCancel: () => doCreateRoom(versionId, { newGame: true, cheats }),
          content: (
            <div>
              <div style={{ marginBottom: snaps.length ? 8 : 0 }}>
                Continue where you left off, or start a new game.
              </div>
              {snaps.length > 0 && (
                <div>
                  <Text type="secondary" style={{ fontSize: 12 }}>Or resume a snapshot:</Text>
                  <div style={{ marginTop: 4, maxHeight: 180, overflowY: "auto" }}>
                    {snaps.map((s) => (
                      <div key={s.slotId} style={{ padding: "2px 0" }}>
                        <a onClick={() => { modal.destroy(); doCreateRoom(versionId, { seedSlot: s.slotId, cheats }); }}>
                          ▶ {s.label || `Snapshot ${s.slotId}`}
                        </a>
                        <Text type="secondary" style={{ fontSize: 11, marginLeft: 8 }}>
                          {s.updatedUtc ? new Date(s.updatedUtc).toLocaleDateString() : ""}
                        </Text>
                      </div>
                    ))}
                  </div>
                </div>
              )}
              <div style={{ marginTop: 10 }}>
                <a onClick={() => { modal.destroy(); setCreating(0); setManageSaves({ gameId: versionId, title }); }}>
                  ⚙ Manage my saves…
                </a>
              </div>
            </div>
          ),
        });
      })
      .catch(() => doCreateRoom(versionId, { cheats }));
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

  if (unconfiguredRef.current) {
    return <div style={{ padding: 48 }}><Empty description="The arcade isn't set up on this server yet." /></div>;
  }

  const anyFilter = filters.system || filters.region || filters.maxPlayers || filters.variant || filters.genre || filters.search;

  return (
    <div className="arcade-page">
      <div className="arcade-page__inner">
        <header className="arcade-header">
          <div className="arcade-header__lede">
            <h1 className="arcade-title">Arcade</h1>
            <p className="arcade-subtitle">Pick a game to open a room, then send friends the link to play together.</p>
          </div>

          {/* Stream quality for rooms YOU start. One encoder per room, so the creator's choice is what
              everyone in the room gets. Lower bitrate = smoother audio + less upstream. */}
          <div className="arcade-conn">
            <div className="arcade-conn__pills">
              <div className="arcade-pill">
                <span className="arcade-dot-ok" />
                <Select
                  bordered={false} value={quality.videoBitrateKbps} options={BITRATE_PRESETS}
                  onChange={(v) => setQ({ videoBitrateKbps: v })}
                  popupClassName="arcade-pill-dropdown" dropdownMatchSelectWidth={false}
                  aria-label="Stream bitrate"
                />
              </div>
              <div className="arcade-pill">
                <Select
                  bordered={false} value={quality.network} optionLabelProp="label"
                  onChange={(v) => setQ({ network: v })}
                  popupClassName="arcade-pill-dropdown" dropdownMatchSelectWidth={false}
                  aria-label="Network profile"
                >
                  {NETWORK_OPTIONS.map((o) => (
                    <Select.Option key={o.value} value={o.value} label={o.short}>{o.label}</Select.Option>
                  ))}
                </Select>
              </div>
              <div className="arcade-pill">
                <Select
                  bordered={false} value={quality.codec || "av1"} optionLabelProp="label"
                  onChange={(v) => setQ({ codec: v })}
                  popupClassName="arcade-pill-dropdown" dropdownMatchSelectWidth={false}
                  aria-label="Video codec"
                >
                  {CODEC_OPTIONS.map((o) => (
                    <Select.Option key={o.value} value={o.value} label={o.short}>{o.label}</Select.Option>
                  ))}
                </Select>
              </div>
            </div>
            <span className="arcade-conn__caption">Applies to rooms you start</span>
          </div>
        </header>

        <LiveRooms rooms={rooms} onJoin={joinRoom} />

        <section className="arcade-section">
          <div className="arcade-section__head arcade-section__head--games">
            <h2 className="arcade-section__title">Games</h2>
            <span className="arcade-section__count">
              {total.toLocaleString()} {anyFilter ? (total === 1 ? "match" : "matches") : (total === 1 ? "title" : "titles")}
            </span>
          </div>

          {games === null ? (
            <div className="arcade-loading"><Spin size="large" /></div>
          ) : games.length === 0 ? (
            <Empty description={anyFilter ? "No games match those filters." : "No games here yet."} />
          ) : (
            <>
              <div className="arcade-grid">
                {games.map((game) => (
                  <GameCard key={game.key} game={game} onStart={createRoom} creating={creating}
                    onHeavy={setHeavyGame}
                    onManageSaves={(id) => setManageSaves({ gameId: id, title: game.title })} />
                ))}
              </div>
              <div className="arcade-more">
                {loading ? <Spin /> : hasMore ? (
                  <Button onClick={() => fetchPage(page + 1, false)}>Load more</Button>
                ) : (
                  <Text type="secondary">— that's all {total.toLocaleString()} —</Text>
                )}
              </div>
            </>
          )}
        </section>
      </div>

      {manageSaves && (
        <SavesManager game={manageSaves} onClose={() => setManageSaves(null)} onResume={doCreateRoom} />
      )}
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
