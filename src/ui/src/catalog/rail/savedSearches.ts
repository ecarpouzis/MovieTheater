/**
 * Saved searches: a name and the search string, device-scoped under `catalog.saved.v1:<section>`
 * (the same store as the tweaks; logout does not clear it). A saved search is the WHOLE query string
 * minus the entity params — the facets, the year range, the flags AND the catalog's view/group/sort —
 * so applying one lands exactly the browse that was saved. Saving under an existing name replaces it.
 */
import { useCallback, useState } from "react";
import { readStored, writeStored } from "../../utils/storage";

export interface SavedSearch { id: string; name: string; search: string }

export const savedSearchesKey = (section: string) => `catalog.saved.v1:${section}`;

export function readSavedSearches(section: string): SavedSearch[] {
  const raw = readStored(savedSearchesKey(section), null) as string | null;
  if (!raw) return [];
  try {
    const parsed: unknown = JSON.parse(raw);
    if (!Array.isArray(parsed)) return [];
    return parsed.filter((s): s is SavedSearch => !!s && typeof s === "object" && typeof (s as SavedSearch).id === "string" && typeof (s as SavedSearch).name === "string" && typeof (s as SavedSearch).search === "string");
  } catch {
    return [];
  }
}

export function writeSavedSearches(section: string, list: SavedSearch[]): void {
  writeStored(savedSearchesKey(section), list.length ? JSON.stringify(list) : null);
}

/** The part of a query string a saved search keeps: everything but the modal/entity params. */
export function savableSearch(search: string, drop: readonly string[] = ["item", "series"]): string {
  const p = new URLSearchParams(search);
  for (const k of drop) p.delete(k);
  const s = p.toString();
  return s ? `?${s}` : "";
}

let idCounter = 0;
const newId = () => `${Date.now().toString(36)}-${(idCounter++).toString(36)}`;

export function useSavedSearches(section: string) {
  const [list, setList] = useState<SavedSearch[]>(() => readSavedSearches(section));
  const save = useCallback((name: string, search: string) => {
    const trimmed = name.trim();
    if (!trimmed) return;
    setList((prev) => {
      const next = [...prev.filter((s) => s.name.toLowerCase() !== trimmed.toLowerCase()), { id: newId(), name: trimmed, search }];
      writeSavedSearches(section, next);
      return next;
    });
  }, [section]);
  const remove = useCallback((id: string) => {
    setList((prev) => {
      const next = prev.filter((s) => s.id !== id);
      writeSavedSearches(section, next);
      return next;
    });
  }, [section]);
  return { list, save, remove };
}
