/**
 * How widely heard a song is, 0–100, drawn as a meter rather than printed as a number.
 *
 * A NUMBER in a tracklist reads as a score — the thing a listener has decided — and this is the
 * opposite of that: it is an audience count (Last.fm listeners, log-scaled server-side), and the
 * album sheet already carries a real 0–100 RATING a few centimetres above it. Two numerals side by
 * side would be compared, so only one of them is a numeral. The meter answers the question the
 * tracklist is actually asked — which of these are the famous ones — at a glance, by height.
 *
 * Renders NOTHING when the value is missing, which is the common state on a shelf the enrich pass
 * has not reached: an empty column is honest, and a zero-length bar would claim nobody has heard it.
 */
function MusicPopularityMeter({ value }) {
  if (typeof value !== "number") return null;
  const pct = Math.max(0, Math.min(100, value));
  return (
    <span
      className="music-song-pop"
      title={`Popularity ${pct} — how widely heard this song is, not how good it is`}
      aria-label={`Popularity ${pct} of 100`}
      role="img"
    >
      {/* Inline width is the DATUM (it is per-row and continuous); everything else is in the
          stylesheet, so themes and the phone breakpoint keep control of the look. */}
      <span className="music-song-pop-fill" style={{ width: `${pct}%` }} />
    </span>
  );
}

// One row in a song list (search results, loose tracks, an album's tracklist).
// The play affordance is the row itself; the trailing buttons — "☰" (append to the running queue)
// and "＋" (open the playlist picker, music-plan.md Phase 3) — are SIBLINGS, not nested inside it.
// A button inside a button is invalid markup and swallows the inner click in some browsers.
//
// Button order mirrors the album modal's action row (Queue, then Playlist), and ☰ is the same glyph
// the play bar uses for the queue, so the two controls read as the same idea at two scales: that
// button opens the queue, this one puts a song on the end of it.
export default function MusicSongRow({
  no,          // track number / "▶" glyph shown at the left
  title,
  meta,        // secondary line-end text (artist — album), optional
  disc,        // CD marker for multi-disc albums, optional
  time,        // formatted duration, optional
  popularity,  // 0–100 "how widely heard", or null/undefined when unknown → no meter
  disabled,
  hint,        // title= tooltip for the row
  onPlay,
  onQueue,     // omitted → no ☰ button
  onAdd,       // omitted → no ＋ button (e.g. when the user can't stream)
}) {
  return (
    <div className="music-song-item">
      <button className="music-song-row" onClick={onPlay} disabled={disabled} title={hint || title}>
        <span className="music-song-no">{no}</span>
        <span className="music-song-title">{title}</span>
        {disc && <span className="music-song-disc">{disc}</span>}
        {meta && <span className="music-song-meta">{meta}</span>}
        <MusicPopularityMeter value={popularity} />
        {time && <span className="music-song-time">{time}</span>}
      </button>
      {onQueue && (
        // Disabled in lockstep with the row: queueing a missing or un-streamable file would be
        // silently dropped by enqueue()'s isPlayable filter, which looks like a broken button.
        <button
          className="music-add-btn music-queue-btn"
          onClick={onQueue}
          disabled={disabled}
          title="Add to queue"
          aria-label="Add to queue"
        >
          ☰
        </button>
      )}
      {onAdd && (
        <button className="music-add-btn" onClick={onAdd} title="Add to playlist" aria-label="Add to playlist">
          ＋
        </button>
      )}
    </div>
  );
}
