/**
 * The active filters as removable chips: the text, one chip per included value, a red "not" chip per
 * excluded value, the year range, the rating floor, each flag — then Clear all and (optionally) Save.
 * Number-valued facets (series, collections) show their label from the loaded facet lists.
 */
import type { ReactNode } from "react";
import type { FacetDef, FacetOptionRow, FacetSpec, FacetState, FacetValue } from "./facetSpec";
import { activeFacetCount, facetEquals } from "./facetSpec";
import type { FacetActions } from "./useFacetState";

export interface ActiveChipsProps {
  spec: FacetSpec;
  state: FacetState;
  actions: Pick<FacetActions, "remove" | "setText" | "setYears" | "setRating" | "setFlag" | "clearAll">;
  facets?: Record<string, FacetOptionRow[]>;
  onSave?: () => void;
}

export function chipLabel(def: FacetDef, value: FacetValue, facets?: Record<string, FacetOptionRow[]>): string {
  if (def.valueType === "number") {
    const row = (facets?.[def.key] ?? []).find((o) => facetEquals(o.value, value));
    if (row) return row.label;
  }
  return String(value);
}

export default function ActiveChips({ spec, state, actions, facets, onSave }: ActiveChipsProps) {
  if (activeFacetCount(state, spec) === 0) return null;
  const chips: ReactNode[] = [];

  if (state.q.trim()) {
    chips.push(
      <button key="q" type="button" className="bx-chip" onClick={() => actions.setText("")} title="Remove">
        <span className="bx-chip-k">search</span>{state.q}<span className="bx-chip-x" aria-hidden="true">×</span>
      </button>,
    );
  }
  for (const def of spec.facets) {
    for (const v of state.include[def.key] ?? []) {
      chips.push(
        <button key={`i:${def.key}:${String(v)}`} type="button" className="bx-chip" onClick={() => actions.remove(def.key, v)} title="Remove">
          <span className="bx-chip-k">{def.one}</span>{chipLabel(def, v, facets)}<span className="bx-chip-x" aria-hidden="true">×</span>
        </button>,
      );
    }
    for (const v of state.exclude[def.key] ?? []) {
      chips.push(
        <button key={`x:${def.key}:${String(v)}`} type="button" className="bx-chip bx-chip-ex" onClick={() => actions.remove(def.key, v)} title="Remove">
          <span className="bx-chip-k">not {def.one.toLowerCase()}</span>{chipLabel(def, v, facets)}<span className="bx-chip-x" aria-hidden="true">×</span>
        </button>,
      );
    }
  }
  if (state.yearMin != null || state.yearMax != null) {
    chips.push(
      <button key="y" type="button" className="bx-chip" onClick={() => actions.setYears(null, null)} title="Remove">
        <span className="bx-chip-k">years</span>{state.yearMin ?? "…"}–{state.yearMax ?? "…"}<span className="bx-chip-x" aria-hidden="true">×</span>
      </button>,
    );
  }
  if (state.ratingMin > 0) {
    const preset = spec.rating?.presets.find((p) => p.value === state.ratingMin);
    chips.push(
      <button key="r" type="button" className="bx-chip" onClick={() => actions.setRating(0)} title="Remove">
        <span className="bx-chip-k">rating</span>{preset?.label ?? `${state.ratingMin}+`}<span className="bx-chip-x" aria-hidden="true">×</span>
      </button>,
    );
  }
  for (const flag of spec.flags ?? []) {
    if (!state.flags[flag.key]) continue;
    chips.push(
      <button key={`f:${flag.key}`} type="button" className="bx-chip" onClick={() => actions.setFlag(flag.key, false)} title="Remove">
        <span className="bx-chip-k">my</span>{flag.label}<span className="bx-chip-x" aria-hidden="true">×</span>
      </button>,
    );
  }

  return (
    <div className="bx-chiprow" role="group" aria-label="Active filters">
      {chips}
      <div className="bx-chip-actions">
        <button type="button" className="bx-chip-clear" onClick={actions.clearAll}>Clear all</button>
        {onSave && <button type="button" className="bx-chip-save" onClick={onSave}>＋ Save search</button>}
      </div>
    </div>
  );
}
