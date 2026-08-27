/**
 * The Music filter rail in the section's sider (desktop): the shared `SectionSiderRail` over the
 * shelf's client-side spec, reading the same URL the page reads and pushing the same URLs. The count
 * on the head line is computed here over the same shared shelf rows the page filters, so the two
 * always agree; the noun follows the Items mode (artists on the one-per-artist grid).
 */
import SectionSiderRail from "../../catalog/rail/SectionSiderRail";
import useSectionRail from "../../catalog/rail/useSectionRail";
import useMusicBrowse, { MUSIC_ENTITY_PARAMS, useMusicResults } from "./useMusicShelf";

export default function MusicSiderRail({ userData }: { userData: { hasPassword?: boolean | null } | null | undefined }) {
  const gated = !userData?.hasPassword;
  const browse = useMusicBrowse(userData);
  const rail = useSectionRail("music", browse.spec, { entityParams: MUSIC_ENTITY_PARAMS, facetsEnabled: !gated });
  const results = useMusicResults(browse, rail.state);
  if (gated) return null;
  const total = browse.loading ? null : browse.itemsMode === "groups" ? results.artists.length : results.albums.length;
  return <SectionSiderRail rail={rail} loading={browse.loading} total={total} />;
}
