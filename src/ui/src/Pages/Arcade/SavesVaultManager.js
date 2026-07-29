import { useCallback, useEffect, useState } from "react";
import { Button, Drawer, Empty, Input, Modal, Select, Space, Table, Tag, Typography, message } from "antd";
import { MovieAPI } from "../../MovieAPI";
import GameCover from "./GameCover";
import { SYSTEM_LABEL, systemLabel } from "./arcadeSystems";
import "./ArcadeModal.css";

const { Text } = Typography;
const PAGE_SIZE = 20;

function fmtBytes(n) {
  if (!(n > 0)) return "0 KB";
  if (n < 1024 * 1024) return `${Math.max(1, Math.round(n / 1024))} KB`;
  return `${(n / (1024 * 1024)).toFixed(1)} MB`;
}

function fmtSave(r) {
  if (r.kind === "dirzip") return "Continue (latest)"; // heavy lane: one dir-save per title
  if (r.slotId === 0) return r.kind === "sram" ? "Cartridge / card" : "Continue (latest)";
  return r.label || `Snapshot ${r.slotId}`;
}

const SYSTEM_OPTIONS = Object.entries(SYSTEM_LABEL).map(([value, label]) => ({ value, label }));

/**
 * The cross-game "saves vault" (arcade-saves-plan follow-on): SavesManager only ever shows one
 * game at a time, which doesn't scale once a player has touched dozens of titles. This is the
 * management surface for EVERYTHING a player has saved — always paged/searched/filtered
 * server-side (never "load everything"), since save rows can run into the thousands over time.
 */
function SavesVaultManager({ onClose, onResume }) {
  const [rows, setRows] = useState(null);
  const [totalCount, setTotalCount] = useState(0);
  const [totalSizeBytes, setTotalSizeBytes] = useState(0);
  const [search, setSearch] = useState("");
  const [system, setSystem] = useState("");
  const [page, setPage] = useState(1);
  const [busyId, setBusyId] = useState(null);

  const refresh = useCallback(() => {
    setRows(null);
    MovieAPI.getAllArcadeSaves({ search, system, skip: (page - 1) * PAGE_SIZE, take: PAGE_SIZE })
      .then((r) => (r.ok ? r.json() : null))
      .then((data) => {
        setRows(data?.rows || []);
        setTotalCount(data?.totalCount || 0);
        setTotalSizeBytes(data?.totalSizeBytes || 0);
      })
      .catch(() => setRows([]));
  }, [search, system, page]);

  useEffect(() => { refresh(); }, [refresh]);
  // A filter change re-seeks to page 1 — a page number left over from a bigger result set could
  // land past the end of a narrower one and show an empty table under active filters.
  useEffect(() => { setPage(1); }, [search, system]);

  async function rename(r) {
    const label = window.prompt("Rename save:", r.label || "");
    if (label === null) return;
    setBusyId(r.id);
    try {
      const res = await MovieAPI.renameArcadeSave(r.id, label.trim());
      if (res.ok) refresh(); else message.error("Couldn't rename that save.");
    } finally { setBusyId(null); }
  }

  function remove(r) {
    Modal.confirm({
      title: `Delete "${fmtSave(r)}" — ${r.title}?`,
      okText: "Delete", okButtonProps: { danger: true },
      onOk: async () => {
        const res = await MovieAPI.deleteArcadeSave(r.id);
        if (res.ok) refresh(); else message.error("Couldn't delete that save.");
      },
    });
  }

  const columns = [
    {
      title: "Game", key: "game",
      render: (_, r) => (
        <Space align="center">
          <GameCover game={r} artId={r.artId} height={40} maxWidth={40} />
          <div style={{ minWidth: 0 }}>
            <div><Text strong ellipsis style={{ maxWidth: 200, display: "inline-block" }}>{r.title}</Text></div>
            <Tag style={{ marginTop: 2 }}>{systemLabel(r.system)}</Tag>
          </div>
        </Space>
      ),
    },
    {
      title: "Save", key: "save",
      render: (_, r) => (
        <div>
          <div>{fmtSave(r)}</div>
          <Text type="secondary" style={{ fontSize: 11 }}>
            {r.kind === "sram" ? "SRAM" : r.kind === "dirzip" ? "game save (zip)" : "state"} · {fmtBytes(r.sizeBytes)}
            {r.isAutosave ? " · auto" : ""}
          </Text>
        </div>
      ),
    },
    {
      title: "Updated", dataIndex: "updatedUtc", key: "updatedUtc",
      render: (v) => (v ? new Date(v).toLocaleString() : "—"),
    },
    {
      title: "", key: "actions",
      render: (_, r) => (
        <Space size={4} wrap>
          {onResume && r.kind !== "dirzip" && (
            <Button
              size="small" type="link"
              onClick={() => { onClose(); onResume(r.gameId, r.slotId >= 1 ? { seedSlot: r.slotId } : {}); }}
            >
              Resume
            </Button>
          )}
          <a href={MovieAPI.arcadeSaveDownloadUrl(r.id)}><Button size="small" type="link">Export</Button></a>
          <Button size="small" type="link" loading={busyId === r.id} onClick={() => rename(r)}>Rename</Button>
          <Button size="small" type="link" danger onClick={() => remove(r)}>Delete</Button>
        </Space>
      ),
    },
  ];

  // `arcade-drawer`: on a phone the 680px panel would hang off a ~390px screen, so
  // ArcadeModal.css widens it to the full screen there.
  return (
    <Drawer open title="My saves — all games" onClose={onClose} width={680} placement="right" className="arcade-drawer" zIndex={1500}>
      <Space style={{ marginBottom: 12, width: "100%", justifyContent: "space-between" }} wrap>
        <Space wrap>
          <Input.Search
            placeholder="Search by game title"
            allowClear
            style={{ width: 220 }}
            onSearch={setSearch}
          />
          <Select
            allowClear
            placeholder="System"
            style={{ width: 160 }}
            options={SYSTEM_OPTIONS}
            value={system || undefined}
            onChange={(v) => setSystem(v || "")}
          />
        </Space>
        <Text type="secondary">
          {totalCount.toLocaleString()} save{totalCount === 1 ? "" : "s"} · {fmtBytes(totalSizeBytes)}
        </Text>
      </Space>

      <Table
        rowKey="id"
        loading={rows === null}
        dataSource={rows || []}
        columns={columns}
        pagination={{
          current: page, pageSize: PAGE_SIZE, total: totalCount, onChange: setPage,
          showSizeChanger: false,
        }}
        locale={{ emptyText: <Empty description="No saves match those filters." /> }}
        size="small"
      />
    </Drawer>
  );
}

export default SavesVaultManager;
