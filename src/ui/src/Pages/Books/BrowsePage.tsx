/**
 * `/books` — the section root: the seven catalog views over the Books source, scoped by the facet
 * state in the URL. The page owns what sits over the results — the count, the active-filter chips
 * with Clear all / Save search — and, on phones, the Filters pill that raises the full-page sheet
 * (the desktop rail lives in the section's sider: `BooksSiderRail`). A kid account browses without
 * filters; the Directory is a folder navigator and ignores them.
 */
import { useQuery } from "@tanstack/react-query";
import { useCallback, useMemo } from "react";
import { useHistory, useLocation } from "react-router-dom";
import CatalogHost from "../../catalog/CatalogHost";
import FacetRail from "../../catalog/rail/FacetRail";
import FilterPill from "../../catalog/rail/FilterPill";
import RailChips from "../../catalog/rail/RailChips";
import SmartSearch from "../../catalog/rail/SmartSearch";
import { BarSearchSlot } from "../../catalog/bar/BarSearch";
import { hasFacetValue } from "../../catalog/rail/facetSpec";
import { savableSearch, useSavedSearches } from "../../catalog/rail/savedSearches";
import useFacetOptions from "../../catalog/rail/useFacetOptions";
import useFacetState from "../../catalog/rail/useFacetState";
import useRailSheet from "../../catalog/rail/useRailSheet";
import { createBooksSource } from "../../catalog/sources/booksSource";
import type { CardItem, DirectoryNode } from "../../catalog/types";
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


export default function BrowsePage({ username, epoch = 0, isKid = false }: BrowsePageProps) {
  const history = useHistory();
  const location = useLocation();
  const sheet = useRailSheet();
  const isMobile = sheet.isMobile;
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

  const saveCurrent = (name: string) => saved.save(name, savableSearch(location.search));

  // The bar's tools: the phone's Filters pill (the desktop rail shows the count on its own head line;
  // the toolbar no longer carries a count — Long Box: counts live where the thing they count lives).
  const barTools = filtersApply && isMobile ? <FilterPill count={activeCount} onClick={sheet.show} /> : null;

  const chips = filtersApply ? (
    <RailChips spec={spec} state={state} actions={actions} facets={facets.data} activeCount={activeCount} onSave={saveCurrent} className="books-browse-chips" />
  ) : null;

  return (
    <>
      {/* The SmartSearch lives in the SectionBar's centre slot on desktop (R9 S1d); the phone's
          sheet keeps its own. */}
      {filtersApply && !isMobile && (
        <BarSearchSlot>
          <SmartSearch spec={spec} facets={facets.data} onAdd={actions.add} onText={actions.setText} placeholder="author:Miller, tag:Noir, series:Batman…" />
        </BarSearchSlot>
      )}
      {filtersApply && isMobile && (
        <FacetRail
          variant="sheet"
          open={sheet.open}
          onClose={sheet.hide}
          spec={spec}
          state={state}
          actions={actions}
          activeCount={activeCount}
          facets={facets.data}
          facetsLoading={facets.isLoading}
          total={total.data}
          grouped={grouped}
          saved={{ list: saved.list, onApply: actions.replaceSearch, onRemove: saved.remove, onSave: saveCurrent }}
        />
      )}
      <CatalogHost section="books" source={source} directoryStart={directoryStart} tools={barTools} beforeResults={chips} />
    </>
  );
}
