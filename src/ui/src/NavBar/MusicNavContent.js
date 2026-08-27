import { useLocation } from "react-router-dom";
import { NavUserBlock } from "./navShared";
import SectionIndexRail from "../catalog/rail/SectionIndexRail";
import useIsMobile from "../hooks/useIsMobile";
import MusicSiderRail from "../Pages/Music/MusicSiderRail";

// The Music sider (R9 S2c): the user block, the section's index (Browse · Playlists · Now playing —
// the same rows as the bar's tabs, the Books shape), then — on desktop, for a password session —
// the generic facet rail over the shelf's spec (Shelf · Artist · Tag · Year). The old shelf picker
// and the phone's antd Search are gone: the shelf is the rail's `kind:` pills, the phone's browse
// raises its own full-page sheet from the bar's Filters pill (and the top bar's search button), the
// desktop's SmartSearch sits in the bar.
const INDEX_GROUPS = [
  {
    key: "music",
    views: [
      { key: "browse", label: "Browse", path: "/music" },
      { key: "playlists", label: "Playlists", path: "/music/playlists" },
      { key: "now", label: "Now playing", path: "/music/now-playing" },
    ],
  },
];

export function musicIndexKey(pathname) {
  if (pathname.startsWith("/music/playlists")) return "playlists";
  if (pathname.startsWith("/music/now-playing")) return "now";
  return "browse";
}

function MusicNavContent({ userData, onUserLoggedIn, setSettingsModalOpen, setAdminModalOpen }) {
  const location = useLocation();
  const isMobile = useIsMobile();
  return (
    <>
      <NavUserBlock userData={userData} onUserLoggedIn={onUserLoggedIn}
        setSettingsModalOpen={setSettingsModalOpen} setAdminModalOpen={setAdminModalOpen} />

      {userData?.hasPassword && (
        <SectionIndexRail groups={INDEX_GROUPS} activeKey={musicIndexKey(location.pathname)} ariaLabel="Music sections" />
      )}

      {!isMobile && musicIndexKey(location.pathname) === "browse" && <MusicSiderRail userData={userData} />}
    </>
  );
}

export default MusicNavContent;
