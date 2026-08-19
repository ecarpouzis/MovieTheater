import { useEffect, useRef } from "react";

// A recorder for video playback failures that nobody is standing there to catch.
//
// Until now the movie/TV players had no self-report at all. A session that broke was reconstructed
// afterwards from two SERVER artifacts — the gateway's access-stream log (bytes/duration per
// segment) and the names of Jellyfin's ffmpeg logs (which rung, which playhead) — and that
// reconstruction works, beautifully, for exactly the failures somebody thought to ask about while
// those logs still held the window. Everything else was invisible: the viewer refreshed, or shrugged
// and went to bed, and the one participant that KNEW what happened — the player — said nothing.
//
// The music player has reported its own failures for months (Music/musicDiag.js) and it is the
// reason the sleeping-phone stall was root-caused at all, after twenty-odd attempts to catch one
// live had failed. This is that instrument, for video: a small in-memory ring of what just happened,
// posted by beacon the moment something goes wrong, with the ladder state attached.
//
// ── WHAT DELIBERATELY DOES NOT FIRE ─────────────────────────────────────────
// The point of a self-report table is that a row MEANS something. The video player's normal life is
// full of multi-second freezes that are not failures, and every one of them would out-number the
// real incidents if it were reported:
//
//   * The ABR restart. Every quality switch — a drop on stalls, a climb, a manual pick, an audio or
//     subtitle change, a channel re-tune — tears the session down and starts a fresh ffmpeg. That
//     costs seconds (a cold transcode start can hold the picture for ~10 s, and a measured refresh
//     took ~11 s to first segment), during which the element fires exactly the same `waiting` a
//     genuine underrun fires. It is EXPECTED, it is even labelled on screen ("Adjusting quality"),
//     and it is the single biggest false-positive risk here. So every switch marks the clock
//     (noteStreamSwitch) and the stall watch stays quiet for SWITCH_GRACE_MS afterwards.
//   * A scrub. The viewer's own seek rebuffers; that is the viewer's doing.
//   * A pause. Nothing is stalling if nothing is meant to be playing.
//   * Blocked autoplay. The startup watch only fires if play() was actually reached (`play` fired
//     but `playing` never did) — a tap-to-start prompt and a frozen TV channel are not failures.
//   * The routine `ended` at the end of a film, and the TV player's end-of-item advance.
//
// Trade, stated plainly: a REAL stall inside 30 s of a switch is not reported. That is the right way
// round — a table full of restarts that read as failures would be worse than no table, because the
// first thing anyone does with an incident row is believe it.

const REPORT_URL = "/API/Stream/Incident";

// One report a minute, five a session — music's limits, and for music's reason: a failing player
// tends to fail in a loop, and a loop must not become a flood. `force` skips the GAP (a terminal,
// once-only failure shouldn't be swallowed because a stall was reported 40 s ago); nothing skips
// the session ceiling.
const REPORT_MIN_GAP_MS = 60_000;
const REPORT_MAX_PER_SESSION = 5;

// The ring: enough to hold the run-up to a failure, small enough to post inside a beacon.
const MAX_EVENTS = 120;

// How long `waiting` must persist, while playing, before it is a stall rather than a hiccup. The
// player's own buffer routinely rides out a second or two on a bitrate spike (that is what the
// 400 MB / 2 min buffer is for); ten seconds of frozen picture is what a viewer calls "it stopped".
const STALL_REPORT_MS = 10_000;

// Silence after a stream switch. Generous on purpose — see the note above: the restart's own
// rebuffer is indistinguishable from an underrun at the element, and a cold ffmpeg start has been
// measured at ~10 s on its own. (The ABR machine's own post-switch grace is 10 s, but that one only
// has to stop a downshift cascade; this one has to keep a row out of the table.)
const SWITCH_GRACE_MS = 30_000;

// A stream that was asked to play and never produced a frame. Long enough to clear a cold transcode
// start plus the manifest retries (hls.js is configured for 30 s timeouts and 6 manifest retries).
const STARTUP_TIMEOUT_MS = 45_000;

// `ended` this far short of the known duration is the stream giving up, not the film finishing.
const EARLY_END_SECONDS = 60;

// Repeated playlist/segment fetch failures. hls.js retries these on its own and usually wins, so a
// single one is noise; three inside half a minute is a delivery path that is genuinely failing.
const PLAYLIST_ERROR_WINDOW_MS = 30_000;
const PLAYLIST_ERRORS_TO_REPORT = 3;

let events = [];
let nextId = 1;
let lastReportAt = 0;
let reportsSent = 0;
let lastSwitchAt = 0;
let lastEstimate = null;      // { bps, at } — the freshest throughput reading the ABR sampler took
let playlistErrors = [];      // timestamps of recent load failures
let context = null;           // set by useVideoIncidents while a player is mounted
let activeWatcher = null;     // the mounted player's watcher, so a switch can reset its clocks

/**
 * Record one event in the ring. Keep `data` small — this is called from media event handlers.
 *
 * Unlike music's ring this is NOT persisted: the music failure is a phone that reloads the page and
 * takes the evidence with it, while a video failure leaves the page standing (the player shows a
 * card, or freezes, and the viewer is looking at it). In-memory is enough here, and costs nothing.
 */
export function noteVideoEvent(event, data) {
  events.push({
    id: nextId++,
    at: Date.now(),
    hidden: typeof document !== "undefined" ? document.visibilityState === "hidden" : null,
    event,
    data: data || null,
  });
  if (events.length > MAX_EVENTS) events = events.slice(-MAX_EVENTS);
}

/** The ring, for tests and for anyone reading it in a console. */
export function videoEvents() {
  return events;
}

/**
 * Empty the ring and the session's report budget. "Clear" means start fresh — the same contract as
 * music's clearDiag, and what every test wants between cases.
 */
export function clearVideoIncidents() {
  events = [];
  nextId = 1;
  lastReportAt = 0;
  reportsSent = 0;
  lastSwitchAt = 0;
  lastEstimate = null;
  playlistErrors = [];
}

/**
 * Who is playing what, so a report carries identity without every call site repeating it.
 *
 * Module-level because at most one video player is ever mounted (both are full-page routes), which
 * is also what lets the shared engine (streamEngine.createHls) report a fatal error without being
 * handed any props. With no context set — the photo-album video player, which shares createHls but
 * is not what this table is for — reports are dropped rather than filed as anonymous rows.
 */
export function setVideoIncidentContext(next) {
  context = next;
}

export function clearVideoIncidentContext() {
  context = null;
}

/**
 * Mark "a new stream is starting". Called from useAdaptiveBitrate on every adapt, from the hook when
 * the source changes, and from the TV player when it re-tunes — i.e. from every path that pays a
 * session restart. This is the discrimination the whole file turns on: what follows is an EXPECTED
 * multi-second freeze, not a stall.
 */
export function noteStreamSwitch(reason) {
  lastSwitchAt = Date.now();
  noteVideoEvent("switch", reason ? { reason } : null);
  activeWatcher?.sourceChanged();
}

/** Milliseconds since the last stream switch (Infinity before the first one). */
export function msSinceStreamSwitch() {
  return lastSwitchAt ? Date.now() - lastSwitchAt : Infinity;
}

/**
 * The ABR sampler's latest throughput reading. Stored, not ringed: it arrives every 5 s on a healthy
 * session and would be the whole ring by itself, but it is the first number anyone wants when
 * reading a stall ("was the link actually gone?").
 */
export function noteBandwidthEstimate(bps) {
  if (bps && isFinite(bps)) lastEstimate = { bps: Math.round(bps), at: Date.now() };
}

/** The ladder/network snapshot that rides along with every report. */
function snapshot() {
  const ladder = context?.ladder || {};
  return {
    quality: ladder.qualityKey ?? null,
    // Infinity doesn't survive JSON — the lossless tier is named, not numbered.
    rung: ladder.autoBps == null ? null : isFinite(ladder.autoBps) ? ladder.autoBps : "direct",
    copied: ladder.copied ?? null,
    codec: ladder.codec ?? null,
    sourceBps: ladder.sourceVideoBps ?? null,
    estimateBps: lastEstimate ? lastEstimate.bps : null,
    estimateAgeMs: lastEstimate ? Date.now() - lastEstimate.at : null,
    sinceSwitchMs: lastSwitchAt ? Date.now() - lastSwitchAt : null,
  };
}

/**
 * Post one incident. Returns whether it was handed off.
 *
 * sendBeacon with `text/plain`, for the same two reasons music uses it: the page may be freezing or
 * unloading (a fetch at that moment is as likely to be dropped as the segment request that just
 * failed), and text/plain keeps it a CORS-simple request that never waits on a preflight.
 *
 * The budget is spent AFTER the hand-off. Spending it first meant a beacon the browser refused
 * outright still armed the gap and burned one of the five — so on music's sleeping phone the reports
 * that failed were the ones that silenced everything after them.
 */
export function reportVideoIncident(kind, { summary = "", force = false } = {}) {
  // No context = no player of ours on screen (see setVideoIncidentContext). Nothing to file.
  if (!context) return false;
  const now = Date.now();
  if (reportsSent >= REPORT_MAX_PER_SESSION) return false;
  if (!force && now - lastReportAt < REPORT_MIN_GAP_MS) return false;

  let positionSeconds = null;
  try {
    const p = context.getPosition?.();
    if (typeof p === "number" && isFinite(p)) positionSeconds = Math.round(p * 100) / 100;
  } catch { /* an element mid-teardown can throw; the report is still worth sending */ }

  const body = JSON.stringify({
    kind,
    summary: String(summary).slice(0, 400),
    player: context.player,
    movieId: context.movieId ?? null,
    seriesId: context.seriesId ?? null,
    miscVideoId: context.miscVideoId ?? null,
    playableId: context.playableId ?? null,
    channelId: context.channelId ?? null,
    positionSeconds,
    userAgent: (typeof navigator !== "undefined" ? navigator.userAgent : "").slice(0, 400),
    state: snapshot(),
    events,
  });

  let queued = false;
  try {
    if (typeof navigator !== "undefined" && navigator.sendBeacon) {
      queued = navigator.sendBeacon(REPORT_URL, new Blob([body], { type: "text/plain" }));
    } else {
      fetch(REPORT_URL, { method: "POST", body, headers: { "Content-Type": "text/plain" }, keepalive: true })
        .catch(() => { /* a failed report must never surface to the viewer */ });
      queued = true;
    }
  } catch {
    queued = false;
  }
  if (queued) {
    lastReportAt = now;
    reportsSent += 1;
  }
  return queued;
}

/**
 * A fatal player error, from the shared engine's staged recovery giving up (streamEngine.createHls
 * onFatal) or from the element's own MediaError. Forced past the rate-limit gap: it is terminal and
 * once-only, and it is the report most worth having.
 */
export function reportFatal(summary, data) {
  noteVideoEvent("fatal", data || null);
  return reportVideoIncident("fatal", { summary, force: true });
}

/**
 * A playlist/segment fetch failure from hls.js. Ringed always; reported when it is fatal, or when it
 * is the third inside PLAYLIST_ERROR_WINDOW_MS — hls.js retries these itself and usually wins, so
 * one is noise and three is a delivery path that is failing.
 */
export function notePlaylistError({ details, fatal = false, code = null } = {}) {
  const now = Date.now();
  noteVideoEvent("load:failed", { details, fatal, code });
  playlistErrors = playlistErrors.filter((t) => now - t < PLAYLIST_ERROR_WINDOW_MS);
  playlistErrors.push(now);
  if (!fatal && playlistErrors.length < PLAYLIST_ERRORS_TO_REPORT) return false;
  playlistErrors = [];
  return reportVideoIncident("playlist-error", {
    summary: `${details || "load error"}${code ? ` (http ${code})` : ""}${fatal ? " — fatal" : ""}`,
    force: fatal,
  });
}

/**
 * The ABR machine dropping a rung because the stream kept stalling — the emergency downgrade. A
 * CLIMB is deliberately not reported: it is the link proving itself, and it costs a restart but not
 * a viewer's evening. A drop is the picture the viewer chose being taken away, which is the event
 * worth counting.
 */
export function reportAbrDowngrade({ fromBps, toBps, estimateBps }) {
  const name = (bps) => (bps == null ? "?" : isFinite(bps) ? `${Math.round(bps / 1e6)} Mbps` : "Original");
  noteVideoEvent("abr:down", { from: name(fromBps), to: name(toBps) });
  return reportVideoIncident("abr-downgrade", {
    summary:
      `dropped ${name(fromBps)} → ${name(toBps)}` +
      (estimateBps && isFinite(estimateBps) ? ` on ~${(estimateBps / 1e6).toFixed(1)} Mbps` : " with no fresh estimate"),
  });
}

/**
 * The failure state machine, framework-free so it can be driven by a test without a DOM or a clock.
 *
 * Everything time-based is decided in `tick()` against Date.now(), rather than by a setTimeout armed
 * at the event — so a test moves time and calls tick, and there is no timer to leak on unmount.
 */
export function createVideoWatcher({ report = reportVideoIncident } = {}) {
  let waitingSince = null;   // when the current `waiting` episode began (null = not waiting)
  let sawPlay = false;       // play() was actually reached for this source
  let sawPlaying = false;    // ...and frames arrived
  let sourceAt = Date.now(); // when this source started loading
  let startupReported = false;

  const clearWait = () => { waitingSince = null; };

  return {
    /** A new stream is loading: everything about the old one's health stops applying. */
    sourceChanged() {
      waitingSince = null;
      sawPlay = false;
      sawPlaying = false;
      startupReported = false;
      sourceAt = Date.now();
    },
    play() { sawPlay = true; },
    playing() { sawPlaying = true; clearWait(); },
    /** Time advancing is ground truth that nothing is stalled. */
    timeupdate: clearWait,
    seeking: clearWait,
    pause: clearWait,
    waiting() {
      if (waitingSince == null) waitingSince = Date.now();
    },
    error(mediaError) {
      const name = MEDIA_ERROR_NAMES[mediaError?.code] || mediaError?.code || "unknown";
      clearWait();
      return reportFatal(`MediaError ${name}${mediaError?.message ? `: ${mediaError.message}` : ""}`, { code: name });
    },
    /**
     * `ended` well short of the known duration — the stream gave up and the element called it a
     * finish. Reported only when a duration is actually known; a live-ish playlist that reports
     * nothing useful must not manufacture incidents.
     */
    ended({ position, duration } = {}) {
      clearWait();
      if (!duration || !isFinite(duration) || duration <= 0) return false;
      if (!isFinite(position)) return false;
      const remaining = duration - position;
      if (remaining <= EARLY_END_SECONDS) return false;
      noteVideoEvent("ended:early", { position: Math.round(position), duration: Math.round(duration) });
      return report("early-ended", {
        summary: `ended at ${Math.round(position)}s of ${Math.round(duration)}s (${Math.round(remaining)}s short)`,
        force: true,
      });
    },
    /** Drive the time-based checks. Called once a second by the hook. */
    tick() {
      const now = Date.now();
      let fired = null;
      if (waitingSince != null && now - waitingSince >= STALL_REPORT_MS && now - lastSwitchAt >= SWITCH_GRACE_MS) {
        const held = Math.round((now - waitingSince) / 1000);
        // Re-arm rather than clear: a stall that persists is still one stall, and the next report
        // has to wait out another STALL_REPORT_MS (on top of the rate limiter) to say so again.
        waitingSince = now;
        noteVideoEvent("stall", { heldSeconds: held });
        if (report("stall", { summary: `frozen ${held}s while playing` })) fired = "stall";
      }
      if (!startupReported && sawPlay && !sawPlaying && now - sourceAt >= STARTUP_TIMEOUT_MS) {
        startupReported = true;
        const waited = Math.round((now - sourceAt) / 1000);
        noteVideoEvent("startup:timeout", { waitedSeconds: waited });
        if (report("startup-timeout", { summary: `no first frame after ${waited}s`, force: true })) {
          fired = fired || "startup-timeout";
        }
      }
      return fired;
    },
    // Readable state, for tests.
    get isWaiting() { return waitingSince != null; },
  };
}

/** MediaError codes, spelled out — `err: 4` means nothing in a table six weeks later. */
export const MEDIA_ERROR_NAMES = {
  1: "ABORTED",
  2: "NETWORK",
  3: "DECODE",
  4: "SRC_NOT_SUPPORTED",
};

/**
 * Wire the watcher to a real <video> and publish the reporting context. Both players call this once;
 * nothing about detection lives in either of them.
 *
 * @param player     "watch" | "tv"
 * @param videoRef   ref to the <video> element
 * @param identity   { movieId, seriesId, miscVideoId, playableId, channelId } — whichever apply
 * @param ladder     { qualityKey, autoBps, copied, codec, sourceVideoBps } for the payload
 * @param sessionKey changes whenever the stream restarts (the Watch player passes its src). The TV
 *                   player has no such value — it calls noteStreamSwitch() from tune() instead,
 *                   which is the same mechanism and fires slightly earlier.
 * @param durationSeconds  the title's known duration in CONTENT seconds, for the early-`ended` test.
 * @param timelineOffsetRef  ref to the seconds the media timeline runs ahead of content time.
 */
export function useVideoIncidents({
  player,
  videoRef,
  identity = {},
  ladder = {},
  sessionKey,
  durationSeconds = 0,
  timelineOffsetRef,
} = {}) {
  const watcherRef = useRef(null);
  if (!watcherRef.current) watcherRef.current = createVideoWatcher();
  // Read inside handlers that aren't re-bound per render.
  const durationRef = useRef(durationSeconds);
  durationRef.current = durationSeconds;

  // Publish identity + ladder state every render: they change as the session does (a re-tune, an
  // ABR adapt, a new episode), and a report is only worth as much as the ids on it.
  const { movieId = null, seriesId = null, miscVideoId = null, playableId = null, channelId = null } = identity;
  const { qualityKey = null, autoBps = null, copied = null, codec = null, sourceVideoBps = null } = ladder;
  useEffect(() => {
    setVideoIncidentContext({
      player,
      movieId,
      seriesId,
      miscVideoId,
      playableId,
      channelId,
      ladder: { qualityKey, autoBps, copied, codec, sourceVideoBps },
      getPosition: () => {
        const video = videoRef?.current;
        if (!video) return null;
        return Math.max(0, (video.currentTime || 0) - (timelineOffsetRef?.current || 0));
      },
    });
  });
  // Separate effect so the publish above (no dep array) can't unpublish on every render.
  useEffect(() => clearVideoIncidentContext, []);

  // A new source is a new stream — and, more importantly, the seconds after it are an expected
  // freeze, not a stall.
  useEffect(() => {
    noteStreamSwitch("source");
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [sessionKey]);

  useEffect(() => {
    const watcher = watcherRef.current;
    activeWatcher = watcher;
    const video = videoRef?.current;
    if (!video) return () => { activeWatcher = null; };

    const onPlay = () => { noteVideoEvent("play"); watcher.play(); };
    const onPlaying = () => { noteVideoEvent("playing"); watcher.playing(); };
    const onWaiting = () => { noteVideoEvent("waiting"); watcher.waiting(); };
    const onTimeUpdate = () => watcher.timeupdate();
    const onSeeking = () => { noteVideoEvent("seeking"); watcher.seeking(); };
    const onPause = () => { noteVideoEvent("pause"); watcher.pause(); };
    const onError = () => watcher.error(video.error);
    const onEnded = () =>
      watcher.ended({
        position: Math.max(0, (video.currentTime || 0) - (timelineOffsetRef?.current || 0)),
        // The title's own duration when the page knows it (the Watch player is told); otherwise the
        // element's, which is what the TV player has for the item it joined.
        duration: durationRef.current || (isFinite(video.duration) ? video.duration : 0),
      });

    video.addEventListener("play", onPlay);
    video.addEventListener("playing", onPlaying);
    video.addEventListener("waiting", onWaiting);
    video.addEventListener("timeupdate", onTimeUpdate);
    video.addEventListener("seeking", onSeeking);
    video.addEventListener("pause", onPause);
    video.addEventListener("error", onError);
    video.addEventListener("ended", onEnded);
    const beat = setInterval(() => watcher.tick(), 1000);
    return () => {
      clearInterval(beat);
      video.removeEventListener("play", onPlay);
      video.removeEventListener("playing", onPlaying);
      video.removeEventListener("waiting", onWaiting);
      video.removeEventListener("timeupdate", onTimeUpdate);
      video.removeEventListener("seeking", onSeeking);
      video.removeEventListener("pause", onPause);
      video.removeEventListener("error", onError);
      video.removeEventListener("ended", onEnded);
      activeWatcher = null;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);
}
