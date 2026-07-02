import { useEffect, useMemo, useRef, useState } from "react";
import { useHistory } from "react-router-dom";
import { Card, Button, Tag, Space, Spin, Empty, Select, Typography, message } from "antd";
import { MovieAPI } from "../../MovieAPI";

const { Title, Text } = Typography;

// Friendly labels for the system codes the catalog stores.
const SYSTEM_LABEL = {
  nes: "NES", snes: "SNES", genesis: "Genesis", gb: "Game Boy", gbc: "Game Boy Color",
  gba: "Game Boy Advance", n64: "Nintendo 64", ps1: "PlayStation", arcade: "Arcade",
};
const systemLabel = (s) => SYSTEM_LABEL[s] || (s ? s.toUpperCase() : "");

/**
 * The /arcade lobby (docs/arcade-plan.md §7): a grid of playable games plus a "live rooms" rail of
 * what friends are playing right now. Creating a game opens a room and jumps to the player as its
 * creator; joining a live room jumps in as a player. Password-gated + age-gated server-side.
 */
export default function ArcadePage() {
  const history = useHistory();
  const [games, setGames] = useState(null); // null = loading
  const [rooms, setRooms] = useState([]);
  const [system, setSystem] = useState("all");
  const [creating, setCreating] = useState(0); // gameId in flight
  const unconfiguredRef = useRef(false);

  useEffect(() => {
    let alive = true;
    MovieAPI.getArcadeGames()
      .then((r) => {
        if (r.status === 501) { unconfiguredRef.current = true; return []; }
        return r.ok ? r.json() : [];
      })
      .then((g) => { if (alive) setGames(g); })
      .catch(() => { if (alive) setGames([]); });
    return () => { alive = false; };
  }, []);

  // Poll the live-rooms rail every 12 s, like the channel rail.
  useEffect(() => {
    let alive = true;
    const load = () =>
      MovieAPI.getArcadeRooms()
        .then((r) => (r.ok ? r.json() : []))
        .then((rs) => { if (alive) setRooms(rs); })
        .catch(() => {});
    load();
    const id = setInterval(load, 12000);
    return () => { alive = false; clearInterval(id); };
  }, []);

  const systems = useMemo(() => {
    const set = new Set((games || []).map((g) => g.system));
    return ["all", ...Array.from(set)];
  }, [games]);

  const visible = useMemo(
    () => (games || []).filter((g) => system === "all" || g.system === system),
    [games, system]
  );

  function createRoom(game) {
    if (creating) return;
    setCreating(game.id);
    MovieAPI.createArcadeRoom(game.id)
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

  if (games === null) {
    return <div style={{ display: "flex", justifyContent: "center", padding: "80px 0" }}><Spin size="large" /></div>;
  }

  if (unconfiguredRef.current) {
    return <div style={{ padding: 48 }}><Empty description="The arcade isn't set up on this server yet." /></div>;
  }

  return (
    <div className="arcade-page" style={{ padding: "24px 32px", maxWidth: 1280, margin: "0 auto" }}>
      <Title level={2} style={{ marginBottom: 4 }}>Arcade</Title>
      <Text type="secondary">Pick a game to open a room, then send friends the link to play together.</Text>

      {rooms.length > 0 && (
        <div style={{ margin: "24px 0" }}>
          <Title level={4}>Live rooms</Title>
          <Space wrap size={[12, 12]}>
            {rooms.map((room) => (
              <Card key={room.roomCode} size="small" hoverable style={{ width: 240 }} onClick={() => history.push(`/arcade/room/${room.roomCode}`)}>
                <Space direction="vertical" size={2} style={{ width: "100%" }}>
                  <Text strong>{room.game.title}</Text>
                  <Tag color="blue">{systemLabel(room.game.system)}</Tag>
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

      <div style={{ margin: "24px 0 12px", display: "flex", alignItems: "center", gap: 12 }}>
        <Title level={4} style={{ margin: 0 }}>Games</Title>
        <Select
          value={system}
          onChange={setSystem}
          style={{ width: 200 }}
          options={systems.map((s) => ({ value: s, label: s === "all" ? "All systems" : systemLabel(s) }))}
        />
      </div>

      {visible.length === 0 ? (
        <Empty description="No games here yet." />
      ) : (
        <Space wrap size={[16, 16]}>
          {visible.map((game) => (
            <Card
              key={game.id}
              hoverable
              style={{ width: 200 }}
              cover={<GameCover game={game} />}
              onClick={() => createRoom(game)}
            >
              <Card.Meta
                title={game.title}
                description={
                  <Space size={4} wrap>
                    <Tag color="blue">{systemLabel(game.system)}</Tag>
                    {game.maxPlayers > 1 && <Tag>{game.maxPlayers}P</Tag>}
                    {game.year && <Text type="secondary" style={{ fontSize: 12 }}>{game.year}</Text>}
                  </Space>
                }
              />
              <Button type="primary" block style={{ marginTop: 12 }} loading={creating === game.id}>
                Start room
              </Button>
            </Card>
          ))}
        </Space>
      )}
    </div>
  );
}

// Box art via /ArcadeImage/{id} (Phase 4 fills it); until then, a labeled placeholder.
function GameCover({ game }) {
  const [broken, setBroken] = useState(!game.hasBoxArt);
  const height = 150;
  if (broken) {
    return (
      <div style={{ height, display: "flex", alignItems: "center", justifyContent: "center", background: "#1f1f1f", color: "#888", padding: 8, textAlign: "center" }}>
        <span style={{ fontSize: 13 }}>{game.title}</span>
      </div>
    );
  }
  return (
    <img
      src={`/ArcadeImage/${game.id}`}
      alt={game.title}
      style={{ height, width: "100%", objectFit: "cover" }}
      onError={() => setBroken(true)}
    />
  );
}
