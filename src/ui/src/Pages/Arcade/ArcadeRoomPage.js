import { useEffect, useRef, useState } from "react";
import { useHistory, useLocation, useParams } from "react-router-dom";
import { Button, Space, Tag, Typography, message, Tooltip, Modal } from "antd";
import { MovieAPI } from "../../MovieAPI";
import { createCloudRetroSession, arcadeInputHint, rotatedVideoSize, videoTransform } from "./cloudRetroClient";
import { useWakeLock } from "../../useWakeLock";

const { Title, Text } = Typography;

// Human-readable connection status.
const STATUS_TEXT = {
  connecting: "Connecting…", signalling: "Negotiating…", connected: "Connected",
  playing: "Playing", disconnected: "Disconnected", closed: "Left room",
  "arcade-full": "The arcade is full", "seat-rejected": "Seat unavailable",
};

/**
 * The /arcade/room/:code player (docs/arcade-plan.md §7–§8). Two ways in: the creator arrives with a
 * descriptor in router state (from the lobby's "Start room"); an invitee arrives cold via the link and
 * Joins to get theirs. Either way we open a CloudRetro session, stream the game to a <video>, send input
 * over the DataChannel, and heartbeat presence. The creator also Binds the CloudRetro room id back to the
 * site once the shim reports it (§8 step 3).
 */
export default function ArcadeRoomPage() {
  const { code } = useParams();
  const location = useLocation();
  const history = useHistory();
  useWakeLock();

  const videoRef = useRef(null);
  const playerRef = useRef(null);
  const sessionRef = useRef(null);
  const descriptorRef = useRef(location.state?.descriptor ?? null);
  const [snapping, setSnapping] = useState(false);

  const [status, setStatus] = useState("connecting");
  const statusRef = useRef(status);
  statusRef.current = status;
  // States where our session is over — no presence to assert. Beating from these would resurrect
  // the room server-side (heartbeats are the rehydration proof-of-life) and hold a dead room in
  // the lobby rail / concurrency cap.
  const TERMINAL_STATUS = ["disconnected", "closed", "arcade-full", "seat-rejected"];
  const [yourSlot, setYourSlot] = useState(location.state?.descriptor?.playerSlot ?? null);
  const [system, setSystem] = useState(location.state?.descriptor?.system ?? null);
  const [players, setPlayers] = useState([]);
  const [fatal, setFatal] = useState(null);
  const [needsTap, setNeedsTap] = useState(false);
  const [discCount, setDiscCount] = useState(location.state?.descriptor?.discCount ?? 0);
  const [disc, setDisc] = useState(0);
  const [isFs, setIsFs] = useState(false);
  // The core's OWN display aspect, reported via the GAME_START `av` payload (and any later t=150).
  // null until it arrives / when the core doesn't specify one — then the per-system table below wins.
  const [coreAspect, setCoreAspect] = useState(null);
  // Quarter-turn rotation the core asks for (vertical arcade cabs report rot=90). The <video> must have
  // its width/height SWAPPED before it is rotated, or the rotated frame overflows its aspect box.
  const [coreRot, setCoreRot] = useState(0);
  // GL cores render bottom-left-origin and ask to be flipped. Held in React (not written onto the
  // element by the shim) so a re-render can't drop it — and so the centring translate exists even for
  // the 21 cores that never report geometry at all.
  const [coreFlip, setCoreFlip] = useState(false);

  // Resolve the join descriptor: creator has it in router state; an invitee Joins for one. If the room
  // is still starting (creator hasn't Bound yet), retry a few times before giving up.
  async function resolveDescriptor() {
    const fromState = location.state?.descriptor;
    if (fromState) return fromState;

    for (let attempt = 0; attempt < 12; attempt++) {
      const res = await MovieAPI.joinArcadeRoom(code);
      if (res.ok) return res.json();
      if (res.status === 409) {
        const body = await res.json().catch(() => ({}));
        if (body.code === "starting") { await delay(1000); continue; }
        throw new Error(body.message || "The room is full.");
      }
      if (res.status === 404) throw new Error("That room has ended.");
      throw new Error("Couldn't join the room.");
    }
    throw new Error("The room is taking too long to start.");
  }

  useEffect(() => {
    let cancelled = false;
    // Defer a tick so React 18 StrictMode's mount→unmount→mount doesn't open two sessions: the first
    // scheduled start is cleared by its cleanup before it fires (prod mounts once → one session).
    const timer = setTimeout(async () => {
      let descriptor;
      try {
        descriptor = await resolveDescriptor();
      } catch (err) {
        if (!cancelled) setFatal(err.message || "Couldn't join the room.");
        return;
      }
      if (cancelled) return;
      setYourSlot(descriptor.playerSlot);
      setSystem(descriptor.system ?? null);
      setDiscCount(descriptor.discCount || 0);

      descriptorRef.current = descriptor;
      sessionRef.current = createCloudRetroSession(descriptor, {
        videoEl: videoRef.current,
        onStatus: (s) => {
          if (cancelled) return;
          setStatus(s);
          if (s === "playing") tryPlayVideo();
        },
        onSeat: (idx) => { if (!cancelled) setYourSlot(idx); },
        onAspect: ({ aspect, rot, flip }) => {
          if (cancelled) return;
          if (aspect != null) setCoreAspect(aspect);
          setCoreRot(rot || 0);
          setCoreFlip(!!flip);
        },
        onRoomId: (roomId) => {
          // Creator: persist the CloudRetro room id so invitees can join the same worker (§8 step 3).
          if (descriptor.isCreator) MovieAPI.bindArcadeRoom(code, roomId).catch(() => {});
        },
        onError: (err) => { if (!cancelled) message.error(err.message || "Connection problem."); },
      });
    }, 0);

    return () => {
      cancelled = true;
      clearTimeout(timer);
      const hadSession = !!sessionRef.current;
      sessionRef.current?.close?.();
      sessionRef.current = null;
      // Only tell the server we left if a session actually opened. StrictMode's throwaway first
      // mount cleans up before the deferred start fires — a Leave there would free the creator's
      // seat 0 and reap the just-created (still-unbound) room out from under the real mount.
      if (hadSession) MovieAPI.leaveArcadeRoom(code);
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [code]);

  // Presence heartbeat + player roster (12 s, like the channel Now poll).
  useEffect(() => {
    let alive = true;
    const beat = () => {
      if (TERMINAL_STATUS.includes(statusRef.current)) return; // dead session asserts no presence
      return MovieAPI.arcadeHeartbeat(code).then((r) => {
        if (!alive || !r || !r.ok) return;
        return r.json().then((s) => { if (alive) { setPlayers(s.players || []); if (s.yourSlot != null) setYourSlot(s.yourSlot); } });
      }).catch(() => {});
    };
    beat();
    const id = setInterval(beat, 12000);
    // Chrome throttles interval timers in backgrounded tabs (and Memory Saver can freeze the tab
    // outright), so beats can stretch far past the server's presence TTL while the player is merely
    // alt-tabbed. Fire an immediate beat at every visibility flip: on HIDE it restarts the TTL clock
    // as late as possible; on SHOW it re-registers the seat the instant the player is back.
    const onVis = () => beat();
    document.addEventListener("visibilitychange", onVis);
    return () => { alive = false; clearInterval(id); document.removeEventListener("visibilitychange", onVis); };
  }, [code]);

  // Auto-recover a session Chrome killed while the tab was hidden. A frozen/discarded background tab
  // drops the signaling WS + WebRTC (observed live: alt-tab → session teardown ~2 min in); the player
  // returns to a dead "Disconnected" room through no action of their own. On refocus, if the session
  // died, reload once — the cold-boot path rejoins the room's seat if the room is still live (and shows
  // "That room has ended" if not). One shot per hidden episode; never for full/rejected/left states.
  useEffect(() => {
    let armed = true; // re-armed each mount; disarmed after one auto-reload so we can't loop
    const onVis = () => {
      if (document.visibilityState !== "visible" || !armed) return;
      if (statusRef.current === "disconnected") {
        armed = false;
        window.location.reload();
      }
    };
    document.addEventListener("visibilitychange", onVis);
    return () => document.removeEventListener("visibilitychange", onVis);
  }, [code]);

  // Leave promptly on tab close (sendBeacon survives teardown; the effect cleanup covers SPA nav).
  useEffect(() => {
    const onHide = () => MovieAPI.beaconLeaveArcadeRoom(code);
    window.addEventListener("pagehide", onHide);
    return () => window.removeEventListener("pagehide", onHide);
  }, [code]);

  // Track fullscreen so we can re-letterbox to the DISPLAY aspect: in fullscreen the UA drops the
  // wrapper's CSS aspectRatio and stretches it to the monitor (16:9), so a 4:3 game would smear wide.
  // The fix is a black full-screen container centering an inner aspect-box (see the player JSX below).
  useEffect(() => {
    const onFsChange = () => setIsFs(!!(document.fullscreenElement || document.webkitFullscreenElement));
    document.addEventListener("fullscreenchange", onFsChange);
    document.addEventListener("webkitfullscreenchange", onFsChange);
    return () => {
      document.removeEventListener("fullscreenchange", onFsChange);
      document.removeEventListener("webkitfullscreenchange", onFsChange);
    };
  }, []);

  function tryPlayVideo() {
    const v = videoRef.current;
    if (!v) return;
    v.play().then(() => setNeedsTap(false)).catch(() => setNeedsTap(true));
  }

  function goFullscreen() {
    const el = playerRef.current || videoRef.current;
    const req = el && (el.requestFullscreen || el.webkitRequestFullscreen || el.webkitEnterFullscreen);
    req?.call(el);
  }

  function copyInvite() {
    const url = `${window.location.origin}/arcade/room/${code}`;
    navigator.clipboard?.writeText(url).then(
      () => message.success("Invite link copied"),
      () => message.info(url)
    );
  }

  // Save a NAMED snapshot (arcade-saves-plan S3): flush the live state, then ask the gateway to copy it
  // into a new numbered slot you can resume later. Owner-only (the gateway rejects a guest's token).
  async function saveSnapshot() {
    const d = descriptorRef.current;
    if (!d || !d.wsUrl || snapping) return;
    const label = window.prompt("Name this snapshot (e.g. \"Level 3 boss\"):", "");
    if (label === null) return; // cancelled
    const token = (d.wsUrl.match(/\/w\/([^/?]+)/) || [])[1];
    if (!token) { message.error("Can't snapshot this session."); return; }
    const base = d.wsUrl.replace(/^ws/, "http").replace(/\/w\/.*$/, "");
    setSnapping(true);
    try {
      sessionRef.current?.save?.();                     // flush current state to /saves/<id>.dat
      await new Promise((r) => setTimeout(r, 1300));     // let the save write before the gateway copies it
      const res = await fetch(`${base}/w-snap/${token}`, {
        method: "post", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ label: label.trim() }),
      });
      const j = await res.json().catch(() => null);
      if (j && j.ok) message.success(`Snapshot saved${j.label ? `: ${j.label}` : ` (slot ${j.slot})`}`);
      else message.warning((j && j.reason) || "Couldn't save the snapshot — play a moment, then try again.");
    } catch { message.error("Couldn't save the snapshot."); }
    finally { setSnapping(false); }
  }

  // Load a saved snapshot LIVE (no room restart): the gateway copies the chosen slot's .dat/.srm over the
  // running session's files, then we tell the core to reload state (shim t=107). Owner-only. gameId is
  // parsed out of the deterministic room id (sv-<user>-<gameId>-<slot>-<system>___<key>).
  async function loadSnapshot() {
    const d = descriptorRef.current;
    if (!d || !d.wsUrl) return;
    const token = (d.wsUrl.match(/\/w\/([^/?]+)/) || [])[1];
    let gameId = null;
    try {
      const qs = new URLSearchParams(d.wsUrl.slice(d.wsUrl.indexOf("?") + 1));
      const m = decodeURIComponent(qs.get("room_id") || "").match(/^sv-\d+-(\d+)-/);
      if (m) gameId = parseInt(m[1], 10);
    } catch { /* ignore */ }
    if (!token || !gameId) { message.error("Can't load snapshots for this session."); return; }
    let saves;
    try { saves = await MovieAPI.listArcadeSaves(gameId); }
    catch { message.error("Couldn't load your saves."); return; }
    const snaps = (saves || []).filter((s) => s.kind === "state" && s.slotId >= 1).sort((a, b) => a.slotId - b.slotId);
    if (snaps.length === 0) { message.info("No snapshots yet — use 📸 Snapshot to make one."); return; }
    const base = d.wsUrl.replace(/^ws/, "http").replace(/\/w\/.*$/, "");
    Modal.info({
      title: "Load a snapshot",
      okText: "Cancel",
      content: (
        <div>
          {snaps.map((s) => (
            <div key={s.slotId} style={{ padding: "6px 0" }}>
              <a onClick={async () => {
                Modal.destroyAll();
                try {
                  const r = await fetch(`${base}/w-load/${token}`, {
                    method: "post", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ slot: s.slotId }),
                  });
                  const j = await r.json().catch(() => null);
                  if (j && j.ok) {
                    await new Promise((res) => setTimeout(res, 250)); // let the file land before the core re-reads it
                    sessionRef.current?.load?.();
                    message.success(`Loaded: ${s.label || `Snapshot ${s.slotId}`}`);
                  } else message.warning((j && j.reason) || "Couldn't load that snapshot.");
                } catch { message.error("Couldn't load that snapshot."); }
              }}>▶ {s.label || `Snapshot ${s.slotId}`}</a>
              {s.createdUtc && <Text type="secondary" style={{ marginLeft: 8, fontSize: 12 }}>{new Date(s.createdUtc).toLocaleString()}</Text>}
            </div>
          ))}
        </div>
      ),
    });
  }

  if (fatal) {
    return (
      <div style={{ padding: 48, textAlign: "center" }}>
        <Title level={3}>Can't join this room</Title>
        <Text type="secondary">{fatal}</Text>
        <div style={{ marginTop: 24 }}>
          <Button type="primary" onClick={() => history.push("/arcade")}>Back to arcade</Button>
        </div>
      </div>
    );
  }

  // Multi-disc: ask the emulator (via the "disc" data channel → patch 0005) to swap discs live. The memory
  // card persists across the swap, so the game continues when it prompts for the next disc.
  function swapDisc(next) {
    const target = Math.max(0, Math.min(discCount - 1, next));
    if (target === disc) return;
    setDisc(target);
    sessionRef.current?.swapDisc?.(target);
    message.info(`Switching to disc ${target + 1}…`);
  }

  return (
    <div className="arcade-room-page" style={{ maxWidth: 1100, margin: "0 auto", padding: "16px 24px" }}>
      <div style={{ display: "flex", alignItems: "center", justifyContent: "space-between", marginBottom: 12, flexWrap: "wrap", gap: 8 }}>
        <Space>
          <Button onClick={() => history.push("/arcade")}>← Arcade</Button>
          <Tag color={status === "playing" ? "green" : "blue"}>{STATUS_TEXT[status] || status}</Tag>
          {yourSlot != null && <Tag color="purple">You are P{yourSlot + 1}</Tag>}
          {discCount > 1 && (
            <Space size={4}>
              <Button size="small" disabled={disc <= 0} onClick={() => swapDisc(disc - 1)}>◀</Button>
              <Tag color="gold">Disc {disc + 1}/{discCount}</Tag>
              <Button size="small" disabled={disc >= discCount - 1} onClick={() => swapDisc(disc + 1)}>▶</Button>
            </Space>
          )}
        </Space>
        <Space>
          <Text type="secondary">Room {code}</Text>
          <Button onClick={copyInvite}>Copy invite link</Button>
        </Space>
      </div>

      {/* Per-system DISPLAY aspect (what the console showed on a TV) — the emulated framebuffer is often
          non-square-pixel (e.g. PSX 512x240) so we stretch it to the correct aspect with object-fit:fill,
          rather than letterboxing the raw pixels (which reads as "squished"). GB/GBA aren't 4:3.
          Two-box layout so fullscreen letterboxes instead of stretching (roadmap WS-A.3): the OUTER box is
          the frame (windowed) / the black full-screen surface (fullscreen); the INNER box always holds the
          display aspect, and object-fit:fill lives INSIDE it. In fullscreen the inner box is sized to the
          largest aspect-correct rectangle that fits the screen via min(100% width, height-driven width). */}
      {(() => {
        // The core's own aspect WINS when it reports one (av.a). Every libretro core fills
        // retro_get_system_av_info's geometry.aspect_ratio; <= 0 means "unspecified", and only then
        // does this table apply. Getting this from the core is what makes per-GAME aspect correct:
        // ps2/gc/dc titles ship both 4:3 and 16:9, and no per-system constant can be right for both.
        //
        // The old code hardcoded 4/3 for everything except gb/gbc/gba, and since the <video> uses
        // objectFit:"fill" that DISTORTS rather than letterboxes — PSP's 16:9 was squeezed into 4:3.
        // The fallbacks below are the true native panel ratios, for cores that report nothing.
        const FALLBACK_AR = {
          gb: 10 / 9, gbc: 10 / 9,   // 160x144
          gba: 3 / 2,                // 240x160
          gg: 10 / 9,                // 160x144
          psp: 16 / 9,               // 480x272
          wsc: 224 / 144,            // ~14:9
          ngpc: 160 / 152,           // ~1.05 — was rendered at 1.33
          lynx: 160 / 102,
          vb: 384 / 224,
        };
        const ar = coreAspect || FALLBACK_AR[system] || 4 / 3;
        // A quarter-turn swaps the element's axes. The box is `ar` wide-over-tall; for the rotated video
        // to fill it, the element must be as wide as the box is TALL and as tall as the box is WIDE:
        //   width  = boxH = boxW / ar  →  calc(100% / ar)   (100% of width  = boxW)
        //   height = boxW = boxH * ar  →  calc(100% * ar)   (100% of height = boxH)
        // cloudRetroClient prepends translate(-50%,-50%) so it rotates about the box centre.
        // Without this, 1942 (rot=90) rendered upright but overflowed the 3:4 box and left dead space.
        const videoStyle = { position: "absolute", top: "50%", left: "50%", objectFit: "fill",
                             display: "block", transform: videoTransform(coreRot, coreFlip),
                             ...rotatedVideoSize(ar, coreRot) };
        const outerStyle = isFs
          ? { position: "relative", background: "#000", width: "100%", height: "100%", display: "flex", alignItems: "center", justifyContent: "center" }
          : { position: "relative", background: "#000", borderRadius: 8, overflow: "hidden" };
        const innerStyle = isFs
          ? { position: "relative", aspectRatio: ar, width: `min(100%, calc(100vh * ${ar}))`, maxHeight: "100%" }
          : { position: "relative", aspectRatio: ar, width: "100%" };
        return (
          <div ref={playerRef} style={outerStyle}>
            <div style={innerStyle}>
              <video
                ref={videoRef}
                autoPlay
                playsInline
                style={videoStyle}
              />
              {needsTap && (
                <button
                  onClick={tryPlayVideo}
                  style={{ position: "absolute", inset: 0, background: "rgba(0,0,0,0.55)", color: "#fff", border: "none", fontSize: 20, cursor: "pointer" }}
                >
                  ▶ Tap to start
                </button>
              )}
            </div>
          </div>
        );
      })()}

      <div style={{ display: "flex", alignItems: "center", justifyContent: "space-between", marginTop: 12, flexWrap: "wrap", gap: 8 }}>
        <Space wrap>
          <Text strong>Players:</Text>
          {players.length === 0
            ? <Text type="secondary">just you</Text>
            : players.map((p, i) => <Tag key={i} color={p.you ? "purple" : "default"}>{p.name}{p.you ? " (you)" : ""}</Tag>)}
        </Space>
        <Space>
          <Tooltip title="Save your place (a state you can reload with Load)">
            <Button onClick={() => { sessionRef.current?.save?.(); message.success("State saved"); }}>Save</Button>
          </Tooltip>
          <Tooltip title="Reload your last saved state">
            <Button onClick={() => { sessionRef.current?.load?.(); message.info("Loading last state…"); }}>Load</Button>
          </Tooltip>
          {yourSlot === 0 && (
            <Tooltip title="Save a named snapshot you can resume later">
              <Button loading={snapping} onClick={saveSnapshot}>📸 Snapshot</Button>
            </Tooltip>
          )}
          {yourSlot === 0 && (
            <Tooltip title="Load a saved snapshot without leaving the room">
              <Button onClick={loadSnapshot}>📂 Load snapshot</Button>
            </Tooltip>
          )}
          <Tooltip title="Fullscreen">
            <Button onClick={goFullscreen}>⛶ Fullscreen</Button>
          </Tooltip>
          <Button danger onClick={() => history.push("/arcade")}>End</Button>
        </Space>
      </div>

      <Text type="secondary" style={{ display: "block", marginTop: 16, fontSize: 12 }}>
        {arcadeInputHint(system)}
      </Text>
    </div>
  );
}

function delay(ms) {
  return new Promise((resolve) => setTimeout(resolve, ms));
}
