// Shared time/duration formatting. These existed as nine near-identical copies
// across the verticals (music player ×5, watch player, movie modal, watch page,
// photo video) before being consolidated here — import these instead of writing
// a local one.

// Clock position: "m:ss", growing to "h:mm:ss" past an hour. Unknown/negative
// reads as a parked clock ("0:00"), never blank — players render this every frame.
export function formatClock(totalSeconds) {
  if (!Number.isFinite(totalSeconds) || totalSeconds < 0) totalSeconds = 0;
  const s = Math.floor(totalSeconds % 60);
  const m = Math.floor((totalSeconds / 60) % 60);
  const h = Math.floor(totalSeconds / 3600);
  const mm = h > 0 ? String(m).padStart(2, "0") : String(m);
  const ss = String(s).padStart(2, "0");
  return h > 0 ? `${h}:${mm}:${ss}` : `${mm}:${ss}`;
}

// Duration label for a track/clip listing: same shape as formatClock, but an
// unknown or zero length renders as nothing (null) rather than a fake "0:00".
export function formatDuration(totalSeconds) {
  if (!Number.isFinite(totalSeconds) || totalSeconds <= 0) return null;
  return formatClock(totalSeconds);
}

// Whole minutes as "2h 16m" / "47m", matching IMDB's normalized runtime.
export function formatRuntime(minutes) {
  if (!minutes || minutes <= 0) return null;
  const h = Math.floor(minutes / 60);
  const m = minutes % 60;
  return h > 0 ? `${h}h${m ? " " + m + "m" : ""}` : `${m}m`;
}
