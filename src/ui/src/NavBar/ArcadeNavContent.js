import { useLocation } from "react-router-dom";
import { NavUserBlock } from "./navShared";
import ArcadeSiderRail from "../Pages/Arcade/ArcadeSiderRail";

// The Arcade sider (R9 S2c): the user block, then — on the lobby — the generic facet rail over the
// lobby's spec (Genre · Players · Region · Mods & hacks · RetroAchievements). The System
// dropdown is gone on purpose: the console carousel above the grid IS the System facet (Eric, canvas
// 2026-08-27) and writes the same `f=system:` the rail's chips remove. The antd Selects and the
// phone's search field retired with S2c: the phone's lobby raises its own full-page sheet from the
// bar's Filters pill (and the top bar's search button), the desktop's SmartSearch sits in the bar.
// `railVisible` is NavBar's: true on desktop, true on a phone only while the drawer is open — the
// drawer is the sider now, and this rail is what it holds (2026-08-27).
function ArcadeNavContent({ userData, onUserLoggedIn, setSettingsModalOpen, railVisible = true }) {
  const location = useLocation();
  // /arcade/trophies is the same lobby with the RA hub open, so the rail belongs there too.
  const onLobby = /^\/arcade\/?(trophies\/?)?$/.test(location.pathname);
  return (
    <>
      <NavUserBlock userData={userData} onUserLoggedIn={onUserLoggedIn}
        setSettingsModalOpen={setSettingsModalOpen} />

      {railVisible && onLobby && <ArcadeSiderRail />}
    </>
  );
}

export default ArcadeNavContent;
