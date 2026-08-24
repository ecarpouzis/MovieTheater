import { useState, useEffect, useRef, useCallback, useMemo } from "react";
import { useParams, useHistory, useLocation } from "react-router-dom";
import { MovieAPI } from "../../MovieAPI";
import { autoBpsLabel, abrProfileFor, isAutoQuality } from "../../streamAbr";
import { useAdaptiveBitrate } from "../../useAdaptiveBitrate";
import VideoPlayer, { formatTime, TICKS_PER_SECOND, QUALITY_LADDER } from "./VideoPlayer";
import { formatRuntime } from "../../utils/format";
import { useCastSender } from "../../castSender";
import { useCastProfile } from "../../useCastProfile";
import { castCeilingBps, castTrackDescriptors, castSubtitleTracks } from "../../castProfiles";
import "./WatchPage.css";
import { readStored, writeStored, STREAM_QUALITY_KEY } from "../../utils/storage";

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

  // Ordered, playable segments of a multi-part movie — Primary first, then Parts by number
  // (normalized.files arrives already in that order). Drives auto-advance: when one part ends we
  // roll into the next. Only meaningful for movies (a series advances by episode, not by file).
  const partSequenceRef = useRef([]);

  const [movie, setMovie] = useState(null);
  const [normalized, setNormalized] = useState(null);
  const [phase, setPhase] = useState("loading"); // loading | resume | playing | ended | error
  const [error, setError] = useState(null);
  const [session, setSession] = useState(null); // Stream/Start response
  const [startAt, setStartAt] = useState(0);
  const [qualityKey, setQualityKey] = useState(() => readStored(STREAM_QUALITY_KEY) || "auto");
  const [audioIndex, setAudioIndex] = useState(null);
  const [subtitleIndex, setSubtitleIndex] = useState(null);

  const sessionRef = useRef(null);
  sessionRef.current = session;
  const positionRef = useRef(0); // local seconds within the current part/stream
  // Combined-timeline plumbing (multi-part movies only): the on-screen part's global start offset
  // (so progress is reported on the whole-movie clock), and a one-shot local start position handed to
  // the load effect when a scrub crosses into another part.
  const currentPartOffsetRef = useRef(0);
  const pendingPartStartRef = useRef(null);
  // The image-subtitle index currently burned into the live transcode (null = none).
  const burnedSubRef = useRef(null);

  // ── adaptive bitrate (shared state machine) ──────────────────────────────────
  const qualityKeyRef = useRef(qualityKey);
  qualityKeyRef.current = qualityKey;
  // An adapt restarts the stream at the live position. Late-bound through a ref because
  // restartAtPosition is defined below (it depends on the autoBpsRef this hook owns).
  const restartAtPositionRef = useRef(null);
  // Monotonic id for the in-flight restart. An ABR adapt and a user quality/audio/subtitle change
  // can both fire restartAtPosition at once; this lets a superseded restart stop the session it
  // started and bail instead of leaking a transcode (that otherwise self-heals only via Jellyfin's
  // 60s reaper) or stomping the newer session. Mirrors TvPage's tuneSeqRef.
  const restartSeqRef = useRef(0);
  // The active quality picks the ABR strategy: "Auto" opens at the lossless tier and only drops on
  // stalls; "Mobile Auto" opens low and climbs fast to a 1080p/8 Mbps cap (see ABR_PROFILES). Fixed
  // rungs ignore the profile entirely.
  const abrProfile = abrProfileFor(qualityKey);
  // Whether the server is COPYING the video (`isDirectStream` = its videoIsCopied verdict, true even on
  // an HLS session that only transcodes the audio). It freezes the CLIMB — a copy is already lossless,
  // so every rung above it is the same bytes — but not the drop: a copy the link can't carry is exactly
  // when "auto" must fall back to a transcode. The source video's own bitrate rides along so a drop
  // skips the rungs whose cap sits above it and would re-deliver that same copy.
  const videoCopiedRef = useRef(false);
  videoCopiedRef.current = !!session?.isDirectStream;
  const sourceVideoBpsRef = useRef(null);
  sourceVideoBpsRef.current = session?.videoBitrateBps ?? null;
  // An ABR adapt restarts the stream — a multi-second freeze that looks exactly like a failure unless
  // it says otherwise (2026-08-16: an unlabeled climb restart got refreshed away, which was slower
  // than waiting). While set, the buffering bulbs carry an "Adjusting quality" line; the flag expires
  // on a timer safely past a normal restart, and the label only renders while actually buffering.
  const [adjusting, setAdjusting] = useState(false);
  const adjustingTimerRef = useRef(null);
  const markAdjusting = useCallback(() => {
    setAdjusting(true);
    clearTimeout(adjustingTimerRef.current);
    adjustingTimerRef.current = setTimeout(() => setAdjusting(false), 15_000);
  }, []);
  useEffect(() => () => clearTimeout(adjustingTimerRef.current), []);
  const { autoBps, autoBpsRef, handleStall, handleBandwidth, reseed } = useAdaptiveBitrate({
    qualityKeyRef,
    profile: abrProfile,
    videoCopiedRef,
    sourceVideoBpsRef,
    onAdapt: (nextBps) => {
      markAdjusting();
      restartAtPositionRef.current?.({ quality: qualityKeyRef.current, bpsOverride: nextBps });
    },
  });

  // Identity + ladder state for the player's self-reports (videoIncidents). The path id is the
  // *context* title, so which id space it belongs to is decided by `kind` — the same three-id-space
  // split the rest of the page runs on. playableId is the unambiguous one when it's known.
  const incident = useMemo(
    () => ({
      identity: {
        movieId: kind === "movie" ? Number(movieId) : null,
        seriesId: kind === "series" ? Number(movieId) : null,
        miscVideoId: kind === "misc" ? Number(movieId) : null,
        playableId: streamTarget.playableId,
      },
      autoBps,
      sourceVideoBps: session?.videoBitrateBps ?? null,
    }),
    [kind, movieId, streamTarget.playableId, autoBps, session]
  );

  // ── casting (Chromecast) ────────────────────────────────────────────────────
  // A cast is the SAME streaming session as local playback, just decoded somewhere else: one
  // /API/Stream/Start, one ffmpeg, one progress beat, one Stop. What changes is who negotiates it
  // (the receiver's profile, not this browser's — castProfiles.js) and who plays it (the receiver's
  // own player, not our hls.js). Everything else in this page is deliberately unchanged, which is
  // why a quality/audio/subtitle change works while casting without a second code path: they all
  // funnel through restartAtPosition, and the effect below pushes whatever session comes out of it
  // to the receiver.
  const [castError, setCastError] = useState(null);
  const [castStarting, setCastStarting] = useState(false);
  // Declared ahead of the hook because its callbacks close over them, and assigned below once the
  // values they mirror exist. startSession reads these — it must not re-create on every cast tick.
  const castingRef = useRef(false);
  const castProfileRef = useRef(null);
  const castCapsRef = useRef(null);
  // Set once the page is on its way out, so the cast teardown can't be mistaken for the viewer
  // stopping the cast from their TV.
  const leavingRef = useRef(false);

  const cast = useCastSender({
    // Fires for every way a cast can end that ISN'T us: stopped from the TV, the Google Home app
    // taking the device, the receiver rebooting. Pull playback back into this tab at the position it
    // reached, so the film doesn't silently restart from the resume point.
    onSessionEnded: (position) => {
      castingRef.current = false;
      // Leaving the page ends the cast on purpose (see the teardown effect). Falling through to a
      // restart there would mint a fresh transcode for a page that is already gone — the exact leak
      // the Stop beacon exists to prevent, arriving one line after it.
      if (leavingRef.current) return;
      setCastStarting(false);
      setCastError(null);
      if (position > 0) positionRef.current = position;
      restartAtPositionRef.current?.({});
    },
  });

  const castPrefs = useCastProfile(cast.device);
  const casting = cast.connected;
  castingRef.current = casting;
  castProfileRef.current = castPrefs.profile;
  castCapsRef.current = castPrefs.capabilities;

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
    async ({ startSeconds = null, quality = qualityKey, audio = audioIndex, bpsOverride } = {}) => {
      // Auto walks the adaptive ladder (current cap in autoBpsRef); the fixed rungs
      // use their own cap; an explicit override (an adaptive switch) wins.
      const bps =
        bpsOverride !== undefined
          ? bpsOverride
          : isAutoQuality(quality)
          ? autoBpsRef.current
          : (QUALITY_LADDER.find((q) => q.key === quality) || QUALITY_LADDER[0]).bps;
      // The lossless tier (Auto at the top, or manual "Original") carries no cap — a null bitrate tells
      // the server to copy the original video rather than re-encode it. Only finite caps go on the wire.
      let maxBitrateBps = bps != null && isFinite(bps) ? bps : null;
      // Casting rewrites BOTH halves of the negotiation. The profile because the receiver decodes, not
      // this browser (an HEVC copy to a 3rd-gen Chromecast is a black picture). The cap because the ABR
      // ladder cannot run here at all — it drives off hls.js's bandwidth estimate and stall events, and
      // while casting there is no hls.js in this tab; the receiver is pulling segments over its own
      // wifi link, which this page has no instrument for. castCeilingBps pins a sane number instead of
      // letting an Auto session hand a 23 Mbps remux to a dongle on 2.4 GHz.
      const capabilities = castingRef.current ? castCapsRef.current : null;
      if (castingRef.current) {
        maxBitrateBps = castCeilingBps(castProfileRef.current, bps, isAutoQuality(quality));
      }
      const response = await MovieAPI.startStream({
        ...streamTargetRef.current,
        maxBitrateBps,
        capabilities,
        audioStreamIndex: audio,
        // The burned-in image subtitle (null = none), threaded through *every* (re)start so a quality
        // or audio change keeps it — and turning it off actually drops it from the transcode.
        subtitleStreamIndex: burnedSubRef.current,
        startSeconds,
      });
      if (!response.ok) {
        const body = await response.json().catch(() => ({}));
        const err = { status: response.status, message: body.message };
        throw err;
      }
      const data = await response.json();
      // Stamp which decoder this session was negotiated for. The cast push effect keys off session
      // IDENTITY, and entering a cast flips `casting` a beat before the re-negotiated session
      // arrives — so without this the effect fires once on the session built for THIS BROWSER and
      // hands the receiver an HEVC/fMP4 stream (black picture) or a direct-play .mkv url (outright
      // load failure, which then disconnects the cast), a second before the right one replaces it.
      data.negotiatedForCast = castingRef.current;
      return data;
    },
    [qualityKey, audioIndex, autoBpsRef]
  );

  // ── initial load: movie meta + a session (not yet attached to <video>) ─────
  useEffect(() => {
    let cancelled = false;

    // A cross-part scrub sets where in the new part to begin; consume it once (normal loads resume/0).
    const pendingStart = pendingPartStartRef.current;
    pendingPartStartRef.current = null;
    Promise.all([
      MovieAPI.getTitle(movieId, kind)
        .then((r) => r.json())
        .catch(() => null),
      startSession({ startSeconds: pendingStart }),
    ])
      .then(([titleBody, startData]) => {
        if (cancelled) return;
        if (titleBody?.data) {
          setMovie(titleBody.data);
          setNormalized(titleBody.normalized || null);
        }
        setSession(startData);
        const resumeTicks = startData.resumePositionTicks;
        // Resume is stored per Playable, so it only makes sense for the title's main entry (no explicit
        // part). When auto-advancing into a specific part we always start it from the beginning.
        if (resumeTicks && resumeTicks / TICKS_PER_SECOND > 60 && streamTargetRef.current.mediaFileId == null) {
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
    // A cast dies with the page. The stream it is playing is THIS page's session, and the teardown
    // below stops it — so without ending the cast too, the television would sit on a URL whose
    // transcode has just been killed and stall a few seconds later with no explanation. Keeping the
    // cast alive instead would mean owning the session outside the page's lifetime (a background
    // controller, a re-attachable session, a "still casting" bar on every other page), which is a
    // larger feature than this one. Ending it is the honest half.
    const endCast = () => {
      if (!castingRef.current) return;
      leavingRef.current = true;
      cast.disconnect();
    };
    const onPageHide = () => {
      endCast();
      stopCurrentSession(true);
    };
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
      endCast();
      stopCurrentSession(true);
    };
    // cast.disconnect is a stable useCallback, so naming `cast` here would re-run this teardown on
    // every remote-player tick — stopping the session it is meant to protect, once a second.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [stopCurrentSession]);

  // ── player callbacks ────────────────────────────────────────────────────────
  const handleProgress = useCallback(
    (seconds, paused) => {
      positionRef.current = seconds;
      const s = sessionRef.current;
      if (!s) return;
      // Multi-part movie: report on the whole-movie clock (this part's offset + local seconds) so the
      // single per-Playable resume returns to the right part. Single-file titles have offset 0.
      const globalSeconds = currentPartOffsetRef.current + seconds;
      MovieAPI.reportStreamProgress({
        playSessionId: s.playSessionId,
        ...streamTargetRef.current,
        positionTicks: Math.round(globalSeconds * TICKS_PER_SECOND),
        paused,
      });
    },
    []
  );

  const restartAtPosition = useCallback(
    async (overrides) => {
      const seq = ++restartSeqRef.current;
      const position = positionRef.current;
      stopCurrentSession();
      try {
        const next = await startSession({ startSeconds: position, ...overrides });
        // A newer restart superseded us while startSession was in flight — stop the stream we just
        // minted (don't leave it for the 60s reaper) and bail without stomping the newer session.
        if (seq !== restartSeqRef.current) {
          MovieAPI.stopStream({ playSessionId: next.playSessionId, ...streamTargetRef.current });
          return null;
        }
        setStartAt(position);
        setSession(next);
        return next;
      } catch (err) {
        if (seq !== restartSeqRef.current) return null;
        setError(err);
        setPhase("error");
        return null;
      }
    },
    [startSession, stopCurrentSession]
  );
  restartAtPositionRef.current = restartAtPosition;

  // Entering a cast re-negotiates the session for the RECEIVER at the current position — a different
  // device profile and a different cap, which is a different TranscodingUrl. LEAVING is deliberately
  // not handled here: it goes through the sender's onSessionEnded, which is the only path that also
  // covers the endings this tab never initiates (stopped from the TV, device taken by the Home app,
  // receiver rebooted).
  const wasCastingRef = useRef(false);
  useEffect(() => {
    if (casting === wasCastingRef.current) return;
    wasCastingRef.current = casting;
    if (!casting) return;
    setCastError(null);
    setCastStarting(true);
    restartAtPosition({});
  }, [casting, restartAtPosition]);

  const handleSelectQuality = useCallback(
    (rung) => {
      setQualityKey(rung.key);
      writeStored(STREAM_QUALITY_KEY, rung.key);
      // Selecting an Auto mode reseeds at that mode's opener with a fresh streak, then adapts from
      // there; a fixed rung just restarts at its cap.
      if (isAutoQuality(rung.key)) {
        const opener = abrProfileFor(rung.key).openBps;
        reseed(opener);
        restartAtPosition({ quality: rung.key, bpsOverride: opener });
      } else {
        restartAtPosition({ quality: rung.key });
      }
    },
    [restartAtPosition, reseed]
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
      // Sidecar text tracks toggle client-side (no restart). Image subs can only be burned in, so the
      // stream must restart when the burned-in selection changes — including turning it OFF (index →
      // null) or switching to a text track, which otherwise leaves the old sub baked into the picture.
      const nextBurn = track && !track.deliveryUrl ? index : null;
      if (nextBurn !== burnedSubRef.current) {
        burnedSubRef.current = nextBurn;
        restartAtPosition();
        return;
      }
      // A sidecar VTT change costs no restart on either path. Locally the <track> element swaps
      // mode; on a cast the receiver was handed EVERY castable VTT track in the load request, so
      // this only tells it which one to show.
      if (casting) cast.setActiveTextTrack(track?.deliveryUrl ? index : null);
    },
    [session, restartAtPosition, casting, cast]
  );

  const handleEnded = useCallback(
    (seconds) => {
      handleProgress(seconds, true);
      stopCurrentSession();

      // Multi-part movie: roll into the next part instead of ending. The URL's mediaFileId is the
      // source of truth, so we just point it at the next segment — the load effect starts it at 0.
      const seq = partSequenceRef.current;
      if (seq.length > 1) {
        const primaryId = seq.find((f) => f.role === "Primary")?.mediaFileId;
        const curId = streamTargetRef.current.mediaFileId ?? primaryId;
        const idx = seq.findIndex((f) => f.mediaFileId === curId);
        if (idx >= 0 && idx < seq.length - 1) {
          setStartAt(0);
          positionRef.current = 0;
          const params = new URLSearchParams(search);
          params.set("mediaFileId", String(seq[idx + 1].mediaFileId));
          history.replace({ search: `?${params.toString()}` });
          return;
        }
      }
      setPhase("ended");
    },
    [handleProgress, stopCurrentSession, search, history]
  );

  // Seek the combined timeline: find which part a global-second position lands in and load it at the
  // right local offset (the load effect starts the new part's transcode there). The player handles
  // seeks that stay inside the current part directly; only a cross-part jump reaches here.
  const seekGlobal = useCallback(
    (globalSeconds) => {
      const seq = partSequenceRef.current;
      if (seq.length < 2) return;
      let idx = seq.length - 1;
      let acc = 0;
      for (let i = 0; i < seq.length; i++) {
        const d = (seq[i].durationTicks || 0) / TICKS_PER_SECOND;
        if (globalSeconds < acc + d) { idx = i; break; }
        acc += d;
      }
      const offset = (seq.slice(0, idx).reduce((t, f) => t + (f.durationTicks || 0) / TICKS_PER_SECOND, 0));
      const local = Math.max(0, globalSeconds - offset);
      stopCurrentSession();
      pendingPartStartRef.current = local;
      setStartAt(local);
      positionRef.current = local;
      const params = new URLSearchParams(search);
      params.set("mediaFileId", String(seq[idx].mediaFileId));
      history.replace({ search: `?${params.toString()}` });
    },
    [search, history, stopCurrentSession]
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

  // The play-through order for a multi-part movie: the playable Primary + Parts, in file order.
  // Empty unless there's more than one segment, so single-file movies/episodes keep ending normally.
  const partSequence = useMemo(() => {
    if (kind !== "movie" || !Array.isArray(normalized?.files)) return [];
    const segs = normalized.files.filter((f) => (f.role === "Primary" || f.role === "Part") && f.isPlayable);
    return segs.length > 1 ? segs : [];
  }, [normalized, kind]);
  partSequenceRef.current = partSequence;

  // Stitch the parts into one virtual timeline: each part's start offset (seconds) + the combined
  // runtime. combinedDuration is 0 unless every part reports a duration (then the player falls back
  // to a plain per-part timeline rather than guessing boundaries).
  const { partOffsets, combinedDuration } = useMemo(() => {
    const offs = [];
    let acc = 0;
    let complete = partSequence.length > 1;
    for (const f of partSequence) {
      offs.push(acc);
      const d = (f.durationTicks || 0) / TICKS_PER_SECOND;
      if (d <= 0) complete = false;
      acc += d;
    }
    return { partOffsets: offs, combinedDuration: complete ? acc : 0 };
  }, [partSequence]);

  // Which part is on screen (the explicit mediaFileId, or the Primary when bare).
  const currentPartIndex = useMemo(() => {
    if (partSequence.length < 2) return -1;
    const primaryId = partSequence.find((f) => f.role === "Primary")?.mediaFileId;
    const curId = streamTarget.mediaFileId ?? primaryId;
    const i = partSequence.findIndex((f) => f.mediaFileId === curId);
    return i < 0 ? 0 : i;
  }, [partSequence, streamTarget.mediaFileId]);

  const currentPartOffset = currentPartIndex >= 0 ? partOffsets[currentPartIndex] || 0 : 0;
  currentPartOffsetRef.current = currentPartOffset;

  const FILE_ROLE_LABEL = { Part: "Part", Variant: "Variant", Extra: "Extra" };
  const subLabel = episodeInfo
    ? `S${episodeInfo.season}E${episodeInfo.episode}${episodeInfo.title ? " · " + episodeInfo.title : ""}`
    : fileInfo && fileInfo.role && fileInfo.role !== "Primary"
    ? fileInfo.label || `${FILE_ROLE_LABEL[fileInfo.role] || fileInfo.role}${fileInfo.partNumber ? " " + fileInfo.partNumber : ""}`
    : null;

  const year = movie?.releaseDate ? new Date(movie.releaseDate).getFullYear() : null;
  const runtime = formatRuntime(episodeInfo?.runtimeMinutes || normalized?.runtimeMinutes) || movie?.runtime;
  const metaLine = [subLabel, year, !episodeInfo ? movie?.rating : null, runtime].filter(Boolean).join("  ·  ");
  const poster = movie ? MovieAPI.getMoviePoster(movie.id, movie.posterVersion, kind) : null;
  const durationSeconds = session ? session.durationTicks / TICKS_PER_SECOND : 0;
  const resumeSeconds = session?.resumePositionTicks ? session.resumePositionTicks / TICKS_PER_SECOND : 0;

  // ── what the receiver gets ─────────────────────────────────────────────────
  // Only the tracks a Default Media Receiver can actually render survive to the cast menu; the
  // dropped ones become a note rather than dead options (see castSubtitleTracks).
  const castSubs = useMemo(
    () => castSubtitleTracks(session?.subtitleTracks),
    [session]
  );
  const effectiveSubtitleIndex = subtitleIndex ?? session?.selectedSubtitleIndex ?? null;

  // Push every session to the receiver — the first one and every restart a quality / audio /
  // subtitle / cast-profile change produces. Routing them all through one effect keyed on the
  // session object's identity is what makes those controls work while casting with no parallel
  // implementation: they already end in a new `session`, and a new session is a new load.
  useEffect(() => {
    // negotiatedForCast, not just `casting`: only a session actually built for the receiver may be
    // handed to it (see the stamp in startSession).
    if (!casting || !session?.negotiatedForCast || phase !== "playing") return undefined;
    let cancelled = false;
    const activeTrack = castSubs.tracks.find(
      (t) => t.index === effectiveSubtitleIndex && !!t.deliveryUrl
    );
    cast
      .loadMedia({
        url: session.hlsUrl,
        isHls: session.isHls !== false,
        // positionRef, not startAt: on the very first cast this is where local playback had got to,
        // and startAt still holds whatever the page opened at.
        startTime: positionRef.current,
        title,
        subtitle: metaLine,
        poster,
        durationSeconds,
        tracks: castTrackDescriptors(session.subtitleTracks),
        activeTrackId: activeTrack ? activeTrack.index : null,
      })
      .then(() => {
        if (!cancelled) setCastStarting(false);
      })
      .catch((err) => {
        if (cancelled) return;
        setCastStarting(false);
        // The overwhelmingly likely cause of a load failure is the receiver being unable to FETCH
        // the stream — which on this stack means the gateway's CORS allow-list doesn't include the
        // cast receiver origin, or the gateway isn't reachable from the TV's network position. The
        // SDK's error is a bare code, so the message says the useful part instead.
        setCastError(
          `The TV couldn't load this stream${err?.message ? ` (${err.message})` : ""}. ` +
            "Playing here instead."
        );
        cast.disconnect();
      });
    return () => {
      cancelled = true;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [casting, session, phase]);

  // The film running out on the receiver. There is no remote "ended" event in the mirrored player
  // surface, so the sender reconstructs it from the receiver's own idleReason; this just routes it
  // into the same handler local playback uses (which also handles rolling into the next part).
  const castEndedRef = useRef(false);
  const castHadMediaRef = useRef(false);
  useEffect(() => {
    if (!casting) {
      castEndedRef.current = false;
      castHadMediaRef.current = false;
      return;
    }
    if (cast.remote.mediaLoaded) {
      castHadMediaRef.current = true;
      castEndedRef.current = false;
      return;
    }
    // Nothing loaded. FINISHED only means OUR film ended if this cast has actually held it: a
    // receiver can be sitting on a FINISHED idleReason from whatever the household watched before we
    // connected, and reading that at connect time would roll the credits before the first frame.
    // (A mid-film restart passes through un-loaded too, but with INTERRUPTED, not FINISHED.)
    if (!castHadMediaRef.current || !cast.remote.finished || castEndedRef.current) return;
    castEndedRef.current = true;
    handleEnded(cast.remote.currentTime || durationSeconds);
  }, [casting, cast.remote.mediaLoaded, cast.remote.finished, cast.remote.currentTime,
    durationSeconds, handleEnded]);

  // Changing the receiver's profile (or the Dolby switch) changes the DeviceProfile the server
  // negotiates from, so it can only take effect on a fresh session — restart, don't reload.
  const castNegotiationRef = useRef(null);
  useEffect(() => {
    if (!casting) {
      castNegotiationRef.current = castPrefs.negotiationKey;
      return;
    }
    if (castNegotiationRef.current === castPrefs.negotiationKey) return;
    castNegotiationRef.current = castPrefs.negotiationKey;
    restartAtPosition({});
  }, [casting, castPrefs.negotiationKey, restartAtPosition]);

  // The single prop the player takes for everything cast-related: whether the button should exist,
  // what it's connected to, the mirrored transport, and the receiver-specific settings. The player
  // renders and routes; every decision above stays here, with the session that owns it.
  const castProp = useMemo(
    () => ({
      supported: cast.supported && cast.state !== "no-devices" && cast.state !== "unavailable",
      connected: casting,
      connecting: cast.state === "connecting" || castStarting,
      deviceName: castPrefs.deviceName,
      videoCapable: cast.device ? cast.device.videoCapable : true,
      error: castError || cast.error,
      profileKey: castPrefs.profile.key,
      dolbyPassthrough: castPrefs.dolby,
      onSelectProfile: castPrefs.selectProfile,
      onToggleDolby: castPrefs.toggleDolby,
      connect: cast.connect,
      disconnect: cast.disconnect,
      subtitleNote: castSubs.dropped.length
        ? `${castSubs.dropped.length} subtitle track${castSubs.dropped.length > 1 ? "s" : ""} can't be shown on a TV`
        : null,
      remote: cast.remote,
      playPause: cast.playPause,
      seek: cast.seek,
      setVolume: cast.setVolume,
      toggleMuted: cast.toggleMuted,
      setPlaybackRate: cast.setPlaybackRate,
    }),
    [cast, casting, castStarting, castError, castPrefs, castSubs.dropped.length]
  );

  const errorCopy = (() => {
    if (!error) return null;
    if (error.status === 401) return { head: "Please sign in", body: "You need to be signed in to enter the screening room." };
    // Passwordless accounts fall through to the generic 403 — we don't tell them
    // streaming exists, since only an admin can grant a first password anyway.
    if (error.status === 403) return { head: "Not this picture", body: error.message || "This movie isn't available on your account." };
    if (error.status === 503) return { head: "The theater is full", body: error.message || "Too many screens are running right now — try again in a few minutes." };
    if (error.status === 404 || error.status === 501)
      return { head: "The projector isn't installed yet", body: "Streaming hasn't been switched on for this server." };
    // Surface the status + server message so an unexpected failure (500 = server threw,
    // 502 = Jellyfin/transcode path) is self-diagnosing instead of a dead-end "couldn't start".
    return {
      head: "Something broke the reel",
      body: `${error.message || "The stream could not be started."}${
        error.status ? ` (error ${error.status})` : " — no response from the server"
      }`,
    };
  })();

  return (
    <div className="watch-room">
      {/* poster ember backdrop behind every non-playing state */}
      {phase !== "playing" && poster && (
        <div className="watch-backdrop" style={{ backgroundImage: `url(${poster})` }} aria-hidden="true" />
      )}
      {/* Room vignette for the title-card states — a REAL element under the same phase guard as the
          backdrop, never a .watch-room::after. A pseudo-element on the room is a positioned z-auto
          box that comes last in tree order, so it paints over every sibling INCLUDING the player:
          as .watch-room::after this scrim spent its life dimming the corners of every movie by up
          to 55% (found 2026-08-22). Nothing may paint over .vp-stage — the picture is the master. */}
      {phase !== "playing" && <div className="watch-scrim" aria-hidden="true" />}

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
                // resumeSeconds is the whole-movie clock. If it falls past the loaded first part, jump
                // to the part it belongs to; otherwise just start the current part there.
                if (combinedDuration > 0 && resumeSeconds >= durationSeconds) {
                  seekGlobal(resumeSeconds);
                } else {
                  setStartAt(resumeSeconds);
                  positionRef.current = resumeSeconds;
                  setPhase("playing");
                }
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
          subtitleTracks={casting ? castSubs.tracks : session.subtitleTracks || []}
          selectedAudioIndex={audioIndex ?? session.selectedAudioIndex ?? null}
          selectedSubtitleIndex={effectiveSubtitleIndex}
          cast={castProp}
          onSelectQuality={handleSelectQuality}
          onSelectAudio={handleSelectAudio}
          onSelectSubtitle={handleSelectSubtitle}
          onProgress={handleProgress}
          onBandwidth={handleBandwidth}
          onStall={handleStall}
          onEnded={handleEnded}
          bufferingLabel={adjusting ? "Adjusting quality" : null}
          incident={incident}
          combinedDuration={combinedDuration}
          partOffset={currentPartOffset}
          partBoundaries={partOffsets.slice(1)}
          onSeekGlobal={seekGlobal}
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
                // A multi-part movie ends on its last part — replay from part 1 by dropping the
                // mediaFileId so the load effect restarts at the Primary.
                if (partSequence.length > 1 && streamTarget.mediaFileId != null) {
                  const params = new URLSearchParams(search);
                  params.delete("mediaFileId");
                  const qs = params.toString();
                  history.replace({ search: qs ? `?${qs}` : "" });
                  return;
                }
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
