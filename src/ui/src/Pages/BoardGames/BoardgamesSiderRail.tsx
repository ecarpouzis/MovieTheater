/**
 * The Boardgames filter rail in the section's sider (desktop): the generic FacetRail over the
 * client-side spec, reading the same URL the page reads and pushing the same URLs. The SmartSearch
 * lives in the SectionBar's centre slot (the page mounts it); the rail carries the count on its head
 * line — computed here over the same cached rows the page filters, so the two always agree.
 */
import { useLocation } from "react-router-dom";
import FacetRail from "../../catalog/rail/FacetRail";
import { savableSearch, useSavedSearches } from "../../catalog/rail/savedSearches";
import useFacetOptions from "../../catalog/rail/useFacetOptions";
import useFacetState from "../../catalog/rail/useFacetState";
import { isGroupedBrowse } from "../../catalog/state/useCatalogView";
import useBoardgamesBrowse, { BOARDGAMES_ENTITY_PARAMS, useBoardgamesResults, type BoardgamesViewer } from "./useBoardgamesBrowse";

export default function BoardgamesSiderRail({ userData }: { userData: BoardgamesViewer | null | undefined }) {
  const location = useLocation();
  const browse = useBoardgamesBrowse(userData);
  const { state, actions, activeCount } = useFacetState(browse.spec, { entityParams: BOARDGAMES_ENTITY_PARAMS });
  const grouped = isGroupedBrowse(location.search, "boardgames");
  const facets = useFacetOptions(browse.spec);
  const results = useBoardgamesResults(browse, state);
  const saved = useSavedSearches("boardgames");
  return (
    <div className="bx-rail-on-sider">
      <FacetRail
        variant="rail"
        search={false}
        spec={browse.spec}
        state={state}
        actions={actions}
        activeCount={activeCount}
        facets={facets.data}
        facetsLoading={facets.isLoading || browse.loading}
        total={browse.loading ? null : results.length}
        grouped={grouped}
        saved={{
          list: saved.list,
          onApply: actions.replaceSearch,
          onRemove: saved.remove,
          onSave: (name) => saved.save(name, savableSearch(location.search, BOARDGAMES_ENTITY_PARAMS)),
        }}
      />
    </div>
  );
}
