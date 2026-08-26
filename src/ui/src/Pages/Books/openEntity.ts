/**
 * The Books modals live in the URL: `?item=<id>` opens the item modal, `?series=<id>` the series
 * modal — exactly one at a time. Open is a PUSH (Back closes it, and Back walks item ↔ series when
 * one opened the other); close is a REPLACE (the site's convention: closing must not grow history).
 *
 * This is the ONE place the single-issue collapse lives: a series that is one issue in the library
 * IS that issue, so opening "the series" opens the item. Every surface (cards, group headers, Explore
 * rails, the shelf) routes through `openEntity`, so none can drift.
 */
import type { History, Location } from "history";

export type EntityTarget =
  | { kind: "item"; id: number }
  | { kind: "series"; id: number; single?: { isSingleIssueSeries: boolean; itemId: number } | null };

export interface EntityParams { item: number | null; series: number | null }

function positiveInt(raw: string | null): number | null {
  if (!raw || !/^[0-9]+$/.test(raw)) return null;
  const n = Number(raw);
  return Number.isSafeInteger(n) && n > 0 ? n : null;
}

export function readEntityParams(search: string): EntityParams {
  const p = new URLSearchParams(search);
  return { item: positiveInt(p.get("item")), series: positiveInt(p.get("series")) };
}

type Nav = Pick<History, "push" | "replace">;
type Loc = Pick<Location, "pathname" | "search" | "state">;

/** The resolved target: a single-issue series collapses to its item. */
export function resolveTarget(t: EntityTarget): { param: "item" | "series"; id: number } {
  if (t.kind === "series" && t.single?.isSingleIssueSeries && t.single.itemId > 0) return { param: "item", id: t.single.itemId };
  return { param: t.kind, id: t.id };
}

export function openEntity(history: Nav, location: Loc, t: EntityTarget): void {
  const { param, id } = resolveTarget(t);
  const params = new URLSearchParams(location.search);
  const already = params.get(param) === String(id) && !params.has(param === "item" ? "series" : "item");
  if (already) return;
  params.delete("item");
  params.delete("series");
  params.set(param, String(id));
  history.push({ pathname: location.pathname, search: `?${params.toString()}`, state: location.state });
}

export function closeEntity(history: Nav, location: Loc): void {
  const params = new URLSearchParams(location.search);
  if (!params.has("item") && !params.has("series")) return;
  params.delete("item");
  params.delete("series");
  const search = params.toString();
  history.replace({ pathname: location.pathname, search: search ? `?${search}` : "", state: location.state });
}
