/**
 * The Boardgames filter rail in the section's sider (desktop): the shared `SectionSiderRail` over
 * the client-side spec, reading the same URL the page reads and pushing the same URLs. The count on
 * the head line is computed here over the same cached rows the page filters, so the two always agree.
 */
import SectionSiderRail from "../../catalog/rail/SectionSiderRail";
import useSectionRail from "../../catalog/rail/useSectionRail";
import useBoardgamesBrowse, { BOARDGAMES_ENTITY_PARAMS, useBoardgamesResults, type BoardgamesViewer } from "./useBoardgamesBrowse";

export default function BoardgamesSiderRail({ userData }: { userData: BoardgamesViewer | null | undefined }) {
  const browse = useBoardgamesBrowse(userData);
  const rail = useSectionRail("boardgames", browse.spec, { entityParams: BOARDGAMES_ENTITY_PARAMS });
  const results = useBoardgamesResults(browse, rail.state);
  return <SectionSiderRail rail={rail} loading={browse.loading} total={browse.loading ? null : results.length} />;
}
