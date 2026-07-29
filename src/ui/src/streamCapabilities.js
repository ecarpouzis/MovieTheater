// Client-side decode capability detection for the streaming control plane
// (streaming-plan.md §14.1). The result is sent to /API/Stream/Start, which builds
// a Jellyfin DeviceProfile from it so HEVC/AV1-capable browsers get the source
// copied or HEVC-encoded instead of always re-encoded to H.264.
//
// Principle from the plan: *detect, never assume*. We only claim a codec when the
// platform affirmatively reports it; anything unknown falls back to the H.264/TS
// baseline server-side, so an old or non-reporting browser still plays.

let cached = null;

// True only if MSE (hls.js path) or a <video> element (Safari native HLS) reports
// it can play the given MIME/codec string.
function canPlay(mime) {
  try {
    if (window.MediaSource && window.MediaSource.isTypeSupported(mime)) return true;
  } catch {
    /* isTypeSupported can throw on malformed codec strings on some engines */
  }
  try {
    return document.createElement("video").canPlayType(mime) === "probably";
  } catch {
    return false;
  }
}

// Output channel capacity (5.1 = 6, 7.1 = 8): how many channels the OS audio device exposes, so we
// only ask the server to *preserve* surround when the browser can actually emit it. The catch — this
// reflects the SYSTEM audio config: a stereo-configured output reports 2 even with a 5.1 receiver
// attached, so the OS must be set to 5.1 for this to read 6. Unknown/blocked → 2 (safe stereo).
function detectMaxAudioChannels() {
  try {
    const Ctx = window.AudioContext || window.webkitAudioContext;
    if (!Ctx) return 2;
    const ctx = new Ctx();
    const max = ctx.destination.maxChannelCount || 2;
    if (ctx.close) ctx.close();
    return Math.max(2, Math.min(max, 8));
  } catch {
    return 2;
  }
}

// Can the <video> element play a Matroska (.mkv) container directly? MKV is the dominant library
// container, so direct-playing it (raw file, no ffmpeg remux) is a big transcode saver. Chromium
// returns "maybe" (not "probably") for matroska, so canPlay()'s strict check misses it — probe
// canPlayType directly and accept any non-"no" answer (mirrors jellyfin-web's testCanPlayMkv).
// Firefox is excluded: it reports support but preloads the entire file (jellyfin-web #15521).
function detectMkv() {
  try {
    if (/firefox/i.test(navigator.userAgent || "")) return false;
    const v = document.createElement("video");
    const ok = (mime) => {
      const r = v.canPlayType(mime);
      return r === "probably" || r === "maybe";
    };
    return (
      ok("video/x-matroska") ||
      ok("video/mkv") ||
      ok('video/x-matroska; codecs="avc1.42E01E"')
    );
  } catch {
    return false;
  }
}

// Display capability, not decode: only pass HDR through to a display that can show
// it, so an SDR screen gets a tonemapped transcode instead of washed-out HDR.
function detectHdr() {
  try {
    return (
      typeof window.matchMedia === "function" &&
      (window.matchMedia("(dynamic-range: high)").matches ||
        window.matchMedia("(video-dynamic-range: high)").matches)
    );
  } catch {
    return false;
  }
}

export function detectStreamCapabilities() {
  if (cached) return cached;

  // hvc1 (Safari/most) and hev1 (some MSE builds) both denote HEVC; either is enough. The base probe is
  // Main profile, 8-bit (".1").
  const supportsHevc =
    canPlay('video/mp4; codecs="hvc1.1.6.L93.B0"') ||
    canPlay('video/mp4; codecs="hev1.1.6.L93.B0"');
  // HEVC Main 10 (10-bit, ".2"): nearly all HEVC decoders handle it (all HDR HEVC is 10-bit), but probe
  // it so the copy path doesn't hand a Main-10 source to a Main-only 8-bit decoder (→ green/garbage).
  const supportsHevcMain10 =
    canPlay('video/mp4; codecs="hvc1.2.4.L153.B0"') ||
    canPlay('video/mp4; codecs="hev1.2.4.L153.B0"');
  const supportsAv1 = canPlay('video/mp4; codecs="av01.0.05M.08"');
  // AV1 10-bit (Main profile, high tier). Base supportsAv1 is 8-bit; this gates HDR/10-bit AV1 copy.
  const supportsAv110bit = canPlay('video/mp4; codecs="av01.0.15M.10"');
  // Dolby Vision decode — effectively Safari only. Copying a DOVI source to a non-DV browser renders
  // broken, so we only advertise DOVI passthrough when the client truly decodes it (else it tonemaps).
  const supportsDolbyVision =
    canPlay('video/mp4; codecs="dvh1.05.06"') ||
    canPlay('video/mp4; codecs="dvh1.08.06"') ||
    canPlay('video/mp4; codecs="dvhe.05.06"') ||
    canPlay('video/mp4; codecs="dvhe.08.06"');
  // fMP4/CMAF support tracks plain H.264-in-mp4 support: hls.js plays fMP4 wherever
  // it plays H.264 MSE, and Safari has native fMP4 HLS since Safari 10.
  const supportsFmp4 = canPlay('video/mp4; codecs="avc1.42E01E"');
  // MP3 audio over MSE (hls.js path): Firefox's MSE has no MP3 decoder, so a server that
  // copies MP3 into the HLS leaves playback frozen at 0:00. mp4a.40.34 is MP3-in-mp4; when
  // this is false the server transcodes audio to AAC instead. (Safari uses native HLS and
  // plays MP3 regardless; unknown/old → false → AAC, the safe baseline.)
  const supportsMp3 = canPlay('audio/mp4; codecs="mp4a.40.34"');
  // HE-AAC (SBR, mp4a.40.5). Near-universal, but when a client can't decode it an HE-AAC source must be
  // transcoded to LC-AAC rather than copied (a copied HE-AAC track would play silent/broken there).
  const supportsHeAac = canPlay('audio/mp4; codecs="mp4a.40.5"');
  // Dolby Digital (AC-3) and Dolby Digital Plus (E-AC-3): if MSE/the <video> element decodes these,
  // the server can copy a surround track losslessly (or transcode a DTS source to it) instead of
  // downmixing to stereo. Chrome/Edge desktop decode both; the browser then emits multichannel LPCM.
  const supportsAc3 = canPlay('audio/mp4; codecs="ac-3"');
  const supportsEac3 = canPlay('audio/mp4; codecs="ec-3"');
  // FLAC: the audio on most Blu-ray remuxes in this library. When the browser decodes it (Chromium
  // and Firefox both do), a FLAC-audio MKV can direct-play — and on the HLS path the track is COPIED
  // losslessly instead of re-encoded to AAC. Without this flag those files always landed in an HLS
  // session just for the audio, which is the population the keyframe force-encode exists for.
  const supportsFlac = canPlay('audio/mp4; codecs="flac"');
  const maxAudioChannels = detectMaxAudioChannels();
  const supportsHdr = detectHdr();
  const supportsMkv = detectMkv();

  cached = {
    supportsHevc, supportsHevcMain10, supportsAv1, supportsAv110bit, supportsHdr, supportsDolbyVision,
    supportsFmp4, supportsMp3, supportsAc3, supportsEac3, supportsHeAac, supportsFlac, maxAudioChannels, supportsMkv,
  };
  return cached;
}
