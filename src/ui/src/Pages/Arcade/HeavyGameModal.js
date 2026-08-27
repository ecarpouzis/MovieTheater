import { useEffect, useRef, useState } from "react";
import { Alert, Button, Input, Modal, Progress, Typography, message } from "antd";
import { MovieAPI } from "../../MovieAPI";
import "../../Components/SheetModal.css";
import usePolling from "../../hooks/usePolling";
import { SHEET_Z } from "../../Components/sheetModal";

const { Text, Paragraph } = Typography;

const GB = 1024 * 1024 * 1024;
const fmtGB = (b) => (b > 0 ? `${(b / GB).toFixed(1)} GB` : "");

/**
 * The heavy-lane card action (docs/arcade-heavy-lane-plan.md §7.1/§7.5): these titles stream via
 * Moonlight, not in the browser, so instead of creating a room the card opens this modal —
 * status-aware around the lane's three states:
 *
 *   unstaged  → "Prepare (N GB)": THIS page drives the gateway's chunked copier to completion, one
 *               bounded call at a time, with live progress (bulk-job house rules — the loop lives in
 *               the client, with a no-progress safety break). Preparing is a disk copy, not a
 *               session, so it's fine while someone else plays.
 *   staged    → "Play via Moonlight": open Moonlight on your device → Ziggy → the app (exact name
 *               shown), plus a copyable CLI one-liner. No moonlight:// deep link exists upstream yet.
 *   busy      → "In use by <user>" — one heavy session at a time (host-enforced).
 *
 * Pairing helper: first-time devices need a one-time PIN pairing; editor-gated server-side (pairing
 * is physical-seat-equivalent trust — plan §10), so the section is discreet and failures are honest.
 */
export default function HeavyGameModal({ game, onClose, onPlayInBrowser }) {
  const [status, setStatus] = useState(null);   // /Heavy/Status payload
  const [preparing, setPreparing] = useState(false);
  const [progress, setProgress] = useState(null); // last stage-chunk response
  const [pin, setPin] = useState("");
  const [deviceName, setDeviceName] = useState("");
  const [pairing, setPairing] = useState(false);
  const [showPair, setShowPair] = useState(false);
  const alive = useRef(true);
  const preparingRef = useRef(false);

  const versionId = game.versions?.[0]?.id;

  const loadStatus = () =>
    MovieAPI.getArcadeHeavyStatus()
      .then((r) => (r.ok ? r.json() : null))
      .then((s) => { if (alive.current) setStatus(s); return s; })
      .catch(() => null);

  useEffect(() => {
    alive.current = true;
    return () => { alive.current = false; preparingRef.current = false; };
  }, []);
  // The lane lock can change under us (someone starts/quits a stream) — refresh while open,
  // visibility-aware like every informational poll (usePolling fires the first beat itself).
  usePolling(loadStatus, 12000);

  // This game's descriptor entry, by its ArcadeGame row id.
  const app = status?.apps?.find?.((a) => a.arcadeGameId === versionId);
  const staging = progress || app?.staging;
  const staged = staging?.state === "done" || staging?.state === "local";
  const appTitle = app?.title || game.title;

  // Drive the chunked stager to completion: one bounded call per iteration, live progress, and a
  // no-progress safety break so a stuck copy can't loop forever (bulk-job house rule).
  async function prepare() {
    if (preparingRef.current) return;
    preparingRef.current = true;
    setPreparing(true);
    let lastBytes = -1;
    let stalls = 0;
    try {
      for (let i = 0; i < 10000 && preparingRef.current; i++) {
        const r = await MovieAPI.stageArcadeHeavy(versionId);
        if (!r.ok) { message.error("Preparing failed — is the heavy lane configured?"); return; }
        const p = await r.json();
        if (!alive.current) return;
        setProgress(p);
        if (p.state === "done") { message.success(`${game.title} is ready to play.`); return; }
        if (p.state === "error") { message.error(p.error || "Preparing failed."); return; }
        const at = (p.stagedBytes || 0) + (p.verifiedBytes || 0);
        stalls = at === lastBytes ? stalls + 1 : 0;
        lastBytes = at;
        if (stalls >= 5) { message.error("Preparing stalled — check the gateway log on the host."); return; }
      }
    } finally {
      preparingRef.current = false;
      if (alive.current) { setPreparing(false); loadStatus(); }
    }
  }

  function pair() {
    if (!pin.trim() || !deviceName.trim()) { message.warning("Enter the PIN Moonlight shows and a device name."); return; }
    setPairing(true);
    MovieAPI.pairArcadeHeavy(pin.trim(), deviceName.trim())
      .then(async (r) => {
        const body = await r.json().catch(() => ({}));
        if (r.ok && body.ok) { message.success(`Paired "${deviceName.trim()}" — it can now see Ziggy's apps.`); setPin(""); setDeviceName(""); setShowPair(false); }
        else message.error(body.detail || body.message || "Pairing failed.");
      })
      .catch(() => message.error("Pairing failed."))
      .finally(() => setPairing(false));
  }

  const busy = status?.locked && status?.title !== appTitle;
  const pct = staging?.totalBytes > 0
    ? Math.floor((100 * ((staging.stagedBytes || 0) + (staging.verifiedBytes || 0))) / (2 * staging.totalBytes))
    : 0;
  const copyCmd = `moonlight stream Ziggy "${appTitle}"`;

  return (
    <Modal
      title={`${game.title} — play${game.capture ? "" : " via Moonlight"}`}
      open
      onCancel={onClose}
      footer={<Button onClick={onClose}>Close</Button>}
      // The site's dialog layer (Components/sheetModal.js); `sheet-modal` = the shared shell.
      zIndex={SHEET_Z}
      wrapClassName="sheet-modal"
    >
      <Paragraph type="secondary" style={{ marginBottom: 12 }}>
        {game.capture
          ? "This title runs on the game PC. Play it right in the browser (no app needed), or stream it with lowest latency via Moonlight/Artemis on the same network."
          : "This title runs on the game PC and streams to your device — it doesn't play in the browser. You'll need the Moonlight app (or Artemis on Android) on the same network."}
      </Paragraph>

      {status === null ? (
        <Text type="secondary">Checking the lane…</Text>
      ) : (
        <>
          {busy && (
            <Alert
              type="warning"
              showIcon
              style={{ marginBottom: 12 }}
              message={`In use: ${status.title}${status.byUser ? ` — ${status.byUser}` : ""}`}
              description="One streamed session runs at a time. You can still prepare this title now and play when it frees up."
            />
          )}

          {!staged ? (
            <div style={{ marginBottom: 12 }}>
              {(staging?.state === "copy" || staging?.state === "verify" || preparing) && (
                <Progress percent={pct} status={preparing ? "active" : "normal"}
                  format={() => (staging?.state === "verify" ? "verifying…" : `${pct}%`)} />
              )}
              {staging?.state === "error" && (
                <Alert type="error" showIcon style={{ marginBottom: 8 }} message={staging.error || "Preparing failed."} />
              )}
              <Button type="primary" loading={preparing} onClick={prepare}>
                {staging?.state === "copy" || staging?.state === "verify"
                  ? "Resume preparing"
                  : `Prepare${staging?.totalBytes ? ` (${fmtGB(staging.totalBytes)})` : ""}`}
              </Button>
              <div style={{ marginTop: 6 }}>
                <Text type="secondary" style={{ fontSize: 12 }}>
                  Copies the game from the library to the game PC's fast disk. Keep this window open;
                  you can play once it finishes. {app ? "" : "— This title isn't registered on the host yet."}
                </Text>
              </div>
            </div>
          ) : (
            <div style={{ marginBottom: 12 }}>
              {/* Capture lane (H5): a heavy title with a CloudRetroGameKey ALSO plays in the browser —
                  the room routes to the capture worker, which launches the native game and streams the
                  desktop over the same WebRTC pipeline the retro cards use. This sits ALONGSIDE Artemis
                  so neither launch path is lost. */}
              {game.capture && !busy && (
                <div style={{ marginBottom: 14 }}>
                  <Button type="primary" onClick={() => onPlayInBrowser?.(versionId)} style={{ marginBottom: 6 }}>
                    ▶ Play in browser
                  </Button>
                  <div>
                    <Text type="secondary" style={{ fontSize: 12 }}>
                      No app, no pairing — plays right here in the tab, and friends can join with a controller
                      over the internet. Slightly higher latency than Artemis (~80–120 ms vs ~40–70 on LAN).
                    </Text>
                  </div>
                </div>
              )}
              {/* Artemis .art trampoline: the closest thing to launching from the card — the tapped
                  file streams the game directly on a paired Android device. (moonlight:// links
                  still don't exist upstream, and the browser can't carry the Moonlight protocol
                  itself, so a fully in-page launch isn't possible on this lane.) */}
              <Button type={game.capture ? "default" : "primary"} href={`/API/Arcade/Heavy/Shortcut/${versionId}`} style={{ marginBottom: 6 }}>
                ▶ Launch on this device (Artemis){game.capture ? " — lowest latency" : ""}
              </Button>
              <div style={{ marginBottom: 10 }}>
                <Text type="secondary" style={{ fontSize: 12 }}>
                  Android + Artemis, already paired: this downloads a tiny <Text code>.art</Text> shortcut —
                  tap it in your downloads and the stream starts straight into the game. Keep the file
                  and it's a one-tap launcher from now on (you can even add it to your home screen).
                </Text>
              </div>
              <Text type="secondary" style={{ fontSize: 12 }}>Or by hand: </Text>
              <Text style={{ fontSize: 12 }}>open <b>Moonlight/Artemis</b> → <b>Ziggy</b> → <b>{appTitle}</b>.</Text>
              <div>
                <Text type="secondary" style={{ fontSize: 12 }}>Desktop shortcut: </Text>
                <Text code copyable style={{ fontSize: 12 }}>{copyCmd}</Text>
              </div>
            </div>
          )}

          <div style={{ borderTop: "1px solid rgba(128,128,128,0.25)", paddingTop: 10 }}>
            {showPair ? (
              <div>
                <Text strong>Pair a new device</Text>
                <Paragraph type="secondary" style={{ fontSize: 12, marginBottom: 8 }}>
                  On the device, open Moonlight and tap Ziggy — it shows a 4-digit PIN. Enter it here
                  with a name for the device. (Editor-only.)
                </Paragraph>
                <div style={{ display: "flex", gap: 8 }}>
                  <Input style={{ width: 90 }} maxLength={4} placeholder="PIN" value={pin}
                    onChange={(e) => setPin(e.target.value.replace(/\D/g, ""))} />
                  <Input style={{ flex: 1 }} maxLength={60} placeholder="Device name (e.g. Living room TV)"
                    value={deviceName} onChange={(e) => setDeviceName(e.target.value)} />
                  <Button type="primary" loading={pairing} onClick={pair}>Pair</Button>
                </div>
              </div>
            ) : (
              <button type="button" className="arcade-link" onClick={() => setShowPair(true)}>
                First time on this device? Pair it…
              </button>
            )}
          </div>
        </>
      )}
    </Modal>
  );
}
