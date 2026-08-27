/**
 * The Photos filter rail in the section's sider (desktop, on `/photos/browse`): the generic FacetRail
 * over the reel's spec — Album · People · Kind · Camera · Date range — reading the same URL the
 * catalog page reads and pushing the same URLs. The SmartSearch lives in the SectionBar's centre
 * slot (the page mounts it); the count on the head line is the reel's total for the state (one
 * `top=1` page, held five minutes).
 */
import { useQuery } from "@tanstack/react-query";
import { useMemo } from "react";
import { useLocation } from "react-router-dom";
import FacetRail from "../../catalog/rail/FacetRail";
import type { FacetState } from "../../catalog/rail/facetSpec";
import { facetStateKey } from "../../catalog/rail/facetUrl";
import { savableSearch, useSavedSearches } from "../../catalog/rail/savedSearches";
import useFacetOptions from "../../catalog/rail/useFacetOptions";
import useFacetState from "../../catalog/rail/useFacetState";
import { isGroupedBrowse } from "../../catalog/state/useCatalogView";
import useShowHiddenPhotos from "../../hooks/useShowHiddenPhotos";
import { PHOTOS_ENTITY_PARAMS, photosFacetSpec, photosFilterParams } from "./photosFacetSpec";

export function usePhotosResultTotal(state: FacetState, includeHidden: boolean, enabled = true) {
  const filter = photosFilterParams(state);
  return useQuery({
    queryKey: ["photos", "count", includeHidden, facetStateKey(state)],
    queryFn: async ({ signal }) => {
      const p = new URLSearchParams(filter);
      p.set("skip", "0");
      p.set("top", "1");
      if (includeHidden) p.set("includeHidden", "true");
      const r = await fetch(`/API/Photos/Browse?${p.toString()}`, { signal });
      if (!r.ok) throw new Error(`count → ${r.status}`);
      const data = (await r.json()) as { total?: number };
      return typeof data.total === "number" ? data.total : -1;
    },
    enabled,
    staleTime: 5 * 60 * 1000,
  });
}

export default function PhotosSiderRail({ refreshKey = 0 }: { refreshKey?: number }) {
  const location = useLocation();
  const [showHidden] = useShowHiddenPhotos();
  const spec = useMemo(() => photosFacetSpec(String(refreshKey), !!showHidden), [refreshKey, showHidden]);
  const { state, actions, activeCount } = useFacetState(spec, { entityParams: PHOTOS_ENTITY_PARAMS });
  const grouped = isGroupedBrowse(location.search, "photos");
  const facets = useFacetOptions(spec);
  const total = usePhotosResultTotal(state, !!showHidden);
  const saved = useSavedSearches("photos");
  return (
    <div className="bx-rail-on-sider">
      <FacetRail
        variant="rail"
        search={false}
        spec={spec}
        state={state}
        actions={actions}
        activeCount={activeCount}
        facets={facets.data}
        facetsLoading={facets.isLoading}
        total={total.data}
        grouped={grouped}
        saved={{
          list: saved.list,
          onApply: actions.replaceSearch,
          onRemove: saved.remove,
          onSave: (name) => saved.save(name, savableSearch(location.search, PHOTOS_ENTITY_PARAMS)),
        }}
      />
    </div>
  );
}
