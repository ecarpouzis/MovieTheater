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
import { hasFacetValue } from "../../catalog/rail/facetSpec";
import sectionRailSurfaces from "../../catalog/rail/sectionRailSurfaces";
import useRailSheet from "../../catalog/rail/useRailSheet";
import useSectionRail from "../../catalog/rail/useSectionRail";
import { createBooksSource } from "../../catalog/sources/booksSource";
import type { CardItem, DirectoryNode } from "../../catalog/types";
import { fetchFacets, fetchFolder } from "./booksApi";
import { booksFacetSpec } from "./booksFacetSpec";
import { useMediaToken } from "./booksMedia";
import { bk } from "./booksQuery";
import { openEntity } from "./openEntity";
import { isDirectoryBrowse, useBooksResultTotal } from "./useBooksBrowse";

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
  const spec = useMemo(() => booksFacetSpec(username), [username]);
  const directory = isDirectoryBrowse(location.search);
  const filtersApply = !isKid && !directory;
  const rail = useSectionRail("books", spec, { facetsEnabled: filtersApply });
  const { state, actions } = rail;
  const { epoch: mediaEpoch } = useMediaToken();

  const total = useBooksResultTotal(state, spec, !directory);

  // The Group pill's axes come from the HOST (`/browse/facets` → `groupAxes`), never from a constant
  // here: a stale host does not 400 on `groupBy=author`, it silently answers with COLLECTIONS, so the
  // only safe reading of "can this binary group by a credit" is the binary's own answer. Until it
  // lands (and on an older host, which sends no list) `booksGroupsFor` falls back to the five axes
  // every host has always had. Half-hour stale on a payload the host memory-caches for 48 h.
  const facets = useQuery({ queryKey: bk.facets("comic"), queryFn: ({ signal }) => fetchFacets("comic", signal), staleTime: 30 * 60 * 1000 });
  const groupAxes = facets.data?.groupAxes;

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
    () => createBooksSource({ facetState: state, spec, epoch, mediaEpoch, groupAxes, onOpen, onOpenSeries, onScope: scope }),
    [state, spec, epoch, mediaEpoch, groupAxes, onOpen, onOpenSeries, scope],
  );

  // The bar's tools: the phone's Filters pill (the desktop rail shows the count on its own head line;
  // the toolbar no longer carries a count — Long Box: counts live where the thing they count lives).
  const railSurfaces = sectionRailSurfaces(rail, sheet, {
    total: total.data,
    placeholder: "author:Miller, tag:Noir, series:Batman…",
    chipsClassName: "books-browse-chips",
  });
  // A kid account browses without filters, and the Directory ignores them.
  const barTools = filtersApply ? railSurfaces.pill : null;

  return (
    <>
      {filtersApply && railSurfaces.surfaces}
      <CatalogHost section="books" source={source} directoryStart={directoryStart} tools={barTools} beforeResults={filtersApply ? railSurfaces.chips : null} />
    </>
  );
}
