import { useEffect, useMemo, useRef, useState, useCallback } from "react";
import { useHistory, useLocation } from "react-router-dom";
import { Card, Button, Tag, Space, Spin, Empty, Typography, message, Select, Modal } from "antd";
import { MovieAPI } from "../../MovieAPI";
import "./ArcadePage.css";

const { Title, Text } = Typography;
const PAGE_SIZE = 60;

const SYSTEM_LABEL = {
  nes: "NES", snes: "SNES", genesis: "Genesis", gb: "Game Boy", gbc: "Game Boy Color",
  gba: "Game Boy Advance", n64: "Nintendo 64", ps1: "PlayStation", arcade: "Arcade",
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
  "nes", "snes", "genesis", "gb", "gbc", "gba", "n64", "ps1",
  "psp", "dc", "sms", "gg", "sg1000", "segacd", "sega32x", "pce", "ngpc", "wsc",
  "a2600", "a7800", "lynx", "vb", "fds",
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
  const unconfiguredRef = useRef(false);

  // The active filters, from the URL (set by the navbar panel).
  const filters = useMemo(() => {
    const p = new URLSearchParams(location.search);
    return {
      system: p.get("system") || "",
      region: p.get("region") || "",
      maxPlayers: p.get("players") || "",
      variant: p.get("variant") || "",
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

  function createRoom(versionId) {
    if (creating || !versionId) return;
    setCreating(versionId);
    // Durable saves (arcade-saves-plan): if this user has a save for the game, offer Continue vs New game.
    MovieAPI.listArcadeSaves(versionId)
      .then((saves) => {
        const hasSave = Array.isArray(saves) && saves.length > 0;
        if (!hasSave) return doCreateRoom(versionId, false);
        Modal.confirm({
          title: "Resume your saved game?",
          content: "You have saved progress for this game. Continue where you left off, or start a new game.",
          okText: "Continue",
          cancelText: "New game",
          onOk: () => doCreateRoom(versionId, false),
          onCancel: () => doCreateRoom(versionId, true),
        });
      })
      .catch(() => doCreateRoom(versionId, false));
  }

  function doCreateRoom(versionId, newGame) {
    return MovieAPI.createArcadeRoom(versionId, newGame)
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
              <GameCard key={game.key} game={game} onStart={createRoom} creating={creating} />
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
    </div>
  );
}

// One card per game (docs/arcade-dedupe-multidisc-plan.md). A version dropdown picks which ROM launches
// (region / revision / edition / disc / hack); the tags track the selection. Multiple versions collapse
// here so the grid shows each game once. Box art is shared per game via the representative `artId`.
function GameCard({ game, onStart, creating }) {
  const [sel, setSel] = useState(game.versions?.[0]?.id);
  // When the filters change the default version (e.g. you filtered a region), reset the selection.
  useEffect(() => { setSel(game.versions?.[0]?.id); }, [game.versions?.[0]?.id]);
  const version = game.versions?.find((v) => v.id === sel) || game.versions?.[0];
  return (
    <Card hoverable style={{ width: 200 }} cover={<GameCover game={game} />} onClick={() => onStart(sel)}>
      <Card.Meta
        title={game.title}
        description={
          <Space size={4} wrap>
            <Tag color="purple">{systemLabel(game.system)}</Tag>
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
        onClick={(e) => { e.stopPropagation(); onStart(sel); }}>
        Start room
      </Button>
    </Card>
  );
}

// Box art via /ArcadeImage/{artId}; until it's populated, a labeled placeholder. The art keeps its
// own aspect (`contain`, never `cover` — cover cropped the title off) and a blurred copy of the same
// image fills the frame behind it, so varied box aspect ratios don't sit in black bars. See ArcadePage.css.
function GameCover({ game }) {
  const [broken, setBroken] = useState(!(game.hasBoxArt || ART_SYSTEMS.has(game.system)));
  if (broken) {
    return (
      <div className="arcade-cover">
        <div className="arcade-cover__placeholder">{game.title}</div>
      </div>
    );
  }
  const src = `/ArcadeImage/${game.artId}`;
  return (
    <div className="arcade-cover">
      <img className="arcade-cover__bg" src={src} alt="" aria-hidden="true" loading="lazy" decoding="async" />
      <img className="arcade-cover__img" src={src} alt={game.title} loading="lazy" decoding="async"
        onError={() => setBroken(true)} />
    </div>
  );
}
