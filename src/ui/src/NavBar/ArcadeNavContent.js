import { useLocation } from "react-router-dom";
import { NavUserBlock } from "./navShared";
import useIsMobile from "../hooks/useIsMobile";
import ArcadeSiderRail from "../Pages/Arcade/ArcadeSiderRail";

// The Arcade sider (R9 S2c): the user block, then — on desktop, on the lobby — the generic facet rail
// over the lobby's spec (Region · Players · Genre · Mods & hacks · RetroAchievements). The System
// dropdown is gone on purpose: the console carousel above the grid IS the System facet (Eric, canvas
// 2026-08-27) and writes the same `f=system:` the rail's chips remove. The antd Selects and the
// phone's search field retired with S2c: the phone's lobby raises its own full-page sheet from the
// bar's Filters pill (and the top bar's search button), the desktop's SmartSearch sits in the bar.
function ArcadeNavContent({ userData, onUserLoggedIn, setSettingsModalOpen }) {
  const location = useLocation();
  const isMobile = useIsMobile();
  // /arcade/trophies is the same lobby with the RA hub open, so the rail belongs there too.
  const onLobby = /^\/arcade\/?(trophies\/?)?$/.test(location.pathname);
  return (
    <>
      <NavUserBlock userData={userData} onUserLoggedIn={onUserLoggedIn}
        setSettingsModalOpen={setSettingsModalOpen} />

      {!isMobile && onLobby && <ArcadeSiderRail />}
    </>
  );
}

export default ArcadeNavContent;
