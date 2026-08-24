// ── Chromecast device profiles ────────────────────────────────────────────────
//
// The load-bearing fact of casting: WHEN CASTING, THE CAST DEVICE IS THE DECODER, NOT THE BROWSER.
// Everything in streamCapabilities.js probes *this browser* (MediaSource.isTypeSupported, a throwaway
// <video>'s canPlayType, the OS audio output, the display's dynamic range) and the server turns that
// into a Jellyfin DeviceProfile. Hand a Chromecast a stream negotiated from a desktop Chrome probe and
// you get the classic cast failure: audio plays, picture is black — Chrome said "I decode HEVC", the
// 3rd-gen Chromecast on the other end does not.
//
// So a cast session sends a profile describing the RECEIVER. The shape below is deliberately identical
// to detectStreamCapabilities()'s return value, because MovieAPI.startStream spreads it straight into
// the /API/Stream/Start body — which means the whole server side (BuildWebDeviceProfile: segment
// container, encode-target codec list, copy candidates, HDR ranges, direct-play containers) already
// reacts correctly with ZERO server changes. Keep the keys in sync with streamCapabilities.js.
//
// Everything here is conservative on purpose. A cast that plays a 720p transcode is a working cast; a
// cast that black-screens because we guessed the receiver decodes Main-10 is a bug report. The viewer
// can always move UP via the profile override in the player menu.

/**
 * Baseline: what EVERY video-capable Cast receiver has been able to do since 2013 —
 * H.264 High up to 1080p in MPEG-TS HLS, with AAC audio.
 *
 * Notable falses and why:
 *  - supportsMkv FALSE is the most important flag in this file. Chromecast cannot play Matroska at
 *    all, and MKV is the dominant container in this library. Left true, the server would hand back a
 *    direct-play URL to the raw .mkv (isHls:false) and the receiver would fail the load outright.
 *  - supportsFmp4 FALSE selects MPEG-TS segments (BuildWebDeviceProfile: segmentContainer). TS +
 *    H.264 + AAC is the single most universally supported combination on Cast hardware. The 4K
 *    profile below flips it, because HEVC has no TS mapping in that builder.
 *  - supportsFlac / supportsMp3 FALSE drop those from the HLS COPY candidate list, so their sources
 *    re-encode to AAC. Both are technically playable on Cast; neither is worth a silent-audio report.
 *  - supportsAc3 / supportsEac3 FALSE by default: on Cast, Dolby is PASS-THROUGH, not decode. It
 *    reaches an AVR over HDMI intact, but a receiver wired straight to a TV with no Dolby decoder
 *    plays SILENCE. Opt in via dolbyPassthrough when there's an AVR in the chain.
 *  - supportsHeAac TRUE — HE-AAC (SBR) is on Google's supported-media list, so an HE-AAC source may
 *    be copied instead of needlessly re-encoded.
 *
 * maxAudioChannels 6: the server clamps this to [6,8] regardless (BuildWebDeviceProfile deliberately
 * refuses to trust a stereo-reading probe), so 6 is simply the honest value. Cast decodes 5.1 AAC.
 */
const BASELINE = {
  supportsHevc: false,
  supportsHevcMain10: false,
  supportsAv1: false,
  supportsAv110bit: false,
  supportsHdr: false,
  supportsDolbyVision: false,
  supportsFmp4: false,
  supportsMp3: false,
  supportsAc3: false,
  supportsEac3: false,
  supportsHeAac: true,
  supportsFlac: false,
  supportsMkv: false,
  maxAudioChannels: 6,
};

/**
 * 4K/HEVC receivers: Chromecast Ultra, Chromecast with Google TV (4K), Google TV Streamer, and the
 * Android TV boxes/TVs that register as Cast receivers (Shield, Bravia, most 2019+ smart TVs).
 *
 * supportsFmp4 flips true because BuildWebDeviceProfile only offers HEVC when fMP4 is on
 * (caps.Hevc && useFmp4) — HEVC-in-MPEG-TS is not a path we or Jellyfin want.
 *
 * Dolby Vision stays FALSE even here. DV support on cast hardware is profile-specific (5 vs 8.1 vs
 * 7) and a receiver that advertises "DV" but can't take the profile in the file renders magenta/green
 * garbage. False means the source tonemaps to HDR10 or SDR, which always looks like the movie.
 *
 * AV1 stays FALSE everywhere. Only the newest Cast hardware decodes it, our pipeline never
 * AV1-ENCODES (it's copy-only), and AV1 sources are rare in this library — so the upside is near zero
 * and the failure mode is a black picture.
 */
const HEVC_4K = {
  ...BASELINE,
  supportsHevc: true,
  supportsHevcMain10: true,
  supportsHdr: true,
  supportsFmp4: true,
};

/**
 * The selectable profiles. `key` is what's persisted per receiver; `ceilingBps` is the bitrate cap a
 * cast session opens at when the viewer is on an Auto quality mode (see castCeilingBps).
 */
export const CAST_PROFILES = {
  baseline: {
    key: "baseline",
    label: "1080p (safe)",
    hint: "H.264 · works on every Chromecast",
    caps: BASELINE,
    ceilingBps: 8_000_000,
  },
  hevc4k: {
    key: "hevc4k",
    label: "4K HDR",
    hint: "HEVC · Ultra / Google TV",
    caps: HEVC_4K,
    ceilingBps: 20_000_000,
  },
};

export const DEFAULT_CAST_PROFILE = "baseline";

// Model strings that identify a receiver able to decode HEVC. The Cast SDK does not DOCUMENT a model
// name on chrome.cast.Receiver (only label / friendlyName / capabilities), but the mDNS "md" field
// usually rides along, so we read it when it's there and fall through to the safe baseline when it
// isn't. friendlyName is deliberately NOT matched: it is whatever the owner typed ("Living Room"),
// so matching it would be guessing from a label a human chose for a different purpose.
const HEVC_MODEL_PATTERNS = [
  /\bultra\b/i,
  /google\s*tv/i,
  /android\s*tv/i,
  /\bshield\b/i,
  /\bbravia\b/i,
  /\bchromecast\s*hd\b/i,
];

/**
 * Which profile to use for a receiver, given whatever the SDK told us about it and any override the
 * viewer has pinned for this device.
 *
 * Order: an explicit override always wins (the viewer can see their own TV; we can't). Otherwise a
 * recognized 4K/HEVC model name upgrades. Anything unrecognized — including every plain "Chromecast",
 * and the common case where the SDK exposes no model at all — stays on the baseline.
 */
export function castProfileFor({ modelName = null, override = null } = {}) {
  if (override && CAST_PROFILES[override]) return CAST_PROFILES[override];
  const model = String(modelName || "");
  if (model && HEVC_MODEL_PATTERNS.some((re) => re.test(model))) return CAST_PROFILES.hevc4k;
  return CAST_PROFILES[DEFAULT_CAST_PROFILE];
}

/**
 * The capability payload for /API/Stream/Start, with the Dolby pass-through opt-in folded in.
 *
 * Enabling pass-through does two things in BuildWebDeviceProfile: ac3/eac3 join the HLS copy-candidate
 * list (so a Dolby track is remuxed rather than re-encoded to AAC) and join the direct-play audio list.
 * That is exactly right when an AVR is doing the decoding and exactly wrong when it isn't, which is why
 * it is a switch and not a probe — nothing the sender can see reveals what is downstream of the HDMI.
 */
export function castCapabilities(profile, { dolbyPassthrough = false } = {}) {
  const caps = { ...(profile?.caps || BASELINE) };
  if (dolbyPassthrough) {
    caps.supportsAc3 = true;
    caps.supportsEac3 = true;
  }
  return caps;
}

/**
 * The bitrate ceiling a cast session should open at.
 *
 * A cast session cannot run the normal ABR ladder: useAdaptiveBitrate drives off hls.js's
 * bandwidthEstimate and its BUFFER_STALLED_ERROR, and while casting there IS no hls.js instance in
 * this tab — the receiver fetches segments over its own wifi link, which we cannot measure from here.
 * Rather than let an Auto session sit on whatever rung the ladder happened to be on when cast started
 * (which could be the DIRECT sentinel — a 23 Mbps remux pushed at a 3rd-gen Chromecast on 2.4 GHz),
 * an Auto mode pins to the profile's ceiling. An explicitly chosen rung is honored but still clamped
 * to the ceiling: "Original" is a promise about a lossless local copy, not about what a Chromecast on
 * house wifi can carry.
 *
 * @param ladderBps the cap the fixed rung carries (null/Infinity = the lossless DIRECT tier)
 * @param isAuto    whether the viewer is on an Auto mode rather than a pinned rung
 */
export function castCeilingBps(profile, ladderBps, isAuto) {
  const ceiling = profile?.ceilingBps || CAST_PROFILES[DEFAULT_CAST_PROFILE].ceilingBps;
  if (isAuto) return ceiling;
  if (ladderBps == null || !isFinite(ladderBps)) return ceiling;
  return Math.min(ladderBps, ceiling);
}

/**
 * The subtitle tracks a Cast receiver can actually show, and the ones it can't.
 *
 * The Default Media Receiver renders sidecar WebVTT and nothing else. Our other two kinds are both
 * CLIENT-rendered in this tab — libpgs paints PGS bitmaps onto a canvas over the <video>, libass
 * typesets ASS — and neither canvas exists on the TV. They are dropped from the cast menu rather than
 * offered and silently ignored, because a subtitle option that does nothing is worse than one that
 * isn't there. Image subs the server BURNS IN (no deliveryUrl) are a different story: they're painted
 * into the video by ffmpeg, so they arrive on the receiver like any other pixels — those stay.
 *
 * Returns { tracks, dropped } — `dropped` drives the "not available while casting" note in the menu,
 * so a viewer whose only English sub is an ASS track learns why it vanished.
 */
export function castSubtitleTracks(subtitleTracks) {
  const all = subtitleTracks || [];
  const castable = all.filter((t) => !t.deliveryUrl || (t.kind !== "image-pgs" && t.kind !== "ass"));
  return { tracks: castable, dropped: all.filter((t) => !castable.includes(t)) };
}

/**
 * Cast media metadata + track descriptors for a load request, as plain data.
 *
 * Kept here (rather than in castSender.js) so it is testable without the Google SDK globals: the
 * sender turns these records into chrome.cast.media.Track objects one-for-one. Track ids are the
 * Jellyfin stream indices, which is what the rest of the player already keys subtitles by — so
 * activeTrackIds needs no translation table.
 */
export function castTrackDescriptors(subtitleTracks) {
  return castSubtitleTracks(subtitleTracks)
    .tracks.filter((t) => !!t.deliveryUrl)
    .map((t) => ({
      trackId: t.index,
      url: t.deliveryUrl,
      name: t.label,
      language: t.language || "en",
    }));
}
