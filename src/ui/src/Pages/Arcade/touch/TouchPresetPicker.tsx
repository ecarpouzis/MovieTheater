/**
 * The layout switcher in the room's ☰ menu: one chip per built-in preset (the suggested one starred),
 * plus "My layout" when the player has an edited one, and a scope toggle — this game only, or every game
 * on the system. Arcade picks default to the game (every cabinet differs); console picks to the system.
 */
import { useEffect, useState } from "react";
import type { TouchPreset } from "./touchPresets";
import { CUSTOM_PICK } from "./touchLayout";
import type { TouchResolved } from "./TouchLayer";

export type PickScope = "game" | "system";

export interface TouchPresetPickerProps {
  presets: TouchPreset[];
  suggestedId: string;
  resolved: TouchResolved | null;
  systemName: string;
  /** False when the room has no game key to pick for (then only "all games" exists). */
  canPickForGame: boolean;
  defaultScope: PickScope;
  onPick: (presetId: string, scope: PickScope) => void;
}

export default function TouchPresetPicker(p: TouchPresetPickerProps) {
  const initialScope: PickScope = !p.canPickForGame ? "system" : p.resolved?.pickScope || p.defaultScope;
  const [scope, setScope] = useState<PickScope>(initialScope);
  // Follow the pick that's actually deciding the layout once it's known (it arrives a render after mount).
  useEffect(() => { if (p.resolved?.pickScope && p.canPickForGame) setScope(p.resolved.pickScope); }, [p.resolved?.pickScope, p.canPickForGame]);

  const activeId = p.resolved?.presetId ?? null;
  const showingCustom = !!p.resolved && (p.resolved.source === "game" || p.resolved.source === "system");
  const hasCustom = !!p.resolved && (scope === "game" ? p.resolved.hasGameLayout || p.resolved.hasSystemLayout : p.resolved.hasSystemLayout);
  const active = p.presets.find((x) => x.id === activeId);

  if (p.presets.length < 2 && !hasCustom) return null;
  return (
    <div className="room-overlay__presets">
      <div className="room-overlay__chips" role="radiogroup" aria-label="Touch layout">
        {p.presets.map((x) => (
          <button key={x.id} type="button" role="radio" aria-checked={!showingCustom && activeId === x.id}
            className={!showingCustom && activeId === x.id ? "is-on" : ""} onClick={() => p.onPick(x.id, scope)}
            title={x.note || x.name}>
            {x.name}
            {x.id === p.suggestedId && p.presets.length > 1 && <span className="room-overlay__star" aria-label="suggested for this game">★</span>}
          </button>
        ))}
        {hasCustom && (
          <button type="button" role="radio" aria-checked={showingCustom} className={showingCustom ? "is-on" : ""}
            onClick={() => p.onPick(CUSTOM_PICK, scope)}>
            ✎ My layout
          </button>
        )}
      </div>
      {p.canPickForGame && (
        <div className="room-overlay__seg room-overlay__seg--small" role="radiogroup" aria-label="Apply to">
          <button type="button" role="radio" aria-checked={scope === "game"} className={scope === "game" ? "is-on" : ""} onClick={() => setScope("game")}>
            This game
          </button>
          <button type="button" role="radio" aria-checked={scope === "system"} className={scope === "system" ? "is-on" : ""} onClick={() => setScope("system")}>
            All {p.systemName} games
          </button>
        </div>
      )}
      {!showingCustom && active?.note && <p className="room-overlay__note">{active.note}</p>}
    </div>
  );
}
