import { useState, useEffect, useRef, useCallback } from "react";
import { useParams, useHistory } from "react-router-dom";
import Hls from "hls.js";
import { MovieAPI } from "../../MovieAPI";
import { formatTime, TICKS_PER_SECOND } from "../Watch/VideoPlayer";
import { QUALITY_LADDER, formatPlaying, qualityOptions, audioOptions, subtitleOptions, deliveredAudio } from "../../playerMenuModel";
import { useIdleChrome } from "../../useIdleChrome";
import { createHls, bandwidthSample } from "../../streamEngine";
import { autoBpsLabel, abrProfileFor, isAutoQuality } from "../../streamAbr";
import { useAdaptiveBitrate } from "../../useAdaptiveBitrate";
import { useVideoIncidents, noteStreamSwitch } from "../../videoIncidents";
import { useWakeLock } from "../../useWakeLock";
import { useMediaSession } from "../../useMediaSession";
import { usePictureInPicture } from "../../usePictureInPicture";
import { usePgsSubtitle } from "../../usePgsSubtitle";
import { useAssSubtitle } from "../../useAssSubtitle";
import { useSubtitleStyle, useCueLift, useSubtitleOffset, formatDelay } from "../../subtitleStyle";
import { SubtitleStyleControls, SubtitleStylePreview, SubtitleSyncControls } from "../../SubtitleStyleEditor";

import ChannelAdminModal from "./ChannelAdminModal";
import ChannelGrid from "./ChannelGrid";
import "./TvPage.css";
import FallbackImage from "../../Components/FallbackImage";
import { readStored, writeStored, STREAM_QUALITY_KEY } from "../../utils/storage";

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
  const progressRef = useRef(null);
  const scrubbingRef = useRef(false);
  // Grace timer before tearing down the stream on a hidden tab (see the visibility effect).
  const hiddenGraceRef = useRef(null);

  const [channels, setChannels] = useState(null); // null = loading
  const [channel, setChannel] = useState(null);
  const [now, setNow] = useState(null); // { current, next }
  const [muted, setMuted] = useState(true);
  const [volume, setVolume] = useState(() => {
    const v = parseFloat(readStored("TvVolume"));
    return Number.isFinite(v) ? Math.min(Math.max(v, 0), 1) : 1;
  });
  const [gridOpen, setGridOpen] = useState(false); // cross-channel grid guide (the EPG / what's-coming-up)
  const [menuOpen, setMenuOpen] = useState(false); // settings dropdown (quality / guide / manage / off)
  const [isFullscreen, setIsFullscreen] = useState(false);
  const [, setNowTick] = useState(0); // ticks every second to advance the live progress bar
  const [guideOpen, setGuideOpen] = useState(false);
  const [guide, setGuide] = useState(null);
  const [staticBurst, setStaticBurst] = useState(false);
  const [error, setError] = useState(null);
  const [offAir, setOffAir] = useState(false);
  const [adminOpen, setAdminOpen] = useState(false);
  const [quality, setQuality] = useState(() => readStored(STREAM_QUALITY_KEY) || "auto");
  const [qualityOpen, setQualityOpen] = useState(false);
  const [audioTracks, setAudioTracks] = useState([]);
  const [subtitleTracks, setSubtitleTracks] = useState([]);
  const [audioIndex, setAudioIndex] = useState(null); // explicit pick; null = let the server auto-default (English)
  const [playingAudioIndex, setPlayingAudioIndex] = useState(null); // what's actually playing, for the menu highlight
  const [playingVideoCodec, setPlayingVideoCodec] = useState(null); // delivered video codec, for the "Playing" readout
  const [playingDirect, setPlayingDirect] = useState(false); // true = original copied (no re-encode), for the readout
  const [playingHls, setPlayingHls] = useState(true); // true = HLS session (not raw direct play), for the readout
  const [subtitleIndex, setSubtitleIndex] = useState(null); // burned-in subtitle stream; null = off
  // Seconds this session's media timeline runs AHEAD of true content time (streamEngine's
  // timelineOffsetFromInitPts): a mid-file HLS join lands on the previous source keyframe, so
  // currentTime = content + offset. State for the subtitle renderers (it arrives after they mount),
  // ref for the handlers/intervals that aren't re-bound per render. 0 on direct play.
  const [timelineOffset, setTimelineOffset] = useState(0);
  const timelineOffsetRef = useRef(0);
  const [audioOpen, setAudioOpen] = useState(false);
  const [subsOpen, setSubsOpen] = useState(false);
  // Caption appearance — shared with the Watch player (same hook + persisted settings + injected ::cue).
  const { subStyle, setSubStyle, setStyle, styleOpen, setStyleOpen } = useSubtitleStyle();
  // Subtitle timing nudge — client-side re-time of the showing soft track; per-viewer, so it works
  // here despite the channel being a shared broadcast (it never touches the stream itself).
  const {
    offsetMs: subtitleOffsetMs,
    nudge: nudgeSubtitle,
    reset: resetSubtitleOffset,
    toast: offsetToast,
    rateScale: subtitleRateScale,
    abStep: subtitleAbStep,
    abError: subtitleAbError,
    beginSync: beginSubtitleSync,
    capturePoint: captureSubtitleSyncPoint,
    cancelSync: cancelSubtitleSync,
  } = useSubtitleOffset(videoRef, subtitleIndex, subtitleTracks, timelineOffset);
  // The selected SOFT (sidecar VTT) subtitle — only these can be re-timed client-side, so the delay
  // UI gates on it (burned-in image subs are baked into the transcode and can't be moved).
  const activeTextSub =
    subtitleTracks.find(
      (t) => t.index === subtitleIndex && !!t.deliveryUrl && t.kind !== "image-pgs" && t.kind !== "ass"
    ) || null;
  const [skip, setSkip] = useState(null); // { viewers, votes, required, youVoted }
  const [restart, setRestart] = useState(null); // { viewers, votes, required, youVoted }
  const [viewers, setViewers] = useState(null); // { count, names: [{ name, you }] } — who's tuned in
  const [tuning, setTuning] = useState(false); // waiting on the (cold) transcode to produce frames
  const [paused, setPaused] = useState(false); // shared channel pause — frozen for everyone watching
  const [scrubHover, setScrubHover] = useState(null); // { pct, seconds } while pointing at the progress bar (lone viewer only)
  const [fillSnap, setFillSnap] = useState(false); // suppress the fill's 1s ease for one paint after leaving/seeking, so it jumps straight back to the live position

  const canEdit = userData?.canEditMovies ?? false;

  // tune() reads the current quality without re-binding on every change.
  const qualityRef = useRef(quality);
  qualityRef.current = quality;

  const channelRef = useRef(null);
  channelRef.current = channel;
  const tuneRef = useRef(null);

  // Adaptive bitrate: shared state machine. The active quality picks the strategy — "Auto" opens at
  // the lossless tier and only drops on stalls; "Mobile Auto" opens low and climbs fast to a 1080p/
  // 8 Mbps cap (see ABR_PROFILES). An adapt re-tunes the channel at the live offset (per-viewer —
  // never disturbs others on the channel).
  const abrProfile = abrProfileFor(quality);
  // What the live stream is doing, read by the ABR state machine (set on every tune, below): whether
  // the video is being copied — which freezes the climb, since nothing above a copy differs — and the
  // source video's own bitrate, so a drop skips the rungs whose cap sits above it and would hand back
  // that same copy. Dropping off a copy stays allowed: a channel viewer whose link can't carry it
  // falls back to a transcode instead of buffering.
  const videoCopiedRef = useRef(false);
  const sourceVideoBpsRef = useRef(null);
  const { autoBps, autoBpsRef, handleStall, handleBandwidth, reseed } = useAdaptiveBitrate({
    qualityKeyRef: qualityRef,
    profile: abrProfile,
    videoCopiedRef,
    sourceVideoBpsRef,
    onAdapt: () => {
      const ch = channelRef.current;
      if (ch) tuneRef.current?.(ch);
    },
  });

  // Self-reported playback failures — the same shared recorder the Watch player uses (there is no
  // per-player detection logic; see videoIncidents for what fires and what deliberately doesn't).
  // A TV incident is identified by its CHANNEL first: "channel 12 froze" is how it gets reported,
  // and the schedule item riding along is what says which film it was.
  useVideoIncidents({
    player: "tv",
    videoRef,
    identity: { channelId: channel?.id ?? null, playableId: now?.current?.playableId ?? null },
    ladder: {
      qualityKey: quality,
      autoBps,
      copied: playingDirect,
      codec: playingVideoCodec,
      sourceVideoBps: sourceVideoBpsRef.current,
    },
    timelineOffsetRef,
  });

  // tune() (and prewarm) read the current track selection without re-binding on every change.
  const audioIndexRef = useRef(audioIndex);
  audioIndexRef.current = audioIndex;
  const subtitleIndexRef = useRef(subtitleIndex);
  subtitleIndexRef.current = subtitleIndex;

  // The subtitle we actually pass to the transcode — only ever an *image* sub, which has to be
  // burned in. Text (sidecar) subs render client-side via <track>, so they stay null here and
  // never trigger a transcode.
  const burnSubIndexRef = useRef(null);

  // Pause is read inside event handlers (onPlaying) that aren't re-bound on every change.
  const pausedRef = useRef(paused);
  pausedRef.current = paused;

  // Read inside the drift corrector's interval, which isn't re-bound per render.
  const tuningRef = useRef(tuning);
  tuningRef.current = tuning;

  // Where the CHANNEL is, as last stated by the server: { itemId, position, atMs }. Every Now answer
  // re-anchors it (position = the item offset the server reported, atMs = the local clock when that was
  // true, backdated by half the round trip). The drift corrector below plays this back to decide where
  // the picture should be — anchoring on the server, not on our own join, is what makes two viewers
  // converge on the same frame rather than each drifting from wherever they happened to start.
  const syncRef = useRef(null);
  const lastSyncSeekAtRef = useRef(0);

  // Whether any popout is open — read inside the idle timer (not re-bound per change) so a
  // menu/picker/guide left open keeps the chrome up instead of fading mid-interaction.
  const popoutOpenRef = useRef(false);
  popoutOpenRef.current = gridOpen || menuOpen || guideOpen;

  // The schedule item currently playing — used to scope skip votes and to notice when
  // the channel has moved on (a skip elsewhere, or a natural advance) so we re-tune.
  const currentItemIdRef = useRef(null);

  // The movie last tuned to — a pinned audio/subtitle choice is scoped to that film, so we
  // can drop the override when the channel rolls to a different movie.
  const tunedPlayableIdRef = useRef(null);

  // The current item's scheduled end. A restart keeps the same itemId but pushes the end later
  // (the film replays from the top), so a later end on the same item is how other viewers notice.
  const currentEndsAtRef = useRef(null);

  // Circuit breaker: if the same item re-tunes over and over in a short window (e.g. the source
  // file fails to seek/remux and 'ended' fires moments after every join), retrying forever just
  // spins "Tuning in…" — surface an error instead of hammering the transcoder.
  const retuneLoopRef = useRef({ itemId: null, count: 0, firstAt: 0, escalated: false });
  const RETUNE_LOOP_WINDOW_MS = 20_000;
  const RETUNE_LOOP_LIMIT = 3;

  // Drift correction thresholds (see the corrector effect). Wide enough not to hunt around a normal
  // measurement wobble, tight enough that two people on the same channel stay on the same beat.
  const SYNC_TOLERANCE_S = 0.4;   // inside this we're in sync — hold the rate at 1
  const SYNC_SEEK_AFTER_S = 5;    // past this a 3% nudge would take minutes — jump instead
  const SYNC_RATE_STEP = 0.03;    // ±3%: closes a second of drift in ~33s, pitch-preserved and inaudible
  const SYNC_SEEK_COOLDOWN_MS = 15_000; // never re-seek faster than this (see the corrector)

  // A broadcast item escalated to a forced re-encode after the copy path couldn't mid-join it
  // (bad/absent keyframe index → seek loop). Keyed by itemId, so it applies only to that item and
  // clears automatically when the channel advances (a new itemId no longer matches).
  const forceTranscodeItemRef = useRef(null);

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
    const payload = { playSessionId: s.playSessionId, playableId: s.playableId };
    if (useBeacon) MovieAPI.beaconStopStream(payload);
    else MovieAPI.stopStream(payload);
  }, []);

  const destroyHls = useCallback(() => {
    if (hlsRef.current) {
      hlsRef.current.destroy();
      hlsRef.current = null;
    } else if (videoRef.current) {
      // Direct-play / Safari set video.src directly (no hls instance to clean up); release it so the
      // static-file connection doesn't linger after a tune-away or unmount (mirrors the Watch player).
      videoRef.current.removeAttribute("src");
      videoRef.current.load();
    }
  }, []);

  // Dismiss the settings menu (e.g. on a click in the video area).
  const closePopouts = useCallback(() => {
    setMenuOpen(false);
  }, []);

  // The control bar drops away after a few seconds of stillness (useIdleChrome — the Watch
  // player's house-lights fade, shared now), but stays up while paused or while a popout is open
  // so it can't vanish mid-interaction.
  const { visible: chromeVisible, wake: wakeChrome, hide: hideChromeNow } = useIdleChrome({
    videoRef,
    holdWhile: () => popoutOpenRef.current || pausedRef.current,
  });

  // Tap/click on the picture: close an open popout if there is one, otherwise toggle the chrome —
  // showing hides it, hidden shows it (and re-arms the fade). This is the tap-to-hide affordance.
  const onScreenTap = useCallback(() => {
    if (popoutOpenRef.current) {
      closePopouts();
      setGuideOpen(false);
      return;
    }
    if (chromeVisible) {
      hideChromeNow();
    } else {
      wakeChrome();
    }
  }, [chromeVisible, closePopouts, wakeChrome, hideChromeNow]);

  // The bitrate cap for the current quality. A manual rung uses its own cap (incl. the uncapped
  // "Original"); Auto uses the live adaptive cap, which climbs to the lossless/uncapped tier — so a
  // channel watcher with the bandwidth for it gets the original copied bit-for-bit, dropping to a
  // transcode only on stall. Streams are per-viewer, so one viewer adapting never disturbs anyone
  // else sharing the channel.
  const resolveBitrate = useCallback(() => {
    const rungKey = qualityRef.current;
    const rung = QUALITY_LADDER.find((q) => q.key === rungKey) || QUALITY_LADDER[0];
    if (!isAutoQuality(rungKey)) return rung.bps; // manual rung (incl. uncapped "Original")
    const cap = autoBpsRef.current;
    return isFinite(cap) ? cap : null; // lossless tier → uncapped (the server copies the source)
  }, [autoBpsRef]);

  // Start the next item's transcode ahead of the boundary and warm it (fetch its
  // playlist), so the advance can reuse it instead of paying the cold start.
  const prewarmNext = useCallback(
    async (playableId) => {
      // Pin the same track selection the live tune will ask for, so the warmed transcode
      // matches — otherwise the reuse check below would reject it. Only a burned-in (image)
      // subtitle reaches the transcode; text subs ride along as sidecars regardless.
      const audioStreamIndex = audioIndexRef.current;
      const subtitleStreamIndex = burnSubIndexRef.current;
      try {
        const r = await MovieAPI.startStream({
          playableId,
          maxBitrateBps: resolveBitrate(),
          startSeconds: 0,
          audioStreamIndex,
          subtitleStreamIndex,
        });
        if (!r.ok) return;
        const session = await r.json();
        // For a transcode, pull the playlist to actually spawn ffmpeg; direct play has
        // nothing to warm (it's a static file).
        if (session.isHls !== false) fetch(session.hlsUrl).catch(() => {});
        prewarmRef.current = { playableId, session, audioStreamIndex, subtitleStreamIndex };
      } catch {
        /* prewarm is best-effort */
      }
    },
    [resolveBitrate]
  );

  // Re-anchor the channel clock from a Now answer. The offset the server states was true about half a
  // round trip ago, so backdate by that much instead of pretending the answer was instantaneous (the
  // clamp keeps one pathological request from throwing the anchor seconds off).
  const anchorSync = useCallback((nowData, rttMs) => {
    const position = nowData?.current?.offsetSeconds;
    if (typeof position !== "number") {
      syncRef.current = null;
      return;
    }
    syncRef.current = {
      itemId: nowData.current.itemId ?? null,
      position,
      atMs: performance.now() - Math.min(rttMs, 2_000) / 2,
    };
  }, []);

  // ── tune to the channel's live position ─────────────────────────────────────
  const tune = useCallback(
    async (chan) => {
      if (!chan) return;
      const seq = ++tuneSeqRef.current;
      const superseded = () => seq !== tuneSeqRef.current;
      // A re-tune IS a session restart — a new ffmpeg and several seconds of frozen picture by
      // design. Marked here (rather than off a changing src prop, which this player doesn't have)
      // so the incident recorder never files the restart's own rebuffer as a stall. The Watch
      // player's equivalent is its src change; both land on the same noteStreamSwitch.
      noteStreamSwitch("tune");
      clearTimeout(advanceTimerRef.current);
      clearTimeout(prewarmTimerRef.current);
      stopSession();
      destroyHls();
      // Every join re-rolls the timeline offset (0 … one source GOP), and direct play has none —
      // zero it before anything can attach, so a stale offset can never outlive its stream.
      timelineOffsetRef.current = 0;
      setTimelineOffset(0);
      setError(null);
      setOffAir(false);
      setTuning(true);
      setStaticBurst(true);
      setTimeout(() => setStaticBurst(false), 420);

      try {
        const askedAt = performance.now();
        // No signal here: the monotonic tune id IS this call's cancellation — a superseded tune drops
        // its own answer, and aborting would also lose the presence beat the poll relies on.
        const nowData = await MovieAPI.getChannelNow(chan.id);
        if (superseded()) return;
        setNow(nowData);
        if (!nowData.current) {
          setOffAir(true);
          setTuning(false);
          currentItemIdRef.current = null;
          currentEndsAtRef.current = null;
          syncRef.current = null;
          setSkip(null);
          setRestart(null);
          setViewers(null);
          return;
        }
        currentItemIdRef.current = nowData.current.itemId ?? null;
        currentEndsAtRef.current = nowData.current.endsAtUtc ?? null;
        anchorSync(nowData, performance.now() - askedAt);

        const loopTs = Date.now();
        const loop = retuneLoopRef.current;
        if (loop.itemId === nowData.current.itemId && loopTs - loop.firstAt < RETUNE_LOOP_WINDOW_MS) {
          loop.count += 1;
        } else {
          retuneLoopRef.current = { itemId: nowData.current.itemId, count: 1, firstAt: loopTs, escalated: false };
        }
        if (retuneLoopRef.current.count > RETUNE_LOOP_LIMIT) {
          if (!retuneLoopRef.current.escalated) {
            // The copy/remux path can't mid-join this title — its keyframe index doesn't map to the
            // requested seek, so playback 'ended' immediately on every retry. Escalate ONCE to a forced
            // re-encode: ffmpeg lays down its own keyframes, so the join lands. Costs a transcode, but
            // only for this offending item and only after the cheap copy path has demonstrably failed.
            // Reset the loop window so the breaker can still catch a re-encode that also fails.
            forceTranscodeItemRef.current = nowData.current.itemId;
            retuneLoopRef.current = { itemId: nowData.current.itemId, count: 1, firstAt: loopTs, escalated: true };
          } else {
            // Even a full re-encode couldn't keep it playing — stop hammering the transcoder.
            clearTimeout(advanceTimerRef.current);
            clearTimeout(prewarmTimerRef.current);
            setError(new Error("This title won't stay playing — try switching channels."));
            setTuning(false);
            return;
          }
        }

        setSkip(nowData.skip || null);
        setRestart(nowData.restart || null);
        setViewers(nowData.viewers || null);
        setPaused(nowData.paused || false);

        // A pinned audio/subtitle choice is per-film. When the channel rolls to a different movie
        // (a natural advance, a skip, or a channel switch) drop the override so the next film
        // English-defaults again instead of inheriting a stream index that doesn't map to it.
        if (tunedPlayableIdRef.current !== nowData.current.playableId) {
          tunedPlayableIdRef.current = nowData.current.playableId;
          if (audioIndexRef.current != null) {
            audioIndexRef.current = null;
            setAudioIndex(null);
          }
          if (subtitleIndexRef.current != null) {
            subtitleIndexRef.current = null;
            setSubtitleIndex(null);
          }
          burnSubIndexRef.current = null;
        }

        // Reuse a transcode we prewarmed for this item near the last boundary (instant
        // advance); a prewarm is only valid for a fresh-start join (~offset 0).
        let session = null;
        const pw = prewarmRef.current;
        prewarmRef.current = null;
        if (pw) {
          // Only reuse a prewarm for the same movie *and* the same track selection — a mid-flight
          // audio/subtitle change makes the warmed transcode wrong even if the movie matches.
          if (
            pw.playableId === nowData.current.playableId &&
            pw.audioStreamIndex === audioIndexRef.current &&
            pw.subtitleStreamIndex === burnSubIndexRef.current &&
            nowData.current.offsetSeconds < 8 &&
            // A prewarm is a copy stream; don't reuse it for an item we've escalated to re-encode.
            forceTranscodeItemRef.current !== nowData.current.itemId
          ) {
            session = pw.session;
          } else {
            MovieAPI.stopStream({ playSessionId: pw.session.playSessionId, playableId: pw.playableId });
          }
        }

        if (!session) {
          // "Auto" uses the live adaptive cap (resolveBitrate) — optimistic at the lossless tier,
          // dropping a rung on stall. A solo viewer with the bandwidth gets the original copied;
          // a constrained one falls back to a transcode instead of buffering.
          const startResponse = await MovieAPI.startStream({
            playableId: nowData.current.playableId,
            maxBitrateBps: resolveBitrate(),
            startSeconds: Math.floor(nowData.current.offsetSeconds),
            audioStreamIndex: audioIndexRef.current,
            subtitleStreamIndex: burnSubIndexRef.current,
            // Escalated to a forced re-encode (see the retune-loop breaker above) — only this item.
            forceTranscode: forceTranscodeItemRef.current === nowData.current.itemId,
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
          MovieAPI.stopStream({ playSessionId: session.playSessionId, playableId: nowData.current.playableId });
          return;
        }
        sessionRef.current = { playSessionId: session.playSessionId, playableId: nowData.current.playableId };

        // Surface the track menus and reflect what's actually playing (incl. the server's English
        // auto-default) so the settings menu highlights the live audio track.
        setAudioTracks(session.audioTracks || []);
        setSubtitleTracks(session.subtitleTracks || []);
        setPlayingAudioIndex(session.selectedAudioIndex ?? null);
        setPlayingVideoCodec(session.videoCodec ?? null);
        setPlayingDirect(!!session.isDirectStream);
        setPlayingHls(session.isHls !== false);
        videoCopiedRef.current = !!session.isDirectStream;
        sourceVideoBpsRef.current = session.videoBitrateBps ?? null;

        const video = videoRef.current;
        if (!video) return;
        video.playbackRate = 1; // a fresh join starts on-tempo; the drift corrector re-nudges if it has to
        const joinAt = nowData.current.offsetSeconds;
        // Tuning a frozen channel loads the frame but must NOT start it: the picture holds on the
        // paused instant. (The buffered frame still renders, and 'loadeddata' clears the tuning card.)
        const frozen = !!nowData.paused;
        // Keyed off this tune's own Now answer, not pausedRef: a resume re-tunes immediately and must
        // never be left holding a still frame by a ref that hasn't re-rendered yet. (The 'playing'
        // handler covers the other direction — a pause landing mid-tune.)
        const startPlayback = () => {
          if (!frozen) video.play().catch(() => {});
        };
        if (session.isHls === false) {
          // Direct play: the original file. Seek to the live offset via a range request —
          // no transcode, so the channel joins near-instantly.
          video.src = session.hlsUrl;
          video.addEventListener(
            "loadedmetadata",
            () => {
              video.currentTime = joinAt;
              startPlayback();
            },
            { once: true }
          );
        } else if (Hls.isSupported()) {
          // Join at the live channel offset directly (startPosition) instead of loading from 0 and
          // seeking — that seek churn was a source of join-time A/V desync on mobile. Buffer config +
          // error recovery are shared with the Watch player (createHls); backBufferLength stays small
          // because a channel is forward-only (a lone-viewer scrub re-tunes a fresh stream).
          const hls = createHls({
            backBufferLength: 10,
            startPosition: joinAt,
            onStall: handleStall,
            // A truly fatal decode error (past the standard network/media recovery) drops the channel
            // to "No signal" instead of leaving the picture silently stuck.
            onFatal: () => setError(new Error("The signal dropped.")),
            onTimelineOffset: (offset) => {
              timelineOffsetRef.current = offset;
              setTimelineOffset(offset);
            },
          });
          hlsRef.current = hls;
          hls.on(Hls.Events.MANIFEST_PARSED, startPlayback);
          hls.loadSource(session.hlsUrl);
          hls.attachMedia(video);
        } else {
          // Safari native HLS: seek on metadata is the only join lever available.
          video.src = session.hlsUrl;
          video.addEventListener(
            "loadedmetadata",
            () => {
              video.currentTime = joinAt;
              startPlayback();
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
            prewarmTimerRef.current = setTimeout(() => prewarmNext(nextItem.playableId), msUntilEnd - 20_000);
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
    [stopSession, destroyHls, resolveBitrate, prewarmNext, handleStall, anchorSync]
  );
  // tune() is invoked from the ABR adapt path (useAdaptiveBitrate onAdapt) via this ref, avoiding a
  // tune/adapt dependency cycle.
  tuneRef.current = tune;

  // ── channel list ────────────────────────────────────────────────────────────
  // keepSelection: after an admin edit, hold the current channel if it still exists
  // rather than snapping back to the first one.
  const loadChannels = useCallback(
    (keepSelection = false) => {
      setError(null);
      // getChannelList/getChannelMeta hand back the Response — they're shared with the guide page and
      // the lineup hook, which want a failure to be tolerable. The room doesn't: the status is the
      // error copy, so it's read here.
      return MovieAPI.getChannelList()
        .then((r) => {
          if (!r.ok) throw Object.assign(new Error(), { status: r.status });
          return r.json();
        })
        .then((list) => {
          setChannels(list);
          const wanted = channelId ? list.find((c) => String(c.id) === String(channelId)) : null;
          // A channel reached by id but not in the guide list (e.g. a watch-party channel, which is hidden
          // from List) — fetch its metadata directly and tune it, rather than snapping to the first channel.
          if (channelId && !wanted && !keepSelection) {
            return MovieAPI.getChannelMeta(channelId)
              .then((r) => (r.ok ? r.json() : null))
              .then((meta) => setChannel((prev) => meta || prev || list[0] || null))
              .catch(() => setChannel((prev) => prev || list[0] || null));
          }
          setChannel((prev) => {
            if (keepSelection && prev) {
              const stillThere = list.find((c) => c.id === prev.id);
              if (stillThere) return stillThere;
            }
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
  // Report every 10s paused or not: Jellyfin's HLS job has a 60s ping timeout kept alive ONLY by
  // these reports (segment fetches don't reset it), so skipping the beat during a shared pause
  // would let the server kill ffmpeg and force a cold restart on resume. Report the real paused
  // flag so throttling stays honest; passive=true keeps background play from claiming a watch.
  useEffect(() => {
    const beat = setInterval(() => {
      const video = videoRef.current;
      const s = sessionRef.current;
      if (video && s) {
        MovieAPI.reportStreamProgress({
          playSessionId: s.playSessionId,
          playableId: s.playableId,
          // Content time, not player time: a mid-file join shifts the media timeline (see
          // timelineOffsetRef), and the server's resume/progress bookkeeping is in content seconds.
          positionTicks: Math.max(0, Math.round((video.currentTime - timelineOffsetRef.current) * TICKS_PER_SECOND)),
          paused: video.paused,
          passive: true,
        });
      }
    }, 10_000);
    return () => clearInterval(beat);
  }, []);

  // ── keep every viewer on the same frame ─────────────────────────────────────
  // A channel is one broadcast, but each viewer decodes it separately: join latency, the keyframe
  // ffmpeg actually seeks to, and every rebuffer leave the picture a little behind the schedule — and
  // those errors only accumulate, so two people watching together drift apart over an evening (and a
  // re-tune was the only thing that ever resnapped them). Compare where we are against where the
  // server says the channel is, then close the gap by nudging the playback rate — pitch-preserved and
  // inaudible at 3% — falling back to a jump only when a nudge would take minutes. Purely per-viewer:
  // it never touches the shared schedule, so correcting yourself can't shove anyone else.
  useEffect(() => {
    const correct = setInterval(() => {
      const video = videoRef.current;
      const anchor = syncRef.current;
      if (!video || !anchor || pausedRef.current || tuningRef.current) return;
      if (video.paused || video.seeking || !video.readyState) return;
      if (anchor.itemId !== currentItemIdRef.current) return; // channel moved on — wait for a fresh anchor

      const expected = anchor.position + (performance.now() - anchor.atMs) / 1000;
      // Both sides in CONTENT time. The channel clock states a content offset, while currentTime can
      // sit a whole source GOP ahead of it on a mid-file HLS join — comparing them raw reads that
      // constant as drift and seeks every cooldown, which restarts the encoder, re-rolls the offset,
      // and never converges (the ~15s tune/seek storm in the 2026-07-29 logs).
      const drift = video.currentTime - timelineOffsetRef.current - expected; // > 0 → ahead of the channel
      if (!isFinite(drift)) return;

      if (Math.abs(drift) > SYNC_SEEK_AFTER_S) {
        // Rate-limited: a picture that's frozen (not merely behind) keeps reading as huge drift, and
        // seeking every beat would just hammer the transcoder instead of letting it recover.
        if (performance.now() - lastSyncSeekAtRef.current < SYNC_SEEK_COOLDOWN_MS) return;
        lastSyncSeekAtRef.current = performance.now();
        video.playbackRate = 1;
        try {
          video.currentTime = expected + timelineOffsetRef.current; // back into player time
        } catch {
          /* not seekable yet — the next beat retries */
        }
        return;
      }
      const rate =
        Math.abs(drift) <= SYNC_TOLERANCE_S ? 1 : drift > 0 ? 1 - SYNC_RATE_STEP : 1 + SYNC_RATE_STEP;
      if (Math.abs(video.playbackRate - rate) > 0.001) video.playbackRate = rate;
    }, 3_000);
    return () => clearInterval(correct);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  useEffect(() => {
    const video = videoRef.current;
    if (!video) return undefined;
    const onEnded = () => channel && tune(channel);
    const onPlaying = () => {
      setTuning(false); // first frames arrived — hide the "Tuning…" card
      if (pausedRef.current) videoRef.current?.pause(); // joined a frozen channel — hold the frame
      else wakeChrome(); // playback is live — start the idle fade countdown
    };
    // Safety net: if frames are actually advancing, the channel IS tuned — clear the "Tuning in…"
    // card even when the 'playing' event was missed (a seek-resume that never re-fires it, a reused
    // prewarm, or a handler that threw before the state flush). No-op once already cleared.
    const onTimeUpdate = () => {
      if (video.currentTime > 0 && !video.paused) setTuning(false);
    };
    // Tuning into a frozen channel never plays, so 'playing'/'timeupdate' never fire — the held frame
    // arriving is the only signal that we're tuned. Without this the "Tuning in…" card sits over a
    // channel someone paused hours ago.
    const onLoadedData = () => {
      if (pausedRef.current) setTuning(false);
    };
    video.addEventListener("ended", onEnded);
    video.addEventListener("playing", onPlaying);
    video.addEventListener("timeupdate", onTimeUpdate);
    video.addEventListener("loadeddata", onLoadedData);
    return () => {
      video.removeEventListener("ended", onEnded);
      video.removeEventListener("playing", onPlaying);
      video.removeEventListener("timeupdate", onTimeUpdate);
      video.removeEventListener("loadeddata", onLoadedData);
    };
  }, [channel, tune, wakeChrome]);

  // ── adaptive-bitrate sampler (channel Auto) ─────────────────────────────────
  // hls.js refines bandwidthEstimate as segments load; sample it while playing so Auto can climb
  // back toward lossless after a drop. Direct-play has no hls.js/estimate — but that's already the
  // original file, so there's nothing to climb to.
  useEffect(() => {
    const sample = setInterval(() => {
      if (!isAutoQuality(qualityRef.current)) return;
      // bandwidthSample discards the fresh-instance placeholder (see streamEngine): this sampler runs
      // unconditionally, including straight through a re-tune, so it would otherwise feed the ABR a
      // canned 500 kbps "measurement" every time the channel restarts its stream.
      const est = bandwidthSample(hlsRef.current);
      if (est) handleBandwidth(est);
    }, 5000);
    return () => clearInterval(sample);
  }, [handleBandwidth]);

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
          playableId: prewarmRef.current.playableId,
        });
        prewarmRef.current = null;
      }
      destroyHls();
    };
  }, [stopSession, destroyHls]);

  // Keep the screen awake while a channel is up — shared with the Watch player.
  useWakeLock();

  // ── hidden-tab teardown ─────────────────────────────────────────────────────
  // A muted, inaudible, hidden tab gets its timers throttled to ~once/min by the browser, so the
  // 10s passive beat can slip below Jellyfin's 60s ping timeout — the server then kills/cold-restarts
  // the transcode while segments keep needlessly downloading, all for a channel nobody's watching.
  // A channel is a passive broadcast with nothing to preserve, so after a short grace we deliberately
  // stop the stream (a clean stop, not a server kill) and re-tune at the live offset on return.
  useEffect(() => {
    const onVisibility = () => {
      if (document.visibilityState === "hidden") {
        clearTimeout(hiddenGraceRef.current);
        hiddenGraceRef.current = setTimeout(() => {
          clearTimeout(advanceTimerRef.current);
          clearTimeout(prewarmTimerRef.current);
          stopSession();
          destroyHls();
          if (prewarmRef.current) {
            MovieAPI.stopStream({
              playSessionId: prewarmRef.current.session.playSessionId,
              playableId: prewarmRef.current.playableId,
            });
            prewarmRef.current = null;
          }
        }, 30_000);
      } else {
        clearTimeout(hiddenGraceRef.current);
        // Re-tune only if the grace timer actually tore the stream down while we were away.
        if (!sessionRef.current && channelRef.current) tuneRef.current?.(channelRef.current);
      }
    };
    document.addEventListener("visibilitychange", onVisibility);
    return () => {
      document.removeEventListener("visibilitychange", onVisibility);
      clearTimeout(hiddenGraceRef.current);
    };
  }, [stopSession, destroyHls]);

  // Arm the idle fade once playing starts. Re-arm when the channel, pause, or popout state
  // changes so a resume or a just-closed menu restarts the countdown rather than fading instantly.
  useEffect(() => {
    wakeChrome();
  }, [wakeChrome, channel?.id, paused, gridOpen, menuOpen, guideOpen]);

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
      writeStored(STREAM_QUALITY_KEY, key);
      setQualityOpen(false);
      // Selecting an Auto mode reseeds at that mode's opener with a fresh streak.
      if (isAutoQuality(key)) reseed(abrProfileFor(key).openBps);
      if (channel) tune(channel);
    },
    [channel, tune, reseed]
  );

  // Pin a specific audio track and re-tune at the live offset. No "auto" entry is needed —
  // leaving audio unpinned is what lets the server English-default, and picking a track here
  // overrides that for this viewer until they leave.
  const selectAudio = useCallback(
    (index) => {
      audioIndexRef.current = index;
      setAudioIndex(index);
      setAudioOpen(false);
      if (channel) tune(channel);
    },
    [channel, tune]
  );

  // Subtitles: text (sidecar) tracks toggle client-side via <track> — free, no transcode. Image
  // subs (no deliveryUrl) can only be burned in, so those re-tune; null = off. We re-tune only
  // when the *burned-in* sub changes, so flipping between text tracks or off never transcodes.
  const selectSubtitle = useCallback(
    (index) => {
      const track = subtitleTracks.find((t) => t.index === index);
      const nextBurn = track && !track.deliveryUrl ? index : null; // only image subs burn
      const prevBurn = burnSubIndexRef.current;
      subtitleIndexRef.current = index;
      burnSubIndexRef.current = nextBurn;
      setSubtitleIndex(index);
      setSubsOpen(false);
      if (channel && nextBurn !== prevBurn) tune(channel);
    },
    [channel, subtitleTracks, tune]
  );

  // Local, per-viewer volume (the shared channel state is only play/pause). Dragging to 0
  // mutes; dragging up unmutes. Persisted so the next visit remembers it.
  const toggleMute = useCallback(() => setMuted((m) => !m), []);
  const changeVolume = useCallback((v) => {
    setVolume(v);
    writeStored("TvVolume", v);
    setMuted(v === 0);
  }, []);

  // The channel button opens the cross-channel grid guide — the classic "what's coming up" view.
  const openGrid = useCallback((e) => {
    e?.stopPropagation();
    setMenuOpen(false);
    setGridOpen(true);
  }, []);
  // Picking a channel from the guide tunes to it and closes it.
  const pickChannel = useCallback((ch) => {
    setGridOpen(false);
    setChannel(ch);
  }, []);
  const toggleMenu = useCallback((e) => {
    e.stopPropagation();
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
    // Leaving the channel aborts the beat in flight: its answer describes the channel we just left,
    // and the re-tune below would tune us straight back to it.
    const ctrl = new AbortController();
    const poll = setInterval(async () => {
      try {
        const askedAt = performance.now();
        const data = await MovieAPI.getChannelNow(channel.id, ctrl.signal);
        // Re-anchor the channel clock on every beat, so the drift corrector tracks the server rather
        // than compounding our own measurement error between tunes.
        if (data.current && !data.paused) anchorSync(data, performance.now() - askedAt);
        setSkip(data.skip || null);
        setRestart(data.restart || null);
        setViewers(data.viewers || null);

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
        } else if (data.current && currentItemIdRef.current == null) {
          // We were off-air (e.g. tuned in before the schedule existed) and the channel has
          // since come alive — recover without waiting for a manual reload or channel switch.
          tune(channel);
        }
      } catch {
        /* transient (or aborted) — the next poll retries */
      }
    }, 12_000);
    return () => {
      clearInterval(poll);
      ctrl.abort();
    };
  }, [channel, tune, anchorSync]);

  // Cast a skip vote for the current item. If it carries the majority the server collapses
  // the schedule and we jump straight to the next movie; otherwise we just reflect the tally.
  const voteSkip = useCallback(async () => {
    if (!channel) return;
    try {
      const data = await MovieAPI.voteChannelSkip(channel.id, currentItemIdRef.current ?? 0);
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
      const data = await MovieAPI.voteChannelRestart(channel.id, currentItemIdRef.current ?? 0);
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
      const data = await MovieAPI.toggleChannelPlayPause(channel.id);
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

  // Scrubbing the bar moves the shared channel timeline, so it's only offered when you're the lone
  // viewer (and not frozen). A live tune always sets `viewers`, so treat a not-yet-loaded count as 1.
  const canSeek = !!now?.current && !paused && (viewers?.count ?? 1) <= 1;

  // Seek the channel to an absolute offset in the current film. The server shifts the schedule (refusing
  // if anyone else tuned in meanwhile) and we re-tune at the new live position — the mirror of voteRestart.
  const seekTo = useCallback(
    async (offsetSeconds) => {
      if (!channel) return;
      try {
        const data = await MovieAPI.seekChannel(channel.id, currentItemIdRef.current ?? 0, offsetSeconds);
        if (data.seeked) tune(channel);
      } catch {
        /* ignore — they can scrub again */
      }
    },
    [channel, tune]
  );

  // OS media integration (shared hook): lock-screen now-playing + media keys. TV maps play/pause to the
  // SHARED channel pause (same as the on-screen button) and prev/next to channel down/up; no seek (the
  // channel timeline is shared and forward-only). Position state still drives a read-only lock scrubber.
  const pip = usePictureInPicture(videoRef);
  useMediaSession({
    videoRef,
    title: now?.current?.title,
    subtitle: channel?.name,
    poster: now?.current?.posterId
      ? MovieAPI.getPosterThumbnail(now.current.posterId, now.current.posterVersion, now.current.kind)
      : null,
    actions: {
      play: () => { if (pausedRef.current) togglePlayPause(); },
      pause: () => { if (!pausedRef.current) togglePlayPause(); },
      previoustrack: () => switchBy(-1),
      nexttrack: () => switchBy(1),
    },
  });

  // Map a pointer's x to a position on the bar → { pct 0..1, seconds into the film }.
  const offsetFromPointer = useCallback(
    (clientX) => {
      const el = progressRef.current;
      const duration = now?.current?.durationSeconds;
      if (!el || !duration) return null;
      const rect = el.getBoundingClientRect();
      const pct = Math.min(Math.max((clientX - rect.left) / rect.width, 0), 1);
      return { pct, seconds: pct * duration };
    },
    [now]
  );

  const onScrubDown = useCallback(
    (e) => {
      if (!canSeek) return;
      e.stopPropagation();
      e.currentTarget.setPointerCapture?.(e.pointerId);
      scrubbingRef.current = true;
      const p = offsetFromPointer(e.clientX);
      if (p) setScrubHover(p);
      wakeChrome();
    },
    [canSeek, offsetFromPointer, wakeChrome]
  );

  const onScrubMove = useCallback(
    (e) => {
      if (!canSeek) return;
      const p = offsetFromPointer(e.clientX);
      if (p) setScrubHover(p);
    },
    [canSeek, offsetFromPointer]
  );

  const onScrubUp = useCallback(
    (e) => {
      if (!scrubbingRef.current) return;
      e.stopPropagation();
      scrubbingRef.current = false;
      const p = offsetFromPointer(e.clientX);
      setScrubHover(null);
      setFillSnap(true);
      if (p) seekTo(p.seconds);
    },
    [offsetFromPointer, seekTo]
  );

  const onScrubLeave = useCallback(() => {
    if (!scrubbingRef.current) {
      setScrubHover(null);
      setFillSnap(true);
    }
  }, []);

  // The fill normally eases over 1s so it advances smoothly with the clock. After the pointer
  // leaves (or a seek lands) we want it at the live position immediately, not crawling back from
  // the hover spot — hold "no transition" for a couple of frames, then restore the smooth ease.
  useEffect(() => {
    if (!fillSnap) return undefined;
    let inner;
    const outer = requestAnimationFrame(() => {
      inner = requestAnimationFrame(() => setFillSnap(false));
    });
    return () => { cancelAnimationFrame(outer); if (inner) cancelAnimationFrame(inner); };
  }, [fillSnap]);

  useEffect(() => {
    const onKey = (e) => {
      if (e.target.tagName === "INPUT" || adminOpen) return;
      // While the guide is open it owns the keyboard (its own Esc closes it).
      if (gridOpen) return;
      if (e.key === "ArrowUp") switchBy(1);
      else if (e.key === "ArrowDown") switchBy(-1);
      else if (e.key === "m") setMuted((m) => !m);
      else if (e.key === "g") setGuideOpen((g) => !g);
      else if (e.key === "c") setGridOpen(true); // open the channel guide
      else if (e.key === "k" || e.key === " ") togglePlayPause();
      else if (e.key === "f") toggleFullscreen();
      else if (/^[1-9]$/.test(e.key) && channels) {
        const target = channels[parseInt(e.key, 10) - 1];
        if (target) setChannel(target);
      } else return;
      e.preventDefault();
      wakeChrome();
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [switchBy, channels, adminOpen, gridOpen, togglePlayPause, toggleFullscreen, wakeChrome]);

  useEffect(() => {
    const video = videoRef.current;
    if (!video) return;
    video.muted = muted;
    video.volume = volume;
  }, [muted, volume]);

  // Sidecar text subtitles render client-side: show the chosen track, DISABLE the rest. Re-applies
  // when the track list changes (a new film) so freshly-mounted <track>s pick up the selection.
  // "hidden" would leave them active and the browser would fetch every cue file at once — see the
  // same effect in VideoPlayer.js for what that costs on a title with 33 embedded subtitle tracks.
  useEffect(() => {
    const video = videoRef.current;
    if (!video) return;
    for (const track of Array.from(video.textTracks)) {
      track.mode = String(track.id) === String(subtitleIndex) ? "showing" : "disabled";
    }
  }, [subtitleIndex, subtitleTracks]);

  // Vertical lift for the showing track's cues (size/color/font/edge/box ride on the injected ::cue
  // rule from useSubtitleStyle). reloadKey = subtitleTracks: a new film replaces the <track> set, so
  // re-apply when it changes.
  useCueLift(videoRef, subtitleIndex, subtitleTracks, subStyle.liftPct);

  // Client-rendered PGS (Blu-ray bitmap) subs via libpgs — keeps the video copied instead of burned.
  const activePgsSub = subtitleTracks.find((t) => t.index === subtitleIndex && t.kind === "image-pgs");
  usePgsSubtitle(videoRef, activePgsSub ? activePgsSub.deliveryUrl : null, timelineOffset);

  // Client-rendered ASS/SSA via libass — full typesetting, also keeps the video copied.
  const activeAssSub = subtitleTracks.find((t) => t.index === subtitleIndex && t.kind === "ass");
  useAssSubtitle(videoRef, activeAssSub ? activeAssSub.deliveryUrl : null, timelineOffset);

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
    if (!guideOpen || !channel) return undefined;
    // Closing the list or changing channel aborts the read — a late answer would paint the previous
    // channel's lineup under the new channel's name.
    const ctrl = new AbortController();
    MovieAPI.getChannelGuide(channel.id, 12, ctrl.signal)
      .then(setGuide)
      // An abort is a teardown, not a failure — leave what's on screen alone; only a real error empties it.
      .catch((err) => { if (err.name !== "AbortError") setGuide([]); });
    return () => ctrl.abort();
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
    // Passwordless accounts fall through to the generic 403 — only an admin can
    // grant a first password, so there's nothing to point them at.
    if (error.status === 403) return "This TV isn't available on your account.";
    if (error.status === 404 || error.status === 501) return "The broadcast tower isn't built yet.";
    return error.message || "The signal dropped.";
  })();

  const volumeIcon = muted || volume === 0 ? "🔇" : volume < 0.5 ? "🔉" : "🔊";

  // The bar fades out only once the chrome is idle and nothing is popped out over the picture.
  const chromeHidden = !chromeVisible && !gridOpen && !menuOpen && !guideOpen;

  return (
    /* eslint-disable jsx-a11y/no-static-element-interactions, jsx-a11y/click-events-have-key-events, jsx-a11y/media-has-caption */
    <div className={`tv-room${chromeHidden ? " tv-room--idle" : ""}`} ref={roomRef} onMouseMove={wakeChrome}>
      {/* The picture area. Nothing in the control bar overlaps it; only the picker / menu /
          guide pop out over it, and only while open. */}
      <div className="tv-screen" onClick={onScreenTap}>
        <video ref={videoRef} className="tv-video" autoPlay playsInline muted crossOrigin="anonymous">
          {subtitleTracks
            .filter((t) => t.deliveryUrl && t.kind !== "image-pgs" && t.kind !== "ass")
            .map((t) => (
              <track key={t.index} id={String(t.index)} kind="subtitles" label={t.label} src={t.deliveryUrl} srcLang={t.language || "en"} />
            ))}
        </video>

        {/* live caption-style preview (shared component): faithful sample at the real caption height */}
        {styleOpen && <SubtitleStylePreview subStyle={subStyle} />}

        {/* transient subtitle-delay readout while nudging */}
        {offsetToast && activeTextSub && (
          <div className="tv-sub-toast" aria-live="polite">
            Subtitle delay {formatDelay(subtitleOffsetMs)}
          </div>
        )}

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
              qualityOptions(quality).map((q) => (
                <button
                  key={q.key}
                  className={`tv-channel-item tv-channel-item--qopt${q.selected ? " tv-channel-item--on" : ""}`}
                  onClick={() => selectQuality(q.key)}
                >
                  <span className="tv-channel-num">·</span>
                  {q.label}
                  {q.hint ? <span className="tv-qopt-hint">{q.hint}</span> : null}
                </button>
              ))}
            {audioTracks.length > 1 && (
              <>
                <button
                  className={`tv-channel-item${audioOpen ? " tv-channel-item--on" : ""}`}
                  onClick={() => setAudioOpen((a) => !a)}
                >
                  <span className="tv-channel-num">A</span>
                  Audio
                  <span className="tv-qopt-hint">
                    {audioTracks.find((t) => t.index === (audioIndex ?? playingAudioIndex))?.label || "Default"}
                  </span>
                </button>
                {audioOpen &&
                  audioOptions(audioTracks, audioIndex ?? playingAudioIndex).map((t) => (
                    <button
                      key={t.index}
                      className={`tv-channel-item tv-channel-item--qopt${t.selected ? " tv-channel-item--on" : ""}`}
                      onClick={() => selectAudio(t.index)}
                    >
                      <span className="tv-channel-num">·</span>
                      {t.label}
                    </button>
                  ))}
              </>
            )}
            {subtitleTracks.length > 0 && (
              <>
                <button
                  className={`tv-channel-item${subsOpen ? " tv-channel-item--on" : ""}`}
                  onClick={() => setSubsOpen((s) => !s)}
                >
                  <span className="tv-channel-num">S</span>
                  Subtitles
                  <span className="tv-qopt-hint">
                    {subtitleIndex == null ? "Off" : subtitleTracks.find((t) => t.index === subtitleIndex)?.label || "On"}
                  </span>
                </button>
                {subsOpen && (
                  <>
                    {subtitleOptions(subtitleTracks, subtitleIndex).map((t) => (
                      <button
                        key={t.index ?? "off"}
                        className={`tv-channel-item tv-channel-item--qopt${t.selected ? " tv-channel-item--on" : ""}`}
                        onClick={() => selectSubtitle(t.index)}
                      >
                        <span className="tv-channel-num">·</span>
                        {t.label}
                        {t.hint && <span className="tv-qopt-hint">{t.hint}</span>}
                      </button>
                    ))}
                    {/* subtitle timing fix — soft text tracks only (client-side re-time, per-viewer) */}
                    {activeTextSub && (
                      <SubtitleSyncControls
                        offsetMs={subtitleOffsetMs}
                        nudge={nudgeSubtitle}
                        reset={resetSubtitleOffset}
                        rateScale={subtitleRateScale}
                        abStep={subtitleAbStep}
                        abError={subtitleAbError}
                        beginSync={beginSubtitleSync}
                        capturePoint={captureSubtitleSyncPoint}
                        cancelSync={cancelSubtitleSync}
                      />
                    )}
                    {/* caption appearance editor (shared with the Watch player) + live on-video preview */}
                    <button
                      className={`tv-channel-item${styleOpen ? " tv-channel-item--on" : ""}`}
                      onClick={() => setStyleOpen((o) => !o)}
                      aria-expanded={styleOpen}
                    >
                      <span className="tv-channel-num">✦</span>
                      Subtitle style
                      <span className="tv-qopt-hint">{styleOpen ? "▾" : "▸"}</span>
                    </button>
                    {styleOpen && (
                      <SubtitleStyleControls subStyle={subStyle} setStyle={setStyle} setSubStyle={setSubStyle} />
                    )}
                  </>
                )}
              </>
            )}
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

            {/* What's actually being delivered — quality, codec, and whether it's the original copied
                bit-for-bit (no re-encode) or a transcode. */}
            {now?.current && (
              <>
                <div className="tv-menu-section">Playing</div>
                <div className="tv-menu-readout">
                  {formatPlaying({
                    qualityKey: quality,
                    autoLabel: autoBpsLabel(autoBps),
                    videoCodec: playingVideoCodec,
                    isHls: playingHls,
                    isDirectStream: playingDirect,
                    audio: deliveredAudio(audioTracks, audioIndex ?? playingAudioIndex),
                  })}
                </div>
              </>
            )}
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

      {/* flattened control bar — lives below the picture so it never covers it; slides away
          while idle (tap the picture, or move the mouse, to bring it back) */}
      <div className={`tv-bar${chromeHidden ? " tv-bar--hidden" : ""}`}>
        {/* Live progress. For the lone viewer it doubles as a scrubber — drag/click to seek the
            channel (which shifts the shared schedule); otherwise it's a read-only fill. */}
        <div
          ref={progressRef}
          className={`tv-bar-progress${canSeek ? " tv-bar-progress--seekable" : ""}`}
          onPointerDown={onScrubDown}
          onPointerMove={onScrubMove}
          onPointerUp={onScrubUp}
          onPointerLeave={onScrubLeave}
          role={canSeek ? "slider" : undefined}
          aria-label={canSeek ? "Seek" : undefined}
          aria-valuemin={canSeek ? 0 : undefined}
          aria-valuemax={canSeek ? Math.round(now?.current?.durationSeconds || 0) : undefined}
          aria-valuenow={canSeek ? Math.round(((scrubHover ? scrubHover.pct * 100 : progressPct) / 100) * (now?.current?.durationSeconds || 0)) : undefined}
        >
          <div className="tv-bar-progress-fill" style={{ width: `${scrubHover ? scrubHover.pct * 100 : progressPct}%`, transition: scrubHover || fillSnap ? "none" : undefined }} />
          {canSeek && <div className="tv-bar-progress-thumb" style={{ left: `${scrubHover ? scrubHover.pct * 100 : progressPct}%` }} />}
          {scrubHover && (
            <div className="tv-bar-progress-tip" style={{ left: `${scrubHover.pct * 100}%` }}>
              {formatTime(scrubHover.seconds)}
            </div>
          )}
        </div>
        <div className="tv-bar-row">
          {channel && (
            <button className="tv-bar-channel" onClick={openGrid} title="Open the channel guide (C)">
              <span className="tv-bar-channel-num">{channelNumber}</span>
              <span className="tv-bar-channel-name">{channel.name}</span>
              <span className="tv-bar-channel-guide">
                <span className="tv-bar-guide-icon" aria-hidden="true" />
                <span className="tv-bar-guide-label">Guide</span>
              </span>
            </button>
          )}

          {now?.current && (
            <div className="tv-bar-info">
              {now.current.posterId ? (
                <FallbackImage
                  className="tv-bar-poster"
                  src={MovieAPI.getPosterThumbnail(now.current.posterId, now.current.posterVersion, now.current.kind)}
                  alt=""
                />
              ) : null}
              <span className="tv-bar-textcol">
                <span className="tv-bar-titleline">
                  <span className="tv-bar-tag">Now</span>
                  <span className="tv-bar-title">{now.current.title}</span>
                  <span className="tv-bar-time">ends {new Date(now.current.endsAtUtc).toLocaleTimeString([], { hour: "numeric", minute: "2-digit" })}</span>
                </span>
                {now.current.reason ? <span className="tv-bar-reason">✨ {now.current.reason}</span> : null}
                {now.current.plot ? <span className="tv-bar-plot">{now.current.plot}</span> : null}
              </span>
            </div>
          )}

          <div className="tv-bar-spacer" />

          {/* "Up Next" lives on the right, mirroring the Now block on the left so the bar reads
              balanced instead of piling everything into the left corner. */}
          {now?.next?.[0] && (
            <div className="tv-bar-upnext">
              {now.next[0].posterId ? (
                <FallbackImage
                  className="tv-bar-poster tv-bar-poster--sm"
                  src={MovieAPI.getPosterThumbnail(now.next[0].posterId, now.next[0].posterVersion, now.next[0].kind)}
                  alt=""
                />
              ) : null}
              <span className="tv-bar-textcol">
                <span className="tv-bar-titleline">
                  <span className="tv-bar-tag">Up Next</span>
                  <span className="tv-bar-time">{new Date(now.next[0].startsAtUtc).toLocaleTimeString([], { hour: "numeric", minute: "2-digit" })}</span>
                </span>
                <span className="tv-bar-next-title">{now.next[0].title}</span>
              </span>
            </div>
          )}

          <div className="tv-bar-controls">
          {viewers?.count > 1 && (
            <div
              className="tv-bar-viewers"
              tabIndex={0}
              role="group"
              aria-label={`${viewers.count} watching`}
            >
              <span className="tv-bar-viewers-eye" aria-hidden="true">👁</span>
              <span className="tv-bar-viewers-count">{viewers.count}</span>
              {/* hover/focus to reveal who's connected */}
              <div className="tv-viewers-tip" role="tooltip">
                <div className="tv-viewers-tip-head">Watching now</div>
                {viewers.names.map((v, i) => (
                  <div key={i} className="tv-viewers-tip-name">
                    {v.name}
                    {v.you && <span className="tv-viewers-tip-you">you</span>}
                  </div>
                ))}
              </div>
            </div>
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

          {pip.supported && (
            <button
              className={`tv-bar-icon-btn${pip.active ? " tv-bar-icon-btn--on" : ""}`}
              onClick={(e) => { e.stopPropagation(); pip.toggle(); }}
              title="Picture in picture"
            >
              <span className="tv-glyph-pip" />
            </button>
          )}

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
      </div>

      {/* The cross-channel grid guide (EPG) — the classic "what's coming up" chooser (channel button + C). */}
      <ChannelGrid
        open={gridOpen}
        channels={channels || []}
        currentChannelId={channel?.id ?? null}
        onPick={pickChannel}
        onClose={() => setGridOpen(false)}
      />

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
