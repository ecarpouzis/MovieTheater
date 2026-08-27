import GameCover from "./GameCover";
import { systemLabel } from "./arcadeSystems";
import { ratingTooltip } from "./arcadeRating";

// The art column: a fixed BOX, identical on every card.
//
// 1. EVERY CARD IS THE SAME HEIGHT. COVER_H (180) is deliberately taller than the details column can
//    ever be (title + tags + summary — all clamped or fixed), so the ART sets the card's height on
//    every card and the grid comes out flat. Nothing here is a percentage of the card.
// 2. THE ART IS NEVER CLIPPED. The cover renders at its true aspect, at whatever size fits inside the
//    box (coverBox), and CENTERS in it — never cropped, never letterboxed.
// 3. THE DETAILS COLUMN ALWAYS HAS THE SAME ROOM, because ART_W is fixed regardless of cover shape.
export const COVER_H = 180;
const ART_W = 140;
/** ART_W as a share of COVER_H — the cover-size tweak scales BOTH so the box keeps its shape. */
const ART_RATIO = ART_W / COVER_H;
const ART_STYLE = { flex: `0 0 ${ART_W}px`, width: ART_W, height: COVER_H };

/**
 * One card per game (docs/arcade-dedupe-multidisc-plan.md): box art at its natural aspect on the
 * left, and to its right a column of title / tags / summary closing on a year · studio foot line — the
 * foot and the 4-line summary are what now fill the room the launch controls used to take up, so the
 * details column runs the full height of the art. The card is a pure display tile — clicking
 * it anywhere opens the full-page game modal (GameModal), which is where you pick the ROM version,
 * toggle cheats, choose a controller scheme, start the room, and manage saves. Those launch controls
 * used to be crammed into the card footer; they moved to the modal so the card can stay light.
 *
 * A game's several ROMs (region / revision / edition / disc / hack) collapse into one card; which one
 * launches (and which cheats are on offer) is decided in the modal.
 */
function GameCard({ game, onOpen, cellH, metadata, hoverClass, eager }) {
  // The tweak contract (R9 S3): the card's height IS the art box, so the cover-size tweak scales the
  // whole card. The art column stays a FIXED box at any scale — that is what keeps every card in a
  // grid row the same height and the details column the same width (see the three laws above).
  const coverH = cellH || COVER_H;
  const artW = Math.round(coverH * ART_RATIO);
  const artStyle = coverH === COVER_H ? ART_STYLE : { flex: `0 0 ${artW}px`, width: artW, height: coverH };
  const rich = metadata !== "minimal";
  // Heavy-lane titles (docs/arcade-heavy-lane-plan.md §7.1) stream via Moonlight; the lobby routes
  // their card click to HeavyGameModal instead of the standard game modal (onOpen decides).
  const heavy = game.lane === "heavy";
  // Genres arrive comma- OR semicolon-joined ("Action; Adventure"), so split on both.
  const genre = game.genres ? game.genres.split(/[;,]/)[0].trim() : null;
  const version = game.versions?.[0];
  const region = version?.region && version.region !== "Unknown" ? version.region : null;
  // The bottom line: year + studio, the two facts a box art shopper actually scans for. It sits in the
  // space the launch controls used to occupy, so the details column reaches the bottom of the art
  // instead of leaving ~90px of empty card under the summary.
  const credit = [game.year, game.developer || game.publisher].filter(Boolean).join(" · ");

  return (
    <div className={`arcade-card bx-card${hoverClass ? ` ${hoverClass}` : ""}`} onClick={() => onOpen?.(game)} role="button" tabIndex={0}
      onKeyDown={(e) => { if (e.key === "Enter" || e.key === " ") { e.preventDefault(); onOpen?.(game); } }}>
      {/* The score pins to the CARD's top-right corner, clear of the art. */}
      {game.rating != null && (
        <span className="arcade-card__rating" title={ratingTooltip(game)}>
          ★ {game.rating}
        </span>
      )}

      <div className="arcade-card__art" style={artStyle}>
        {/* `bx-cover` goes on the COVER itself, not on this fixed box: the cover is drawn at its true
            aspect in exact pixels (never cropped, never letterboxed — see coverBox), and the package's
            Rounded / Hover / Dim rules all select `.bx-cover`. */}
        <GameCover game={game} height={coverH} maxWidth={artW} className="bx-cover" eager={eager} />
      </div>

      <div className="arcade-card__body">
        {/* Two lines, reserved whether or not the title needs them, so every card in a grid row lines up. */}
        <div className="arcade-card__title" title={game.title}>{game.title}</div>

        {rich && (
        <div className="arcade-tags">
          <span className="arcade-chip arcade-chip--system">{systemLabel(game.system)}</span>
          <span className="arcade-chip">{game.maxPlayers}P</span>
          {heavy && <span className="arcade-chip arcade-chip--genre" title="Streams to your device via Moonlight — couch play, not in-browser">Moonlight</span>}
          {region && <span className="arcade-chip">{region}</span>}
          {genre && <span className="arcade-chip arcade-chip--genre" title={genre}>{genre}</span>}
          {game.raAchievements && <span className="arcade-chip arcade-chip--ra" title="Tracks RetroAchievements">🏆</span>}
          {game.raHighScores && <span className="arcade-chip arcade-chip--ra" title="Has a high-score leaderboard">🥇</span>}
          {game.raSpeedruns && <span className="arcade-chip arcade-chip--ra" title="Has a speedrun (time) leaderboard">⏱️</span>}
        </div>
        )}

        {rich && <div className="arcade-card__summary">{game.summary}</div>}

        {/* Pinned to the bottom of the details column so it lines up across a grid row. Both halves are
            optional; the row itself always renders so the columns stay flat. */}
        {rich && (
        <div className="arcade-card__foot">
          <span className="arcade-card__credit" title={credit || undefined}>{credit}</span>
          {game.versionCount > 1 && (
            <span className="arcade-card__versions" title="Pick the region / revision / disc in the game window">
              {game.versionCount} versions
            </span>
          )}
        </div>
        )}
      </div>
    </div>
  );
}

/**
 * A card-shaped placeholder: same shape and the same 140x180 art column as a real card, so a row of
 * them holds its height and nothing moves when the real cards land. Used both for a slot whose page
 * is still on the wire and, as a whole grid, for the lobby's first paint — the same skeleton-first
 * convention as the movie and boardgame grids.
 */
export function PendingGameCard() {
  return (
    <div className="arcade-card arcade-card--pending" aria-hidden="true">
      <div className="arcade-card__art arcade-pending__art skeleton-block" />
      <div className="arcade-card__body">
        <div className="arcade-pending__line arcade-pending__line--title skeleton-block" />
        <div className="arcade-pending__line arcade-pending__line--tag skeleton-block" />
        <div className="arcade-pending__line skeleton-block" />
        <div className="arcade-pending__line skeleton-block" />
        <div className="arcade-pending__line arcade-pending__line--short skeleton-block" />
      </div>
    </div>
  );
}

export default GameCard;
