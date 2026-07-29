import { useCallback, useEffect, useRef, useState } from "react";
import { Button, Empty, Modal, Space, Spin, Typography, message } from "antd";
import { MovieAPI } from "../../MovieAPI";
import "./ArcadeModal.css";

const { Text } = Typography;

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
    if (r.kind === "dirzip") return "Continue (latest)"; // heavy lane: one dir-save per title
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
    // .dat = save state, .srm = SRAM/card, .zip = a heavy title's directory save (dirzip —
    // exactly what Export produces, and what a Deck/EmuDeck save dir zips to). Default: state.
    const kind = /\.srm$/i.test(file.name) ? "sram" : /\.zip$/i.test(file.name) ? "dirzip" : "state";
    setBusy(true);
    try {
      const res = await MovieAPI.importArcadeSave(game.gameId, file, { kind, label: file.name });
      if (res.ok) { message.success("Save imported."); refresh(); }
      else message.error("Couldn't import that file.");
    } catch { message.error("Couldn't import that file."); }
    finally { setBusy(false); }
  }

  return (
    <Modal
      open
      title={`My saves — ${game.title}`}
      onCancel={onClose}
      footer={null}
      width={520}
      // Above the nav bar (1300) so the sheet covers it rather than sliding under it;
      // `arcade-modal` is the shared shell — bounded to the viewport, body scrolls, full
      // screen on a phone or a short window (ArcadeModal.css).
      zIndex={1500}
      wrapClassName="arcade-modal"
    >
      {rows === null ? (
        <div style={{ textAlign: "center", padding: 24 }}><Spin /></div>
      ) : rows.length === 0 ? (
        <Empty description="No saves for this game yet." />
      ) : (
        // The list used to cap itself at 360px and scroll on its own. The shell already bounds
        // the dialog and scrolls the body, so a second scroller here would just be a smaller
        // window inside a window — .arcade-modal-scroll opts back out of it.
        <div className="arcade-modal-scroll">
          {rows.slice().sort((a, b) => a.slotId - b.slotId).map((r) => (
            <div key={r.id} style={{ display: "flex", alignItems: "center", gap: 8, padding: "6px 0", borderBottom: "1px solid #f0f0f0" }}>
              <div style={{ flex: 1, minWidth: 0 }}>
                <Text strong ellipsis>{fmt(r)}</Text>
                <div><Text type="secondary" style={{ fontSize: 11 }}>
                  {r.kind === "sram" ? "SRAM" : r.kind === "dirzip" ? "game save (zip)" : "state"} · {(r.sizeBytes / 1024).toFixed(0)} KB
                  {r.updatedUtc ? ` · ${new Date(r.updatedUtc).toLocaleString()}` : ""}
                </Text></div>
              </div>
              <Space size={4}>
                {/* dirzip = a Moonlight-streamed title's save; it seeds at stream launch, so there
                    is no browser room to resume into. */}
                {onResume && r.kind !== "dirzip" && (
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
        <Text type="secondary" style={{ fontSize: 12 }}>Import a .srm (card), .dat (state), or .zip (game save) file.</Text>
        <input ref={fileRef} type="file" accept=".srm,.dat,.state,.zip" style={{ display: "none" }} onChange={onImport} />
        <Button loading={busy} onClick={() => fileRef.current?.click()}>⬆ Import save</Button>
      </div>
    </Modal>
  );
}

export default SavesManager;
