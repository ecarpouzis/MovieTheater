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
