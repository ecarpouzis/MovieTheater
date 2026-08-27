import { NavUserBlock } from "./navShared";
import poweredByBggImage from "../../powered_by_BGG_SM.png";
import useIsMobile from "../hooks/useIsMobile";
import BoardgamesSiderRail from "../Pages/BoardGames/BoardgamesSiderRail";

/**
 * The Boardgames sider (R9 S2c): the user block, then — on desktop — the generic facet rail over the
 * section's client-side spec (Players · Age · Play time · Weight · Publisher · Family · Designer ·
 * Category · Mechanic · Year), then the BGG attribution. The old Players / Age / Play Time Selects
 * and the phone's title search are gone: the phone's browse raises its own full-page sheet from the
 * bar's Filters pill (and the top bar's search button), the desktop's SmartSearch sits in the bar.
 */
function BoardGameNavContent({ userData, onUserLoggedIn, setSettingsModalOpen }) {
  const isMobile = useIsMobile();
  return (
    <>
      <NavUserBlock userData={userData} onUserLoggedIn={onUserLoggedIn}
        setSettingsModalOpen={setSettingsModalOpen} />

      {!isMobile && <BoardgamesSiderRail userData={userData} />}

      <div style={{ marginTop: "auto", padding: "12px", borderTop: "1px solid var(--sidebar-border)" }}>
        <img
          src={poweredByBggImage}
          alt="Powered by BoardGameGeek"
          style={{ width: "100%", display: "block", borderRadius: "6px" }}
        />
      </div>
    </>
  );
}

export default BoardGameNavContent;
