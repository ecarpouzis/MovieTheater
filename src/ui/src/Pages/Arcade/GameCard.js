import { useEffect, useState } from "react";
import { Button, Dropdown, Select } from "antd";
import GameCover from "./GameCover";
import CheatPicker from "./CheatPicker";
import { systemLabel } from "./arcadeSystems";

// The art column: a fixed BOX, identical on every card. Three rules fall out of it, in this order.
//
// 1. EVERY CARD IS THE SAME HEIGHT. COVER_H (180) is deliberately taller than the tallest the details
//    column can ever be (~165px: a 2-line title 36 + tags 18 + a 2-line summary 32 + the footer's
//    controls + actions rows 58 + gaps 21 — every one of those is clamped or fixed, so 165 is a
//    ceiling, not a typical value). So the ART sets the card's height, on every card, and the grid
//    comes out flat. Nothing here is a percentage of the card: the art must never take its size from
//    the thing whose size it is deciding.
//
// 2. THE ART IS NEVER CLIPPED. The cover renders at its true aspect, at whatever size fits inside the
//    box (coverBox), and CENTERS in it. A portrait cover reaches the full 180 height; a landscape one
//    is limited by ART_W and comes out shorter. The leftover is plain card background — the art is
//    never cropped to fill the box, and never letterboxed inside a frame.
//
// 3. THE DETAILS COLUMN ALWAYS HAS THE SAME ROOM. Because ART_W is fixed, the details column's width
//    doesn't depend on the shape of the box art — which is what went wrong before: a landscape cover
//    is nearly twice as wide as a portrait one at the same height, so the column it left behind
//    swung wildly from card to card. At the narrowest card the grid can make (a 355px track) the
//    details column is 175px, enough for the actions row (Start room + My saves ≈ 165px).
const COVER_H = 180;
const ART_W = 140;
// The column that box lives in. Hoisted — a fresh object literal per render is a new prop identity.
const ART_STYLE = { flex: `0 0 ${ART_W}px`, width: ART_W, height: COVER_H };

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
function GameCard({ game, onStart, onManageSaves, onHeavy, creating }) {
  // Heavy-lane titles (docs/arcade-heavy-lane-plan.md §7.1) stream via Moonlight instead of playing
  // in the browser: the card's one action opens the Prepare/Play modal, and the room-centric extras
  // (cheats, browser saves) don't apply.
  const heavy = game.lane === "heavy";
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

  // Play-button dropdown (arcade Vulkan/GL launch picker): the default action forces Vulkan on
  // systems that have the choice at all (game.supportsHwToggle, from CloudRetroHost.HwToggleSystems);
  // everything else launches with no override, same as before this feature existed.
  const defaultHwContext = !heavy && game.supportsHwToggle ? "vulkan" : "";
  const start = (hwContext = defaultHwContext) =>
    (heavy ? onHeavy?.(game) : onStart(sel, game.title, cheats, hwContext));

  // Is one of this card's popups (version, cheats) open? Both render INSIDE their chip, so they're
  // part of the card's box — and the card is a stacking context of its own (position: relative for the
  // rating chip, plus a transform on hover). A z-index on the chip therefore can't lift the popup out
  // of the card, so the cards that come after it in the grid paint straight over the open list: you
  // see a sliver of it and nothing else. The CARD is what has to rise. (Rendering the popup into
  // <body> instead would dodge this, but then it doesn't travel with the card when the grid scrolls.)
  const [menuOpen, setMenuOpen] = useState(false);

  return (
    <div className={`arcade-card${menuOpen ? " arcade-card--menu-open" : ""}`} onClick={start}>
      {/* The score pins to the CARD's top-right corner, not the art's — over the art it sat on top of
          the box, which is the one part of the card anyone is looking at. */}
      {game.rating != null && (
        <span className="arcade-card__rating" title={game.ratingCount ? `${game.ratingCount.toLocaleString()} votes` : undefined}>
          ★ {game.rating}
        </span>
      )}

      <div className="arcade-card__art" style={ART_STYLE}>
        <GameCover game={game} height={COVER_H} maxWidth={ART_W} />
      </div>

      <div className="arcade-card__body">
        {/* Two lines, reserved whether or not the title needs them, so every card in a grid row lines up.
            One line with an ellipsis used to truncate most of the 007 titles to "007 - The World Is N…". */}
        <div className="arcade-card__title" title={game.title}>{game.title}</div>

        <div className="arcade-tags">
          <span className="arcade-chip arcade-chip--system">{systemLabel(game.system)}</span>
          <span className="arcade-chip">{game.maxPlayers}P</span>
          {heavy && <span className="arcade-chip arcade-chip--genre" title="Streams to your device via Moonlight — couch play, not in-browser">Moonlight</span>}
          {region && <span className="arcade-chip">{region}</span>}
          {genre && <span className="arcade-chip arcade-chip--genre" title={genre}>{genre}</span>}
        </div>

        <div className="arcade-card__summary">{game.summary}</div>

        <div className="arcade-card__footer">
          {/* Row 1 — what you're about to launch. Both are dropdowns and both are optional, so the row
              collapses to nothing on a single-version game with no cheats. Heavy titles have neither
              (cheats and versions are room-lane concepts). */}
          {!heavy && (multiVersion || version?.cheatCount > 0) && (
            <div className="arcade-card__controls">
              {multiVersion && (
                <span className="arcade-chip arcade-chip--select" onClick={stop} title={version?.label}>
                  <Select
                    size="small"
                    bordered={false}
                    value={sel}
                    onChange={setSel}
                    onDropdownVisibleChange={setMenuOpen}
                    getPopupContainer={(t) => t.parentElement}
                    popupClassName="arcade-version-dropdown"
                    dropdownMatchSelectWidth={false}
                    options={game.versions.map((v) => ({ value: v.id, label: v.label }))}
                  />
                </span>
              )}
              <CheatPicker version={version} value={cheats} onChange={setCheats} disabled={creating === sel} onOpenChange={setMenuOpen} />
            </div>
          )}

          {/* Row 2 — the actions. Always present, always in the same place. */}
          <div className="arcade-card__actions">
            {!heavy && game.supportsHwToggle ? (
              // Play-button dropdown: default click forces Vulkan; the arrow offers "Force GL" for
              // the rare title that runs better/only on the legacy GL path. Wrapped in a stop-on-click
              // span (same pattern as the version Select above) so neither the primary button nor the
              // arrow's popup interaction bubbles up to the card's own onClick.
              <span onClick={stop}>
                <Dropdown.Button
                  type="primary"
                  className="arcade-btn-start"
                  loading={creating === sel}
                  onClick={() => start("vulkan")}
                  menu={{ items: [{ key: "gl", label: "Force GL" }], onClick: () => start("gl") }}
                >
                  ▶ Start room
                </Dropdown.Button>
              </span>
            ) : (
              <Button
                type="primary"
                className="arcade-btn-start"
                loading={creating === sel}
                onClick={(e) => { stop(e); start(); }}
              >
                {heavy ? "🎮 Play via Moonlight" : "▶ Start room"}
              </Button>
            )}
            {/* Heavy titles get dirzip vault rows too (H4) — export/import/delete all apply;
                SavesManager hides Resume for them (nothing to resume in a browser). */}
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
