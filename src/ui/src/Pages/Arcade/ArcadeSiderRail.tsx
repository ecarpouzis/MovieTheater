/**
 * The Arcade filter rail in the section's sider (desktop): the shared `SectionSiderRail` over the
 * lobby's spec — Region (deselect) · Players · Genre · Mods & hacks · RetroAchievements; no System
 * section, because the console carousel above the grid IS the System facet (it writes the same
 * `f=system:` this rail's chips remove). Reads the same URL the page reads, pushes the same URLs;
 * the count on its head line is the scope's total from one `pageSize=1` page.
 */
import SectionSiderRail from "../../catalog/rail/SectionSiderRail";
import useSectionRail from "../../catalog/rail/useSectionRail";
import { ARCADE_ENTITY_PARAMS } from "./arcadeFacetSpec";
import useArcadeBrowse, { useArcadeResultTotal } from "./useArcadeBrowse";

export default function ArcadeSiderRail() {
  const browse = useArcadeBrowse();
  const rail = useSectionRail("arcade", browse.spec, { entityParams: ARCADE_ENTITY_PARAMS });
  const total = useArcadeResultTotal(browse.filters, browse.filterKey);
  return <SectionSiderRail rail={rail} loading={!browse.facets} total={total.data} />;
}
