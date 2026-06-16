import { useState, useEffect, useRef, useCallback, useMemo } from "react";
import { useParams, useHistory, useLocation } from "react-router-dom";
import { MovieAPI } from "../../MovieAPI";
import { initialAutoBps, rungDown, rungUp, shouldStepUp, isBottomRung, autoBpsLabel } from "../../streamAbr";
import VideoPlayer, { formatTime, TICKS_PER_SECOND, QUALITY_LADDER } from "./VideoPlayer";
import "./WatchPage.css";

// Adaptive-bitrate pacing (§14.4): don't switch rungs more than once per window,
// and only climb after a sustained good streak — each switch is a visible reload.
const ABR_COOLDOWN_MS = 20_000;
const ABR_STABLE_FOR_UP_MS = 90_000;

// Format whole minutes as "2h 16m", matching the modal's convention.
function formatRuntime(minutes) {
  if (!minutes || minutes <= 0) return null;
  const h = Math.floor(minutes / 60);
  const m = minutes % 60;
  return h > 0 ? `${h}h${m ? " " + m + "m" : ""}` : `${m}m`;
}

/**
 * /watch/:movieId — the screening room (streaming-plan.md §7).
 *
 * The path id is the *context* title (a movie, or a series for an episode); the
 * stream target is refined by query params:
 *   ?kind=series&playableId=N  → play episode N of the series
 *   ?mediaFileId=N             → play a specific Part / Variant / Extra of the movie
 * Bare /watch/:movieId still plays a movie's Primary file (the common case).
 *
 * Owns the streaming session: Start (with quality/audio/subtitle restarts at
 * position), the ~10s progress beat, and the Stop beacon that kills the
 * server-side transcode when the tab closes.
 */
function WatchPage({ userData }) {
  const { movieId } = useParams();
  const history = useHistory();
  const { search } = useLocation();

  // What to stream: a movie id (legacy → its Primary), and/or an explicit
  // playableId (episode / misc) and a specific mediaFileId. Server resolves
  // movieId → playableId when playableId is absent; for a series the path id is
  // only context (poster / title), so movieId is left null.
  const kind = useMemo(() => new URLSearchParams(search).get("kind") || "movie", [search]);
  const streamTarget = useMemo(() => {
    const q = new URLSearchParams(search);
    const num = (v) => (v != null && v !== "" ? Number(v) : null);
    return {
      movieId: kind === "series" ? null : Number(movieId),
      playableId: num(q.get("playableId")),
      mediaFileId: num(q.get("mediaFileId")),
    };
  }, [search, movieId, kind]);
  const streamTargetRef = useRef(streamTarget);
  streamTargetRef.current = streamTarget;

  const [movie, setMovie] = useState(null);
  const [normalized, setNormalized] = useState(null);
  const [phase, setPhase] = useState("loading"); // loading | resume | playing | ended | error
  const [error, setError] = useState(null);
  const [session, setSession] = useState(null); // Stream/Start response
  const [startAt, setStartAt] = useState(0);
  const [qualityKey, setQualityKey] = useState(() => window.localStorage.getItem("StreamQuality") || "auto");
  const [audioIndex, setAudioIndex] = useState(null);
  const [subtitleIndex, setSubtitleIndex] = useState(null);
  const [autoBps, setAutoBps] = useState(() => initialAutoBps());

  const sessionRef = useRef(null);
  sessionRef.current = session;
  const positionRef = useRef(0);

  // ── adaptive-bitrate state (read inside callbacks without re-binding them) ──
  const qualityKeyRef = useRef(qualityKey);
  qualityKeyRef.current = qualityKey;
  const autoBpsRef = useRef(autoBps);
  autoBpsRef.current = autoBps;
  const lastSwitchAtRef = useRef(0);
  const stableSinceRef = useRef(Date.now());

  const goBack = useCallback(() => {
    if (history.length > 1) history.goBack();
    else history.push("/");
  }, [history]);

  // ── session helpers ─────────────────────────────────────────────────────────
  const stopCurrentSession = useCallback((useBeacon = false) => {
    const s = sessionRef.current;
    if (!s) return;
    const payload = { playSessionId: s.playSessionId, ...streamTargetRef.current };
    if (useBeacon) MovieAPI.beaconStopStream(payload);
    else MovieAPI.stopStream(payload);
  }, []);

  const startSession = useCallback(
    async ({ startSeconds = null, quality = qualityKey, audio = audioIndex, subtitle = subtitleIndex, burnSubtitle = false, bpsOverride } = {}) => {
      // Auto walks the adaptive ladder (current cap in autoBpsRef); the fixed rungs
      // use their own cap; an explicit override (an adaptive switch) wins.
      const bps =
        bpsOverride !== undefined
          ? bpsOverride
          : quality === "auto"
          ? autoBpsRef.current
          : (QUALITY_LADDER.find((q) => q.key === quality) || QUALITY_LADDER[0]).bps;
      const response = await MovieAPI.startStream({
        ...streamTargetRef.current,
        maxBitrateBps: bps,
        audioStreamIndex: audio,
        subtitleStreamIndex: burnSubtitle ? subtitle : null,
        startSeconds,
      });
      if (!response.ok) {
        const body = await response.json().catch(() => ({}));
        const err = { status: response.status, message: body.message };
        throw err;
      }
      return await response.json();
    },
    [qualityKey, audioIndex, subtitleIndex]
  );

  // ── initial load: movie meta + a session (not yet attached to <video>) ─────
  useEffect(() => {
    let cancelled = false;

    Promise.all([
      MovieAPI.getTitle(movieId, kind)
        .then((r) => r.json())
        .catch(() => null),
      startSession(),
    ])
      .then(([titleBody, startData]) => {
        if (cancelled) return;
        if (titleBody?.data) {
          setMovie(titleBody.data);
          setNormalized(titleBody.normalized || null);
        }
        setSession(startData);
        const resumeTicks = startData.resumePositionTicks;
        if (resumeTicks && resumeTicks / TICKS_PER_SECOND > 60) {
          setPhase("resume");
        } else {
          setPhase("playing");
        }
      })
      .catch((err) => {
        if (cancelled) return;
        setError(err);
        setPhase("error");
      });

    return () => {
      cancelled = true;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [movieId, search]);

  // ── teardown: leaving the page or closing the tab kills the transcode ──────
  useEffect(() => {
    const onPageHide = () => stopCurrentSession(true);
    const onVisibility = () => {
      // A backgrounded tab reports where it is so server throttling stays honest.
      if (document.visibilityState === "hidden" && sessionRef.current) {
        MovieAPI.reportStreamProgress({
          playSessionId: sessionRef.current.playSessionId,
          ...streamTargetRef.current,
          positionTicks: Math.round(positionRef.current * TICKS_PER_SECOND),
          paused: false,
        });
      }
    };
    window.addEventListener("pagehide", onPageHide);
    document.addEventListener("visibilitychange", onVisibility);
    return () => {
      window.removeEventListener("pagehide", onPageHide);
      document.removeEventListener("visibilitychange", onVisibility);
      stopCurrentSession(true);
    };
  }, [stopCurrentSession]);

  // ── player callbacks ────────────────────────────────────────────────────────
  const handleProgress = useCallback(
    (seconds, paused) => {
      positionRef.current = seconds;
      const s = sessionRef.current;
      if (!s) return;
      MovieAPI.reportStreamProgress({
        playSessionId: s.playSessionId,
        ...streamTargetRef.current,
        positionTicks: Math.round(seconds * TICKS_PER_SECOND),
        paused,
      });
    },
    []
  );

  const restartAtPosition = useCallback(
    async (overrides) => {
      const position = positionRef.current;
      stopCurrentSession();
      try {
        const next = await startSession({ startSeconds: position, ...overrides });
        setStartAt(position);
        setSession(next);
      } catch (err) {
        setError(err);
        setPhase("error");
      }
    },
    [startSession, stopCurrentSession]
  );

  // Move the adaptive cap and restart at the live position. Updates the ref first so
  // the restart picks up the new cap synchronously, ahead of the state re-render.
  const adaptTo = useCallback(
    (nextBps) => {
      if (nextBps === autoBpsRef.current) return;
      autoBpsRef.current = nextBps;
      setAutoBps(nextBps);
      lastSwitchAtRef.current = Date.now();
      stableSinceRef.current = Date.now();
      restartAtPosition({ quality: "auto", bpsOverride: nextBps });
    },
    [restartAtPosition]
  );

  // Stall = the connection can't keep up: drop a rung immediately (within cooldown).
  const handleStall = useCallback(() => {
    if (qualityKeyRef.current !== "auto" || isBottomRung(autoBpsRef.current)) return;
    if (Date.now() - lastSwitchAtRef.current < ABR_COOLDOWN_MS) return;
    adaptTo(rungDown(autoBpsRef.current));
  }, [adaptTo]);

  // Throughput telemetry: climb a rung only after a sustained streak with clear
  // headroom; any sample short of that headroom resets the streak.
  const handleBandwidth = useCallback(
    (estimateBps) => {
      if (qualityKeyRef.current !== "auto") return;
      if (!shouldStepUp(autoBpsRef.current, estimateBps)) {
        stableSinceRef.current = Date.now();
        return;
      }
      if (Date.now() - lastSwitchAtRef.current < ABR_COOLDOWN_MS) return;
      if (Date.now() - stableSinceRef.current >= ABR_STABLE_FOR_UP_MS) {
        adaptTo(rungUp(autoBpsRef.current));
      }
    },
    [adaptTo]
  );

  const handleSelectQuality = useCallback(
    (rung) => {
      setQualityKey(rung.key);
      window.localStorage.setItem("StreamQuality", rung.key);
      // Re-entering Auto reseeds from the connection estimate and a fresh streak.
      if (rung.key === "auto") {
        const seed = initialAutoBps();
        autoBpsRef.current = seed;
        setAutoBps(seed);
        stableSinceRef.current = Date.now();
        restartAtPosition({ quality: "auto", bpsOverride: seed });
      } else {
        restartAtPosition({ quality: rung.key });
      }
    },
    [restartAtPosition]
  );

  const handleSelectAudio = useCallback(
    (track) => {
      setAudioIndex(track.index);
      restartAtPosition({ audio: track.index });
    },
    [restartAtPosition]
  );

  const handleSelectSubtitle = useCallback(
    (index) => {
      setSubtitleIndex(index);
      const track = session?.subtitleTracks?.find((t) => t.index === index);
      // Sidecar text tracks toggle client-side; image subs need a burn-in restart.
      if (track && !track.deliveryUrl) {
        restartAtPosition({ subtitle: index, burnSubtitle: true });
      }
    },
    [session, restartAtPosition]
  );

  const handleEnded = useCallback(
    (seconds) => {
      handleProgress(seconds, true);
      stopCurrentSession();
      setPhase("ended");
    },
    [handleProgress, stopCurrentSession]
  );

  // ── derived presentation ───────────────────────────────────────────────────
  const title = movie?.title || "";
  // When an episode or a specific file is in play, find it for a "S1E2 · Title" /
  // "Director's Cut" sub-label and a more accurate runtime.
  const episodeInfo = useMemo(() => {
    if (streamTarget.playableId == null || !Array.isArray(normalized?.seasons)) return null;
    for (const s of normalized.seasons) {
      const ep = (s.episodes || []).find((e) => e.playableId === streamTarget.playableId);
      if (ep) return { season: s.season, ...ep };
    }
    return null;
  }, [normalized, streamTarget.playableId]);
  const fileInfo = useMemo(() => {
    if (streamTarget.mediaFileId == null || !Array.isArray(normalized?.files)) return null;
    return normalized.files.find((f) => f.mediaFileId === streamTarget.mediaFileId) || null;
  }, [normalized, streamTarget.mediaFileId]);

  const FILE_ROLE_LABEL = { Part: "Part", Variant: "Variant", Extra: "Extra" };
  const subLabel = episodeInfo
    ? `S${episodeInfo.season}E${episodeInfo.episode}${episodeInfo.title ? " · " + episodeInfo.title : ""}`
    : fileInfo && fileInfo.role && fileInfo.role !== "Primary"
    ? fileInfo.label || `${FILE_ROLE_LABEL[fileInfo.role] || fileInfo.role}${fileInfo.partNumber ? " " + fileInfo.partNumber : ""}`
    : null;

  const year = movie?.releaseDate ? new Date(movie.releaseDate).getFullYear() : null;
  const runtime = formatRuntime(episodeInfo?.runtimeMinutes || normalized?.runtimeMinutes) || movie?.runtime;
  const metaLine = [subLabel, year, !episodeInfo ? movie?.rating : null, runtime].filter(Boolean).join("  ·  ");
  const poster = movie ? MovieAPI.getMoviePoster(movie.id, movie.posterVersion) : null;
  const durationSeconds = session ? session.durationTicks / TICKS_PER_SECOND : 0;
  const resumeSeconds = session?.resumePositionTicks ? session.resumePositionTicks / TICKS_PER_SECOND : 0;

  const errorCopy = (() => {
    if (!error) return null;
    if (error.status === 401) return { head: "Please sign in", body: "You need to be signed in to enter the screening room." };
    // Passwordless accounts fall through to the generic 403 — we don't tell them
    // streaming exists, since only an admin can grant a first password anyway.
    if (error.status === 403) return { head: "Not this picture", body: error.message || "This movie isn't available on your account." };
    if (error.status === 503) return { head: "The theater is full", body: error.message || "Too many screens are running right now — try again in a few minutes." };
    if (error.status === 404 || error.status === 501)
      return { head: "The projector isn't installed yet", body: "Streaming hasn't been switched on for this server." };
    return { head: "Something broke the reel", body: error.message || "The stream could not be started." };
  })();

  return (
    <div className="watch-room">
      {/* poster ember backdrop behind every non-playing state */}
      {phase !== "playing" && poster && (
        <div className="watch-backdrop" style={{ backgroundImage: `url(${poster})` }} aria-hidden="true" />
      )}

      {phase === "loading" && (
        <div className="watch-card watch-card--entrance">
          <div className="watch-overline">Now Seating</div>
          <h1 className="watch-title">{title || " "}</h1>
          <div className="watch-rule" />
          <div className="watch-meta">{metaLine || " "}</div>
          <div className="watch-bulbs"><span /><span /><span /></div>
        </div>
      )}

      {phase === "resume" && (
        <div className="watch-card watch-card--entrance">
          <div className="watch-overline">Welcome Back</div>
          <h1 className="watch-title">{title}</h1>
          <div className="watch-rule" />
          <div className="watch-meta">You left off at {formatTime(resumeSeconds)}</div>
          <div className="watch-actions">
            <button
              className="watch-ticket-btn watch-ticket-btn--primary"
              onClick={() => {
                setStartAt(resumeSeconds);
                positionRef.current = resumeSeconds;
                setPhase("playing");
              }}
            >
              ▶&nbsp; Resume
            </button>
            <button
              className="watch-ticket-btn"
              onClick={() => {
                setStartAt(0);
                positionRef.current = 0;
                setPhase("playing");
              }}
            >
              ↺&nbsp; From the beginning
            </button>
          </div>
        </div>
      )}

      {phase === "playing" && session && (
        <VideoPlayer
          src={session.hlsUrl}
          poster={poster}
          title={title}
          metaLine={metaLine}
          durationSeconds={durationSeconds}
          startAt={startAt}
          isHls={session.isHls !== false}
          isDirectStream={session.isDirectStream}
          videoCodec={session.videoCodec}
          qualityKey={qualityKey}
          qualityDetail={autoBpsLabel(autoBps)}
          audioTracks={session.audioTracks || []}
          subtitleTracks={session.subtitleTracks || []}
          selectedAudioIndex={audioIndex ?? session.selectedAudioIndex ?? null}
          selectedSubtitleIndex={subtitleIndex ?? session.selectedSubtitleIndex ?? null}
          onSelectQuality={handleSelectQuality}
          onSelectAudio={handleSelectAudio}
          onSelectSubtitle={handleSelectSubtitle}
          onProgress={handleProgress}
          onBandwidth={handleBandwidth}
          onStall={handleStall}
          onEnded={handleEnded}
          onBack={goBack}
        />
      )}

      {phase === "ended" && (
        <div className="watch-card watch-card--entrance">
          <div className="watch-overline">Fin</div>
          <h1 className="watch-title">{title}</h1>
          <div className="watch-rule" />
          <div className="watch-actions">
            <button
              className="watch-ticket-btn watch-ticket-btn--primary"
              onClick={() => {
                setStartAt(0);
                positionRef.current = 0;
                startSession({ startSeconds: 0 })
                  .then((next) => {
                    setSession(next);
                    setPhase("playing");
                  })
                  .catch((err) => {
                    setError(err);
                    setPhase("error");
                  });
              }}
            >
              ↺&nbsp; Watch again
            </button>
            <button className="watch-ticket-btn" onClick={goBack}>
              ←&nbsp; Back to browsing
            </button>
          </div>
        </div>
      )}

      {phase === "error" && errorCopy && (
        <div className="watch-card watch-card--entrance">
          <div className="watch-overline watch-overline--dim">Intermission</div>
          <h1 className="watch-title watch-title--small">{errorCopy.head}</h1>
          <div className="watch-rule" />
          <p className="watch-error-body">{errorCopy.body}</p>
          <div className="watch-actions">
            <button className="watch-ticket-btn" onClick={goBack}>
              ←&nbsp; Back
            </button>
          </div>
        </div>
      )}
    </div>
  );
}

export default WatchPage;
