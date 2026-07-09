import { useEffect, useState } from "react";
import { Button, Select } from "antd";
import GameCover from "./GameCover";
import CheatPicker from "./CheatPicker";
import { systemLabel } from "./arcadeSystems";

/**
 * One card per game (docs/arcade-dedupe-multidisc-plan.md): box art at its natural aspect on the
 * left, every textual detail in a column to its right — never crammed underneath. Layout follows the
 * design README's "Game card (art left, details right)".
 *
 * A game's several ROMs (region / revision / edition / disc / hack) collapse into one card; the version
 * picker in the footer decides which one launches, and — because cheats are per-ROM — which cheats are on
 * offer. Controls (version, cheats) sit on their own row directly above the actions (start, saves), so a
 * card's height doesn't depend on how many of them a given game happens to have.
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

  // Cheats are per-ROM, so switching version resets the selection to that ROM's defaults (the PS2
  // widescreen patch, where the emulator has one). Keyed off the version id, never merged across
  // versions: a code id from one dump is meaningless on another.
  const [cheats, setCheats] = useState(version?.defaultCheats || []);
  useEffect(() => { setCheats(version?.defaultCheats || []); }, [version?.id]); // eslint-disable-line react-hooks/exhaustive-deps

  const start = () => onStart(sel, game.title, cheats);

  return (
    <div className="arcade-card" onClick={start}>
      <div className="arcade-card__art">
        <GameCover game={game} height={118} />
        {game.rating != null && (
          <span className="arcade-card__rating" title={game.ratingCount ? `${game.ratingCount.toLocaleString()} votes` : undefined}>
            ★ {game.rating}
          </span>
        )}
      </div>

      <div className="arcade-card__body">
        {/* Two lines, reserved whether or not the title needs them, so every card in a grid row lines up.
            One line with an ellipsis used to truncate most of the 007 titles to "007 - The World Is N…". */}
        <div className="arcade-card__title" title={game.title}>{game.title}</div>

        <div className="arcade-tags">
          <span className="arcade-chip arcade-chip--system">{systemLabel(game.system)}</span>
          <span className="arcade-chip">{game.maxPlayers}P</span>
          {region && <span className="arcade-chip">{region}</span>}
          {genre && <span className="arcade-chip arcade-chip--genre" title={genre}>{genre}</span>}
        </div>

        <div className="arcade-card__summary">{game.summary}</div>

        <div className="arcade-card__footer">
          {/* Row 1 — what you're about to launch. Both are dropdowns and both are optional, so the row
              collapses to nothing on a single-version game with no cheats. */}
          {(multiVersion || version?.cheatCount > 0) && (
            <div className="arcade-card__controls">
              {multiVersion && (
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
              )}
              <CheatPicker version={version} value={cheats} onChange={setCheats} disabled={creating === sel} />
            </div>
          )}

          {/* Row 2 — the actions. Always present, always in the same place. */}
          <div className="arcade-card__actions">
            <Button
              type="primary"
              className="arcade-btn-start"
              loading={creating === sel}
              onClick={(e) => { stop(e); start(); }}
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
    </div>
  );
}

export default GameCard;
