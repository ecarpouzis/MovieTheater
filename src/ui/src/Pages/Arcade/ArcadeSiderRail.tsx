/**
 * The Arcade filter rail in the section's sider (desktop): the generic FacetRail over the lobby's
 * spec — Region (deselect) · Players · Genre · Mods & hacks · RetroAchievements; no System section,
 * because the console carousel above the grid IS the System facet (it writes the same `f=system:`
 * this rail's chips remove). Reads the same URL the page reads, pushes the same URLs; the count on
 * its head line is the scope's total from one `pageSize=1` page.
 */
import { useLocation } from "react-router-dom";
import FacetRail from "../../catalog/rail/FacetRail";
import { savableSearch, useSavedSearches } from "../../catalog/rail/savedSearches";
import useFacetOptions from "../../catalog/rail/useFacetOptions";
import useFacetState from "../../catalog/rail/useFacetState";
import { isGroupedBrowse } from "../../catalog/state/useCatalogView";
import { ARCADE_ENTITY_PARAMS } from "./arcadeFacetSpec";
import useArcadeBrowse, { useArcadeResultTotal } from "./useArcadeBrowse";

export default function ArcadeSiderRail() {
  const location = useLocation();
  const browse = useArcadeBrowse();
  const { state, actions, activeCount } = useFacetState(browse.spec, { entityParams: ARCADE_ENTITY_PARAMS });
  const grouped = isGroupedBrowse(location.search, "arcade");
  const facets = useFacetOptions(browse.spec);
  const total = useArcadeResultTotal(browse.filters, browse.filterKey);
  const saved = useSavedSearches("arcade");
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
        facetsLoading={facets.isLoading || !browse.facets}
        total={total.data}
        grouped={grouped}
        saved={{
          list: saved.list,
          onApply: actions.replaceSearch,
          onRemove: saved.remove,
          onSave: (name) => saved.save(name, savableSearch(location.search, ARCADE_ENTITY_PARAMS)),
        }}
      />
    </div>
  );
}
