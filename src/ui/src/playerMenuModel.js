import { detectStreamCapabilities } from "./streamCapabilities";
import { isAutoQuality } from "./streamAbr";

// The players' shared menu VOCABULARY and option model. The Watch player's flat dropdown and the
// TV menu's accordion are deliberately different chrome — but what the options ARE (the quality
// ladder, which tracks exist, which one is on, which subtitles are burned in, what's actually
// being delivered) kept drifting when it lived twice. The lists are built here; each player maps
// them onto its own markup. This file also owns the pure vocabulary that used to live inside
// VideoPlayer.js (which still re-exports it, so old import paths keep working).

// The quality ladder from streaming-plan.md §7. "Auto" (§14.4) adapts the cap to
// measured bandwidth; "Original" omits the cap entirely, letting compatible sources
// direct-stream with no re-encode. The numbered rungs pin a fixed cap.
export const QUALITY_LADDER = [
  { key: "auto", label: "Auto", bps: null, hint: "adapts to your connection" },
  { key: "auto-mobile", label: "Mobile Auto", bps: null, hint: "low data · caps at 1080p" },
  { key: "original", label: "Original", bps: null, hint: "direct stream when possible" },
  { key: "1080-12", label: "1080p", bps: 12_000_000, hint: "12 Mbps" },
  { key: "1080-8", label: "1080p", bps: 8_000_000, hint: "8 Mbps" },
  { key: "720-4", label: "720p", bps: 4_000_000, hint: "4 Mbps" },
  { key: "480-15", label: "480p", bps: 1_500_000, hint: "1.5 Mbps" },
];

// Pretty-print the negotiated output codec for the player readout (§14.1).
export function codecLabel(codec) {
  const map = { hevc: "HEVC", h265: "HEVC", h264: "H.264", avc: "H.264", av1: "AV1", vp9: "VP9" };
  return map[String(codec).toLowerCase()] || String(codec).toUpperCase();
}

// Speaker layout from a channel count, for the "Playing" readout (so 5.1 surround is visible, not
// just assumed). The server now preserves the source channel count up to the client's output, so
// this reflects what's actually delivered.
export function channelLayout(channels) {
  if (!channels) return null;
  if (channels >= 8) return "7.1";
  if (channels === 7) return "6.1";
  if (channels === 6) return "5.1";
  if (channels === 2) return "2.0";
  if (channels === 1) return "Mono";
  return `${channels}ch`;
}

// The delivered layout is capped at what this client can actually emit, so a stereo machine reads
// "2.0" for a 5.1 source (which it gets downmixed) instead of falsely claiming surround.
export function deliveredLayout(channels) {
  if (!channels) return null;
  const max = detectStreamCapabilities().maxAudioChannels || 2;
  return channelLayout(Math.min(channels, max));
}

// The "Playing" readout, shared by the Watch player and the TV/channel menu so both report delivery
// quality identically and truthfully: the active quality, the output codec, and — the part the viewer
// actually cares about — whether the video is the original copied bit-for-bit ("no re-encode") or a
// transcode. `autoLabel` is the live adaptive-cap label (e.g. "Auto · Original" / "Auto · 8 Mbps").
export function formatPlaying({ qualityKey, autoLabel, videoCodec, isHls, isDirectStream, audio }) {
  const rung = QUALITY_LADDER.find((q) => q.key === qualityKey);
  // Lead with the unambiguous live verdict. "Video copied" (isDirectStream) is NOT the whole story: a
  // copied video can still ride an HLS session that re-encodes the audio/container (e.g. an E-AC-3 track
  // the browser can't decode) — which is NOT raw direct play and can behave differently (seek/segmenting).
  // Only a NON-HLS session is a true bit-for-bit direct play; an HLS session that merely copies the video
  // says exactly that instead of falsely claiming "Original". The option's "…when possible" marketing hint
  // stays in the Quality menu, not here, so this never hedges about what's actually being delivered.
  const parts = [
    !isDirectStream ? "Transcoded" : isHls ? "Video copied · HLS transcode" : "Original · no re-encode",
  ];
  if (!isDirectStream) {
    parts.push(
      isAutoQuality(qualityKey)
        ? (autoLabel || "Auto").replace(/^Auto · /, "")
        : [rung?.label, rung?.hint].filter(Boolean).join(" ")
    );
  }
  if (videoCodec) parts.push(codecLabel(videoCodec));
  if (audio) parts.push(audio);
  return parts.join(" · ");
}

// ── The option model ─────────────────────────────────────────────────────────

/** The quality menu: every rung, with `selected` on the active one. */
export function qualityOptions(currentKey) {
  return QUALITY_LADDER.map((q) => ({ ...q, selected: q.key === currentKey }));
}

/** The audio menu. Callers already gate on audioTracks.length > 1 before showing a menu at all. */
export function audioOptions(audioTracks, selectedIndex) {
  return (audioTracks || []).map((t) => ({
    index: t.index,
    label: t.label,
    selected: selectedIndex === t.index,
    track: t,
  }));
}

/**
 * The subtitle menu: a leading "Off" entry (index null — the selection value both players send),
 * then every track, hinting "burned in" for tracks with no side-delivery URL (picking one forces
 * a transcode with the subtitle rendered into the picture).
 */
export function subtitleOptions(subtitleTracks, selectedIndex) {
  return [
    { index: null, label: "Off", hint: null, selected: selectedIndex == null },
    ...(subtitleTracks || []).map((t) => ({
      index: t.index,
      label: t.label,
      hint: t.deliveryUrl ? null : "burned in",
      selected: selectedIndex === t.index,
      track: t,
    })),
  ];
}

/**
 * The delivered audio layout for the "Playing" readout: the selected track's channels, falling
 * back to the first track when nothing is explicitly selected — both players carried this exact
 * lookup inline, fallback included.
 */
export function deliveredAudio(audioTracks, selectedIndex) {
  const tracks = audioTracks || [];
  return deliveredLayout((tracks.find((t) => t.index === selectedIndex) || tracks[0])?.channels);
}
