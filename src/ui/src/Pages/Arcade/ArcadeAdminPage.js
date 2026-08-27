import { useEffect, useState } from "react";
import { Button, Input, List, Tag } from "antd";
import AdminShell from "../../admin/AdminShell";
import { AdminCard, AdminStats, NeedsAttention } from "../../admin/AdminOverview";
import { MovieAPI } from "../../MovieAPI";
import { useDebouncedCallback } from "../../hooks/useDebounce";
import ArcadeGameConfig from "./ArcadeGameConfig";
import SavesVaultManager from "./SavesVaultManager";
import RetroAchievementsModal from "./RetroAchievementsModal";

// `/arcade/admin?tab=` — the arcade's operator tools on the site's admin shell (R9 S6).
//
// All three tools are DIALOGS today (the per-game config opens from inside the game modal, the saves
// vault is a drawer, the trophy hub is a modal) and this pass does not rewrite any of them: each tab
// is the card that opens its tool. What is new is the URL and the Overview report.
//
// The per-game config has no page of its own because it is per GAME — so the tab adds the one thing
// it was missing outside the game modal: a way to pick the game. Nothing inside the config changed.

function GameConfigTab() {
  const [q, setQ] = useState("");
  const [rows, setRows] = useState(null);
  const [picked, setPicked] = useState(null);

  const search = useDebouncedCallback((needle) => {
    if (!needle.trim()) { setRows(null); return; }
    MovieAPI.getArcadeGames({ search: needle.trim(), pageSize: 20 })
      .then((r) => (r.ok ? r.json() : null))
      .then((d) => setRows(d?.games ?? []))
      .catch(() => setRows([]));
  }, 300);

  return (
    <div className="adm-tab">
      <AdminCard
        title="Per-game configuration"
        description="Core options, the graphics profile a game is pinned to, cheats and operator notes — the same panel the game modal's ⚙ opens, for whichever game you name. A game with no pin follows its system's default profile, and pinning it to today's default silently freezes it there: the two are different states on purpose."
      >
        <Input.Search
          placeholder="Find a game…"
          allowClear
          value={q}
          onChange={(e) => { setQ(e.target.value); search(e.target.value); }}
        />
        {rows && (
          <List
            size="small"
            dataSource={rows}
            locale={{ emptyText: "No game matches." }}
            renderItem={(g) => (
              <List.Item actions={[<Button key="cfg" size="small" onClick={() => setPicked(g)}>Configure</Button>]}>
                <span className="adm-att-label">{g.name}</span>
                {g.system && <Tag>{g.system}</Tag>}
                {g.region && <Tag color="blue">{g.region}</Tag>}
              </List.Item>
            )}
          />
        )}
      </AdminCard>
      {picked && <ArcadeGameConfig game={picked} onClose={() => setPicked(null)} />}
    </div>
  );
}

function SavesVaultTab() {
  const [open, setOpen] = useState(false);
  return (
    <div className="adm-tab">
      <AdminCard
        title="Saves vault"
        description="Every save state and battery save the arcade holds, across every player and system: rename, download, delete, or resume a room straight from one. Deleting is the only destructive thing on this page — a save is the only copy."
        actions={<Button type="primary" onClick={() => setOpen(true)}>Open the vault</Button>}
      />
      {open && <SavesVaultManager onClose={() => setOpen(false)} onResume={() => setOpen(false)} />}
    </div>
  );
}

function RaTab() {
  const [open, setOpen] = useState(false);
  return (
    <div className="adm-tab">
      <AdminCard
        title="RetroAchievements"
        description="The account link and the trophy room. Scoring itself runs on ONE site service account inside the worker — this panel is the human end of it."
        actions={<Button type="primary" onClick={() => setOpen(true)}>Open the trophy hub</Button>}
      />
      <RetroAchievementsModal open={open} onClose={() => setOpen(false)} />
    </div>
  );
}

// The Overview is a REPORT off endpoints the arcade already serves:
//   /API/Arcade/Filters     — the catalog's facet counts (total cards, systems, variants, RA)
//   /API/Arcade/Rooms       — what is live right now
//   /API/Arcade/HostStatus  — the worker host's own health, including the watchdog's staleness
// There is NO endpoint that reports box-art coverage or a per-system core assignment, so this page
// does not claim to count either — those are the arcade-boxart / arcade-ingest CLIs' business.
function ArcadeOverviewTab() {
  const [filters, setFilters] = useState(null);
  const [rooms, setRooms] = useState(null);
  const [host, setHost] = useState(undefined);

  useEffect(() => {
    let alive = true;
    MovieAPI.getArcadeFilters().then((r) => (r.ok ? r.json() : null)).then((v) => alive && setFilters(v)).catch(() => alive && setFilters(null));
    MovieAPI.getArcadeRooms().then((r) => (r.ok ? r.json() : null)).then((v) => alive && setRooms(v)).catch(() => alive && setRooms(null));
    MovieAPI.getArcadeHostStatus().then((v) => alive && setHost(v)).catch(() => alive && setHost(null));
    return () => { alive = false; };
  }, []);

  const roomList = Array.isArray(rooms) ? rooms : rooms?.rooms ?? [];
  const systems = filters?.systems ?? [];
  const hostDown = host ? !!host.degraded : false;
  const hostStale = host ? !!host.stale : false;

  return (
    <div className="adm-tab">
      <AdminStats
        stats={[
          { label: "Game cards", value: filters?.total },
          { label: "Systems", value: systems.length || null },
          { label: "With achievements", value: filters?.ra?.find?.((x) => x.value === "yes")?.count ?? null },
          { label: "Live rooms", value: rooms ? roomList.length : null },
        ]}
      />

      <NeedsAttention
        basePath="/arcade/admin"
        description="Each row names the tab that fixes it, or the tool that does."
        rows={[
          { key: "degraded", label: "The arcade host is degraded", count: hostDown ? 1 : 0, always: hostDown, tone: "bad", detail: host?.detail || host?.kind || "No room will start until it recovers." },
          { key: "stale", label: "The host watchdog has gone quiet", count: hostStale ? 1 : 0, always: hostStale, tone: "warn", detail: "Drift in a patched core would go unnoticed while it is silent." },
          { key: "nohost", label: "The arcade is not configured on this server", count: host === null ? 1 : 0, always: host === null, tone: "warn", detail: "HostStatus did not answer — either the arcade is off, or this account cannot see it." },
        ]}
        clearText="The host is up and the catalog answered."
      />

      <AdminCard
        title="Systems in the catalog"
        description="Card counts per system, as the lobby's own facets report them."
      >
        <div className="adm-facts">
          {systems.length === 0 && <span className="adm-muted">The filters endpoint did not answer.</span>}
          {systems.map((s) => (
            <span key={s.value}><b>{s.value}</b> {s.count.toLocaleString()}</span>
          ))}
        </div>
      </AdminCard>

      <AdminCard
        title="What this page cannot report"
        description="Box-art coverage, per-system core assignments and worker binaries have no status endpoint on the site — they are the arcade CLIs' and the worker log's business, not this report's. Nothing here guesses at them."
      />
    </div>
  );
}

export default function ArcadeAdminPage({ userData }) {
  const allowed = !!userData?.isAdmin;
  return (
    <AdminShell
      section="arcade"
      eyebrow="Arcade administration"
      allowed={allowed}
      deniedBody="The arcade's operator tools are for administrators. The saves vault and the trophy room are on the bar for every player."
      tabs={[
        { key: "overview", label: "Overview", render: () => <ArcadeOverviewTab /> },
        { key: "game-config", label: "Game config", render: () => <GameConfigTab /> },
        { key: "saves", label: "Saves vault", render: () => <SavesVaultTab /> },
        { key: "ra", label: "RetroAchievements", render: () => <RaTab /> },
      ]}
    />
  );
}
