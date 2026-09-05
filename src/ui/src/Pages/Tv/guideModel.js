import { detectStreamCapabilities } from "../../streamCapabilities";

/**
 * The pure half of the guide's v2 detail panel: the preview's stream shape, the phone cut-off, the
 * `?restart=1` hand-off contract with the TV room, and the one-line "meta" that sits under a title
 * in both the grid cells and the panel (2002 · PG · 1h 35m / S03 E09 · Fishing Cubans · TV-PG).
 * Kept free of React and of the network so every rule here is a unit test away. (Named guideModel
 * rather than guidePreview: the component is GuidePreview.js and Windows treats the two names as
 * one file.)
 */

// The preview asks for the ladder's bottom rung (playerMenuModel.QUALITY_LADDER "480-15"). Below the
// source bitrate this defeats direct play AND stream copy on the server, and ≤ 2 Mbps also caps the
// re-encode at 854 px wide (StreamController.MaxWidthForCeiling) — a cheap picture, not a copy of a
// 40 Mbps remux into a 300 px box.
export const PREVIEW_BPS = 1_500_000;

// A plain SDR H.264/AAC stereo encode is what every browser decodes without ceremony; the probe's
// HEVC/AV1/HDR/DV/MKV yeses would only buy a tone-mapping-free copy the preview does not want.
export function previewCapabilities() {
  return {
    ...detectStreamCapabilities(),
    supportsHevc: false,
    supportsHevcMain10: false,
    supportsAv1: false,
    supportsAv110bit: false,
    supportsHdr: false,
    supportsDolbyVision: false,
    supportsMkv: false,
    maxAudioChannels: 2,
  };
}

// Video preview is desktop/tablet only; phones get the poster (data, and one ffmpeg per tap).
export const PREVIEW_MIN_WIDTH = 768;
export function previewEnabledFor(width) {
  return Number.isFinite(width) && width >= PREVIEW_MIN_WIDTH;
}

// Fast clicking across cells must not spawn one ffmpeg per click.
export const PREVIEW_DEBOUNCE_MS = 500;

// ── the `/tv/<id>?restart=1` hand-off ────────────────────────────────────────────────────────────
// "Start over" from the guide tunes the channel AND casts the room's existing Restart vote in the
// same tune. The room reads the intent once, at first render, then strips it from the URL.
export function restartHref(channelId) {
  return `/tv/${channelId}?restart=1`;
}
export function restartIntent(search) {
  try {
    return new URLSearchParams(search || "").get("restart") === "1";
  } catch {
    return false;
  }
}

// ── the meta line ────────────────────────────────────────────────────────────────────────────────
function runtimeLabel(startUtc, endUtc) {
  const ms = Date.parse(endUtc) - Date.parse(startUtc);
  if (!Number.isFinite(ms) || ms <= 0) return null;
  const mins = Math.round(ms / 60_000);
  if (mins < 60) return `${mins} min`;
  const h = Math.floor(mins / 60);
  const m = mins % 60;
  return m ? `${h}h ${m}m` : `${h}h`;
}

function episodeCode(prog) {
  if (prog.season == null && prog.episode == null) return null;
  const s = prog.season != null ? `S${String(prog.season).padStart(2, "0")}` : null;
  const e = prog.episode != null ? `E${String(prog.episode).padStart(2, "0")}` : null;
  return [s, e].filter(Boolean).join(" ");
}

/**
 * The items of a programme's meta line, in display order, typed so the panel can draw the
 * certificate as a boxed tag and the IMDb score in its yellow: `{ kind: "text" | "tag" | "imdb",
 * text }`. `{ full }` adds the IMDb score and genre (the detail panel); the grid cell keeps the
 * short form. Episodes lead with S/E and the episode's own title, movies with the year; both end
 * with the certificate and the slot length.
 */
export function programMetaItems(prog, { full = false } = {}) {
  if (!prog) return [];
  const items = [];
  const code = episodeCode(prog);
  if (code) {
    items.push({ kind: "text", text: code });
    if (prog.episodeTitle) items.push({ kind: "text", text: prog.episodeTitle });
  } else if (prog.year) {
    items.push({ kind: "text", text: String(prog.year) });
  }
  if (prog.rating) items.push({ kind: "tag", text: prog.rating });
  const rt = runtimeLabel(prog.startUtc, prog.endUtc);
  if (rt) items.push({ kind: "text", text: rt });
  if (full) {
    if (prog.imdbRating != null && Number(prog.imdbRating) > 0) items.push({ kind: "imdb", text: Number(prog.imdbRating).toFixed(1) });
    if (prog.genre) items.push({ kind: "text", text: prog.genre, part: "genre" });
  }
  return items;
}

// The same line as plain strings (the grid cell, tooltips, tests).
export function programMetaParts(prog, opts) {
  return programMetaItems(prog, opts).map((i) => (i.kind === "imdb" ? `IMDb ${i.text}` : i.text));
}

export function programMeta(prog, opts) {
  return programMetaParts(prog, opts).join(" · ");
}

/**
 * The cell's headline. An episode reads as its SERIES (the meta line carries S/E + the episode
 * title, as on a real guide); everything else is the title as-is. `title` stays the server's
 * composite so search and the in-room list keep matching on it.
 */
export function programHeadline(prog) {
  if (!prog) return "";
  if (prog.seriesTitle && (prog.season != null || prog.episode != null)) return prog.seriesTitle;
  return prog.title || "";
}

// "9:20 – 11:00 PM · 40 min left" for the panel's progress strip.
export function minutesLeft(prog, nowMs) {
  const end = Date.parse(prog?.endUtc);
  if (!Number.isFinite(end)) return null;
  return Math.max(0, Math.ceil((end - nowMs) / 60_000));
}

/**
 * The guide's row filter, shared by the grid (which draws the rows) and the page (which auto-selects
 * the first VISIBLE row's programme). A row survives when it is a favourite (while the pill is on)
 * AND matches the search — on the channel's own name and category AND on every programme title in the
 * fetched window, so typing a film's name finds the channel showing it. `favoriteIds` null = no
 * favourites filter; an empty Set = "my favourites, of which there are none".
 */
export function rowMatches(ch, items, needle, favoriteIds) {
  if (favoriteIds && !favoriteIds.has(Number(ch.id))) return false;
  if (!needle) return true;
  if ((ch.name || "").toLowerCase().includes(needle)) return true;
  if ((ch.category || "").toLowerCase().includes(needle)) return true;
  return (items || []).some((p) => (p.title || "").toLowerCase().includes(needle));
}
