import { useCallback, useEffect, useRef, useState } from "react";
import { useHistory, useParams } from "react-router-dom";
import Button from "antd/es/button";
import message from "antd/es/message";
import { MovieAPI } from "../../MovieAPI";
import "./WatchPartyPage.css";

/**
 * The watch-party lobby (docs/playlists-watchparty-plan.md). A party is a private playlist channel reached
 * only by this link; its shared timeline waits here until everyone presses Begin, then the whole group is
 * sent to the TV player at the same instant and watches in sync (the ordinary channel machinery handles the
 * shared pause/seek from there). Presence + ready are polled ~2s, mirroring the arcade room.
 */
export default function WatchPartyPage({ userData }) {
  const { token } = useParams();
  const history = useHistory();
  const [lobby, setLobby] = useState(null); // { channelId, name, started, amHost, itemCount, roster[] }
  const [fatal, setFatal] = useState(null);
  const [ready, setReady] = useState(false);
  const [busy, setBusy] = useState(false);
  const startedRef = useRef(false);

  // Once the party begins, send everyone to the player (replace, so Back doesn't return to the lobby).
  const handleState = useCallback(
    (s) => {
      if (!s) return;
      setLobby(s);
      const me = (s.roster || []).find((m) => m.you);
      if (me) setReady(me.ready);
      if (s.started && !startedRef.current) {
        startedRef.current = true;
        history.replace(`/tv/${s.channelId}`);
      }
    },
    [history]
  );

  // Resolve on mount, then heartbeat ~2s (tighter than the channel poll — a lobby wants a snappy "everyone's
  // ready → go").
  useEffect(() => {
    if (!userData?.hasPassword) return undefined;
    let alive = true;
    MovieAPI.getWatchparty(token)
      .then(async (r) => {
        if (!alive) return;
        if (r.status === 403) { setFatal("This watch party isn't available on your account."); return; }
        if (!r.ok) { setFatal("This watch party doesn't exist — the link may have expired."); return; }
        handleState(await r.json());
      })
      .catch(() => alive && setFatal("Couldn't reach the watch party."));

    const beat = () =>
      MovieAPI.watchpartyHeartbeat(token)
        .then((r) => (r && r.ok ? r.json() : null))
        .then((s) => { if (alive && s) handleState(s); })
        .catch(() => {});
    const id = setInterval(beat, 2000);
    return () => { alive = false; clearInterval(id); };
  }, [token, userData?.hasPassword, handleState]);

  // Leave promptly on tab close (beacon survives teardown; the cleanup covers SPA nav) — but NOT once the
  // party has started (we're navigating to the player, and a Leave would drop us from its roster).
  useEffect(() => {
    const onHide = () => { if (!startedRef.current) MovieAPI.beaconLeaveWatchparty(token); };
    window.addEventListener("pagehide", onHide);
    return () => {
      window.removeEventListener("pagehide", onHide);
      if (!startedRef.current) MovieAPI.leaveWatchparty(token);
    };
  }, [token]);

  const toggleReady = async () => {
    const next = !ready;
    setReady(next); // optimistic
    setBusy(true);
    try {
      const r = await MovieAPI.watchpartyReady(token, next);
      if (r.ok) handleState(await r.json());
    } finally {
      setBusy(false);
    }
  };

  const begin = async () => {
    setBusy(true);
    try {
      const r = await MovieAPI.watchpartyBegin(token);
      if (r.ok) handleState(await r.json());
    } finally {
      setBusy(false);
    }
  };

  const copyInvite = () => {
    const url = `${window.location.origin}/watch-together/${token}`;
    navigator.clipboard?.writeText(url).then(
      () => message.success("Invite link copied"),
      () => message.info(url)
    );
  };

  if (!userData?.hasPassword) {
    return (
      <div className="wp-shell">
        <div className="wp-card">
          <h2>Watch party</h2>
          <p className="wp-sub">Log in to join this watch party.</p>
          <Button type="primary" onClick={() => history.push("/")}>Go to sign in</Button>
        </div>
      </div>
    );
  }

  if (fatal) {
    return (
      <div className="wp-shell">
        <div className="wp-card">
          <h2>Can't join this watch party</h2>
          <p className="wp-sub">{fatal}</p>
          <Button type="primary" onClick={() => history.push("/")}>Back home</Button>
        </div>
      </div>
    );
  }

  if (!lobby) {
    return <div className="wp-shell"><div className="wp-card"><p className="wp-sub">Loading…</p></div></div>;
  }

  const roster = lobby.roster || [];
  const readyCount = roster.filter((m) => m.ready).length;
  const allReady = roster.length > 0 && readyCount === roster.length;

  return (
    <div className="wp-shell">
      <div className="wp-card">
        <div className="wp-badge">WATCH PARTY</div>
        <h2 className="wp-name">{lobby.name}</h2>
        <p className="wp-sub">
          {lobby.itemCount} {lobby.itemCount === 1 ? "title" : "titles"} · starts together when everyone's ready
        </p>

        <div className="wp-roster">
          {roster.map((m, i) => (
            <div className={`wp-member${m.ready ? " wp-member--ready" : ""}`} key={i}>
              <span className="wp-dot" aria-hidden="true" />
              <span className="wp-mname">{m.name}{m.you ? " (you)" : ""}</span>
              <span className="wp-state">{m.ready ? "Ready" : "Not ready"}</span>
            </div>
          ))}
          {roster.length <= 1 && (
            <div className="wp-alone">Share the link below so others can join.</div>
          )}
        </div>

        <div className="wp-actions">
          <Button
            type={ready ? "default" : "primary"}
            size="large"
            loading={busy}
            onClick={toggleReady}
          >
            {ready ? "✓ You're ready" : "I'm ready"}
          </Button>
          {lobby.amHost && (
            <Button size="large" onClick={begin} disabled={busy} title="Start now, without waiting for everyone">
              {allReady ? "Begin ▶" : "Start now ▶"}
            </Button>
          )}
        </div>

        <button className="wp-invite" onClick={copyInvite}>🔗 Copy invite link</button>

        <p className="wp-hint">
          {allReady
            ? "Everyone's ready — starting…"
            : `${readyCount} of ${roster.length} ready`}
        </p>
      </div>
    </div>
  );
}
