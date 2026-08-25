import { useCallback, useEffect, useMemo, useRef, useState } from "react";

// ── Google Cast sender ────────────────────────────────────────────────────────
//
// The half of casting that talks to Google's SDK. The decision-making half (which device profile,
// which bitrate ceiling, which subtitle tracks survive the trip) is castProfiles.js, kept separate so
// it can be tested without the SDK's globals.
//
// WHY THE DEFAULT MEDIA RECEIVER. A *custom* receiver would let the TV report its own codec support
// back over a message channel — which is how you'd get an honest device profile instead of the
// conservative guess in castProfiles.js. It also costs a registered application id in the Google Cast
// Developer Console (a paid developer account) and a publicly-hosted HTTPS receiver page, both of
// which are outside this repo. The Default Media Receiver (CC1AD845) needs neither, plays HLS with
// sidecar WebVTT, and is what ships here. If a receiver app id ever exists, the only change is the
// setOptions call below plus reading the reported profile in place of castProfileFor().
//
// WHAT THE SDK RUNS ON. cast.framework exists in desktop Chrome/Edge and Chrome on Android. It does
// NOT exist in any iOS browser (Apple doesn't allow it) or in Firefox/Safari. Everything here is
// written to end in `supported: false` rather than throwing on those, so the cast button simply never
// appears. iOS users' route to a TV is AirPlay, which is a different (unbuilt) button.
//
// THE OTHER HALF OF "IT WORKS" IS NOT IN THIS FILE: the receiver fetches segments from the stream
// gateway itself, from its own origin, so StreamGateway's CORS allow-list has to include the cast
// receiver origin. Without that the load fails with a bare, unhelpful error on the TV. See
// GatewayCorsOrigins in MovieTheater.StreamGateway/Program.cs.

const SDK_URL = "https://www.gstatic.com/cv/js/sender/v1/cast_sender.js?loadCastFramework=1";
// How long the hook waits for the SDK before REPORTING "still loading" as the reason there is no
// button. It is a reporting deadline, not a give-up: the loader below never times out on its own,
// so a framework that arrives late on a slow cellular link still produces the button. (It used to
// be a hard cut-off, which silently lost the button for any viewer whose two SDK scripts took
// longer than this to fetch.) Where the SDK can never call back — a Chromium build without the
// media router — the promise simply stays pending, and nothing but this hook awaits it.
const SDK_TIMEOUT_MS = 6_000;

let sdkPromise = null;

/**
 * Load cast_sender.js once per page and resolve `{ ok, reason }`.
 *
 * `ok` true means the framework is usable. When it isn't, `reason` says WHY, in the vocabulary the
 * settings menu turns into a sentence (see tvStatusLine in playerMenuModel):
 *   - "unsupported-browser" — no window.chrome: Firefox, Safari, every iOS browser
 *   - "insecure-context"    — plain http://; the SDK refuses to initialize there
 *   - "sdk-blocked"         — the script tag was refused (CSP) or failed to load (ad blocker,
 *                             filtering DNS, offline)
 *   - "sdk-unavailable"     — the script ran but said no, or left no framework behind (a Chromium
 *                             fork with casting disabled)
 *
 * The callback name is fixed by Google and must exist BEFORE the script executes, so it is assigned
 * first. Resolving ok:false (rather than rejecting) is deliberate: "this browser can't cast" is an
 * ordinary answer, not an error, and every caller treats it as one.
 */
export function loadCastFramework() {
  if (sdkPromise) return sdkPromise;
  sdkPromise = new Promise((resolve) => {
    const no = (reason) => resolve({ ok: false, reason });
    if (typeof window === "undefined" || typeof document === "undefined") return no("unsupported-browser");
    if (window.cast?.framework && window.chrome?.cast) return resolve({ ok: true, reason: null });
    // Preconditions the SDK itself has, checked before paying for it. The framework is a Chromium
    // feature (window.chrome is defined there and nowhere else) and it requires a secure context.
    // Skipping the fetch on Firefox, Safari and every iOS browser isn't just tidiness: without this
    // they each load ~100 kB of script that can never work, and the menu would blame a slow network
    // for a browser that has no Cast at all. It also keeps the test DOM — which refuses script tags
    // outright — from logging a DOMException on every suite run.
    if (!window.chrome) return no("unsupported-browser");
    if (window.isSecureContext === false) return no("insecure-context");

    let settled = false;
    const finish = (value) => {
      if (settled) return;
      settled = true;
      resolve(value);
    };

    const previous = window.__onGCastApiAvailable;
    window.__onGCastApiAvailable = (isAvailable, reason) => {
      // Chain rather than clobber: another script on the page (or a previous mount in a hot reload)
      // may own this hook too, and stealing it silently would break whoever set it first.
      try { previous?.(isAvailable, reason); } catch { /* not ours to fix */ }
      const usable = !!isAvailable && !!window.cast?.framework;
      finish(usable ? { ok: true, reason: null } : { ok: false, reason: "sdk-unavailable" });
    };

    // Injecting the tag can THROW rather than fire onerror — a CSP that doesn't list gstatic, and
    // the test DOM, both refuse it synchronously. Unhandled, that rejects this promise and every
    // caller's .then never runs, so "no cast support" would present as a silently half-initialized
    // player instead of a missing button.
    try {
      const script = document.createElement("script");
      script.src = SDK_URL;
      script.async = true;
      script.onerror = () => finish({ ok: false, reason: "sdk-blocked" }); // offline, an extension, a filtering DNS
      document.head.appendChild(script);
    } catch {
      finish({ ok: false, reason: "sdk-blocked" });
    }
  });
  return sdkPromise;
}

/** Reset the module-level SDK singleton. Tests only — nothing in the app re-loads the framework. */
export function resetCastFrameworkForTests() {
  sdkPromise = null;
}

// The four states the context reports, normalized to our own strings so nothing outside this file
// has to reach into the SDK's enums.
function normalizeCastState(castState) {
  const S = window.cast?.framework?.CastState;
  if (!S) return "unavailable";
  if (castState === S.CONNECTED) return "connected";
  if (castState === S.CONNECTING) return "connecting";
  if (castState === S.NOT_CONNECTED) return "idle";
  return "no-devices";
}

/**
 * Whatever the SDK is willing to say about the connected receiver.
 *
 * modelName is NOT a documented field of chrome.cast.Receiver — the documented surface is
 * label/friendlyName/capabilities/volume/receiverType. In practice the mDNS "md" record rides along
 * under one of a few names depending on SDK version, so we look for it and shrug when it's missing;
 * castProfileFor() is written to fall back to the safe profile on a null model.
 *
 * `videoCapable` is documented and real, and it matters: casting a film to a Chromecast Audio or a
 * Google Home speaker group would "succeed" and play sound over a black living room.
 */
function describeDevice(session) {
  const device = session?.getCastDevice?.();
  if (!device) return null;
  const Capability = window.chrome?.cast?.Capability;
  const capabilities = device.capabilities || [];
  return {
    friendlyName: device.friendlyName || "the TV",
    modelName: device.modelName || device.model || null,
    videoCapable: !Capability || !capabilities.length || capabilities.includes(Capability.VIDEO_OUT),
  };
}

/** The receiver's live media status object, or null when it holds nothing. */
function mediaStatusOf(context) {
  try {
    return context?.getCurrentSession?.()?.getMediaSession?.() ?? null;
  } catch {
    return null;
  }
}

/** Why the receiver went idle, when it has gone idle at all. Null while media is playing. */
function idleReasonOf(context) {
  return mediaStatusOf(context)?.idleReason ?? null;
}

// The standard Cast media control channel. SET_PLAYBACK_RATE rides it directly because the sender
// framework's RemotePlayerController has no rate control of its own — play/pause/seek/volume/mute is
// the whole surface it wraps.
const MEDIA_NAMESPACE = "urn:x-cast:com.google.cast.media";
let mediaRequestId = 1;

/**
 * The cast sender, as one hook.
 *
 * Returns a stable-ish object describing whether casting is possible, what it's connected to, the
 * mirrored remote-player state, and the transport. The page above owns the STREAMING session (start,
 * restart, progress, stop) exactly as it does for local playback — this hook only owns the SDK.
 *
 * `onSessionEnded` fires when the cast session goes away for any reason, including ones this tab
 * didn't cause: the viewer stopping the cast from the TV, the Google Home app taking the device, or
 * the receiver going idle. The page uses it to fall back to local playback at the remote position,
 * which is the only reason the position is handed back with it.
 */
export function useCastSender({ onSessionEnded } = {}) {
  const [supported, setSupported] = useState(false);
  // Why `supported` is false, once known: a loader reason (above), or "sdk-timeout" while the SDK
  // is still on its way. Null while probing, and null again once the framework is up. This is
  // what lets the settings menu answer "why is there no cast button?" instead of the viewer
  // guessing between "my browser can't" and "it didn't find my TV".
  const [reason, setReason] = useState(null);
  const [state, setState] = useState("unavailable"); // unavailable | no-devices | idle | connecting | connected
  const [device, setDevice] = useState(null);
  const [error, setError] = useState(null);
  // The mirrored RemotePlayer. Held as state (the UI renders it every tick) AND as a ref (the
  // teardown path needs the last position after React has stopped re-rendering this tree).
  const [remote, setRemote] = useState({
    currentTime: 0, duration: 0, paused: false, buffering: false,
    volume: 1, muted: false, mediaLoaded: false, finished: false, playbackRate: 1,
  });
  const remoteRef = useRef(remote);
  remoteRef.current = remote;

  const playerRef = useRef(null);
  const controllerRef = useRef(null);
  // The last state the SDK reported, readable from inside the (once-registered) listener. `state`
  // itself is captured stale there, and the end-of-session decision depends on the PREVIOUS value.
  const stateRef = useRef("unavailable");
  // Late-bound so the SDK listeners registered once at mount always call the CURRENT callback rather
  // than the one that existed when the effect ran.
  const endedRef = useRef(onSessionEnded);
  endedRef.current = onSessionEnded;

  useEffect(() => {
    let cancelled = false;
    let context = null;
    let onCastState = null;

    const slow = setTimeout(() => { if (!cancelled) setReason("sdk-timeout"); }, SDK_TIMEOUT_MS);
    loadCastFramework().then((result) => {
      clearTimeout(slow);
      if (cancelled) return;
      if (!result.ok) {
        setReason(result.reason);
        return;
      }
      setReason(null);
      const cast = window.cast.framework;
      const chromeCast = window.chrome.cast;
      try {
        context = cast.CastContext.getInstance();
        context.setOptions({
          receiverApplicationId: chromeCast.media.DEFAULT_MEDIA_RECEIVER_APP_ID,
          // ORIGIN_SCOPED: rejoin a session this site started, never one another site did. The
          // alternative (TAB_AND_ORIGIN_SCOPED) drops the session on a tab change, which would kill
          // a cast the moment the viewer opened a new tab.
          autoJoinPolicy: chromeCast.AutoJoinPolicy.ORIGIN_SCOPED,
          // Reconnect to a cast this origin already owns after a reload, instead of stranding it.
          resumeSavedSession: true,
        });
      } catch (err) {
        setError(err?.message || "Cast could not start.");
        return;
      }

      setSupported(true);

      const player = new cast.RemotePlayer();
      const controller = new cast.RemotePlayerController(player);
      playerRef.current = player;
      controllerRef.current = controller;

      // ANY_CHANGE rather than a dozen field-specific listeners: the fields move together (a seek
      // changes currentTime and playerState; a load changes six at once) and one snapshot per change
      // keeps the mirrored state internally consistent. Mirroring into plain numbers here means
      // nothing downstream ever touches an SDK object.
      const syncPlayer = () => {
        const PlayerState = window.chrome?.cast?.media?.PlayerState;
        setRemote({
          currentTime: player.currentTime || 0,
          duration: player.duration || 0,
          paused: !!player.isPaused,
          buffering: !!PlayerState && player.playerState === PlayerState.BUFFERING,
          volume: player.volumeLevel ?? 1,
          muted: !!player.isMuted,
          mediaLoaded: !!player.isMediaLoaded,
          // The film reaching its end. The mirrored RemotePlayer has no "ended" field — it just goes
          // un-loaded — and un-loaded is equally what a stop, an error, or another app taking the
          // device looks like. idleReason is the receiver's own word for WHY, so it's the only
          // reading that won't roll the credits on a cast that actually failed.
          finished: idleReasonOf(context) === window.chrome?.cast?.media?.IdleReason?.FINISHED,
          // The receiver's OWN reported rate, never the one we asked for. SET_PLAYBACK_RATE is a
          // fire-and-forget message on the media namespace (below) and a receiver is free to ignore
          // it; rendering the request back would put a tick beside "1.5×" on a film playing at
          // normal speed. Reading the status means the menu can only ever show what is true.
          playbackRate: mediaStatusOf(context)?.playbackRate ?? 1,
        });
      };
      controller.addEventListener(cast.RemotePlayerEventType.ANY_CHANGE, syncPlayer);

      onCastState = (event) => {
        const next = normalizeCastState(event.castState);
        const previous = stateRef.current;
        stateRef.current = next;
        setState(next);
        if (next === "connected") {
          setError(null);
          setDevice(describeDevice(context.getCurrentSession()));
          return;
        }
        setDevice(null);
        // ONLY a transition out of a live cast is a session end — the previous state has to be
        // checked, not just the new one. CAST_STATE_CHANGED also fires for ordinary discovery
        // churn: no-devices → idle the moment a Chromecast wakes up on the network, and back again
        // when it sleeps. Firing the end callback on those would restart the local stream (and
        // re-negotiate the whole session) every time a dongle blinked, for a viewer who never
        // touched the cast button.
        if (previous === "connected") endedRef.current?.(remoteRef.current.currentTime);
      };
      context.addEventListener(cast.CastContextEventType.CAST_STATE_CHANGED, onCastState);

      // Seed from the current state — the event only fires on CHANGES, and a resumed session (or a
      // device that was already discovered) is present before we ever subscribe.
      const initial = normalizeCastState(context.getCastState());
      stateRef.current = initial;
      setState(initial);
      if (initial === "connected") setDevice(describeDevice(context.getCurrentSession()));
      syncPlayer();
    });

    return () => {
      cancelled = true;
      clearTimeout(slow);
      // Detach the listeners but NEVER end the session here: this effect tears down on any unmount of
      // the page, and a re-render that killed the viewer's cast would be indefensible. Ending is an
      // explicit act (disconnect(), or the page's own teardown).
      try {
        if (context && onCastState) {
          context.removeEventListener(
            window.cast.framework.CastContextEventType.CAST_STATE_CHANGED, onCastState
          );
        }
      } catch { /* SDK already gone */ }
    };
  }, []);

  const connect = useCallback(async () => {
    const context = window.cast?.framework?.CastContext?.getInstance?.();
    if (!context) return false;
    setError(null);
    try {
      await context.requestSession();
      return true;
    } catch (err) {
      // "cancel" is the viewer closing the device picker — an ordinary outcome, not a failure worth
      // showing them. Everything else (no receiver, receiver_unavailable, timeout) is worth naming.
      const code = typeof err === "string" ? err : err?.code || err?.message;
      if (code && String(code) !== "cancel") setError(`Couldn't connect to the TV (${code}).`);
      return false;
    }
  }, []);

  const disconnect = useCallback(() => {
    try {
      window.cast?.framework?.CastContext?.getInstance?.()?.endCurrentSession?.(true);
    } catch { /* nothing left to end */ }
  }, []);

  /**
   * Hand the receiver a stream to play.
   *
   * `startTime` is absolute content seconds — the same clock the local player and every progress
   * report use. Jellyfin's HLS playlist spans the whole title regardless of where its ffmpeg was
   * pre-positioned, so the receiver's currentTime lines up with ours without a translation.
   */
  const loadMedia = useCallback(
    async ({ url, isHls = true, startTime = 0, title, subtitle, poster, tracks = [], activeTrackId = null, durationSeconds = 0 }) => {
      const session = window.cast?.framework?.CastContext?.getInstance?.()?.getCurrentSession?.();
      const media = window.chrome?.cast?.media;
      if (!session || !media) throw new Error("No cast session.");

      const info = new media.MediaInfo(url, isHls ? "application/x-mpegURL" : "video/mp4");
      info.streamType = media.StreamType.BUFFERED;
      if (durationSeconds > 0) info.duration = durationSeconds;

      const metadata = new media.MovieMediaMetadata();
      metadata.title = title || "";
      if (subtitle) metadata.subtitle = subtitle;
      if (poster) {
        // The Chromecast fetches artwork itself, from its own network position — a relative path
        // means nothing to it. Absolutize against this origin and let it fail silently if the route
        // isn't reachable; a missing backdrop is cosmetic, a failed load must not sink the cast.
        try { metadata.images = [new window.chrome.cast.Image(new URL(poster, window.location.origin).href)]; }
        catch { /* unparseable poster url — skip the artwork */ }
      }
      info.metadata = metadata;

      info.tracks = tracks.map((t) => {
        const track = new media.Track(t.trackId, media.TrackType.TEXT);
        track.trackContentId = new URL(t.url, window.location.origin).href;
        track.trackContentType = "text/vtt";
        track.subtype = media.TextTrackType.SUBTITLES;
        track.name = t.name;
        track.language = t.language;
        return track;
      });

      const request = new media.LoadRequest(info);
      request.currentTime = startTime > 0 ? startTime : 0;
      request.autoplay = true;
      request.activeTrackIds = activeTrackId != null ? [activeTrackId] : [];
      await session.loadMedia(request);
    },
    []
  );

  /**
   * Change which subtitle track the receiver is showing, without reloading the media.
   *
   * editTracksInfo is callback-shaped (it predates the promise API), so it's wrapped. A failure is
   * swallowed: the only consequence is that the subtitle didn't change, and the menu will still show
   * the viewer's pick — which is worth a follow-up if it ever gets reported, not a thrown error mid-film.
   */
  const setActiveTextTrack = useCallback((trackId) => {
    const session = window.cast?.framework?.CastContext?.getInstance?.()?.getCurrentSession?.();
    const mediaSession = session?.getMediaSession?.();
    const media = window.chrome?.cast?.media;
    if (!mediaSession || !media) return;
    try {
      const request = new media.EditTracksInfoRequest(trackId != null ? [trackId] : []);
      mediaSession.editTracksInfo(request, () => {}, () => {});
    } catch { /* receiver rejected the edit — selection stays as the viewer left it */ }
  }, []);

  // ── transport ──────────────────────────────────────────────────────────────
  // Each guards on the controller existing: they are reachable from the UI in the moment between a
  // session ending and the page re-rendering without the cast plate.
  const playPause = useCallback(() => { controllerRef.current?.playOrPause?.(); }, []);
  const seek = useCallback((seconds) => {
    const player = playerRef.current;
    if (!player || !controllerRef.current) return;
    player.currentTime = Math.max(0, seconds);
    controllerRef.current.seek();
  }, []);
  const setVolume = useCallback((level) => {
    const player = playerRef.current;
    if (!player || !controllerRef.current) return;
    player.volumeLevel = Math.min(Math.max(level, 0), 1);
    controllerRef.current.setVolumeLevel();
  }, []);
  const toggleMuted = useCallback(() => { controllerRef.current?.muteOrUnmute?.(); }, []);

  /**
   * Ask the receiver to play at `rate`.
   *
   * Best-effort by construction: the message goes out, the receiver answers with a media status, and
   * the mirrored `remote.playbackRate` reports whatever it actually did. Nothing here waits on or
   * asserts success — a receiver that doesn't honour rates simply keeps reporting 1, and the menu
   * shows 1. The TV player's drift corrector depends on that read-back to know whether nudging is
   * working, and falls back to seeking when it isn't.
   */
  const setPlaybackRate = useCallback((rate) => {
    const session = window.cast?.framework?.CastContext?.getInstance?.()?.getCurrentSession?.();
    const mediaSession = session?.getMediaSession?.();
    if (!session || !mediaSession) return;
    try {
      const message = {
        type: "SET_PLAYBACK_RATE",
        requestId: mediaRequestId++,
        mediaSessionId: mediaSession.mediaSessionId,
        playbackRate: rate,
      };
      // sendMessage resolves/rejects; a rejection means the receiver refused the message, which is
      // the same outcome as ignoring it and needs no separate handling.
      session.sendMessage(MEDIA_NAMESPACE, message)?.catch?.(() => {});
    } catch { /* session went away between the guard and the send */ }
  }, []);

  return useMemo(
    () => ({
      supported, reason, state, device, error, remote,
      connected: state === "connected",
      connect, disconnect, loadMedia, setActiveTextTrack,
      playPause, seek, setVolume, toggleMuted, setPlaybackRate,
    }),
    [supported, reason, state, device, error, remote, connect, disconnect, loadMedia, setActiveTextTrack,
      playPause, seek, setVolume, toggleMuted, setPlaybackRate]
  );
}
