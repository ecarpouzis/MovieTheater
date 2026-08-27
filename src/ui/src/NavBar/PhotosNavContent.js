import { useLocation } from "react-router-dom";
import { NavUserBlock } from "./navShared";
import SectionIndexRail from "../catalog/rail/SectionIndexRail";
import usePhotosAlbum, { photosNavGroups, photosSection } from "../hooks/usePhotosAlbum";
import PhotosSiderRail from "../Pages/Photos/PhotosSiderRail";

// The family album's rail (docs/photos-plan.md §4).
//
// /photos used to be served the MOVIE rail — a title/actor/genre search over a film library, on a
// page that has neither. What an album needs instead is an index: the ways into it, and the count
// beside each so a member can see where the collection actually is.
//
// Every row is a real URL (§ the route map), so a view can be bookmarked, shared and refreshed. The
// counts come from the shared album store, which the page is reading at the same moment — one status
// request feeds both. Since R9 S2c the index is the site's generic `SectionIndexRail` (the photos
// rail's own classes were the prototype it generalized), and on `/photos/browse` the sider carries
// the reel's facet rail under it (Album · People · Kind · Camera · Date range) — in the phone
// drawer too, since the drawer IS the sider (2026-08-27).
//
// Log Out, the theme switch and the admin show-hidden checkbox are NOT here: they live in the
// navbar's shared footer, the same as on every other section.
function PhotosNavContent({ userData, onUserLoggedIn, setSettingsModalOpen, railVisible = true }) {
  const location = useLocation();
  // Enabled unconditionally because this component only renders on /photos — no other section ever
  // issues a photos request.
  const { state, status, unnamed } = usePhotosAlbum({ username: userData?.username });

  const active = photosSection(location.pathname);
  const groups = state === "ready" ? photosNavGroups(status, unnamed.length) : [];

  return (
    <>
      <NavUserBlock userData={userData} onUserLoggedIn={onUserLoggedIn}
        setSettingsModalOpen={setSettingsModalOpen} />

      {/* Nothing is listed until the server has said the album is open. The rail is not the gate —
          it just has no index to draw for someone the gate refused. */}
      <SectionIndexRail groups={groups} activeKey={active} ariaLabel="Album sections" />

      {railVisible && state === "ready" && active === "browse" && <PhotosSiderRail />}
    </>
  );
}

export default PhotosNavContent;
