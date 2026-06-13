import { useState, useEffect, useRef, useCallback } from "react";
import { useParams, useHistory } from "react-router-dom";
import Hls from "hls.js";
import { MovieAPI } from "../../MovieAPI";
import { formatTime, TICKS_PER_SECOND, QUALITY_LADDER } from "../Watch/VideoPlayer";
import { initialAutoBps } from "../../streamAbr";
import ChannelAdminModal from "./ChannelAdminModal";
import "./TvPage.css";

/**
 * /tv/:channelId? — passive broadcast mode (streaming-plan.md §7/§8).
 *
 * Joins the channel's current schedule item at the live offset, auto-advances,
 * and stays out of the way. No seeking — it's TV. Starts muted to satisfy
 * autoplay policy, with a tap-to-unmute affordance. Progress is reported with
 * passive=true so background play never claims you watched Heat.
 */
function TvPage({ userData }) {
  const { channelId } = useParams();
  const history = useHistory();

  const videoRef = useRef(null);
  const hlsRef = useRef(null);
  const sessionRef = useRef(null);
  const advanceTimerRef = useRef(null);
  const overlayTimerRef = useRef(null);
  const wakeLockRef = useRef(null);

  const [channels, setChannels] = useState(null); // null = loading
  const [channel, setChannel] = useState(null);
  const [now, setNow] = useState(null); // { current, next }
  const [muted, setMuted] = useState(true);
  const [overlayVisible, setOverlayVisible] = useState(true);
  const [guideOpen, setGuideOpen] = useState(false);
  const [guide, setGuide] = useState(null);
  const [staticBurst, setStaticBurst] = useState(false);
  const [error, setError] = useState(null);
  const [offAir, setOffAir] = useState(false);
  const [adminOpen, setAdminOpen] = useState(false);

  const canEdit = userData?.canEditMovies ?? false;

  // ── helpers ─────────────────────────────────────────────────────────────────
  const stopSession = useCallback((useBeacon = false) => {
    const s = sessionRef.current;
    if (!s) return;
    sessionRef.current = null;
    const payload = { playSessionId: s.playSessionId, movieId: s.movieId };
    if (useBeacon) MovieAPI.beaconStopStream(payload);
    else MovieAPI.stopStream(payload);
  }, []);

  const destroyHls = useCallback(() => {
    if (hlsRef.current) {
      hlsRef.current.destroy();
      hlsRef.current = null;
    }
  }, []);

  const wakeOverlay = useCallback(() => {
    setOverlayVisible(true);
    clearTimeout(overlayTimerRef.current);
    overlayTimerRef.current = setTimeout(() => setOverlayVisible(false), 4500);
  }, []);

  // ── tune to the channel's live position ─────────────────────────────────────
  const tune = useCallback(
    async (chan) => {
      if (!chan) return;
      clearTimeout(advanceTimerRef.current);
      stopSession();
      destroyHls();
      setOffAir(false);
      setStaticBurst(true);
      setTimeout(() => setStaticBurst(false), 420);
      wakeOverlay();

      try {
        const nowResponse = await fetch(`/API/Channel/${chan.id}/Now`);
        if (!nowResponse.ok) throw Object.assign(new Error(), { status: nowResponse.status });
        const nowData = await nowResponse.json();
        setNow(nowData);
        if (!nowData.current) {
          setOffAir(true);
          return;
        }

        const rungKey = window.localStorage.getItem("StreamQuality") || "original";
        const rung = QUALITY_LADDER.find((q) => q.key === rungKey) || QUALITY_LADDER[0];
        // TV is passive and doesn't adapt mid-play; "Auto" maps to a sane fixed cap
        // from the connection estimate rather than streaming uncapped in the background.
        const maxBitrateBps = rungKey === "auto" ? initialAutoBps() : rung.bps;
        const startResponse = await MovieAPI.startStream({
          movieId: nowData.current.movieId,
          maxBitrateBps,
          startSeconds: Math.floor(nowData.current.offsetSeconds),
        });
        if (!startResponse.ok) {
          const body = await startResponse.json().catch(() => ({}));
          throw Object.assign(new Error(body.message || ""), { status: startResponse.status });
        }
        const session = await startResponse.json();
        sessionRef.current = { playSessionId: session.playSessionId, movieId: nowData.current.movieId };

        const video = videoRef.current;
        if (!video) return;
        if (Hls.isSupported()) {
          const hls = new Hls({ maxBufferLength: 60, backBufferLength: 30 });
          hlsRef.current = hls;
          hls.on(Hls.Events.MANIFEST_PARSED, () => {
            video.currentTime = nowData.current.offsetSeconds;
            video.play().catch(() => {});
          });
          hls.on(Hls.Events.ERROR, (_e, data) => {
            if (!data.fatal) return;
            if (data.type === Hls.ErrorTypes.NETWORK_ERROR) hls.startLoad();
            else if (data.type === Hls.ErrorTypes.MEDIA_ERROR) hls.recoverMediaError();
          });
          hls.loadSource(session.hlsUrl);
          hls.attachMedia(video);
        } else {
          video.src = session.hlsUrl;
          video.addEventListener(
            "loadedmetadata",
            () => {
              video.currentTime = nowData.current.offsetSeconds;
              video.play().catch(() => {});
            },
            { once: true }
          );
        }

        // Advance when the schedule says this item ends (+ a little grace).
        const msUntilEnd = new Date(nowData.current.endsAtUtc).getTime() - Date.now();
        advanceTimerRef.current = setTimeout(() => tune(chan), Math.max(msUntilEnd, 5_000) + 3_000);
      } catch (err) {
        setError(err);
      }
    },
    [stopSession, destroyHls, wakeOverlay]
  );

  // ── channel list ────────────────────────────────────────────────────────────
  // keepSelection: after an admin edit, hold the current channel if it still exists
  // rather than snapping back to the first one.
  const loadChannels = useCallback(
    (keepSelection = false) =>
      fetch("/API/Channel/List")
        .then((r) => {
          if (!r.ok) throw Object.assign(new Error(), { status: r.status });
          return r.json();
        })
        .then((list) => {
          setChannels(list);
          setChannel((prev) => {
            if (keepSelection && prev) {
              const stillThere = list.find((c) => c.id === prev.id);
              if (stillThere) return stillThere;
            }
            const wanted = channelId ? list.find((c) => String(c.id) === String(channelId)) : list[0];
            return wanted || list[0] || null;
          });
        })
        .catch((err) => setError(err)),
    [channelId]
  );

  useEffect(() => {
    loadChannels();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  // Tune whenever the channel changes.
  useEffect(() => {
    if (channel) {
      history.replace(`/tv/${channel.id}`);
      tune(channel);
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [channel?.id]);

  // ── passive progress beat + ended fallback ─────────────────────────────────
  useEffect(() => {
    const beat = setInterval(() => {
      const video = videoRef.current;
      const s = sessionRef.current;
      if (video && s && !video.paused) {
        MovieAPI.reportStreamProgress({
          playSessionId: s.playSessionId,
          movieId: s.movieId,
          positionTicks: Math.round(video.currentTime * TICKS_PER_SECOND),
          paused: false,
          passive: true,
        });
      }
    }, 10_000);
    return () => clearInterval(beat);
  }, []);

  useEffect(() => {
    const video = videoRef.current;
    if (!video) return undefined;
    const onEnded = () => channel && tune(channel);
    video.addEventListener("ended", onEnded);
    return () => video.removeEventListener("ended", onEnded);
  }, [channel, tune]);

  // ── teardown / wake lock / keyboard ─────────────────────────────────────────
  useEffect(() => {
    const onPageHide = () => stopSession(true);
    window.addEventListener("pagehide", onPageHide);
    return () => {
      window.removeEventListener("pagehide", onPageHide);
      clearTimeout(advanceTimerRef.current);
      clearTimeout(overlayTimerRef.current);
      stopSession(true);
      destroyHls();
      wakeLockRef.current?.release?.().catch(() => {});
    };
  }, [stopSession, destroyHls]);

  useEffect(() => {
    const acquire = async () => {
      try {
        wakeLockRef.current = await navigator.wakeLock?.request("screen");
      } catch {
        /* not supported / denied — fine */
      }
    };
    acquire();
    const onVisibility = () => document.visibilityState === "visible" && acquire();
    document.addEventListener("visibilitychange", onVisibility);
    return () => document.removeEventListener("visibilitychange", onVisibility);
  }, []);

  const switchBy = useCallback(
    (delta) => {
      if (!channels?.length || !channel) return;
      const idx = channels.findIndex((c) => c.id === channel.id);
      const next = channels[(idx + delta + channels.length) % channels.length];
      setChannel(next);
    },
    [channels, channel]
  );

  useEffect(() => {
    const onKey = (e) => {
      if (e.target.tagName === "INPUT" || adminOpen) return;
      if (e.key === "ArrowUp") switchBy(1);
      else if (e.key === "ArrowDown") switchBy(-1);
      else if (e.key === "m") setMuted((m) => !m);
      else if (e.key === "g") setGuideOpen((g) => !g);
      else if (e.key === "f") {
        if (document.fullscreenElement) document.exitFullscreen();
        else document.querySelector(".tv-room")?.requestFullscreen?.();
      } else if (/^[1-9]$/.test(e.key) && channels) {
        const target = channels[parseInt(e.key, 10) - 1];
        if (target) setChannel(target);
      } else return;
      e.preventDefault();
      wakeOverlay();
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [switchBy, channels, wakeOverlay, adminOpen]);

  useEffect(() => {
    const video = videoRef.current;
    if (video) video.muted = muted;
  }, [muted]);

  // ── guide data ──────────────────────────────────────────────────────────────
  useEffect(() => {
    if (guideOpen && channel) {
      fetch(`/API/Channel/${channel.id}/Guide?hours=12`)
        .then((r) => (r.ok ? r.json() : []))
        .then(setGuide)
        .catch(() => setGuide([]));
    }
  }, [guideOpen, channel]);

  // ── presentation ────────────────────────────────────────────────────────────
  const channelNumber = channels && channel ? channels.findIndex((c) => c.id === channel.id) + 1 : null;
  const progressPct =
    now?.current?.durationSeconds > 0
      ? Math.min(((now.current.durationSeconds - (new Date(now.current.endsAtUtc).getTime() - Date.now()) / 1000) / now.current.durationSeconds) * 100, 100)
      : 0;

  const errorCopy = (() => {
    if (!error) return null;
    if (error.status === 401) return "Sign in to turn on the TV.";
    if (error.status === 403 && userData && !userData.hasPassword) return "Streaming is for password-protected accounts — set a password from the user menu.";
    if (error.status === 403) return "This TV isn't available on your account.";
    if (error.status === 404 || error.status === 501) return "The broadcast tower isn't built yet.";
    return error.message || "The signal dropped.";
  })();

  return (
    /* eslint-disable jsx-a11y/no-static-element-interactions, jsx-a11y/click-events-have-key-events, jsx-a11y/media-has-caption */
    <div className="tv-room" onMouseMove={wakeOverlay} onClick={wakeOverlay}>
      <video ref={videoRef} className="tv-video" autoPlay playsInline muted />

      {/* channel-change static burst */}
      <div className={`tv-static${staticBurst ? " tv-static--on" : ""}`} aria-hidden="true" />

      {/* persistent channel bug */}
      {channel && (
        <div className={`tv-bug${overlayVisible ? "" : " tv-bug--dim"}`}>
          <span className="tv-bug-num">{channelNumber}</span>
          <span className="tv-bug-name">{channel.name}</span>
        </div>
      )}

      {/* tap to unmute */}
      {muted && !error && !offAir && (
        <button className="tv-unmute" onClick={(e) => { e.stopPropagation(); setMuted(false); }}>
          <span className="tv-unmute-glyph">🔇</span> Tap to unmute
        </button>
      )}

      {/* now / next overlay */}
      {now?.current && (
        <div className={`tv-panel${overlayVisible ? "" : " tv-panel--hidden"}`}>
          <div className="tv-panel-row">
            <span className="tv-panel-label">Now</span>
            <span className="tv-panel-title">{now.current.title}</span>
            <span className="tv-panel-time">ends {new Date(now.current.endsAtUtc).toLocaleTimeString([], { hour: "numeric", minute: "2-digit" })}</span>
          </div>
          <div className="tv-panel-progress">
            <div className="tv-panel-progress-fill" style={{ width: `${progressPct}%` }} />
          </div>
          {now.next?.[0] && (
            <div className="tv-panel-row tv-panel-row--next">
              <span className="tv-panel-label">Next</span>
              <span className="tv-panel-title">{now.next[0].title}</span>
              <span className="tv-panel-time">{new Date(now.next[0].startsAtUtc).toLocaleTimeString([], { hour: "numeric", minute: "2-digit" })}</span>
            </div>
          )}
        </div>
      )}

      {/* channel switcher */}
      {channels && channels.length > 0 && (
        <div className={`tv-channels${overlayVisible ? "" : " tv-channels--hidden"}`}>
          {channels.map((c, i) => (
            <button
              key={c.id}
              className={`tv-channel-item${channel?.id === c.id ? " tv-channel-item--on" : ""}`}
              onClick={(e) => {
                e.stopPropagation();
                setChannel(c);
              }}
            >
              <span className="tv-channel-num">{i + 1}</span>
              {c.name}
            </button>
          ))}
          <button className={`tv-channel-item tv-channel-item--guide${guideOpen ? " tv-channel-item--on" : ""}`} onClick={(e) => { e.stopPropagation(); setGuideOpen((g) => !g); }}>
            <span className="tv-channel-num">G</span>
            Guide
          </button>
          {canEdit && (
            <button className="tv-channel-item tv-channel-item--manage" onClick={(e) => { e.stopPropagation(); setAdminOpen(true); }}>
              <span className="tv-channel-num">✎</span>
              Manage
            </button>
          )}
          <button className="tv-channel-item tv-channel-item--off" onClick={() => history.push("/")}>
            <span className="tv-channel-num">⏻</span>
            Off
          </button>
        </div>
      )}

      {/* EPG strip */}
      {guideOpen && guide && (
        <div className="tv-guide">
          {guide.map((g, i) => (
            <div key={i} className="tv-guide-item">
              <div className="tv-guide-time">
                {new Date(g.startUtc).toLocaleTimeString([], { hour: "numeric", minute: "2-digit" })}
              </div>
              <div className="tv-guide-title">{g.title}</div>
            </div>
          ))}
          {guide.length === 0 && <div className="tv-guide-item tv-guide-item--empty">Nothing scheduled</div>}
        </div>
      )}

      {/* full-screen states */}
      {channels && channels.length === 0 && !error && (
        <div className="tv-state">
          <div className="tv-state-head">No channels are broadcasting</div>
          <button className="tv-state-btn" onClick={() => history.push("/")}>← Back</button>
        </div>
      )}

      {offAir && !error && (
        <div className="tv-state">
          <div className="tv-state-head">Off the air</div>
          <p>This channel has nothing scheduled right now.</p>
          <button className="tv-state-btn" onClick={() => history.push("/")}>← Back</button>
        </div>
      )}

      {error && (
        <div className="tv-state">
          <div className="tv-state-head">No signal</div>
          <p>{errorCopy}</p>
          <button className="tv-state-btn" onClick={() => history.push("/")}>← Back</button>
        </div>
      )}

      {canEdit && (
        <ChannelAdminModal
          open={adminOpen}
          onClose={() => setAdminOpen(false)}
          onChanged={() => loadChannels(true)}
        />
      )}
    </div>
  );
}

export default TvPage;
