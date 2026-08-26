/** The Novels filter rail in the section's sider (desktop) — the generic FacetRail over the Novels spec, reading the URL. */
import { useMemo } from "react";
import { useLocation } from "react-router-dom";
import FacetRail from "../../catalog/rail/FacetRail";
import { savableSearch, useSavedSearches } from "../../catalog/rail/savedSearches";
import useFacetOptions from "../../catalog/rail/useFacetOptions";
import useFacetState from "../../catalog/rail/useFacetState";
import { novelsFacetSpec } from "./novelsFacetSpec";
import { useNovelsTotal } from "./useNovelsBrowse";

export default function NovelsSiderRail({ username }: { username: string }) {
  const location = useLocation();
  const spec = useMemo(() => novelsFacetSpec(username), [username]);
  const { state, actions, activeCount } = useFacetState(spec);
  const facets = useFacetOptions(spec);
  const total = useNovelsTotal(state);
  const saved = useSavedSearches("books-novels");
  return (
    <div className="bx-rail-on-sider">
      <FacetRail
        variant="rail"
        title="Novels"
        spec={spec}
        state={state}
        actions={actions}
        activeCount={activeCount}
        facets={facets.data}
        facetsLoading={facets.isLoading}
        total={total.data}
        grouped={false}
        saved={{ list: saved.list, onApply: actions.replaceSearch, onRemove: saved.remove, onSave: (name) => saved.save(name, savableSearch(location.search)) }}
      />
    </div>
  );
}
