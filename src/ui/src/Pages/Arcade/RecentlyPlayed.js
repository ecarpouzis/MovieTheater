import GameCover from "./GameCover";
import { systemLabel } from "./arcadeSystems";

// The strip's art box. Portrait-ish and a little smaller than the grid card's (140×180), so a row of
// recents reads as a compact shelf above the catalog rather than a second grid.
const ART_H = 132;
const ART_W = 112;
const ART_STYLE = { height: ART_H };

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

/**
 * One recently-played tile. Like GameCard it is a pure display tile: clicking anywhere opens the game
 * modal, which is where you pick the version, start the room (Continue vs New game) and manage saves.
 * It used to carry its own Continue + My saves buttons, which duplicated the modal, made the tile a
 * two-target widget, and skipped the version/cheat/renderer choices the modal offers.
 *
 * `playedVersionId` is the ROM row this player's save belongs to — it rides into the modal as the
 * pre-selected version so Start resumes the save the strip is advertising.
 */
function RecentCard({ row, onOpen }) {
  const game = row.game;
  const open = () => onOpen(game, row.playedVersionId);
  return (
    <div
      className="arcade-recent__card"
      role="button"
      tabIndex={0}
      onClick={open}
      onKeyDown={(e) => { if (e.key === "Enter" || e.key === " ") { e.preventDefault(); open(); } }}
    >
      <div className="arcade-recent__art" style={ART_STYLE}>
        <GameCover game={game} height={ART_H} maxWidth={ART_W} />
      </div>
      <div className="arcade-recent__title" title={game.title}>{game.title}</div>
      <div className="arcade-recent__meta">
        <span className="arcade-chip arcade-chip--system">{systemLabel(game.system)}</span>
        <span className="arcade-recent__when">{timeAgo(row.lastPlayedUtc)}</span>
      </div>
    </div>
  );
}

/** "Recently played" strip (arcade-saves-plan follow-on): the signed-in player's own play activity,
 * derived server-side from save recency. Rendered only when there IS history — a brand-new player
 * sees no empty strip, same convention as LiveRooms.
 *
 * Rows are { game, lastPlayedUtc, saveCount, playedVersionId }, where `game` is the same full card
 * payload the grid gets — that's what lets a tile open the standard game modal. */
function RecentlyPlayed({ rows, onOpen }) {
  // Drop rows without a card before rendering. This strip is one section of the lobby, but it renders
  // INSIDE the page component, so an exception here white-screens the whole arcade — grid and all —
  // not just the shelf. `key={r.game.key}` on a row whose `game` is missing throws exactly that
  // ("Cannot read properties of undefined (reading 'key')"), so a single malformed row costs the user
  // the entire page. A missing card is never worth more than a missing tile.
  const safe = (rows || []).filter((r) => r?.game?.key);
  if (safe.length === 0) return null;
  return (
    <section className="arcade-section">
      <div className="arcade-section__head">
        <h2 className="arcade-section__title">Recently played</h2>
        <span className="arcade-section__count">{safe.length} title{safe.length === 1 ? "" : "s"}</span>
      </div>
      <div className="arcade-recent">
        {safe.map((r) => (
          <RecentCard key={r.game.key} row={r} onOpen={onOpen} />
        ))}
      </div>
    </section>
  );
}

export default RecentlyPlayed;
