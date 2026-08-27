/**
 * The Photos filter rail in the section's sider (desktop, on `/photos/browse`): the shared
 * `SectionSiderRail` over the reel's spec — Album · People · Kind · Camera · Date range — reading the
 * same URL the catalog page reads and pushing the same URLs. The count on the head line is the
 * reel's total for the state (one `top=1` page, held five minutes).
 */
import { useMemo } from "react";
import type { FacetState } from "../../catalog/rail/facetSpec";
import { facetStateKey } from "../../catalog/rail/facetUrl";
import SectionSiderRail from "../../catalog/rail/SectionSiderRail";
import useResultCount from "../../catalog/rail/useResultCount";
import useSectionRail from "../../catalog/rail/useSectionRail";
import useShowHiddenPhotos from "../../hooks/useShowHiddenPhotos";
import { PHOTOS_ENTITY_PARAMS, photosFacetSpec, photosFilterParams } from "./photosFacetSpec";

export function usePhotosResultTotal(state: FacetState, includeHidden: boolean, enabled = true) {
  return useResultCount(["photos", "count", includeHidden, facetStateKey(state)], ({ signal }) => {
    const p = new URLSearchParams(photosFilterParams(state));
    p.set("skip", "0");
    p.set("top", "1");
    if (includeHidden) p.set("includeHidden", "true");
    return fetch(`/API/Photos/Browse?${p.toString()}`, { signal });
  }, enabled);
}

export default function PhotosSiderRail({ refreshKey = 0 }: { refreshKey?: number }) {
  const [showHidden] = useShowHiddenPhotos();
  const spec = useMemo(() => photosFacetSpec(String(refreshKey), !!showHidden), [refreshKey, showHidden]);
  const rail = useSectionRail("photos", spec, { entityParams: PHOTOS_ENTITY_PARAMS });
  const total = usePhotosResultTotal(rail.state, !!showHidden);
  return <SectionSiderRail rail={rail} total={total.data} />;
}
