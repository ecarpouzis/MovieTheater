import { useHistory, useLocation } from "react-router-dom";
import SectionIndexRail from "../catalog/rail/SectionIndexRail";
import { NavUserBlock } from "./navShared";

/**
 * The TV rail (R9 S1c): the user block, then the section's index — the Guide, and the viewer's
 * playlists (the My Playlists sheet, which used to hang off the movies rail's Login block while the
 * channels pages fell through to that rail). Favourites are a toggle inside the guide; the
 * facet rail proper arrives with S2.
 */
export default function TvNavContent({ userData, onUserLoggedIn, setSettingsModalOpen, onOpenPlaylists }) {
  const location = useLocation();
  const history = useHistory();
  const views = [{ key: "guide", label: "Guide", path: "/channels" }];
  if (userData?.hasPassword) views.push({ key: "playlists", label: "My playlists", path: "#playlists" });
  const groups = [{ key: "tv", label: "TV", views }];
  const activeKey = location.pathname.startsWith("/channels") ? "guide" : "";
  return (
    <>
      <NavUserBlock userData={userData} onUserLoggedIn={onUserLoggedIn} setSettingsModalOpen={setSettingsModalOpen} />
      <SectionIndexRail
        groups={groups}
        activeKey={activeKey}
        ariaLabel="TV sections"
        onNavigate={(path) => (path === "#playlists" ? onOpenPlaylists?.() : history.push(path))}
      />
    </>
  );
}
