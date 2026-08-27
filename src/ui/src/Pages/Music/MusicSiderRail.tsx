/**
 * The Music filter rail in the section's sider (desktop): the generic FacetRail over the shelf's
 * client-side spec, reading the same URL the page reads and pushing the same URLs. The SmartSearch
 * lives in the SectionBar's centre slot (the page mounts it); the rail carries the count on its head
 * line — computed here over the same shared shelf rows the page filters, so the two always agree.
 */
import { useLocation } from "react-router-dom";
import FacetRail from "../../catalog/rail/FacetRail";
import { savableSearch, useSavedSearches } from "../../catalog/rail/savedSearches";
import useFacetOptions from "../../catalog/rail/useFacetOptions";
import useFacetState from "../../catalog/rail/useFacetState";
import { isGroupedBrowse } from "../../catalog/state/useCatalogView";
import useMusicBrowse, { MUSIC_ENTITY_PARAMS, useMusicResults } from "./useMusicShelf";

export default function MusicSiderRail({ userData }: { userData: { hasPassword?: boolean | null } | null | undefined }) {
  const location = useLocation();
  const browse = useMusicBrowse(userData);
  const { state, actions, activeCount } = useFacetState(browse.spec, { entityParams: MUSIC_ENTITY_PARAMS });
  const grouped = isGroupedBrowse(location.search, "music");
  const facets = useFacetOptions(browse.spec, !!userData?.hasPassword);
  const results = useMusicResults(browse, state);
  const saved = useSavedSearches("music");
  if (!userData?.hasPassword) return null;
  const total = browse.loading ? null : browse.itemsMode === "groups" ? results.artists.length : results.albums.length;
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
        total={total}
        grouped={grouped}
        saved={{
          list: saved.list,
          onApply: actions.replaceSearch,
          onRemove: saved.remove,
          onSave: (name) => saved.save(name, savableSearch(location.search, MUSIC_ENTITY_PARAMS)),
        }}
      />
    </div>
  );
}
