import { useEffect, useState } from "react";
import { Button, Select } from "antd";
import GameCover from "./GameCover";
import { systemLabel } from "./arcadeSystems";

/**
 * One card per game (docs/arcade-dedupe-multidisc-plan.md): box art at its natural aspect on the
 * left, every textual detail in a column to its right — never crammed underneath. Layout follows the
 * design README's "Game card (art left, details right)".
 *
 * A game's several ROMs (region / revision / edition / disc / hack) collapse into one card; the
 * version picker below decides which one actually launches.
 */
function GameCard({ game, onStart, onManageSaves, creating }) {
  // Genres arrive comma- OR semicolon-joined ("Action; Adventure", "Shooter, Tactical, Adventure"),
  // so split on both — otherwise the chip prints two genres.
  const genre = game.genres ? game.genres.split(/[;,]/)[0].trim() : null;
  const [sel, setSel] = useState(game.versions?.[0]?.id);
  // When the filters change the default version (e.g. you filtered to a region), reset the selection.
  useEffect(() => { setSel(game.versions?.[0]?.id); }, [game.versions?.[0]?.id]);

  const version = game.versions?.find((v) => v.id === sel) || game.versions?.[0];
  const multiVersion = game.versionCount > 1;
  const region = version?.region && version.region !== "Unknown" ? version.region : null;
  const stop = (e) => e.stopPropagation();

  return (
    <div className="arcade-card" onClick={() => onStart(sel, game.title)}>
      <div className="arcade-card__art">
        <GameCover game={game} height={118} />
        {game.rating != null && (
          <span className="arcade-card__rating" title={game.ratingCount ? `${game.ratingCount.toLocaleString()} votes` : undefined}>
            ★ {game.rating}
          </span>
        )}
      </div>

      <div className="arcade-card__body">
        <div className="arcade-card__title" title={game.title}>{game.title}</div>

        {/* Two FIXED tag lines, so every card has the same structure whatever the chip lengths:
            line 1 = system + players, line 2 = region (or the version picker) + genre. */}
        <div className="arcade-tags">
          <span className="arcade-chip arcade-chip--system">{systemLabel(game.system)}</span>
          <span className="arcade-chip">{game.maxPlayers}P</span>
        </div>
        <div className="arcade-tags">
          {multiVersion ? (
            // The version picker takes the region chip's slot — region IS what distinguishes most
            // versions — so the card keeps its two-line tag structure instead of growing a third row.
            // It flexes into the free space; the genre chip beside it shrinks first.
            <span className="arcade-chip arcade-chip--select" onClick={stop} title={version?.label}>
              <Select
                size="small"
                bordered={false}
                value={sel}
                onChange={setSel}
                getPopupContainer={(t) => t.parentElement}
                popupClassName="arcade-version-dropdown"
                dropdownMatchSelectWidth={false}
                options={game.versions.map((v) => ({ value: v.id, label: v.label }))}
              />
            </span>
          ) : (
            region && <span className="arcade-chip">{region}</span>
          )}
          {genre && <span className="arcade-chip arcade-chip--genre" title={genre}>{genre}</span>}
        </div>

        <div className="arcade-card__summary">{game.summary}</div>

        <div className="arcade-card__actions">
          <Button
            type="primary"
            className="arcade-btn-start"
            loading={creating === sel}
            onClick={(e) => { stop(e); onStart(sel, game.title); }}
          >
            ▶ Start room
          </Button>
          <button
            type="button"
            className="arcade-link"
            onClick={(e) => { stop(e); onManageSaves?.(sel); }}
          >
            My saves
          </button>
        </div>
      </div>
    </div>
  );
}

export default GameCard;
