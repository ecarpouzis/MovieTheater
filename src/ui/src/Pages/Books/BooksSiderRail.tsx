/**
 * The Books filter rail in the section's sider (desktop): the generic FacetRail over the Books spec,
 * reading the same URL the page reads and pushing the same URLs — nothing is shared through props
 * across the sider/page boundary. The Directory is a folder navigator that ignores the catalog
 * filters, so it gets a note instead of inert controls.
 */
import { useMemo } from "react";
import { useLocation } from "react-router-dom";
import FacetRail from "../../catalog/rail/FacetRail";
import { savableSearch, useSavedSearches } from "../../catalog/rail/savedSearches";
import useFacetOptions from "../../catalog/rail/useFacetOptions";
import useFacetState from "../../catalog/rail/useFacetState";
import { booksFacetSpec } from "./booksFacetSpec";
import { isDirectoryBrowse, isGroupedBrowse, useBooksResultTotal } from "./useBooksBrowse";

export default function BooksSiderRail({ username }: { username: string }) {
  const location = useLocation();
  const spec = useMemo(() => booksFacetSpec(username), [username]);
  const { state, actions, activeCount } = useFacetState(spec);
  const directory = isDirectoryBrowse(location.search);
  const grouped = isGroupedBrowse(location.search);
  const facets = useFacetOptions(spec, !directory);
  const total = useBooksResultTotal(state, spec, !directory);
  const saved = useSavedSearches("books");

  if (directory) {
    return <div className="bx-rail-on-sider bx-rail-note">Browsing folders — filters and search apply in the catalog views.</div>;
  }
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
          onSave: (name) => saved.save(name, savableSearch(location.search)),
        }}
      />
    </div>
  );
}
