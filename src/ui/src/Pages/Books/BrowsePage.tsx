/**
 * `/books` — the section root: the seven catalog views over the Books source, scoped by the facet
 * state in the URL. The rail's facets, search, chips and saved searches arrive in S2; the URL codec is
 * already the one they will write, so a filtered link works today.
 */
import { useQuery } from "@tanstack/react-query";
import { useCallback, useMemo } from "react";
import { useHistory, useLocation } from "react-router-dom";
import CatalogHost from "../../catalog/CatalogHost";
import type { FacetState } from "../../catalog/rail/facetSpec";
import { hasFacetValue } from "../../catalog/rail/facetSpec";
import { parseFacetState, writeFacetState } from "../../catalog/rail/facetUrl";
import { createBooksSource } from "../../catalog/sources/booksSource";
import type { CardItem, DirectoryNode } from "../../catalog/types";
import { fetchFolder } from "./booksApi";
import { booksFacetSpec } from "./booksFacetSpec";
import { useMediaToken } from "./booksMedia";
import { bk } from "./booksQuery";
import { booksTweakExtras, siteTheme } from "./booksTheme";
import { openEntity } from "./openEntity";

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
  const spec = useMemo(() => booksFacetSpec(username), [username]);
  const state = useMemo(() => parseFacetState(location.search, spec), [location.search, spec]);
  const { epoch: mediaEpoch } = useMediaToken();
  const theme = siteTheme();
  const tweakExtras = useMemo(() => booksTweakExtras(theme), [theme]);

  // The Directory's "start here" — a "Browse this folder" link carries ?dir=<folderId>.
  const dir = positiveInt(new URLSearchParams(location.search).get("dir"));
  const folder = useQuery({ queryKey: bk.admin("folder-node", dir ?? 0), queryFn: () => fetchFolder(dir!, { top: 1 }), enabled: dir != null, staleTime: 10 * 60 * 1000 });
  const directoryStart = useMemo<DirectoryNode[] | undefined>(() => {
    if (dir == null) return undefined;
    const f = folder.data?.folder;
    return [{ id: String(dir), label: f?.name ?? f?.path ?? `Folder ${dir}`, count: f?.descendantItemCount, hasChildren: (f?.directChildCount ?? 0) > 0 }];
  }, [dir, folder.data]);

  /** Scope in place (a group header): apply a facet or a year range, drop the grouping a level, push. */
  const scope = useCallback((patch: { facet?: { key: string; value: string | number }; years?: [number, number]; group?: string }) => {
    if (isKid) return;
    const next: FacetState = { ...state, include: { ...state.include }, exclude: { ...state.exclude }, flags: { ...state.flags } };
    if (patch.facet && !hasFacetValue(next.include[patch.facet.key], patch.facet.value)) {
      next.include[patch.facet.key] = [...(next.include[patch.facet.key] ?? []), patch.facet.value];
    }
    if (patch.years) { next.yearMin = patch.years[0]; next.yearMax = patch.years[1]; }
    const params = new URLSearchParams(location.search);
    writeFacetState(params, next, spec);
    if (patch.group) params.set("group", patch.group);
    params.delete("item");
    params.delete("series");
    history.push({ pathname: location.pathname, search: `?${params.toString()}`, state: location.state });
  }, [state, spec, history, location.pathname, location.search, location.state, isKid]);

  const onOpen = useCallback((item: CardItem) => openEntity(history, location, { kind: "item", id: item.id }), [history, location]);
  const onOpenSeries = useCallback((seriesId: number, _label: string, single?: { isSingleIssueSeries: boolean; itemId: number } | null) =>
    openEntity(history, location, { kind: "series", id: seriesId, single }), [history, location]);

  const source = useMemo(
    () => createBooksSource({ facetState: state, spec, epoch, mediaEpoch, tweakExtras, onOpen, onOpenSeries, onScope: scope }),
    [state, spec, epoch, mediaEpoch, tweakExtras, onOpen, onOpenSeries, scope],
  );

  return <CatalogHost section="books" source={source} directoryStart={directoryStart} />;
}
