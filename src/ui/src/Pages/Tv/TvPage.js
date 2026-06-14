import { useState, useEffect, useRef, useCallback } from "react";
import { useParams, useHistory } from "react-router-dom";
import Hls from "hls.js";
import { MovieAPI } from "../../MovieAPI";
import { formatTime, TICKS_PER_SECOND, QUALITY_LADDER, HLS_LOAD_CONFIG } from "../Watch/VideoPlayer";
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

  const roomRef = useRef(null);
  const videoRef = useRef(null);
  const hlsRef = useRef(null);
  const sessionRef = useRef(null);
  const advanceTimerRef = useRef(null);
  const wakeLockRef = useRef(null);

  const [channels, setChannels] = useState(null); // null = loading
  const [channel, setChannel] = useState(null);
  const [now, setNow] = useState(null); // { current, next }
  const [muted, setMuted] = useState(true);
  const [volume, setVolume] = useState(() => {
    const v = parseFloat(window.localStorage.getItem("TvVolume"));
    return Number.isFinite(v) ? Math.min(Math.max(v, 0), 1) : 1;
  });
  const [pickerOpen, setPickerOpen] = useState(false); // channel picker popout
  const [menuOpen, setMenuOpen] = useState(false); // settings dropdown (quality / guide / manage / off)
  const [isFullscreen, setIsFullscreen] = useState(false);
  const [, setNowTick] = useState(0); // ticks every second to advance the live progress bar
  const [guideOpen, setGuideOpen] = useState(false);
  const [guide, setGuide] = useState(null);
  const [staticBurst, setStaticBurst] = useState(false);
  const [error, setError] = useState(null);
  const [offAir, setOffAir] = useState(false);
  const [adminOpen, setAdminOpen] = useState(false);
  const [quality, setQuality] = useState(() => window.localStorage.getItem("StreamQuality") || "auto");
  const [qualityOpen, setQualityOpen] = useState(false);
  const [skip, setSkip] = useState(null); // { viewers, votes, required, youVoted }
  const [restart, setRestart] = useState(null); // { viewers, votes, required, youVoted }
  const [tuning, setTuning] = useState(false); // waiting on the (cold) transcode to produce frames
  const [paused, setPaused] = useState(false); // shared channel pause — frozen for everyone watching

  const canEdit = userData?.canEditMovies ?? false;

  // tune() reads the current quality without re-binding on every change.
  const qualityRef = useRef(quality);
  qualityRef.current = quality;

  // Pause is read inside event handlers (onPlaying) that aren't re-bound on every change.
  const pausedRef = useRef(paused);
  pausedRef.current = paused;

  // The schedule item currently playing — used to scope skip votes and to notice when
  // the channel has moved on (a skip elsewhere, or a natural advance) so we re-tune.
  const currentItemIdRef = useRef(null);

  // The current item's scheduled end. A restart keeps the same itemId but pushes the end later
  // (the film replays from the top), so a later end on the same item is how other viewers notice.
  const currentEndsAtRef = useRef(null);

  // A transcode started ahead of time for the *next* item, so an advance is instant
  // instead of paying the ~8s cold start. { movieId, session } once warmed.
  const prewarmRef = useRef(null);
  const prewarmTimerRef = useRef(null);

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

  // Dismiss the channel picker / settings menu (e.g. on a click in the video area).
  const closePopouts = useCallback(() => {
    setPickerOpen(false);
    setMenuOpen(false);
  }, []);

  // The bitrate cap for the current quality (Auto → a connection-based fixed cap).
  const resolveBitrate = useCallback(() => {
    const rungKey = qualityRef.current;
    const rung = QUALITY_LADDER.find((q) => q.key === rungKey) || QUALITY_LADDER[0];
    return rungKey === "auto" ? initialAutoBps() : rung.bps;
  }, []);

  // Start the next item's transcode ahead of the boundary and warm it (fetch its
  // playlist), so the advance can reuse it instead of paying the cold start.
  const prewarmNext = useCallback(
    async (movieId) => {
      try {
        const r = await MovieAPI.startStream({ movieId, maxBitrateBps: resolveBitrate(), startSeconds: 0 });
        if (!r.ok) return;
        const session = await r.json();
        // For a transcode, pull the playlist to actually spawn ffmpeg; direct play has
        // nothing to warm (it's a static file).
        if (session.isHls !== false) fetch(session.hlsUrl).catch(() => {});
        prewarmRef.current = { movieId, session };
      } catch {
        /* prewarm is best-effort */
      }
    },
    [resolveBitrate]
  );

  // ── tune to the channel's live position ─────────────────────────────────────
  const tune = useCallback(
    async (chan) => {
      if (!chan) return;
      const seq = ++tuneSeqRef.current;
      const superseded = () => seq !== tuneSeqRef.current;
      clearTimeout(advanceTimerRef.current);
      clearTimeout(prewarmTimerRef.current);
      stopSession();
      destroyHls();
      setError(null);
      setOffAir(false);
      setTuning(true);
      setStaticBurst(true);
      setTimeout(() => setStaticBurst(false), 420);

      try {
        const nowResponse = await fetch(`/API/Channel/${chan.id}/Now`);
        if (superseded()) return;
        if (!nowResponse.ok) throw Object.assign(new Error(), { status: nowResponse.status });
        const nowData = await nowResponse.json();
        if (superseded()) return;
        setNow(nowData);
        if (!nowData.current) {
          setOffAir(true);
          setTuning(false);
          currentItemIdRef.current = null;
          currentEndsAtRef.current = null;
          setSkip(null);
          setRestart(null);
          return;
        }
        currentItemIdRef.current = nowData.current.itemId ?? null;
        currentEndsAtRef.current = nowData.current.endsAtUtc ?? null;
        setSkip(nowData.skip || null);
        setRestart(nowData.restart || null);
        setPaused(nowData.paused || false);

        // Reuse a transcode we prewarmed for this item near the last boundary (instant
        // advance); a prewarm is only valid for a fresh-start join (~offset 0).
        let session = null;
        const pw = prewarmRef.current;
        prewarmRef.current = null;
        if (pw) {
          if (pw.movieId === nowData.current.movieId && nowData.current.offsetSeconds < 8) {
            session = pw.session;
          } else {
            MovieAPI.stopStream({ playSessionId: pw.session.playSessionId, movieId: pw.movieId });
          }
        }

        if (!session) {
          // "Auto" (the default) maps to a connection-based fixed cap rather than streaming
          // uncapped — important on phones, where uncapped channels buffer and drift A/V.
          const startResponse = await MovieAPI.startStream({
            movieId: nowData.current.movieId,
            maxBitrateBps: resolveBitrate(),
            startSeconds: Math.floor(nowData.current.offsetSeconds),
          });
          if (superseded()) return;
          if (!startResponse.ok) {
            const body = await startResponse.json().catch(() => ({}));
            throw Object.assign(new Error(body.message || ""), { status: startResponse.status });
          }
          session = await startResponse.json();
        }
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
        if (session.isHls === false) {
          // Direct play: the original file. Seek to the live offset via a range request —
          // no transcode, so the channel joins near-instantly.
          video.src = session.hlsUrl;
          video.addEventListener(
            "loadedmetadata",
            () => {
              video.currentTime = joinAt;
              video.play().catch(() => {});
            },
            { once: true }
          );
        } else if (Hls.isSupported()) {
          // startPosition joins at the live offset directly instead of loading from 0 and
          // seeking — the seek churn was a source of the join-time A/V desync on mobile.
          const hls = new Hls({ maxBufferLength: 30, backBufferLength: 10, startPosition: joinAt, ...HLS_LOAD_CONFIG });
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

        // While paused the timeline is frozen: don't arm the auto-advance or prewarm, and hold the
        // picture on a still frame. A resume (ours or someone else's) re-tunes us at the live offset.
        if (nowData.paused) {
          video.pause();
        } else {
          // Advance when the schedule says this item ends (+ a little grace).
          const msUntilEnd = new Date(nowData.current.endsAtUtc).getTime() - Date.now();
          advanceTimerRef.current = setTimeout(() => tune(chan), Math.max(msUntilEnd, 5_000) + 3_000);

          // Prewarm the next item ~20s before the boundary so the advance is instant. Only
          // worth it when there's enough lead and we're not joining right at the end.
          const nextItem = nowData.next?.[0];
          if (nextItem && msUntilEnd > 30_000) {
            prewarmTimerRef.current = setTimeout(() => prewarmNext(nextItem.movieId), msUntilEnd - 20_000);
          }
        }
      } catch (err) {
        // Only the active tune may surface an error — a superseded one stays quiet so a
        // transient blip on an abandoned request can't leave a stuck "No signal".
        if (!superseded()) {
          setError(err);
          setTuning(false);
        }
      }
    },
    [stopSession, destroyHls, resolveBitrate, prewarmNext]
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
    const onPlaying = () => {
      setTuning(false); // first frames arrived — hide the "Tuning…" card
      if (pausedRef.current) videoRef.current?.pause(); // joined a frozen channel — hold the frame
    };
    video.addEventListener("ended", onEnded);
    video.addEventListener("playing", onPlaying);
    return () => {
      video.removeEventListener("ended", onEnded);
      video.removeEventListener("playing", onPlaying);
    };
  }, [channel, tune]);

  // ── teardown / wake lock / keyboard ─────────────────────────────────────────
  useEffect(() => {
    const onPageHide = () => stopSession(true);
    window.addEventListener("pagehide", onPageHide);
    return () => {
      window.removeEventListener("pagehide", onPageHide);
      clearTimeout(advanceTimerRef.current);
      clearTimeout(prewarmTimerRef.current);
      stopSession(true);
      // Don't leak a prewarmed transcode that never got consumed.
      if (prewarmRef.current) {
        MovieAPI.stopStream({
          playSessionId: prewarmRef.current.session.playSessionId,
          movieId: prewarmRef.current.movieId,
        });
        prewarmRef.current = null;
      }
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

  // Local, per-viewer volume (the shared channel state is only play/pause). Dragging to 0
  // mutes; dragging up unmutes. Persisted so the next visit remembers it.
  const toggleMute = useCallback(() => setMuted((m) => !m), []);
  const changeVolume = useCallback((v) => {
    setVolume(v);
    window.localStorage.setItem("TvVolume", String(v));
    setMuted(v === 0);
  }, []);

  // The picker and the settings menu are mutually exclusive popouts.
  const togglePicker = useCallback((e) => {
    e.stopPropagation();
    setMenuOpen(false);
    setPickerOpen((o) => !o);
  }, []);
  const toggleMenu = useCallback((e) => {
    e.stopPropagation();
    setPickerOpen(false);
    setMenuOpen((o) => !o);
  }, []);

  const toggleFullscreen = useCallback(() => {
    if (document.fullscreenElement) document.exitFullscreen();
    else roomRef.current?.requestFullscreen?.();
  }, []);

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
        setRestart(data.restart || null);

        // Shared pause: someone froze the channel — hold the frame and stop here (no advance while
        // frozen). When we were paused and the channel is now playing again, a resume happened
        // elsewhere; fall through so the schedule-shift below re-tunes us at the live offset.
        if (data.paused) {
          setPaused(true);
          videoRef.current?.pause();
          clearTimeout(advanceTimerRef.current);
          return;
        }
        if (pausedRef.current) setPaused(false);

        if (data.current && currentItemIdRef.current != null) {
          const advanced = data.current.itemId !== currentItemIdRef.current;
          // Same item, end pushed meaningfully later → the film was restarted or resumed; rejoin.
          const restarted =
            !advanced &&
            currentEndsAtRef.current &&
            new Date(data.current.endsAtUtc).getTime() - new Date(currentEndsAtRef.current).getTime() > 2000;
          if (advanced || restarted) tune(channel);
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

  // Cast a restart vote for the current item. If it carries the server rewinds the schedule item
  // to the top and we rejoin from the start; otherwise we just reflect the tally. (A lone viewer's
  // vote always carries — a single-viewer channel restarts the moment they ask.)
  const voteRestart = useCallback(async () => {
    if (!channel) return;
    try {
      const r = await fetch(`/API/Channel/${channel.id}/Restart`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ itemId: currentItemIdRef.current ?? 0 }),
      });
      if (!r.ok) return;
      const data = await r.json();
      if (data.restarted) tune(channel);
      else setRestart(data.restart || null);
    } catch {
      /* ignore — they can tap again */
    }
  }, [channel, tune]);

  // Flip the shared pause. Anyone watching can freeze or resume the channel for everyone — no vote.
  // Resuming re-tunes at the (now schedule-shifted) live offset; pausing holds the current frame.
  const togglePlayPause = useCallback(async () => {
    if (!channel) return;
    try {
      const r = await fetch(`/API/Channel/${channel.id}/PlayPause`, { method: "POST" });
      if (!r.ok) return;
      const data = await r.json();
      setPaused(data.paused);
      if (data.paused) {
        videoRef.current?.pause();
        clearTimeout(advanceTimerRef.current); // frozen — no auto-advance
        clearTimeout(prewarmTimerRef.current);
      } else {
        tune(channel); // rejoin where the channel left off
      }
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
      else if (e.key === "k" || e.key === " ") togglePlayPause();
      else if (e.key === "f") toggleFullscreen();
      else if (/^[1-9]$/.test(e.key) && channels) {
        const target = channels[parseInt(e.key, 10) - 1];
        if (target) setChannel(target);
      } else return;
      e.preventDefault();
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [switchBy, channels, adminOpen, togglePlayPause, toggleFullscreen]);

  useEffect(() => {
    const video = videoRef.current;
    if (!video) return;
    video.muted = muted;
    video.volume = volume;
  }, [muted, volume]);

  // Keep the fullscreen button's icon in sync with the actual state (incl. Esc to exit).
  useEffect(() => {
    const onChange = () => setIsFullscreen(!!document.fullscreenElement);
    document.addEventListener("fullscreenchange", onChange);
    return () => document.removeEventListener("fullscreenchange", onChange);
  }, []);

  // Tick once a second while something is playing so the progress bar advances smoothly
  // between the (infrequent) schedule polls. progressPct is recomputed from the clock on render.
  useEffect(() => {
    if (!now?.current || paused) return undefined;
    const t = setInterval(() => setNowTick((n) => n + 1), 1000);
    return () => clearInterval(t);
  }, [now?.current, paused]);

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

  const volumeIcon = muted || volume === 0 ? "🔇" : volume < 0.5 ? "🔉" : "🔊";

  return (
    /* eslint-disable jsx-a11y/no-static-element-interactions, jsx-a11y/click-events-have-key-events, jsx-a11y/media-has-caption */
    <div className="tv-room" ref={roomRef}>
      {/* The picture area. Nothing in the control bar overlaps it; only the picker / menu /
          guide pop out over it, and only while open. */}
      <div className="tv-screen" onClick={closePopouts}>
        <video ref={videoRef} className="tv-video" autoPlay playsInline muted />

        {/* channel-change static burst */}
        <div className={`tv-static${staticBurst ? " tv-static--on" : ""}`} aria-hidden="true" />

        {/* cold-transcode wait — the source takes a few seconds to start; show it's working */}
        {tuning && !error && !offAir && (
          <div className="tv-tuning" aria-label="Tuning">
            <div className="tv-tuning-bulbs"><span /><span /><span /></div>
            <div className="tv-tuning-label">Tuning in…</div>
          </div>
        )}

        {/* shared pause — the channel is frozen for everyone watching */}
        {paused && !tuning && !error && !offAir && (
          <div className="tv-paused" aria-label="Paused">
            <span className="tv-paused-glyph">❚❚</span>
            <span className="tv-paused-label">Paused</span>
          </div>
        )}

        {/* channel picker — pops out over the picture, anchored above the channel button */}
        {pickerOpen && channels && channels.length > 0 && (
          <div className="tv-picker" onClick={(e) => e.stopPropagation()}>
            <div className="tv-picker-head">Channels</div>
            <div className="tv-picker-list">
              {channels.map((c, i) => (
                <button
                  key={c.id}
                  className={`tv-channel-item${channel?.id === c.id ? " tv-channel-item--on" : ""}`}
                  onClick={() => { setChannel(c); setPickerOpen(false); }}
                >
                  <span className="tv-channel-num">{i + 1}</span>
                  {c.name}
                </button>
              ))}
            </div>
          </div>
        )}

        {/* settings menu — quality / guide / manage / off */}
        {menuOpen && (
          <div className="tv-menu" onClick={(e) => e.stopPropagation()}>
            <button
              className={`tv-channel-item${qualityOpen ? " tv-channel-item--on" : ""}`}
              onClick={() => setQualityOpen((q) => !q)}
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
                  onClick={() => selectQuality(q.key)}
                >
                  <span className="tv-channel-num">·</span>
                  {q.label}
                  {q.hint ? <span className="tv-qopt-hint">{q.hint}</span> : null}
                </button>
              ))}
            <button
              className={`tv-channel-item${guideOpen ? " tv-channel-item--on" : ""}`}
              onClick={() => { setGuideOpen((g) => !g); setMenuOpen(false); }}
            >
              <span className="tv-channel-num">G</span>
              Guide
            </button>
            {canEdit && (
              <button className="tv-channel-item" onClick={() => { setAdminOpen(true); setMenuOpen(false); }}>
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
          <div className="tv-guide" onClick={(e) => e.stopPropagation()}>
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
      </div>

      {/* flattened control bar — lives below the picture so it never covers it */}
      <div className="tv-bar">
        <div className="tv-bar-progress">
          <div className="tv-bar-progress-fill" style={{ width: `${progressPct}%` }} />
        </div>
        <div className="tv-bar-row">
          {channel && (
            <button className="tv-bar-channel" onClick={togglePicker} title="Change channel">
              <span className="tv-bar-channel-num">{channelNumber}</span>
              <span className="tv-bar-channel-name">{channel.name}</span>
              <span className="tv-bar-caret">▾</span>
            </button>
          )}

          {now?.current && (
            <div className="tv-bar-info">
              <div className="tv-bar-now">
                <span className="tv-bar-tag">Now</span>
                <span className="tv-bar-title">{now.current.title}</span>
                <span className="tv-bar-time">ends {new Date(now.current.endsAtUtc).toLocaleTimeString([], { hour: "numeric", minute: "2-digit" })}</span>
              </div>
              {now.next?.[0] && (
                <div className="tv-bar-next">
                  <span className="tv-bar-tag">Next</span>
                  <span className="tv-bar-title">{now.next[0].title}</span>
                  <span className="tv-bar-time">{new Date(now.next[0].startsAtUtc).toLocaleTimeString([], { hour: "numeric", minute: "2-digit" })}</span>
                </div>
              )}
            </div>
          )}

          <div className="tv-bar-spacer" />

          {!paused && (skip?.viewers > 1 || restart?.viewers > 1) && (
            <span className="tv-bar-watching">{skip?.viewers ?? restart?.viewers} watching</span>
          )}

          {now?.current && (
            <div className="tv-bar-transport">
              {/* Skip/restart act on the live timeline, so they're hidden while frozen — resume first. */}
              {restart && !paused && (
                <button
                  className={`tv-skip tv-restart${restart.youVoted ? " tv-skip--voted" : ""}`}
                  onClick={(e) => { e.stopPropagation(); voteRestart(); }}
                  title={restart.viewers > 1 ? (restart.youVoted ? "Voted to restart" : "Vote to restart") : "Restart"}
                >
                  <span className="tv-skip-glyph">⏮</span>
                  {restart.viewers > 1 && <span className="tv-skip-tally">{restart.votes}/{restart.required}</span>}
                </button>
              )}
              {/* shared play/pause — anyone watching freezes or resumes the channel for everyone */}
              <button
                className={`tv-skip tv-playpause${paused ? " tv-skip--voted tv-playpause--paused" : ""}`}
                onClick={(e) => { e.stopPropagation(); togglePlayPause(); }}
                title={paused ? "Resume" : "Pause"}
              >
                <span className="tv-skip-glyph">{paused ? "▶" : "⏸"}</span>
              </button>
              {skip && !paused && (
                <button
                  className={`tv-skip${skip.youVoted ? " tv-skip--voted" : ""}`}
                  onClick={(e) => { e.stopPropagation(); voteSkip(); }}
                  title={skip.viewers > 1 ? (skip.youVoted ? "Voted to skip" : "Vote to skip") : "Skip"}
                >
                  <span className="tv-skip-glyph">⏭</span>
                  {skip.viewers > 1 && <span className="tv-skip-tally">{skip.votes}/{skip.required}</span>}
                </button>
              )}
            </div>
          )}

          <div className="tv-bar-volume">
            <button
              className={`tv-bar-icon-btn${muted || volume === 0 ? " tv-bar-icon-btn--pulse" : ""}`}
              onClick={(e) => { e.stopPropagation(); toggleMute(); }}
              title={muted ? "Unmute" : "Mute"}
            >
              {volumeIcon}
            </button>
            <input
              className="tv-bar-volume-slider"
              type="range"
              min="0"
              max="1"
              step="0.01"
              value={muted ? 0 : volume}
              onChange={(e) => changeVolume(parseFloat(e.target.value))}
              onClick={(e) => e.stopPropagation()}
              aria-label="Volume"
            />
          </div>

          <button
            className="tv-bar-icon-btn"
            onClick={(e) => { e.stopPropagation(); toggleFullscreen(); }}
            title={isFullscreen ? "Exit fullscreen" : "Fullscreen"}
          >
            <span className={`tv-glyph-fs${isFullscreen ? " tv-glyph-fs--exit" : ""}`} />
          </button>

          <button
            className={`tv-bar-icon-btn${menuOpen ? " tv-bar-icon-btn--on" : ""}`}
            onClick={toggleMenu}
            title="Menu"
          >
            ⚙
          </button>
        </div>
      </div>

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
