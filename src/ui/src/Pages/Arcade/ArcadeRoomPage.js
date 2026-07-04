import { useEffect, useRef, useState } from "react";
import { useHistory, useLocation, useParams } from "react-router-dom";
import { Button, Space, Tag, Typography, message, Tooltip } from "antd";
import { MovieAPI } from "../../MovieAPI";
import { createCloudRetroSession, arcadeInputHint } from "./cloudRetroClient";
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

  const [status, setStatus] = useState("connecting");
  const [yourSlot, setYourSlot] = useState(location.state?.descriptor?.playerSlot ?? null);
  const [system, setSystem] = useState(location.state?.descriptor?.system ?? null);
  const [players, setPlayers] = useState([]);
  const [fatal, setFatal] = useState(null);
  const [needsTap, setNeedsTap] = useState(false);

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

      sessionRef.current = createCloudRetroSession(descriptor, {
        videoEl: videoRef.current,
        onStatus: (s) => {
          if (cancelled) return;
          setStatus(s);
          if (s === "playing") tryPlayVideo();
        },
        onSeat: (idx) => { if (!cancelled) setYourSlot(idx); },
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
    const beat = () =>
      MovieAPI.arcadeHeartbeat(code).then((r) => {
        if (!alive || !r || !r.ok) return;
        return r.json().then((s) => { if (alive) { setPlayers(s.players || []); if (s.yourSlot != null) setYourSlot(s.yourSlot); } });
      }).catch(() => {});
    beat();
    const id = setInterval(beat, 12000);
    return () => { alive = false; clearInterval(id); };
  }, [code]);

  // Leave promptly on tab close (sendBeacon survives teardown; the effect cleanup covers SPA nav).
  useEffect(() => {
    const onHide = () => MovieAPI.beaconLeaveArcadeRoom(code);
    window.addEventListener("pagehide", onHide);
    return () => window.removeEventListener("pagehide", onHide);
  }, [code]);

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

  return (
    <div className="arcade-room-page" style={{ maxWidth: 1100, margin: "0 auto", padding: "16px 24px" }}>
      <div style={{ display: "flex", alignItems: "center", justifyContent: "space-between", marginBottom: 12, flexWrap: "wrap", gap: 8 }}>
        <Space>
          <Button onClick={() => history.push("/arcade")}>← Arcade</Button>
          <Tag color={status === "playing" ? "green" : "blue"}>{STATUS_TEXT[status] || status}</Tag>
          {yourSlot != null && <Tag color="purple">You are P{yourSlot + 1}</Tag>}
        </Space>
        <Space>
          <Text type="secondary">Room {code}</Text>
          <Button onClick={copyInvite}>Copy invite link</Button>
        </Space>
      </div>

      {/* Per-system DISPLAY aspect (what the console showed on a TV) — the emulated framebuffer is often
          non-square-pixel (e.g. PSX 512x240) so we stretch it to the correct aspect with object-fit:fill,
          rather than letterboxing the raw pixels (which reads as "squished"). GB/GBA aren't 4:3. */}
      <div ref={playerRef} style={{ position: "relative", background: "#000", borderRadius: 8, overflow: "hidden", aspectRatio: ({ gb: "10 / 9", gbc: "10 / 9", gba: "3 / 2" })[system] || "4 / 3" }}>
        <video
          ref={videoRef}
          autoPlay
          playsInline
          style={{ width: "100%", height: "100%", objectFit: "fill", display: "block" }}
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

      <div style={{ display: "flex", alignItems: "center", justifyContent: "space-between", marginTop: 12, flexWrap: "wrap", gap: 8 }}>
        <Space wrap>
          <Text strong>Players:</Text>
          {players.length === 0
            ? <Text type="secondary">just you</Text>
            : players.map((p, i) => <Tag key={i} color={p.you ? "purple" : "default"}>{p.name}{p.you ? " (you)" : ""}</Tag>)}
        </Space>
        <Space>
          <Tooltip title="Save state in-game">
            <Button onClick={() => sessionRef.current?.save?.()}>Save</Button>
          </Tooltip>
          <Tooltip title="Load last save state">
            <Button onClick={() => sessionRef.current?.load?.()}>Load</Button>
          </Tooltip>
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
