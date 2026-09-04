import { useLocation } from "react-router-dom";
import { NavUserBlock } from "./navShared";
import SectionIndexRail from "../catalog/rail/SectionIndexRail";
import ArcadeSiderRail from "../Pages/Arcade/ArcadeSiderRail";

// The Arcade sider (R9 S2c): the user block, the viewer's own two surfaces, then — on the lobby —
// the generic facet rail over the lobby's spec (Genre · Players · Region · Mods & hacks ·
// RetroAchievements). The System dropdown is gone on purpose: the console carousel above the grid IS
// the System facet (Eric, canvas 2026-08-27) and writes the same `f=system:` the rail's chips remove.
// The antd Selects and the phone's search field retired with S2c: the phone's lobby raises its own
// full-page sheet from the bar's Filters pill (and the top bar's search button), the desktop's
// SmartSearch sits in the bar.
//
// `railVisible` is NavBar's: true on desktop, true on a phone only while the drawer is open — the
// drawer is the sider now, and this rail is what it holds (2026-08-27). It gates the FACET rail
// only. The index rows below are navigation, not queries, so they follow the movies rail's Seen ·
// Want · Rate block and render unconditionally.
//
// Those two rows are the point of this block. Saves and Trophies are things a PLAYER owns — the
// saves vault reads `/API/Arcade/Saves/Mine` and the trophy room `/API/Arcade/Trophies/Mine`, both
// scoped to the signed-in user by the auth cookie — but the only way to either was a small button on
// the lobby's bar and a card on the ADMIN shell. They sit where the movies keep the viewer's lists.
// No counts: the movie rows read theirs out of `userData`, which is already loaded, while a count
// here would mean two speculative fetches on every arcade page for a number nobody navigates by.
function ArcadeNavContent({ userData, onUserLoggedIn, setSettingsModalOpen, railVisible = true }) {
  const location = useLocation();
  const onLobby = /^\/arcade\/?$/.test(location.pathname);

  const activeKey = location.pathname.startsWith("/arcade/saves") ? "saves"
    : location.pathname.startsWith("/arcade/trophies") ? "trophies" : "";
  const groups = userData
    ? [{
      key: "you",
      label: "You",
      views: [
        { key: "saves", label: "My saves", path: "/arcade/saves" },
        { key: "trophies", label: "Trophies", path: "/arcade/trophies" },
      ],
    }]
    : [];

  return (
    <>
      <NavUserBlock userData={userData} onUserLoggedIn={onUserLoggedIn}
        setSettingsModalOpen={setSettingsModalOpen} />

      <SectionIndexRail groups={groups} activeKey={activeKey} ariaLabel="Your arcade" />

      {railVisible && onLobby && <ArcadeSiderRail />}
    </>
  );
}

export default ArcadeNavContent;
