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
  const [quality, setQuality] = useState(() => window.localStorage.getItem("StreamQuality") || "auto");
  const [qualityOpen, setQualityOpen] = useState(false);
  const [skip, setSkip] = useState(null); // { viewers, votes, required, youVoted }

  const canEdit = userData?.canEditMovies ?? false;

  // tune() reads the current quality without re-binding on every change.
  const qualityRef = useRef(quality);
  qualityRef.current = quality;

  // The schedule item currently playing — used to scope skip votes and to notice when
  // the channel has moved on (a skip elsewhere, or a natural advance) so we re-tune.
  const currentItemIdRef = useRef(null);

  // Monotonic id for the in-flight tune. The auto-advance timer and the video's
  // `ended` event can both fire tune() around a boundary; this lets a superseded
  // tune bail out instead of stomping a newer one with a stale error or stream.
  const tuneSeqRef = useRef(0);

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

  // A tap always *shows* the chrome and resets the idle timer — never hides it. (An
  // earlier tap-to-toggle made the picker un-tappable on touch devices: a stray tap
  // hid it, and while hidden the buttons are pointer-events:none.) It fades on its own
  // when idle, and touching the picker keeps it alive while you browse.
  const wakeOverlay = useCallback(() => {
    setOverlayVisible(true);
    clearTimeout(overlayTimerRef.current);
    overlayTimerRef.current = setTimeout(() => setOverlayVisible(false), 4500);
  }, []);

  // ── tune to the channel's live position ─────────────────────────────────────
  const tune = useCallback(
    async (chan) => {
      if (!chan) return;
      const seq = ++tuneSeqRef.current;
      const superseded = () => seq !== tuneSeqRef.current;
      clearTimeout(advanceTimerRef.current);
      stopSession();
      destroyHls();
      setError(null);
      setOffAir(false);
      setStaticBurst(true);
      setTimeout(() => setStaticBurst(false), 420);
      wakeOverlay();

      try {
        const nowResponse = await fetch(`/API/Channel/${chan.id}/Now`);
        if (superseded()) return;
        if (!nowResponse.ok) throw Object.assign(new Error(), { status: nowResponse.status });
        const nowData = await nowResponse.json();
        if (superseded()) return;
        setNow(nowData);
        if (!nowData.current) {
          setOffAir(true);
          currentItemIdRef.current = null;
          setSkip(null);
          return;
        }
        currentItemIdRef.current = nowData.current.itemId ?? null;
        setSkip(nowData.skip || null);

        // TV is passive and doesn't adapt mid-play; "Auto" (the default) maps to a sane
        // fixed cap from the connection estimate rather than streaming uncapped — important
        // on phones, where an uncapped channel buffers and drifts out of A/V sync.
        const rungKey = qualityRef.current;
        const rung = QUALITY_LADDER.find((q) => q.key === rungKey) || QUALITY_LADDER[0];
        const maxBitrateBps = rungKey === "auto" ? initialAutoBps() : rung.bps;
        const startResponse = await MovieAPI.startStream({
          movieId: nowData.current.movieId,
          maxBitrateBps,
          startSeconds: Math.floor(nowData.current.offsetSeconds),
        });
        if (superseded()) return;
        if (!startResponse.ok) {
          const body = await startResponse.json().catch(() => ({}));
          throw Object.assign(new Error(body.message || ""), { status: startResponse.status });
        }
        const session = await startResponse.json();
        if (superseded()) {
          // A newer tune started while we were mid-request — drop this stream so we
          // don't leak a transcode or attach a stale source.
          MovieAPI.stopStream({ playSessionId: session.playSessionId, movieId: nowData.current.movieId });
          return;
        }
        sessionRef.current = { playSessionId: session.playSessionId, movieId: nowData.current.movieId };

        const video = videoRef.current;
        if (!video) return;
        const joinAt = nowData.current.offsetSeconds;
        if (Hls.isSupported()) {
          // startPosition joins at the live offset directly instead of loading from 0 and
          // seeking — the seek churn was a source of the join-time A/V desync on mobile.
          const hls = new Hls({ maxBufferLength: 30, backBufferLength: 10, startPosition: joinAt });
          hlsRef.current = hls;
          hls.on(Hls.Events.MANIFEST_PARSED, () => {
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
          // Safari native HLS: seek on metadata is the only join lever available.
          video.src = session.hlsUrl;
          video.addEventListener(
            "loadedmetadata",
            () => {
              video.currentTime = joinAt;
              video.play().catch(() => {});
            },
            { once: true }
          );
        }

        // Advance when the schedule says this item ends (+ a little grace).
        const msUntilEnd = new Date(nowData.current.endsAtUtc).getTime() - Date.now();
        advanceTimerRef.current = setTimeout(() => tune(chan), Math.max(msUntilEnd, 5_000) + 3_000);
      } catch (err) {
        // Only the active tune may surface an error — a superseded one stays quiet so a
        // transient blip on an abandoned request can't leave a stuck "No signal".
        if (!superseded()) setError(err);
      }
    },
    [stopSession, destroyHls, wakeOverlay]
  );

  // ── channel list ────────────────────────────────────────────────────────────
  // keepSelection: after an admin edit, hold the current channel if it still exists
  // rather than snapping back to the first one.
  const loadChannels = useCallback(
    (keepSelection = false) => {
      setError(null);
      return fetch("/API/Channel/List")
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
        .catch((err) => setError(err));
    },
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

  // Quality is shared with the Watch page via localStorage; changing it re-tunes the
  // current channel at the live offset with the new bitrate cap.
  const selectQuality = useCallback(
    (key) => {
      qualityRef.current = key;
      setQuality(key);
      window.localStorage.setItem("StreamQuality", key);
      setQualityOpen(false);
      if (channel) tune(channel);
    },
    [channel, tune]
  );

  // Poll "what's on now" while a channel is up: it keeps the skip tally fresh, doubles as
  // the presence heartbeat (the server counts a poll as "still watching"), and re-tunes if
  // the channel has moved on — a skip the group passed, or a natural advance we missed.
  useEffect(() => {
    if (!channel) return undefined;
    const poll = setInterval(async () => {
      try {
        const r = await fetch(`/API/Channel/${channel.id}/Now`);
        if (!r.ok) return;
        const data = await r.json();
        setSkip(data.skip || null);
        if (data.current && currentItemIdRef.current != null && data.current.itemId !== currentItemIdRef.current) {
          tune(channel);
        }
      } catch {
        /* transient — the next poll retries */
      }
    }, 12_000);
    return () => clearInterval(poll);
  }, [channel, tune]);

  // Cast a skip vote for the current item. If it carries the majority the server collapses
  // the schedule and we jump straight to the next movie; otherwise we just reflect the tally.
  const voteSkip = useCallback(async () => {
    if (!channel) return;
    try {
      const r = await fetch(`/API/Channel/${channel.id}/Skip`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ itemId: currentItemIdRef.current ?? 0 }),
      });
      if (!r.ok) return;
      const data = await r.json();
      if (data.skipped) tune(channel);
      else setSkip(data.skip || null);
    } catch {
      /* ignore — they can tap again */
    }
  }, [channel, tune]);

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
          {skip && (
            <div className="tv-panel-skip">
              <button
                className={`tv-skip${skip.youVoted ? " tv-skip--voted" : ""}`}
                onClick={(e) => { e.stopPropagation(); voteSkip(); }}
              >
                <span className="tv-skip-glyph">⏭</span>
                {skip.viewers > 1 ? (skip.youVoted ? "Voted to skip" : "Vote to skip") : "Skip"}
                {skip.viewers > 1 && <span className="tv-skip-tally">{skip.votes}/{skip.required}</span>}
              </button>
              {skip.viewers > 1 && <span className="tv-skip-viewers">{skip.viewers} watching</span>}
            </div>
          )}
        </div>
      )}

      {/* channel switcher */}
      {channels && channels.length > 0 && (
        <div
          className={`tv-channels${overlayVisible ? "" : " tv-channels--hidden"}`}
          onTouchStart={wakeOverlay}
        >
          {channels.map((c, i) => (
            <button
              key={c.id}
              className={`tv-channel-item${channel?.id === c.id ? " tv-channel-item--on" : ""}`}
              onClick={(e) => {
                e.stopPropagation();
                setChannel(c);
                wakeOverlay();
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
          <button
            className={`tv-channel-item tv-channel-item--quality${qualityOpen ? " tv-channel-item--on" : ""}`}
            onClick={(e) => { e.stopPropagation(); setQualityOpen((q) => !q); }}
          >
            <span className="tv-channel-num">Q</span>
            Quality
            <span className="tv-qopt-hint">{QUALITY_LADDER.find((q) => q.key === quality)?.label || "Auto"}</span>
          </button>
          {qualityOpen &&
            QUALITY_LADDER.map((q) => (
              <button
                key={q.key}
                className={`tv-channel-item tv-channel-item--qopt${quality === q.key ? " tv-channel-item--on" : ""}`}
                onClick={(e) => { e.stopPropagation(); selectQuality(q.key); }}
              >
                <span className="tv-channel-num">·</span>
                {q.label}
                {q.hint ? <span className="tv-qopt-hint">{q.hint}</span> : null}
              </button>
            ))}
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
