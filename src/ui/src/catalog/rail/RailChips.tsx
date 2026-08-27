/**
 * The row over a section's results: the active-filter chips with Clear all, and the "Save search"
 * affordance that swaps the row for the name prompt. The page owns the saved-search list (one
 * `useSavedSearches` per page, shared with its sheet) and hands in `onSave`; this only draws.
 * Renders an empty surface when nothing is active — `.bx-rail-surface:empty` hides it.
 */
import { useState } from "react";
import ActiveChips from "./ActiveChips";
import type { FacetOptionRow, FacetSpec, FacetState } from "./facetSpec";
import { SaveSearchPrompt } from "./SavedSearchesRail";
import type { FacetActions } from "./useFacetState";

export interface RailChipsProps {
  spec: FacetSpec;
  state: FacetState;
  actions: FacetActions;
  facets?: Record<string, FacetOptionRow[]>;
  activeCount: number;
  /** Save the current search under a name; absent = no save affordance. */
  onSave?: (name: string) => void;
  className?: string;
}

export default function RailChips({ spec, state, actions, facets, activeCount, onSave, className }: RailChipsProps) {
  const [prompt, setPrompt] = useState(false);
  return (
    <div className={`bx-rail-surface bx-chips-row${className ? ` ${className}` : ""}`}>
      {prompt && onSave
        ? <SaveSearchPrompt onSave={(name) => { onSave(name); setPrompt(false); }} onCancel={() => setPrompt(false)} />
        : <ActiveChips spec={spec} state={state} actions={actions} facets={facets} onSave={onSave && activeCount > 0 ? () => setPrompt(true) : undefined} />}
    </div>
  );
}
