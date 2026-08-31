import { shareOf, popularityTitle } from "./musicPopularity";

/**
 * How widely heard a song is: the score as a NUMBER, and the drop from the loudest song beside it as
 * a BAR.
 *
 * Both channels are needed and they carry different things (the reasoning lives in
 * musicPopularity.js). The number is the absolute 0–100 score, so two songs can be compared exactly
 * and against every other popularity on the site. The bar is this song's share of the biggest
 * audience in the SAME list, drawn from raw listener counts — because the score is logarithmic and
 * a bar drawn from it would show a 39× collapse as a couple of pixels.
 *
 * Renders NOTHING when the score is missing, which is the honest state on a shelf the enrich pass
 * has not reached: an empty column says nothing, a zero-length bar would claim nobody has heard it.
 */
function MusicPopularityMeter({ track, peak }) {
  if (typeof track?.popularity !== "number") return null;
  const score = Math.max(0, Math.min(100, track.popularity));
  const share = shareOf(track, peak);
  return (
    <span className="music-song-pop" title={popularityTitle(track, peak)}>
      {/* Inline width is the DATUM (per-row and continuous); everything else is in the stylesheet,
          so themes and the phone breakpoint keep control of the look. A floor of 2% keeps a song
          that really is 1/1000th of the hit beside it from vanishing into the trough. */}
      <span className="music-song-pop-bar" role="img" aria-label={`${Math.round(share * 100)}% of the most-heard song here`}>
        <span className="music-song-pop-fill" style={{ width: `${Math.max(2, share * 100)}%` }} />
      </span>
      <span className="music-song-pop-score">{score}</span>
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
  popularity,      // { popularity, listeners } for this row, or null/undefined → no meter
  popularityPeak,  // the loudest row in THIS list (peakOf), so the bar shows a real drop
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
        <MusicPopularityMeter track={popularity} peak={popularityPeak} />
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
