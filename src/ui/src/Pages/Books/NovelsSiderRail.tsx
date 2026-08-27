/** The Novels filter rail in the section's sider (desktop) — the shared `SectionSiderRail` over the Novels spec, reading the URL. */
import { useMemo } from "react";
import SectionSiderRail from "../../catalog/rail/SectionSiderRail";
import useSectionRail from "../../catalog/rail/useSectionRail";
import { novelsFacetSpec } from "./novelsFacetSpec";
import { useNovelsTotal } from "./useNovelsBrowse";

export default function NovelsSiderRail({ username }: { username: string }) {
  const spec = useMemo(() => novelsFacetSpec(username), [username]);
  // Novels has no grouped views — the groups-only facets never apply.
  const rail = useSectionRail("books-novels", spec, { grouped: false });
  const total = useNovelsTotal(rail.state);
  return <SectionSiderRail rail={rail} total={total.data} title="Novels" />;
}
