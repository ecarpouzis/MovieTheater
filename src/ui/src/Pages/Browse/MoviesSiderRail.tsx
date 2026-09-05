/**
 * The Movies filter rail in the section's sider (desktop): the shared `SectionSiderRail` over the
 * Movies spec, reading the same URL the page reads and pushing the same URLs — nothing crosses the
 * sider/page boundary through props. The count on the head line is `/API/Browse`'s own total for the
 * state (one `pageSize=1` page, held five minutes).
 */
import SectionSiderRail from "../../catalog/rail/SectionSiderRail";
import useSectionRail from "../../catalog/rail/useSectionRail";
import { MOVIES_ENTITY_PARAMS, moviesViewerIdentity, useMoviesFacetSpec, useMoviesResultTotal } from "./useMoviesBrowse";

/** `listsOwner`: the friend whose lists the browse is on (`for=`), or null for the viewer's own. */
export default function MoviesSiderRail({ userData, listsOwner = null }: { userData: { username?: string | null; ageRestriction?: number | null } | null | undefined; listsOwner?: string | null }) {
  const spec = useMoviesFacetSpec(moviesViewerIdentity(userData), listsOwner);
  const rail = useSectionRail("movies", spec, { entityParams: MOVIES_ENTITY_PARAMS });
  const total = useMoviesResultTotal(rail.state);
  return <SectionSiderRail rail={rail} total={total.data} />;
}
