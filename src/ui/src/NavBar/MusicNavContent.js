import { useLocation } from "react-router-dom";
import { NavUserBlock } from "./navShared";
import MusicSiderRail from "../Pages/Music/MusicSiderRail";

// The Music sider (R9 S2c): the user block, then — for a password session, on the browse — the
// generic facet rail over the shelf's spec (Shelf · Artist · Genre · Tag · Year). The old shelf
// picker and the phone's antd Search are gone: the shelf is the rail's `kind:` pills, the desktop's
// SmartSearch sits in the bar, and the phone's browse also raises a full-page sheet from the bar's
// Filters pill (the quick path — the drawer holds this same rail).
//
// It used to draw an index rail too — Browse · Playlists · Now playing — which is the exact set of
// destinations the Music bar tabs already carry. That is the duplicate-options bug Eric called out
// on 2026-08-27 (the same one that deleted Books' `SectionIndexTabs`), and the MusicBrowse artboard
// draws this sider as user block → filters → Log out. Books keeps ITS index (it carries counts and
// an Operate group the bar has no room for) and Movies keeps Seen · Want · Rate · Playlists; a
// section's index rows earn their place by saying something the tabs do not.
export function musicIndexKey(pathname) {
  if (pathname.startsWith("/music/playlists")) return "playlists";
  if (pathname.startsWith("/music/now-playing")) return "now";
  // The Explore landing is a BAR tab: the facet rail hides there — there is no list on that page
  // for it to narrow (R9 S7).
  if (pathname.startsWith("/music/explore")) return "explore";
  return "browse";
}

function MusicNavContent({ userData, onUserLoggedIn, setSettingsModalOpen, railVisible = true }) {
  const location = useLocation();
  return (
    <>
      <NavUserBlock userData={userData} onUserLoggedIn={onUserLoggedIn}
        setSettingsModalOpen={setSettingsModalOpen} />

      {railVisible && musicIndexKey(location.pathname) === "browse" && <MusicSiderRail userData={userData} />}
    </>
  );
}

export default MusicNavContent;
