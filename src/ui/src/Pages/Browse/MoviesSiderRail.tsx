/**
 * The Movies filter rail in the section's sider (desktop): the generic FacetRail over the Movies
 * spec, reading the same URL the page reads and pushing the same URLs — nothing crosses the
 * sider/page boundary through props. The SmartSearch lives in the SectionBar's centre slot (the
 * page mounts it); the rail carries the count on its head line.
 */
import { useLocation } from "react-router-dom";
import FacetRail from "../../catalog/rail/FacetRail";
import { savableSearch, useSavedSearches } from "../../catalog/rail/savedSearches";
import useFacetOptions from "../../catalog/rail/useFacetOptions";
import useFacetState from "../../catalog/rail/useFacetState";
import { isGroupedBrowse } from "../../catalog/state/useCatalogView";
import { moviesViewerIdentity, useMoviesFacetSpec, useMoviesResultTotal } from "./useMoviesBrowse";

export const MOVIES_ENTITY_PARAMS = ["title"] as const;

export default function MoviesSiderRail({ userData }: { userData: { username?: string | null; ageRestriction?: number | null } | null | undefined }) {
  const location = useLocation();
  const spec = useMoviesFacetSpec(moviesViewerIdentity(userData));
  const { state, actions, activeCount } = useFacetState(spec, { entityParams: MOVIES_ENTITY_PARAMS });
  const grouped = isGroupedBrowse(location.search, "movies");
  const facets = useFacetOptions(spec);
  const total = useMoviesResultTotal(state);
  const saved = useSavedSearches("movies");
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
          onSave: (name) => saved.save(name, savableSearch(location.search, MOVIES_ENTITY_PARAMS)),
        }}
      />
    </div>
  );
}
