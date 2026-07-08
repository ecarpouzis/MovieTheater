import { useEffect, useMemo, useRef, useState, useCallback } from "react";
import { useHistory, useLocation } from "react-router-dom";
import { Card, Button, Tag, Space, Spin, Empty, Typography, message, Select, Modal } from "antd";
import { MovieAPI } from "../../MovieAPI";
import "./ArcadePage.css";

const { Title, Text } = Typography;
const PAGE_SIZE = 60;

// Per-room stream quality the creator picks (arcade per-room bitrate/FEC). Persisted so a friend group
// keeps its setting across sessions; applied to every room YOU start (one encoder per room = creator's
// choice). Lower bitrate = smaller video bursts = smoother audio + less upstream for remote players.
const QUALITY_KEY = "arcade.streamQuality";
const BITRATE_PRESETS = [
  // 10 Mbps = "max", best for hi-res 3D cores (GameCube 1280×1056, PS2 upscaled) on a fat pipe. At
  // 4 remote players that's ~40 Mbps upstream, so it's really a post-FiOS / mostly-LAN setting; on
  // cable uplinks prefer 5 or lower. Overkill (but harmless) for retro/2D. Worker clamps 500–20000.
  { label: "Max · 10 Mbps", value: 10000 },
  { label: "Sharp · 8 Mbps", value: 8000 },
  { label: "Balanced · 5 Mbps", value: 5000 },
  { label: "Smooth · 3 Mbps", value: 3000 },
  { label: "Data saver · 1.5 Mbps", value: 1500 },
];
const FEC_OPTIONS = [
  { label: "Error correction: On · best with remote friends", value: 1 },
  { label: "Error correction: Off · LAN only, lighter audio", value: 2 },
];
function loadQuality() {
  try {
    const q = JSON.parse(localStorage.getItem(QUALITY_KEY));
    if (q && typeof q.videoBitrateKbps === "number")
      return { videoBitrateKbps: q.videoBitrateKbps, audioFec: q.audioFec === 2 ? 2 : 1 };
  } catch { /* ignore */ }
  return { videoBitrateKbps: 5000, audioFec: 1 }; // Balanced + FEC on (mixed local+remote default)
}
function saveQuality(q) { try { localStorage.setItem(QUALITY_KEY, JSON.stringify(q)); } catch { /* ignore */ } }

const SYSTEM_LABEL = {
  nes: "NES", snes: "SNES", genesis: "Genesis", gb: "Game Boy", gbc: "Game Boy Color",
  gba: "Game Boy Advance", n64: "Nintendo 64", gc: "GameCube", ps1: "PlayStation", arcade: "Arcade",
  psp: "PSP", dc: "Dreamcast", naomi: "Naomi", atomiswave: "Atomiswave",
  sms: "Master System", gg: "Game Gear", sg1000: "SG-1000", segacd: "Sega CD",
  sega32x: "32X", pce: "TurboGrafx-16", ngpc: "Neo Geo Pocket", wsc: "WonderSwan Color",
  a2600: "Atari 2600", a7800: "Atari 7800", lynx: "Atari Lynx", vb: "Virtual Boy",
  fds: "Famicom Disk System", neogeo: "Neo Geo",
};
const systemLabel = (s) => SYSTEM_LABEL[s] || (s ? s.toUpperCase() : "");

// Systems the box-art route can source (libretro-thumbnails) — so a card requests /ArcadeImage even before
// its art is cached (the route lazily fetches on first view). Arcade/naomi/atomiswave/neogeo are skipped
// (arcade-named art won't match → don't 404 those cards). Mirror of ArcadeBoxArt.ThumbRepo keys.
const ART_SYSTEMS = new Set([
  "nes", "snes", "genesis", "gb", "gbc", "gba", "n64", "gc", "ps1", "ps2",
  "psp", "dc", "sms", "gg", "sg1000", "segacd", "sega32x", "pce", "ngpc", "wsc",
  "a2600", "a7800", "lynx", "vb", "fds",
  // arcade/neogeo now resolve real titles → art via libretro (neogeo) or IGDB cover (arcade).
  "arcade", "neogeo",
]);

/**
 * The /arcade lobby (docs/arcade-plan.md §7). Over ~12,500 games, this is SERVER-SIDE filtered + paged:
 * the filter controls live in the navbar (ArcadeNavContent) as URL params, and this page fetches the
 * matching page and appends more on scroll. A "live rooms" rail shows what friends are playing now.
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

  // Live-rooms rail, polled every 12 s.
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

  function createRoom(versionId, title) {
    if (creating || !versionId) return;
    setCreating(versionId);
    // Durable saves (arcade-saves-plan): if this user has a save/snapshots for the game, offer Continue,
    // any named snapshot, or New game.
    MovieAPI.listArcadeSaves(versionId)
      .then((saves) => {
        const rows = Array.isArray(saves) ? saves : [];
        if (rows.length === 0) return doCreateRoom(versionId, {});
        const snaps = rows.filter((s) => s.slotId >= 1 && s.kind === "state")
          .sort((a, b) => a.slotId - b.slotId);
        const modal = Modal.confirm({
          title: "Resume your saved game?",
          okText: "Continue (latest)",
          cancelText: "New game",
          onOk: () => doCreateRoom(versionId, {}),
          onCancel: () => doCreateRoom(versionId, { newGame: true }),
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
                        <a onClick={() => { modal.destroy(); doCreateRoom(versionId, { seedSlot: s.slotId }); }}>
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
      .catch(() => doCreateRoom(versionId, {}));
  }

  function doCreateRoom(versionId, opts) {
    // Merge the creator's current stream quality (read fresh from storage so a mid-modal change wins).
    return MovieAPI.createArcadeRoom(versionId, { ...opts, ...loadQuality() })
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

  if (unconfiguredRef.current) {
    return <div style={{ padding: 48 }}><Empty description="The arcade isn't set up on this server yet." /></div>;
  }
  if (games === null) {
    return <div style={{ display: "flex", justifyContent: "center", padding: "80px 0" }}><Spin size="large" /></div>;
  }

  const anyFilter = filters.system || filters.region || filters.maxPlayers || filters.variant || filters.search;

  return (
    <div className="arcade-page" style={{ padding: "24px 32px", maxWidth: 1320, margin: "0 auto" }}>
      <Title level={2} style={{ marginBottom: 4 }}>Arcade</Title>
      <Text type="secondary">Pick a game to open a room, then send friends the link to play together. Filter by system, region, players, or hide mods in the sidebar.</Text>

      {/* Stream quality for rooms YOU start (arcade per-room bitrate/FEC). One encoder per room, so the
          creator's choice is what everyone in the room gets. Lower bitrate = smoother audio + less upstream. */}
      <div style={{ margin: "14px 0 4px", display: "flex", alignItems: "center", gap: 14, flexWrap: "wrap" }}>
        <Select
          size="small" value={quality.videoBitrateKbps} style={{ width: 180 }}
          onChange={(v) => setQ({ videoBitrateKbps: v })} options={BITRATE_PRESETS}
        />
        <Select
          size="small" value={quality.audioFec} style={{ width: 300 }}
          onChange={(v) => setQ({ audioFec: v })} options={FEC_OPTIONS}
        />
        <Text type="secondary" style={{ fontSize: 12 }}>Applies to rooms you start.</Text>
      </div>

      {rooms.length > 0 && (
        <div style={{ margin: "24px 0" }}>
          <Title level={4}>Live rooms</Title>
          <Space wrap size={[12, 12]}>
            {rooms.map((room) => (
              <Card key={room.roomCode} size="small" hoverable style={{ width: 240 }} onClick={() => history.push(`/arcade/room/${room.roomCode}`)}>
                <Space direction="vertical" size={2} style={{ width: "100%" }}>
                  <Text strong>{room.game.title}</Text>
                  <Tag color="purple">{systemLabel(room.game.system)}</Tag>
                  <Text type="secondary" style={{ fontSize: 12 }}>
                    {room.players.length} playing{room.starting ? " · starting…" : ` · ${room.seatsFree} seat${room.seatsFree === 1 ? "" : "s"} free`}
                  </Text>
                  <Text type="secondary" style={{ fontSize: 12 }} ellipsis>{room.players.join(", ")}</Text>
                </Space>
              </Card>
            ))}
          </Space>
        </div>
      )}

      <div style={{ margin: "24px 0 12px", display: "flex", alignItems: "baseline", gap: 12 }}>
        <Title level={4} style={{ margin: 0 }}>Games</Title>
        <Text type="secondary">{total.toLocaleString()} {anyFilter ? "match" : "game" + (total === 1 ? "" : "s")}{anyFilter ? (total === 1 ? "" : "es") : ""}</Text>
      </div>

      {games.length === 0 ? (
        <Empty description={anyFilter ? "No games match those filters." : "No games here yet."} />
      ) : (
        <>
          <Space wrap size={[16, 16]} align="start">
            {games.map((game) => (
              <GameCard key={game.key} game={game} onStart={createRoom} creating={creating}
                onManageSaves={(id) => setManageSaves({ gameId: id, title: game.title })} />
            ))}
          </Space>
          <div style={{ textAlign: "center", padding: "28px 0 8px" }}>
            {loading ? <Spin /> : hasMore ? (
              <Button onClick={() => fetchPage(page + 1, false)}>Load more</Button>
            ) : (
              <Text type="secondary">— that's all {total.toLocaleString()} —</Text>
            )}
          </div>
        </>
      )}

      {manageSaves && (
        <SavesManager game={manageSaves} onClose={() => setManageSaves(null)} onResume={doCreateRoom} />
      )}
    </div>
  );
}

// My Saves manager (arcade-saves-plan S3): list a game's saves for the signed-in user with rename,
// delete, download (export), and upload (import). Stateful — refetches after each mutation.
function SavesManager({ game, onClose, onResume }) {
  const [rows, setRows] = useState(null);
  const [busy, setBusy] = useState(false);
  const fileRef = useRef(null);

  const refresh = useCallback(() => {
    MovieAPI.listArcadeSaves(game.gameId).then((s) => setRows(Array.isArray(s) ? s : []));
  }, [game.gameId]);
  useEffect(() => { refresh(); }, [refresh]);

  function fmt(r) {
    if (r.slotId === 0) return r.kind === "sram" ? "Cartridge / card" : "Continue (latest)";
    return r.label || `Snapshot ${r.slotId}`;
  }

  async function rename(r) {
    const label = window.prompt("Rename save:", r.label || "");
    if (label === null) return;
    setBusy(true);
    try {
      const res = await MovieAPI.renameArcadeSave(r.id, label.trim());
      if (res.ok) refresh(); else message.error("Couldn't rename that save.");
    } finally { setBusy(false); }
  }

  function remove(r) {
    Modal.confirm({
      title: `Delete "${fmt(r)}"?`,
      okText: "Delete", okButtonProps: { danger: true },
      onOk: async () => {
        const res = await MovieAPI.deleteArcadeSave(r.id);
        if (res.ok) refresh(); else message.error("Couldn't delete that save.");
      },
    });
  }

  async function onImport(e) {
    const file = e.target.files?.[0];
    e.target.value = "";
    if (!file) return;
    // .dat = save state, .srm = SRAM/card. Default to state; .srm → sram.
    const kind = /\.srm$/i.test(file.name) ? "sram" : "state";
    setBusy(true);
    try {
      const res = await MovieAPI.importArcadeSave(game.gameId, file, { kind, label: file.name });
      if (res.ok) { message.success("Save imported."); refresh(); }
      else message.error("Couldn't import that file.");
    } catch { message.error("Couldn't import that file."); }
    finally { setBusy(false); }
  }

  return (
    <Modal open title={`My saves — ${game.title}`} onCancel={onClose} footer={null} width={520}>
      {rows === null ? (
        <div style={{ textAlign: "center", padding: 24 }}><Spin /></div>
      ) : rows.length === 0 ? (
        <Empty description="No saves for this game yet." />
      ) : (
        <div style={{ maxHeight: 360, overflowY: "auto" }}>
          {rows.slice().sort((a, b) => a.slotId - b.slotId).map((r) => (
            <div key={r.id} style={{ display: "flex", alignItems: "center", gap: 8, padding: "6px 0", borderBottom: "1px solid #f0f0f0" }}>
              <div style={{ flex: 1, minWidth: 0 }}>
                <Text strong ellipsis>{fmt(r)}</Text>
                <div><Text type="secondary" style={{ fontSize: 11 }}>
                  {r.kind === "sram" ? "SRAM" : "state"} · {(r.sizeBytes / 1024).toFixed(0)} KB
                  {r.updatedUtc ? ` · ${new Date(r.updatedUtc).toLocaleString()}` : ""}
                </Text></div>
              </div>
              <Space size={4}>
                {onResume && (
                  <Button size="small" type="link" onClick={() => { onClose(); onResume(game.gameId, r.slotId >= 1 ? { seedSlot: r.slotId } : {}); }}>Resume</Button>
                )}
                <a href={MovieAPI.arcadeSaveDownloadUrl(r.id)}><Button size="small" type="link">Export</Button></a>
                <Button size="small" type="link" onClick={() => rename(r)}>Rename</Button>
                <Button size="small" type="link" danger onClick={() => remove(r)}>Delete</Button>
              </Space>
            </div>
          ))}
        </div>
      )}
      <div style={{ marginTop: 16, display: "flex", justifyContent: "space-between", alignItems: "center" }}>
        <Text type="secondary" style={{ fontSize: 12 }}>Import a .srm (card) or .dat (state) file.</Text>
        <input ref={fileRef} type="file" accept=".srm,.dat,.state" style={{ display: "none" }} onChange={onImport} />
        <Button loading={busy} onClick={() => fileRef.current?.click()}>⬆ Import save</Button>
      </div>
    </Modal>
  );
}

// IGDB score → tag colour band. Null score renders no badge (unenriched or unrated on IGDB).
const ratingColor = (r) => (r >= 78 ? "green" : r >= 62 ? "gold" : r >= 45 ? "orange" : "default");

// Compact multiplayer descriptor from IGDB game_modes: co-op vs competitive, split vs shared screen.
function multiplayerTag(gameModes) {
  const m = (gameModes || "").toLowerCase();
  const coop = m.includes("co-operative") || m.includes("co-op");
  if (!coop && !m.includes("multiplayer")) return null;
  const split = m.includes("split screen");
  return { label: (coop ? "Co-op" : "Versus") + (split ? " · Split" : ""), color: coop ? "cyan" : "volcano" };
}

// One card per game (docs/arcade-dedupe-multidisc-plan.md). A version dropdown picks which ROM launches
// (region / revision / edition / disc / hack); the tags track the selection. Multiple versions collapse
// here so the grid shows each game once. Box art is shared per game via the representative `artId`.
function GameCard({ game, onStart, onManageSaves, creating }) {
  const genre = game.genres ? game.genres.split(",")[0].trim() : null;
  const isParty = (game.themes || "").toLowerCase().includes("party");
  const mp = multiplayerTag(game.gameModes);
  const [sel, setSel] = useState(game.versions?.[0]?.id);
  // When the filters change the default version (e.g. you filtered a region), reset the selection.
  useEffect(() => { setSel(game.versions?.[0]?.id); }, [game.versions?.[0]?.id]);
  const version = game.versions?.find((v) => v.id === sel) || game.versions?.[0];
  return (
    <Card hoverable style={{ width: 200 }} cover={<GameCover game={game} />} onClick={() => onStart(sel, game.title)}>
      <Card.Meta
        title={game.title}
        description={
          <Space size={4} wrap>
            {game.rating != null && <Tag color={ratingColor(game.rating)}>★ {game.rating}</Tag>}
            <Tag color="purple">{systemLabel(game.system)}</Tag>
            {genre && <Tag color="blue">{genre}</Tag>}
            {mp && <Tag color={mp.color}>{mp.label}</Tag>}
            {isParty && <Tag color="magenta">Party</Tag>}
            {game.maxPlayers > 1 && <Tag>{game.maxPlayers}P</Tag>}
            {version?.region && version.region !== "Unknown" && <Tag>{version.region}</Tag>}
            {version?.variant && version.variant !== "Release" && <Tag color="magenta">{version.variant}</Tag>}
          </Space>
        }
      />
      {game.versionCount > 1 && (
        <div onClick={(e) => e.stopPropagation()} style={{ marginTop: 10 }}>
          <Select
            size="small" value={sel} onChange={setSel} style={{ width: "100%" }}
            getPopupContainer={(t) => t.parentElement}
            options={game.versions.map((v) => ({ value: v.id, label: v.label }))}
          />
        </div>
      )}
      <Button type="primary" block style={{ marginTop: 12 }} loading={creating === sel}
        onClick={(e) => { e.stopPropagation(); onStart(sel, game.title); }}>
        Start room
      </Button>
      <div style={{ textAlign: "center", marginTop: 6 }}>
        <a style={{ fontSize: 12 }} onClick={(e) => { e.stopPropagation(); onManageSaves?.(sel); }}>My saves</a>
      </div>
    </Card>
  );
}

// Box art via /ArcadeImage/{artId}; until it's populated, a labeled placeholder. The full box shows at
// its own aspect ratio (fills the card width, height follows the art) — never cropped, stretched, or
// letterboxed. Card heights vary by box shape, which is the honest way to show every box whole.
function GameCover({ game }) {
  const [broken, setBroken] = useState(!(game.hasBoxArt || ART_SYSTEMS.has(game.system)));
  if (broken) {
    return (
      <div className="arcade-cover">
        <div className="arcade-cover__placeholder">{game.title}</div>
      </div>
    );
  }
  return (
    <div className="arcade-cover">
      <img className="arcade-cover__img" src={`/ArcadeImage/${game.artId}`} alt={game.title}
        loading="lazy" decoding="async" onError={() => setBroken(true)} />
    </div>
  );
}
