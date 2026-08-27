/**
 * The Books filter rail in the section's sider (desktop): the shared `SectionSiderRail` over the
 * Books spec, reading the same URL the page reads and pushing the same URLs — nothing is shared
 * through props across the sider/page boundary. The Directory is a folder navigator that ignores the
 * catalog filters, so it gets a note instead of inert controls.
 */
import { useMemo } from "react";
import { useLocation } from "react-router-dom";
import SectionSiderRail from "../../catalog/rail/SectionSiderRail";
import useSectionRail from "../../catalog/rail/useSectionRail";
import { booksFacetSpec } from "./booksFacetSpec";
import { isDirectoryBrowse, useBooksResultTotal } from "./useBooksBrowse";

export default function BooksSiderRail({ username }: { username: string }) {
  const location = useLocation();
  const spec = useMemo(() => booksFacetSpec(username), [username]);
  const directory = isDirectoryBrowse(location.search);
  const rail = useSectionRail("books", spec, { facetsEnabled: !directory });
  const total = useBooksResultTotal(rail.state, spec, !directory);
  return (
    <SectionSiderRail
      rail={rail}
      total={total.data}
      note={directory ? "Browsing folders — filters and search apply in the catalog views." : undefined}
    />
  );
}
