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

  // hvc1 (Safari/most) and hev1 (some MSE builds) both denote HEVC; either is enough.
  const supportsHevc =
    canPlay('video/mp4; codecs="hvc1.1.6.L93.B0"') ||
    canPlay('video/mp4; codecs="hev1.1.6.L93.B0"');
  const supportsAv1 = canPlay('video/mp4; codecs="av01.0.05M.08"');
  // fMP4/CMAF support tracks plain H.264-in-mp4 support: hls.js plays fMP4 wherever
  // it plays H.264 MSE, and Safari has native fMP4 HLS since Safari 10.
  const supportsFmp4 = canPlay('video/mp4; codecs="avc1.42E01E"');
  // MP3 audio over MSE (hls.js path): Firefox's MSE has no MP3 decoder, so a server that
  // copies MP3 into the HLS leaves playback frozen at 0:00. mp4a.40.34 is MP3-in-mp4; when
  // this is false the server transcodes audio to AAC instead. (Safari uses native HLS and
  // plays MP3 regardless; unknown/old → false → AAC, the safe baseline.)
  const supportsMp3 = canPlay('audio/mp4; codecs="mp4a.40.34"');
  // Dolby Digital (AC-3) and Dolby Digital Plus (E-AC-3): if MSE/the <video> element decodes these,
  // the server can copy a surround track losslessly (or transcode a DTS source to it) instead of
  // downmixing to stereo. Chrome/Edge desktop decode both; the browser then emits multichannel LPCM.
  const supportsAc3 = canPlay('audio/mp4; codecs="ac-3"');
  const supportsEac3 = canPlay('audio/mp4; codecs="ec-3"');
  const maxAudioChannels = detectMaxAudioChannels();
  const supportsHdr = detectHdr();

  cached = { supportsHevc, supportsAv1, supportsHdr, supportsFmp4, supportsMp3, supportsAc3, supportsEac3, maxAudioChannels };
  return cached;
}
