import { cloneElement, useEffect, useState } from "react";
import { Button, Checkbox, Dropdown, Modal, Select, Tooltip } from "antd";
import useMediaQuery from "../../hooks/useMediaQuery";
import GameCover from "./GameCover";
import CheatPicker from "./CheatPicker";
import ArcadeGameConfig from "./ArcadeGameConfig";
import ArcadeLeaderboards from "./ArcadeLeaderboards";
import ArcadeAchievements from "./ArcadeAchievements";
import { systemLabel } from "./arcadeSystems";
import { ratingTooltip } from "./arcadeRating";
import "./ArcadeModal.css";
import "./GameModal.css";

/**
 * The full-page game modal (mirrors the movie modal): a card opens this instead of launching inline.
 * The card is now a pure display tile — everything you DO with a game lives here: pick the ROM
 * version, toggle cheats, choose a Wii controller scheme, start the room, and manage your saves.
 *
 * It's a full-screen sheet on every platform, not a floating card, laid out as hero → scrolling
 * details → pinned action bar. That last part is load-bearing rather than decorative: as a card it
 * was an auto-height box parked 100px down the page, so on a TV browser (short viewport, no wheel,
 * no scrollbar to grab) a game with a long summary pushed ▶ Start room off the bottom of the screen
 * with no way to reach it. Start now lives in a real modal footer that the shared shell holds
 * against the bottom edge — see ArcadeModal.css.
 *
 * The launch state (selected version, cheats, controller scheme) used to live on the card; it moved
 * here wholesale. Cheats are per-ROM, so switching the version resets the cheat selection to that
 * ROM's defaults — a code id from one dump is meaningless on another.
 *
 * Heavy-lane titles never reach this modal — the lobby routes them straight to HeavyGameModal (their
 * launch is a Moonlight/capture flow, not a room with cheats/versions/schemes).
 */
export default function GameModal({ game, onClose, onStart, onManageSaves, creating, canEditMovies, renderers = [], initialVersionId = null }) {
  const genre = game.genres ? game.genres.split(/[;,]/)[0].trim() : null;
  // `initialVersionId` is the "Recently played" strip's hand-off: saves live on the ROM row, so a tile
  // opened from there must land on the version whose save it advertised, not on the card's default.
  // Only honoured if that row is actually one of this card's versions (a disc-2 save collapses into the
  // disc-1 anchor entry, so it won't be), otherwise the default stands.
  const defaultVersionId = game.versions?.some((v) => v.id === initialVersionId)
    ? initialVersionId
    : game.versions?.[0]?.id;
  const [sel, setSel] = useState(defaultVersionId);
  const [configOpen, setConfigOpen] = useState(false);
  const [boardsOpen, setBoardsOpen] = useState(false);
  const [achOpen, setAchOpen] = useState(false);
  // Filters can change the default version out from under an open modal (rare), so track it.
  useEffect(() => { setSel(defaultVersionId); }, [defaultVersionId]);

  const version = game.versions?.find((v) => v.id === sel) || game.versions?.[0];
  const multiVersion = game.versionCount > 1;
  // Leaderboard entries only ever populate from an RA leaderboard submission (see ArcadeLeaderboards.js) —
  // a game with no RA score/time board can never get one, competitive room or not.
  const hasLeaderboards = !!(game.raHighScores || game.raSpeedruns);
  const region = version?.region && version.region !== "Unknown" ? version.region : null;

  // Cheats are per-ROM codes now (the quality/emulator OPTIONS moved to ⚙ Configure), and a code from
  // one dump is meaningless on another — so the selection clears whenever the version changes. Nothing is
  // pre-selected: PS2 widescreen & friends are per-game config, applied server-side at Start.
  const [cheats, setCheats] = useState([]);
  useEffect(() => { setCheats([]); }, [version?.id]); // eslint-disable-line react-hooks/exhaustive-deps

  // Renderer/core: the primary Start button sends nothing (renderProfile="" + hwContext="") so the
  // server applies this game's configured profile (⚙ Configure) or the system default. The dropdown
  // enumerates every core-and-renderer COMBINATION this system offers (ArcadeRendererProfiles, fetched
  // by the lobby) and launches the picked one by profile id — that's how an alternate CORE (n64
  // parallel_n64, ps1 pcsx_rearmed) is selectable, not just a bare GL/Vulkan surface.
  // Wii controller-scheme picker (GameCube vs Wiimote+Nunchuk): offered on every Wii title. The
  // server hands each game its default (defaultControllerScheme: "gc" for the GC-native BrawlEx
  // mods, "wiimote" for every other Wii game) so an untouched Start launches on the right scheme.
  const [ctrlScheme, setCtrlScheme] = useState(game.defaultControllerScheme || "wiimote");

  // Competitive room: no save-state loading, no cheats, RA hardcore (for linked players). Off by default.
  const [competitive, setCompetitive] = useState(false);

  const busy = creating === sel;
  // renderProfile = a specific ArcadeRendererProfiles id (may swap the core); hwContext = the legacy
  // bare gl/vulkan fallback (only used if the profile list hasn't loaded). "" for both = server default.
  // A competitive room takes no cheats (the server drops them too) — send an empty list so the UI matches.
  const start = (renderProfile = "", hwContext = "") =>
    onStart(sel, game.title, competitive ? [] : cheats, hwContext,
      game.supportsControllerScheme ? ctrlScheme : "", renderProfile, competitive);

  // Launch menu: one entry per core-and-renderer combination for this system, the default marked. Falls
  // back to a bare Force GL/Vulkan pair only if the profile map hasn't arrived yet.
  const rendererItems = renderers.length > 0
    ? renderers.map((p) => ({
        key: `p:${p.id}`,
        label: p.isDefault ? `${p.label} — default` : p.label,
        onClick: () => start(p.id),
      }))
    : [
        { key: "vulkan", label: "Force Vulkan", onClick: () => start("", "vulkan") },
        { key: "gl", label: "Start GL Core", onClick: () => start("", "gl") },
      ];

  const hasControls = multiVersion || version?.cheatCount > 0 || game.supportsControllerScheme;

  // The hero art has to be sized in PIXELS — GameCover's box is exact by design (see coverBox: a
  // percentage height would make the art size the layout instead of the other way round) — so the
  // breakpoints that scale it are read here rather than expressed in GameModal.css. Roomy screens
  // (a desktop monitor, a TV) get a genuinely big cover; short ones (phone landscape) get out of
  // the way so the controls and Start still fit above the fold.
  const roomy = useMediaQuery("(min-width: 1100px) and (min-height: 820px)");
  const shortViewport = useMediaQuery("(max-height: 620px)");
  const narrow = useMediaQuery("(max-width: 700px)");
  // maxWidth matters as much as height: a 4:3 cartridge box is twice as wide as a 3:4 jewel case at
  // the same height, so it's the width cap — not the height — that actually binds for a Genesis or
  // SNES cover. Capping it at the old 230px is what made box art on a 1080p TV look like a thumbnail.
  const artHeight = shortViewport ? 190 : narrow ? 250 : roomy ? 440 : 330;
  const artMaxWidth = shortViewport ? 200 : narrow ? 240 : roomy ? 460 : 330;

  return (
    <Modal
      open
      onCancel={onClose}
      width={720}
      // Above the nav bar (z-index 1300) so the modal and its close button render over it.
      zIndex={1500}
      // `arcade-modal` is the shared shell (viewport-bounded, body scrolls); `arcade-game-modal`
      // takes it the rest of the way to a full-screen sheet at every size. See ArcadeModal.css.
      wrapClassName="arcade-modal arcade-game-modal"
      // Start lives in a REAL modal footer, not at the end of the body. The shell pins the footer
      // and scrolls the body between header and footer, so the primary action is on screen no
      // matter how long the summary runs or how short the viewport is — a TV browser was pushing
      // it off the bottom with nothing to scroll with.
      footer={
        <div className="agm-foot">
          <div className="agm-foot__ctx">
            <span className="agm-foot__sys">{systemLabel(game.system)}</span>
            {version?.label && <span className="agm-foot__ver" title={version.label}>{version.label}</span>}
            {competitive && <span className="agm-foot__flag">🏁 Competitive</span>}
          </div>
          <div className="agm-actions">
            {game.supportsHwToggle ? (
              <Dropdown.Button
                type="primary"
                className="agm-start"
                loading={busy}
                onClick={() => start("")}
                // The game modal is zIndex 1500; antd's dropdown menu defaults lower and would open
                // BEHIND it (the "Force GL does nothing" report). Lift it above the modal.
                overlayStyle={{ zIndex: 1700 }}
                // Portals to body like the version/pill pickers (GameModal.css note), so it never
                // picked up this app's dark theme — it rendered with no themed background, just
                // inherited light body text floating over whatever was behind the modal ("popping
                // under the cards"). Give it a class to theme, same as .arcade-version-dropdown.
                overlayClassName="agm-start-menu"
                menu={{ items: rendererItems }}
                buttonsRender={([left, right]) => [
                  cloneElement(left, { className: [left.props.className, "arcade-btn-start"].filter(Boolean).join(" ") }),
                  cloneElement(right, { className: [right.props.className, "arcade-btn-start", "arcade-btn-start__arrow"].filter(Boolean).join(" ") }),
                ]}
              >
                ▶ Start room
              </Dropdown.Button>
            ) : (
              <Button type="primary" className="arcade-btn-start agm-start" loading={busy} onClick={() => start()}>
                ▶ Start room
              </Button>
            )}
          </div>
        </div>
      }
    >
      <div className="agm-body">
        <div className="agm-art">
          <GameCover game={game} height={artHeight} maxWidth={artMaxWidth} />
        </div>

        <div className="agm-info">
          <h2 className="agm-title">{game.title}</h2>

          <div className="agm-tags">
            <span className="arcade-chip arcade-chip--system">{systemLabel(game.system)}</span>
            <span className="arcade-chip">{game.maxPlayers}P</span>
            {game.year ? <span className="arcade-chip">{game.year}</span> : null}
            {region && <span className="arcade-chip">{region}</span>}
            {genre && <span className="arcade-chip arcade-chip--genre" title={genre}>{genre}</span>}
            {game.rating != null && (
              <span className="arcade-chip agm-chip-rating" title={ratingTooltip(game)}>
                ★ {game.rating}
              </span>
            )}
            {game.raAchievements && <span className="arcade-chip arcade-chip--ra" title="Tracks RetroAchievements">🏆 Achievements</span>}
            {game.raHighScores && <span className="arcade-chip arcade-chip--ra" title="Has a high-score leaderboard">🥇 High scores</span>}
            {game.raSpeedruns && <span className="arcade-chip arcade-chip--ra" title="Has a speedrun (time) leaderboard">⏱️ Speedruns</span>}
          </div>

          {game.summary && <p className="agm-summary">{game.summary}</p>}

          {hasControls && (
            <div className="agm-controls">
              {multiVersion && (
                <label className="agm-field">
                  <span className="agm-field__label">Version</span>
                  <Select
                    className="agm-select"
                    value={sel}
                    onChange={setSel}
                    popupClassName="arcade-version-dropdown"
                    optionLabelProp="label"
                    options={game.versions.map((v) => ({
                      value: v.id,
                      // 🏆 flags a version whose dump RetroAchievements recognizes — the one where
                      // achievements/scores actually fire (these also sort first, see ArcadeVersions.Rank).
                      label: v.raSupported ? (
                        <span>
                          {v.label} <span className="agm-ra-mark" title="RetroAchievements supported on this version">🏆</span>
                        </span>
                      ) : (
                        v.label
                      ),
                    }))}
                  />
                </label>
              )}

              {game.supportsControllerScheme && (
                <label className="agm-field">
                  <span className="agm-field__label">Controller</span>
                  <Select
                    className="agm-select"
                    value={ctrlScheme}
                    onChange={setCtrlScheme}
                    popupClassName="arcade-version-dropdown"
                    options={[
                      { value: "gc", label: "GameCube controller" },
                      { value: "wiimote", label: "Wiimote + Nunchuk" },
                    ]}
                  />
                </label>
              )}

              {version?.cheatCount > 0 && (
                <label className="agm-field">
                  <span className="agm-field__label">Cheats</span>
                  <CheatPicker version={version} value={cheats} onChange={setCheats} disabled={busy || competitive} block />
                </label>
              )}
            </div>
          )}

          <label className="agm-competitive">
            <Checkbox checked={competitive} disabled={busy} onChange={(e) => setCompetitive(e.target.checked)}>
              <Tooltip title={hasLeaderboards
                ? "No save-state loading, no cheats — so leaderboard times and scores are legit. If you've linked RetroAchievements, the room runs in hardcore mode and your unlocks/runs count."
                : "No save-state loading, no cheats." + (game.raAchievements ? " If you've linked RetroAchievements, the room runs in hardcore mode and your unlocks count." : " This game has no RetroAchievements leaderboard, so there's nothing for this room to score — it just plays clean.")}>
                🏁 Competitive room
              </Tooltip>
            </Checkbox>
          </label>

          {/* Utility links stay in the BODY, next to the panels they open — only Start is promoted
              to the pinned footer. They wrap onto a row of their own so they never crowd each
              other on a narrow info column. */}
          <div className="agm-links">
            <button type="button" className="arcade-link" onClick={() => onManageSaves?.(sel, game.title)}>
              💾 My saves
            </button>
            {hasLeaderboards && (
              <button type="button" className="arcade-link" onClick={() => setBoardsOpen((o) => !o)}>
                🏆 Leaderboards
              </button>
            )}
            {game.raAchievements && (
              <button type="button" className="arcade-link" onClick={() => setAchOpen((o) => !o)}>
                🎖️ Achievements
              </button>
            )}
            {canEditMovies && game.configurable && (
              <button type="button" className="arcade-link" onClick={() => setConfigOpen(true)}>
                ⚙ Configure
              </button>
            )}
          </div>

          {boardsOpen && (
            <div className="agm-boards">
              <ArcadeLeaderboards gameId={sel} />
            </div>
          )}

          {achOpen && (
            <div className="agm-boards">
              <ArcadeAchievements gameId={sel} />
            </div>
          )}
        </div>
      </div>

      {configOpen && (
        <ArcadeGameConfig
          game={{ id: sel, title: game.title, system: game.system }}
          onClose={() => setConfigOpen(false)}
        />
      )}
    </Modal>
  );
}
