/**
 * `/books` — the section root: the seven catalog views over the Books source, scoped by the facet
 * state in the URL. The page owns what sits over the results — the count, the active-filter chips
 * with Clear all / Save search — and, on phones, the Filters pill that raises the full-page sheet
 * (the desktop rail lives in the section's sider: `BooksSiderRail`). A kid account browses without
 * filters; the Directory is a folder navigator and ignores them.
 */
import { useQuery } from "@tanstack/react-query";
import { useCallback, useEffect, useMemo, useState } from "react";
import { useHistory, useLocation } from "react-router-dom";
import CatalogHost from "../../catalog/CatalogHost";
import ActiveChips from "../../catalog/rail/ActiveChips";
import FacetRail from "../../catalog/rail/FacetRail";
import { hasFacetValue } from "../../catalog/rail/facetSpec";
import { savableSearch, useSavedSearches } from "../../catalog/rail/savedSearches";
import { SaveSearchPrompt } from "../../catalog/rail/SavedSearchesRail";
import useFacetOptions from "../../catalog/rail/useFacetOptions";
import useFacetState from "../../catalog/rail/useFacetState";
import { createBooksSource } from "../../catalog/sources/booksSource";
import type { CardItem, DirectoryNode } from "../../catalog/types";
import useIsMobile from "../../hooks/useIsMobile";
import { fetchFolder } from "./booksApi";
import { booksFacetSpec } from "./booksFacetSpec";
import { useMediaToken } from "./booksMedia";
import { bk } from "./booksQuery";
import { booksTweakExtras, siteTheme } from "./booksTheme";
import { openEntity } from "./openEntity";
import { isDirectoryBrowse, isGroupedBrowse, useBooksResultTotal } from "./useBooksBrowse";

export interface BrowsePageProps {
  username: string;
  /** Bumped when an admin job changed the catalog — every band drops. */
  epoch?: number;
  isKid?: boolean;
}

function positiveInt(raw: string | null): number | null {
  if (!raw || !/^[0-9]+$/.test(raw)) return null;
  const n = Number(raw);
  return Number.isSafeInteger(n) && n > 0 ? n : null;
}

function FilterGlyph() {
  return (
    <svg viewBox="0 0 16 16" width="15" height="15" fill="none" stroke="currentColor" strokeWidth="1.5" aria-hidden="true">
      <line x1="2" y1="4" x2="14" y2="4" /><line x1="2" y1="8" x2="14" y2="8" /><line x1="2" y1="12" x2="14" y2="12" />
      <circle cx="6" cy="4" r="1.7" fill="currentColor" stroke="none" /><circle cx="10" cy="8" r="1.7" fill="currentColor" stroke="none" /><circle cx="5" cy="12" r="1.7" fill="currentColor" stroke="none" />
    </svg>
  );
}

export default function BrowsePage({ username, epoch = 0, isKid = false }: BrowsePageProps) {
  const history = useHistory();
  const location = useLocation();
  const isMobile = useIsMobile();
  const spec = useMemo(() => booksFacetSpec(username), [username]);
  const { state, actions, activeCount } = useFacetState(spec);
  const { epoch: mediaEpoch } = useMediaToken();
  const theme = siteTheme();
  const tweakExtras = useMemo(() => booksTweakExtras(theme), [theme]);

  const directory = isDirectoryBrowse(location.search);
  const grouped = isGroupedBrowse(location.search);
  const filtersApply = !isKid && !directory;
  const facets = useFacetOptions(spec, filtersApply);
  const total = useBooksResultTotal(state, spec, !directory);
  const saved = useSavedSearches("books");
  const [sheetOpen, setSheetOpen] = useState(false);
  const [savePrompt, setSavePrompt] = useState(false);
  useEffect(() => { if (!isMobile) setSheetOpen(false); }, [isMobile]);
  useEffect(() => { setSheetOpen(false); }, [location.search]);

  // The Directory's "start here" — a "Browse this folder" link carries ?dir=<folderId>.
  const dir = positiveInt(new URLSearchParams(location.search).get("dir"));
  const folder = useQuery({ queryKey: bk.admin("folder-node", dir ?? 0), queryFn: () => fetchFolder(dir!, { top: 1 }), enabled: dir != null, staleTime: 10 * 60 * 1000 });
  const directoryStart = useMemo<DirectoryNode[] | undefined>(() => {
    if (dir == null) return undefined;
    const f = folder.data?.folder;
    return [{ id: String(dir), label: f?.name ?? f?.path ?? `Folder ${dir}`, count: f?.descendantItemCount, hasChildren: (f?.directChildCount ?? 0) > 0 }];
  }, [dir, folder.data]);

  /** Scope in place (a group header): apply a facet or a year range, drop the grouping a level — one push. */
  const scope = useCallback((patch: { facet?: { key: string; value: string | number }; years?: [number, number]; group?: string }) => {
    if (isKid) return;
    actions.apply((d) => {
      if (patch.facet && !hasFacetValue(d.include[patch.facet.key], patch.facet.value)) {
        d.include[patch.facet.key] = [...(d.include[patch.facet.key] ?? []), patch.facet.value];
      }
      if (patch.years) { d.yearMin = patch.years[0]; d.yearMax = patch.years[1]; }
    }, patch.group ? { group: patch.group } : undefined);
  }, [actions, isKid]);

  const onOpen = useCallback((item: CardItem) => openEntity(history, location, { kind: "item", id: item.id }), [history, location]);
  const onOpenSeries = useCallback((seriesId: number, _label: string, single?: { isSingleIssueSeries: boolean; itemId: number } | null) =>
    openEntity(history, location, { kind: "series", id: seriesId, single }), [history, location]);

  const source = useMemo(
    () => createBooksSource({ facetState: state, spec, epoch, mediaEpoch, tweakExtras, onOpen, onOpenSeries, onScope: scope }),
    [state, spec, epoch, mediaEpoch, tweakExtras, onOpen, onOpenSeries, scope],
  );

  const saveCurrent = (name: string) => { saved.save(name, savableSearch(location.search)); setSavePrompt(false); };

  const lead = (
    <div className="books-browse-lead">
      {filtersApply && isMobile && (
        <button type="button" className="bx-filter-pill" onClick={() => setSheetOpen(true)} aria-label="Filters" title="Filters">
          <FilterGlyph />
          {activeCount > 0 && <span className="bx-tool-num">{activeCount}</span>}
        </button>
      )}
      {!directory && total.data != null && total.data >= 0 && (
        <span className="bx-count" aria-live="polite">{total.data.toLocaleString()}<span className="bx-count-of"> {spec.noun ?? "results"}</span></span>
      )}
    </div>
  );

  const chips = filtersApply ? (
    <div className="bx-rail-surface books-browse-chips">
      {savePrompt
        ? <SaveSearchPrompt onSave={saveCurrent} onCancel={() => setSavePrompt(false)} />
        : <ActiveChips spec={spec} state={state} actions={actions} facets={facets.data} onSave={activeCount > 0 ? () => setSavePrompt(true) : undefined} />}
    </div>
  ) : null;

  return (
    <>
      {filtersApply && isMobile && (
        <FacetRail
          variant="sheet"
          open={sheetOpen}
          onClose={() => setSheetOpen(false)}
          spec={spec}
          state={state}
          actions={actions}
          activeCount={activeCount}
          facets={facets.data}
          facetsLoading={facets.isLoading}
          total={total.data}
          grouped={grouped}
          saved={{ list: saved.list, onApply: actions.replaceSearch, onRemove: saved.remove, onSave: (name) => saved.save(name, savableSearch(location.search)) }}
        />
      )}
      <CatalogHost section="books" source={source} directoryStart={directoryStart} leading={lead} beforeResults={chips} />
    </>
  );
}
