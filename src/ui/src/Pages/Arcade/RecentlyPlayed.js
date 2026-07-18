import GameCover from "./GameCover";
import { systemLabel } from "./arcadeSystems";

// Coarse relative time — a lobby strip only needs "roughly how long ago", not a precise duration.
function timeAgo(iso) {
  const ms = Date.now() - new Date(iso).getTime();
  const min = Math.round(ms / 60000);
  if (min < 1) return "just now";
  if (min < 60) return `${min}m ago`;
  const hr = Math.round(min / 60);
  if (hr < 24) return `${hr}h ago`;
  const day = Math.round(hr / 24);
  if (day < 30) return `${day}d ago`;
  return new Date(iso).toLocaleDateString();
}

function RecentCard({ game, onPlay, onManageSaves, creating }) {
  const stop = (e) => e.stopPropagation();
  return (
    <div className="arcade-recent__card" onClick={() => onPlay(game.gameId, game.title)}>
      <GameCover game={game} artId={game.gameId} height={120} maxWidth={120} className="arcade-recent__art" />
      <div className="arcade-recent__title" title={game.title}>{game.title}</div>
      <div className="arcade-recent__meta">
        <span className="arcade-chip arcade-chip--system">{systemLabel(game.system)}</span>
        <span className="arcade-recent__when">{timeAgo(game.lastPlayedUtc)}</span>
      </div>
      <div className="arcade-recent__actions">
        <button
          type="button"
          className="arcade-btn arcade-btn--join"
          disabled={creating === game.gameId}
          onClick={(e) => { stop(e); onPlay(game.gameId, game.title); }}
        >
          {creating === game.gameId ? "Starting…" : "▶ Continue"}
        </button>
        <button type="button" className="arcade-link" onClick={(e) => { stop(e); onManageSaves(game.gameId, game.title); }}>
          My saves
        </button>
      </div>
    </div>
  );
}

/** "Recently played" strip (arcade-saves-plan follow-on): the signed-in player's own play activity,
 * derived server-side from save recency. Rendered only when there IS history — a brand-new player
 * sees no empty strip, same convention as LiveRooms. */
function RecentlyPlayed({ games, onPlay, onManageSaves, creating }) {
  if (!games || games.length === 0) return null;
  return (
    <section className="arcade-section">
      <div className="arcade-section__head">
        <h2 className="arcade-section__title">Recently played</h2>
        <span className="arcade-section__count">{games.length} title{games.length === 1 ? "" : "s"}</span>
      </div>
      <div className="arcade-recent">
        {games.map((g) => (
          <RecentCard key={g.gameId} game={g} onPlay={onPlay} onManageSaves={onManageSaves} creating={creating} />
        ))}
      </div>
    </section>
  );
}

export default RecentlyPlayed;
