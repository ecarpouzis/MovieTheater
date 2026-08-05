// One row in a song list (search results, loose tracks, an album's tracklist).
// The play affordance is the row itself; the "＋" that opens the playlist picker (music-plan.md
// Phase 3) is a SIBLING button, not nested inside it — a button inside a button is invalid markup
// and swallows the inner click in some browsers.
export default function MusicSongRow({
  no,          // track number / "▶" glyph shown at the left
  title,
  meta,        // secondary line-end text (artist — album), optional
  disc,        // CD marker for multi-disc albums, optional
  time,        // formatted duration, optional
  disabled,
  hint,        // title= tooltip for the row
  onPlay,
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
      {onAdd && (
        <button className="music-add-btn" onClick={onAdd} title="Add to playlist" aria-label="Add to playlist">
          ＋
        </button>
      )}
    </div>
  );
}
