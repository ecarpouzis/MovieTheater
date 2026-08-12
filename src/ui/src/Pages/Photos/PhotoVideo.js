import { useEffect, useRef, useState } from "react";
import { createHls } from "../../streamEngine";
import { detectStreamCapabilities } from "../../streamCapabilities";
import { MovieAPI } from "../../MovieAPI";

// Family video playback (docs/photos-plan.md §2.3), in the lightbox, in place.
//
// DELIBERATELY not the Watch page's VideoPlayer. That component is the movie stack: a quality ladder,
// adaptive bitrate, audio-track politics, four subtitle renderers, resume bookkeeping and a
// picture-in-picture/wake-lock/media-session suite — all of it solving problems the movie library has
// and a shelf of home videos does not. What IS reused is the piece that matters: `createHls`, the
// site's hardened hls.js construction (its load config, stall handling and initPTS timeline offset),
// so an HLS session here behaves exactly like one there. A direct-play answer needs no engine at all —
// it is the original file in a <video>, which is what a phone clip almost always resolves to.
//
// Three states, and only ever one of them: not synced (§2.3 — the media server has never indexed this
// file, so there is nothing to play and the button would be a lie), an error the server explained, or
// the video.

export function formatDuration(seconds) {
  if (!seconds || seconds <= 0 || !Number.isFinite(seconds)) return null;
  const whole = Math.round(seconds);
  const h = Math.floor(whole / 3600);
  const m = Math.floor((whole % 3600) / 60);
  const s = whole % 60;
  const pad = (n) => String(n).padStart(2, "0");
  return h > 0 ? `${h}:${pad(m)}:${pad(s)}` : `${m}:${pad(s)}`;
}

/** A stable per-browser id so Jellyfin counts viewers separately (the site-wide DeviceId convention). */
function deviceToken() {
  try {
    let token = window.localStorage.getItem("streamDeviceToken");
    if (!token) {
      token = Math.random().toString(36).slice(2) + Math.random().toString(36).slice(2);
      window.localStorage.setItem("streamDeviceToken", token);
    }
    return token;
  } catch {
    // Private mode / storage disabled: the server falls back to a per-account id.
    return undefined;
  }
}

export default function PhotoVideo({ assetId, poster, durationSec, synced, playbackConfigured }) {
  const videoRef = useRef(null);
  const hlsRef = useRef(null);
  const [start, setStart] = useState(null);
  const [error, setError] = useState(null);
  const [state, setState] = useState("idle");

  useEffect(() => {
    if (!assetId || !synced || !playbackConfigured) return undefined;
    let cancelled = false;
    setState("starting");
    setError(null);
    setStart(null);

    const caps = detectStreamCapabilities() || {};
    MovieAPI.startPhotoVideo(assetId, {
      deviceToken: deviceToken(),
      supportsHevc: !!caps.supportsHevc,
      supportsFmp4: !!caps.supportsFmp4,
      supportsMkv: !!caps.supportsMkv,
      supportsMp3: !!caps.supportsMp3,
      supportsAc3: !!caps.supportsAc3,
      supportsEac3: !!caps.supportsEac3,
      maxAudioChannels: caps.maxAudioChannels || 2,
    })
      .then(async (response) => {
        if (cancelled) return;
        const body = await response.json().catch(() => null);
        if (!response.ok) {
          setError(body?.message || "This video could not be started.");
          setState("error");
          return;
        }
        setStart(body);
        setState("ready");
      })
      .catch(() => {
        if (!cancelled) {
          setError("This video could not be started.");
          setState("error");
        }
      });

    return () => {
      cancelled = true;
    };
  }, [assetId, synced, playbackConfigured]);

  // Attach the stream. A direct-play answer is a plain src; an HLS one goes through the site's own
  // hls.js construction so it inherits the load config and stall handling the movie player earned.
  useEffect(() => {
    const video = videoRef.current;
    if (!video || !start?.url) return undefined;

    if (!start.isHls) {
      video.src = start.url;
      return () => {
        video.removeAttribute("src");
        video.load();
      };
    }

    // Safari plays HLS natively and does it better than MSE does.
    if (video.canPlayType("application/vnd.apple.mpegurl")) {
      video.src = start.url;
      return () => {
        video.removeAttribute("src");
        video.load();
      };
    }

    const hls = createHls({ onFatal: () => setError("The video stream stopped unexpectedly.") });
    if (!hls) {
      setError("This browser cannot play this video.");
      return undefined;
    }
    hlsRef.current = hls;
    hls.loadSource(start.url);
    hls.attachMedia(video);
    return () => {
      try {
        hls.destroy();
      } catch {
        /* a destroyed engine that was already gone is not an error */
      }
      hlsRef.current = null;
    };
  }, [start]);

  if (!synced) {
    // §2.3: the honest state, not a dead button. The file is fine — the media server has simply never
    // been told about it, which is a pipeline step the owner runs.
    return (
      <div className="photo-video-unsynced">
        <span className="photo-video-unsynced-mark">▶</span>
        <p>Not yet synced for playback.</p>
        <p className="photos-note">
          This video is safe on disk and everything else about it works — tagging, albums, dates. It
          just has not been indexed by the media server yet.
          {durationSec ? ` Length ${formatDuration(durationSec)}.` : ""}
        </p>
      </div>
    );
  }

  if (!playbackConfigured) {
    return (
      <div className="photo-video-unsynced">
        <span className="photo-video-unsynced-mark">▶</span>
        <p>Video playback is not configured on this server.</p>
      </div>
    );
  }

  return (
    <div className="photo-video">
      <video
        ref={videoRef}
        className="photo-video-element"
        controls
        playsInline
        preload="metadata"
        poster={poster || undefined}
      />
      {state === "starting" && <p className="photos-note">Starting…</p>}
      {error && <p className="photos-note photo-video-error">{error}</p>}
      {start && (
        <p className="photos-note">
          {start.directPlay ? "Original file · no re-encode" : "Streaming"}
          {durationSec ? ` · ${formatDuration(durationSec)}` : ""}
        </p>
      )}
    </div>
  );
}
